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
using System.Text.Json;

// TODO_EFCORE: When updating this, make sure we preserve old ON DUPLICATE behavior, to overwrite the old data since day_of_year key will be re-used
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
				db.AcReviewsCrc.Add(new AcReviewCrc
				{
					UserId = userId,
					OriginalCrc = originalCrc,
					DiffCrc = diffCrc,
					MostRecentMatch = matchId
				});

				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] FlagAccountForReview failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}


		public static async Task FlagAccountForReview_Module(
	AppDbContext db,
	Int64 userId,
	ulong matchId,
	string moduleName,
	string modulePath,
	int moduleSize)
		{
			try
			{
				db.AcReviewsModules.Add(new AcReviewModule
				{
					UserId = userId,
					MatchId = matchId,
					ModuleName = moduleName,
					ModulePath = modulePath,
					ModuleSize = (ulong)moduleSize
				});

				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] FlagAccountForReview_Module failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
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