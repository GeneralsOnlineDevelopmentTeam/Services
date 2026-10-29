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
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MySqlConnector;
using Npgsql;

// All timestamps are UTC. MySQL hands back Unspecified and timestamptz refuses anything but Utc on write.
// Unspecified values are assumed to already be UTC, which is how the service has always used them.
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
	public UtcDateTimeConverter()
		: base(v => ToUtc(v), v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
	{
	}

	private static DateTime ToUtc(DateTime value)
	{
		return value.Kind switch
		{
			DateTimeKind.Utc => value,
			DateTimeKind.Local => value.ToUniversalTime(),
			_ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
		};
	}
}

public static class DbHelpers
{
	public static bool IsUniqueViolation(Exception? ex)
	{
		for (Exception? current = ex; current != null; current = current.InnerException)
		{
			if (current is PostgresException pgEx && pgEx.SqlState == PostgresErrorCodes.UniqueViolation)
			{
				return true;
			}

			if (current is MySqlException mySqlEx && mySqlEx.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
			{
				return true;
			}
		}

		return false;
	}
}

// Provider neutral model, provider differences live in Database.Providers.cs.
// Every model change needs a migration for BOTH contexts.
public abstract class AppDbContext : DbContext
{
	public DbSet<User> Users => Set<User>();
	public DbSet<UserDevice> UserDevices => Set<UserDevice>();
	public DbSet<DailyStat> DailyStats => Set<DailyStat>();
	public DbSet<LeaderboardDaily> LeaderboardDaily => Set<LeaderboardDaily>();
	public DbSet<LeaderboardMonthly> LeaderboardMonthly => Set<LeaderboardMonthly>();
	public DbSet<LeaderboardYearly> LeaderboardYearly => Set<LeaderboardYearly>();
	public DbSet<ServiceStat> ServiceStats => Set<ServiceStat>();
	public DbSet<MatchHistoryEntry> MatchHistory => Set<MatchHistoryEntry>();
	public DbSet<ExternalPublicationEntry> ExternalPublications => Set<ExternalPublicationEntry>();
	public DbSet<UserStatsEntry> UserStats => Set<UserStatsEntry>();

	public DbSet<FriendEntry> Friends => Set<FriendEntry>();

	public DbSet<BlockedUserEntry> BlockedUsers => Set<BlockedUserEntry>();

	public DbSet<FriendRequestEntry> FriendRequests => Set<FriendRequestEntry>();
	public DbSet<ConnectionOutcome> ConnectionOutcomes => Set<ConnectionOutcome>();
	public DbSet<UserTokenState> UserTokenStates => Set<UserTokenState>();

	public DbSet<AcReviewCrc> AcReviewsCrc => Set<AcReviewCrc>();
	public DbSet<AcReviewModule> AcReviewsModules => Set<AcReviewModule>();
	public DbSet<AcReviewNewAccountGame> AcReviewsNewAccountGames => Set<AcReviewNewAccountGame>();
	public DbSet<AcReviewProbe> AcReviewsProbes => Set<AcReviewProbe>();

	// Non-generic so the derived contexts can pass their own DbContextOptions<TDerived>
	protected AppDbContext(DbContextOptions options)
		: base(options)
	{
	}

	protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
	{
		base.ConfigureConventions(configurationBuilder);

		configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
	}

	// Every JSON column, so the derived contexts can set the provider specific column type
	protected static void ConfigureJsonColumns(ModelBuilder modelBuilder, Action<PropertyBuilder> configure)
	{
		configure(modelBuilder.Entity<DailyStat>().Property(e => e.Stats));
		configure(modelBuilder.Entity<UserStatsEntry>().Property(e => e.Stats));

		for (int i = 0; i < 8; i++)
		{
			configure(modelBuilder.Entity<MatchHistoryEntry>().Property<string?>($"MemberSlot{i}"));
		}
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		// Fluent configuration here
		base.OnModelCreating(modelBuilder);

		modelBuilder.ApplyConfiguration(new UserConfiguration());
		modelBuilder.ApplyConfiguration(new UserDevicesConfiguration());
		modelBuilder.ApplyConfiguration(new WebSessionConfiguration());
		modelBuilder.ApplyConfiguration(new DailyStatsConfiguration());
		modelBuilder.ApplyConfiguration(new LeaderboardDailyConfiguration());
		modelBuilder.ApplyConfiguration(new LeaderboardMonthlyConfiguration());
		modelBuilder.ApplyConfiguration(new LeaderboardYearlyConfiguration());
		modelBuilder.ApplyConfiguration(new ServiceStatsConfiguration());
		modelBuilder.ApplyConfiguration(new MatchHistoryConfiguration());
		modelBuilder.ApplyConfiguration(new ExternalPublicationConfiguration());
		modelBuilder.ApplyConfiguration(new UserStatsConfiguration());
		modelBuilder.ApplyConfiguration(new FriendConfiguration());
		modelBuilder.ApplyConfiguration(new FriendRequestConfiguration());
		modelBuilder.ApplyConfiguration(new BlockedUserConfiguration());
		modelBuilder.ApplyConfiguration(new ConnectionOutcomeConfiguration());
		modelBuilder.ApplyConfiguration(new UserTokenStateConfiguration());
		modelBuilder.ApplyConfiguration(new AcReviewCrcConfiguration());
		modelBuilder.ApplyConfiguration(new AcReviewModuleConfiguration());
		modelBuilder.ApplyConfiguration(new AcReviewNewAccountGameConfiguration());
		modelBuilder.ApplyConfiguration(new AcReviewProbeConfiguration());
	}
}