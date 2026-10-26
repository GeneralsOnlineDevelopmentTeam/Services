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
using Microsoft.Extensions.Primitives;
using MySqlConnector;
using System.Text;
using System.Text.Json;

public class AcReviewCrc
{
	public Int64 UserId { get; set; }
	public string OriginalCrc { get; set; } = null!;
	public string DiffCrc { get; set; } = null!;
	public ulong MostRecentMatch { get; set; }
}

public class AcReviewModule
{
	public ulong ReportId { get; set; }
	public Int64 UserId { get; set; }
	public ulong MatchId { get; set; }
	public string ModuleName { get; set; } = null!;
	public string ModulePath { get; set; } = null!;
	public ulong ModuleSize { get; set; }
}

// One module reported by an anticheat Type2 probe response
public readonly record struct AcModuleReport(string ModuleName, string ModulePath, int ModuleSize);

// Cached AC:allowed_modules / AC:allowed_module_paths, refreshed whenever the configuration reloads
internal static class AntiCheatModuleAllowList
{
	private sealed class Snapshot
	{
		public string[] Modules = Array.Empty<string>();
		public string[] Paths = Array.Empty<string>();
	}

	private static readonly object s_lock = new();
	private static volatile Snapshot? s_snapshot = null;
	private static IDisposable? s_reloadRegistration = null;

	private static Snapshot? GetSnapshot()
	{
		Snapshot? snapshot = s_snapshot;
		if (snapshot != null)
		{
			return snapshot;
		}

		IConfiguration? config = GenOnlineService.Program.g_Config;
		if (config == null)
		{
			return null;
		}

		lock (s_lock)
		{
			if (s_reloadRegistration == null)
			{
				s_reloadRegistration = ChangeToken.OnChange(() => config.GetReloadToken(), () => Reload());
			}

			return Reload();
		}
	}

	private static Snapshot? Reload()
	{
		IConfiguration? config = GenOnlineService.Program.g_Config;
		if (config == null)
		{
			return null;
		}

		IConfiguration acSettings = config.GetSection("AC");
		Snapshot snapshot = new Snapshot
		{
			Modules = acSettings.GetSection("allowed_modules").Get<List<string>>()?.ToArray() ?? Array.Empty<string>(),
			Paths = acSettings.GetSection("allowed_module_paths").Get<List<string>>()?.ToArray() ?? Array.Empty<string>()
		};

		s_snapshot = snapshot;
		return snapshot;
	}

	// Filename must match an allowed module exactly, or the directory must start with an allowed path
	public static bool IsAllowed(string moduleFilename, string modulePath)
	{
		Snapshot? snapshot = GetSnapshot();
		if (snapshot == null)
		{
			return false;
		}

		if (Array.IndexOf(snapshot.Modules, moduleFilename) >= 0)
		{
			return true;
		}

		foreach (string allowedDir in snapshot.Paths)
		{
			if (modulePath.StartsWith(allowedDir))
			{
				return true;
			}
		}

		return false;
	}
}

public class AcReviewNewAccountGame
{
	public ulong ReportId { get; set; }
	public Int64 UserId { get; set; }
	public ulong MatchId { get; set; }
}

public class AcReviewProbe
{
	public ulong ReportId { get; set; }
	public Int64 UserId { get; set; }
	public ulong MatchId { get; set; }
	public string Reason { get; set; } = null!;
}


public class AcReviewCrcConfiguration : IEntityTypeConfiguration<AcReviewCrc>
{
	public void Configure(EntityTypeBuilder<AcReviewCrc> entity)
	{
		entity.ToTable("ac_reviews_crc");

		entity.HasKey(e => new { e.UserId, e.OriginalCrc, e.DiffCrc, e.MostRecentMatch });

		entity.Property(e => e.UserId)
			.HasColumnName("user_id")
			.HasColumnType("bigint(20)");

		entity.Property(e => e.OriginalCrc)
			.HasColumnName("original_crc")
			.HasColumnType("varchar(64)")
			.IsRequired();

		entity.Property(e => e.DiffCrc)
			.HasColumnName("diff_crc")
			.HasColumnType("varchar(64)")
			.IsRequired();

		entity.Property(e => e.MostRecentMatch)
			.HasColumnName("most_recent_match")
			.HasColumnType("bigint(20) unsigned");
	}
}

