/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
**
**    This program is distributed in the hope that it will be useful,
**    but WITHOUT ANY WARRANTY; without even the implied warranty of
**    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
**    GNU Affero General Public License for more details.
**
**    You should have received a copy of the GNU Affero General Public License
**    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

// MySQL/MariaDB is deprecated and removed in the next major version, along with MySqlAppDbContext and its migrations.

public sealed class AppDbContextFactoryAdapter<TContext> : IDbContextFactory<AppDbContext> where TContext : AppDbContext
{
	private readonly IDbContextFactory<TContext> _inner;

	public AppDbContextFactoryAdapter(IDbContextFactory<TContext> inner)
	{
		_inner = inner;
	}

	public AppDbContext CreateDbContext()
	{
		return _inner.CreateDbContext();
	}

	public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
	{
		return await _inner.CreateDbContextAsync(cancellationToken);
	}
}

// Mirrors the old structure.sql exactly, so existing databases can be baselined instead of recreated
public class MySqlAppDbContext : AppDbContext
{
	// Only used by the design time factory, at runtime the server version is detected from the connection
	public static readonly ServerVersion DesignTimeServerVersion = new MariaDbServerVersion(new Version(12, 1, 2));

	public MySqlAppDbContext(DbContextOptions<MySqlAppDbContext> options)
		: base(options)
	{
	}

	private static readonly ValueConverter<bool, sbyte> TinyIntBool = new(v => v ? (sbyte)1 : (sbyte)0, v => v != 0);

	private static PropertyBuilder Utf8mb4Bin(PropertyBuilder property)
	{
		return property.HasCharSet("utf8mb4").UseCollation("utf8mb4_bin");
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		ConfigureJsonColumns(modelBuilder, p => Utf8mb4Bin(p.HasColumnType("longtext")));

		modelBuilder.Entity<WebSession>(e =>
		{
			e.Property(x => x.Created).HasColumnType("timestamp");
			e.Property(x => x.Expiration).HasColumnType("timestamp");
			e.Property(x => x.LastUsed).HasColumnType("timestamp");
		});

		// Plain "datetime" like the existing schema, not the provider default datetime(6)
		modelBuilder.Entity<MatchHistoryEntry>(e =>
		{
			e.Property(x => x.Started).HasColumnType("datetime");
			e.Property(x => x.TimeFinished).HasColumnType("datetime");
			e.Property(x => x.MaxCamHeight).HasColumnType("smallint(6) unsigned");
		});

		modelBuilder.Entity<ExternalPublicationEntry>(e =>
		{
			e.Property(x => x.NextAttemptAt).HasColumnType("datetime");
			e.Property(x => x.PublishedAt).HasColumnType("datetime");
		});

		modelBuilder.Entity<UserTokenState>(e =>
		{
			e.Property(x => x.PreviousRefreshJtiExpires).HasColumnType("datetime");
			e.Property(x => x.Updated).HasColumnType("datetime");
		});

		modelBuilder.Entity<User>(e =>
		{
			e.Property(x => x.AccountType).HasColumnType("int(1)");
			e.Property(x => x.LastLogin).HasColumnType("datetime");
			e.Property(x => x.ClientID).HasColumnType("smallint(5) unsigned");
			e.Property(x => x.LimitSuperweapons).HasConversion<int>().HasColumnType("int(11)");
			// Any non-zero value is true, like a plain bool column. The default converter only treats 1 as true, which would
			// let a ban written as another value by the website or admin tooling read as not banned.
			e.Property(x => x.IsAdmin).HasConversion(TinyIntBool).HasColumnType("tinyint(4)");
			e.Property(x => x.IsBanned).HasConversion(TinyIntBool).HasColumnType("tinyint(4)");
			e.Property<int?>("PermissionsLevel").HasColumnType("int(11)");
		});

		modelBuilder.Entity<UserDevice>(e =>
		{
			Utf8mb4Bin(e.Property(x => x.HWID_0));
			Utf8mb4Bin(e.Property(x => x.HWID_1));
			Utf8mb4Bin(e.Property(x => x.HWID_2));
			Utf8mb4Bin(e.Property(x => x.HWID_3));
			Utf8mb4Bin(e.Property(x => x.HWID_4));
			Utf8mb4Bin(e.Property(x => x.HWID_5));
			Utf8mb4Bin(e.Property(x => x.IPAddress));
		});

		modelBuilder.Entity<AcReviewModule>().Property(x => x.UserId).HasColumnType("bigint(20) unsigned");
		modelBuilder.Entity<AcReviewNewAccountGame>().Property(x => x.UserId).HasColumnType("bigint(20) unsigned");
		modelBuilder.Entity<AcReviewProbe>().Property(x => x.UserId).HasColumnType("bigint(20) unsigned");
	}
}

public class PostgresAppDbContext : AppDbContext
{
	public PostgresAppDbContext(DbContextOptions<PostgresAppDbContext> options)
		: base(options)
	{
	}

	protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
	{
		base.ConfigureConventions(configurationBuilder);

		// No unsigned types in PostgreSQL (Npgsql would map uint to xid), so widen to the next signed type.
		// ulong values are IDs and sizes that always fit in a signed 64-bit.
		configurationBuilder.Properties<ulong>().HaveConversion<long>();
		configurationBuilder.Properties<uint>().HaveConversion<long>();
		configurationBuilder.Properties<ushort>().HaveConversion<int>();
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		// jsonb gives us the same validity guarantee the old json_valid() CHECK constraint did
		ConfigureJsonColumns(modelBuilder, p => p.HasColumnType("jsonb"));

		// Partial indexes: only the few pending / banned rows are ever looked up
		modelBuilder.Entity<ExternalPublicationEntry>()
			.HasIndex(e => new { e.PublishedAt, e.NextAttemptAt })
			.HasDatabaseName("ix_external_publication_pending")
			.HasFilter("published_at IS NULL");

		modelBuilder.Entity<User>()
			.HasIndex(e => e.IsBanned)
			.HasDatabaseName("ix_users_banned")
			.HasFilter("banned");

		// Index names are unique per schema in PostgreSQL, so don't reuse MySQL's bare "user_id" name
		modelBuilder.Entity<WebSession>()
			.HasIndex(e => e.UserId)
			.HasDatabaseName("ix_web_sessions_user_id");
	}
}

// For "dotnet ef". Creating a migration doesn't connect, so the connection strings only need to be well-formed.
public class MySqlAppDbContextFactory : IDesignTimeDbContextFactory<MySqlAppDbContext>
{
	public MySqlAppDbContext CreateDbContext(string[] args)
	{
		var options = new DbContextOptionsBuilder<MySqlAppDbContext>()
			.UseMySql("Server=localhost;Database=genonline_design_time;User ID=root;Password=design_time_placeholder", MySqlAppDbContext.DesignTimeServerVersion)
			.Options;

		return new MySqlAppDbContext(options);
	}
}

public class PostgresAppDbContextFactory : IDesignTimeDbContextFactory<PostgresAppDbContext>
{
	public PostgresAppDbContext CreateDbContext(string[] args)
	{
		var options = new DbContextOptionsBuilder<PostgresAppDbContext>()
			.UseNpgsql("Host=localhost;Database=genonline_design_time;Username=postgres;Password=design_time_placeholder")
			.Options;

		return new PostgresAppDbContext(options);
	}
}