public class AcReviewModuleConfiguration : IEntityTypeConfiguration<AcReviewModule>
{
	public void Configure(EntityTypeBuilder<AcReviewModule> entity)
	{
		entity.ToTable("ac_reviews_modules");

		entity.HasKey(e => e.ReportId);

		entity.Property(e => e.ReportId)
			.HasColumnName("report_id")
			.HasColumnType("bigint(20) unsigned")
			.ValueGeneratedOnAdd();

		entity.Property(e => e.UserId)
			.HasColumnName("user_id")
			.HasColumnType("bigint(20) unsigned");

		entity.Property(e => e.MatchId)
			.HasColumnName("match_id")
			.HasColumnType("bigint(20) unsigned");

		entity.Property(e => e.ModuleName)
			.HasColumnName("module_name")
			.HasColumnType("varchar(128)")
			.IsRequired();

		entity.Property(e => e.ModulePath)
			.HasColumnName("module_path")
			.HasColumnType("varchar(512)")
			.IsRequired();

		entity.Property(e => e.ModuleSize)
			.HasColumnName("module_size")
			.HasColumnType("bigint(20) unsigned");

		entity.HasIndex(e => new { e.UserId, e.MatchId, e.ModuleName })
			.IsUnique()
			.HasDatabaseName("user_id_match_id_module_name");
	}
}

public class AcReviewNewAccountGameConfiguration : IEntityTypeConfiguration<AcReviewNewAccountGame>
{
	public void Configure(EntityTypeBuilder<AcReviewNewAccountGame> entity)
	{
		entity.ToTable("ac_reviews_new_account_games");

		entity.HasKey(e => e.ReportId);

		entity.Property(e => e.ReportId)
			.HasColumnName("report_id")
			.HasColumnType("bigint(20) unsigned")
			.ValueGeneratedOnAdd();

		entity.Property(e => e.UserId)
			.HasColumnName("user_id")
			.HasColumnType("bigint(20) unsigned");

		entity.Property(e => e.MatchId)
			.HasColumnName("match_id")
			.HasColumnType("bigint(20) unsigned");
	}
}

public class AcReviewProbeConfiguration : IEntityTypeConfiguration<AcReviewProbe>
{
	public void Configure(EntityTypeBuilder<AcReviewProbe> entity)
	{
		entity.ToTable("ac_reviews_probes");

		entity.HasKey(e => e.ReportId);

		entity.Property(e => e.ReportId)
			.HasColumnName("report_id")
			.HasColumnType("bigint(20) unsigned")
			.ValueGeneratedOnAdd();

		entity.Property(e => e.UserId)
			.HasColumnName("user_id")
			.HasColumnType("bigint(20) unsigned");

		entity.Property(e => e.MatchId)
			.HasColumnName("match_id")
			.HasColumnType("bigint(20) unsigned");

		entity.Property(e => e.Reason)
			.HasColumnName("reason")
			.HasColumnType("varchar(256)")
			.IsRequired();
	}
}

	

namespace Database
{
	internal static class DbErrors
	{
		// MySQL/MariaDB error 1062 (ER_DUP_ENTRY), anywhere in the exception chain
		public static bool IsDuplicateKey(Exception ex)
		{
			for (Exception? current = ex; current != null; current = current.InnerException)
			{
				if (current is MySqlException mysqlEx && mysqlEx.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
				{
					return true;
				}
			}

			return false;
		}
	}

	public static class AntiCheat
	{
		public static async Task AC_BanUser(AppDbContext db, long userId, string reason)
		{
			try
			{
				await db.Users
					.Where(u => u.ID == userId)
					.ExecuteUpdateAsync(setters => setters
						.SetProperty(u => u.IsBanned, true)
						.SetProperty(u => u.BanReason, reason)
						.SetProperty(u => u.BannedBy, "Anticheat")
						.SetProperty(u => u.BanVerifiedBy, "x64")
						.SetProperty(u => u.BanAliases, "")
					);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] AC_BanUser failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}


		public static async Task FlagAccountForReview(
	AppDbContext db,
	Int64 userId,
	string originalCrc,
	string diffCrc,
	ulong matchId)
		{
			try
			{
				// Idempotent insert: every column is part of the primary key, so a duplicate is an identical report
				// (same match) and is ignored; a new match still records a new row, as before
				await db.Database.ExecuteSqlInterpolatedAsync($@"
					INSERT INTO ac_reviews_crc (user_id, original_crc, diff_crc, most_recent_match)
					VALUES ({userId}, {originalCrc}, {diffCrc}, {matchId})
					ON DUPLICATE KEY UPDATE user_id = user_id");
			}
			catch (Exception ex)
			{
				if (DbErrors.IsDuplicateKey(ex))
				{
					return;
				}

				Console.WriteLine($"[ERROR] FlagAccountForReview failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}


		// Flags all given modules for one probe response in a single idempotent statement.
		// Returns true when the rows are persisted (or already were), false on a real database error.
		public static async Task<bool> FlagAccountForReview_Modules(
	AppDbContext db,
	Int64 userId,
	ulong matchId,
	IReadOnlyList<AcModuleReport> modules)
		{
			if (modules.Count == 0)
			{
				return true;
			}

			try
			{
				const int ChunkSize = 200;

				for (int offset = 0; offset < modules.Count; offset += ChunkSize)
				{
					int count = Math.Min(ChunkSize, modules.Count - offset);

					// Only parameter placeholders are concatenated here, all values are bound as parameters
					StringBuilder sql = new StringBuilder("INSERT INTO ac_reviews_modules (user_id, match_id, module_name, module_path, module_size) VALUES ");
					List<object> parameters = new List<object>(2 + (count * 3));
					parameters.Add(new MySqlParameter("@uid", userId));
					parameters.Add(new MySqlParameter("@mid", matchId));

					for (int i = 0; i < count; ++i)
					{
						AcModuleReport module = modules[offset + i];

						if (i > 0)
						{
							sql.Append(',');
						}

						sql.Append("(@uid, @mid, @n").Append(i).Append(", @p").Append(i).Append(", @s").Append(i).Append(')');
						parameters.Add(new MySqlParameter("@n" + i, module.ModuleName));
						parameters.Add(new MySqlParameter("@p" + i, module.ModulePath));
						parameters.Add(new MySqlParameter("@s" + i, (ulong)module.ModuleSize));
					}

					// No-op update on the unique key (user_id, match_id, module_name): repeats are ignored, other errors still surface
					sql.Append(" ON DUPLICATE KEY UPDATE report_id = report_id");

					await db.Database.ExecuteSqlRawAsync(sql.ToString(), parameters);
				}

				return true;
			}
			catch (Exception ex)
			{
				if (DbErrors.IsDuplicateKey(ex))
				{
					return true;
				}

				Console.WriteLine($"[ERROR] FlagAccountForReview_Modules failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return false;
			}
		}



		public static async Task FlagAccountForReview_SuspectProbes(
	AppDbContext db,
	Int64 userId,
	ulong matchId,
	string reason)
		{
			try
			{
				db.AcReviewsProbes.Add(new AcReviewProbe
				{
					UserId = userId,
					MatchId = matchId,
					Reason = reason
				});

				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] FlagAccountForReview_SuspectProbes failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}


		public static async Task FlagAccountForReview_NewAccount_FirstMatches(
	AppDbContext db,
	Int64 userId,
	ulong matchId)
		{
			try
			{
				db.AcReviewsNewAccountGames.Add(new AcReviewNewAccountGame
				{
					UserId = userId,
					MatchId = matchId
				});

				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] FlagAccountForReview_NewAccount_FirstMatches failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}




	}
}