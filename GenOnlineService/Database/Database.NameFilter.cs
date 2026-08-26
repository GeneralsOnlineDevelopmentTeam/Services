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

using GenOnlineService;
using GenOnlineService.NameFilter;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class NameFilterReject
{
	public Int64 ID { get; set; }

	public Int64 UserID { get; set; }
	public string AttemptedName { get; set; } = String.Empty;
	public string Skeleton { get; set; } = String.Empty;

	public int RuleID { get; set; } = -1;
	public ENameRuleAction Action { get; set; } = ENameRuleAction.Block;
	public ENameRejectSource Source { get; set; } = ENameRejectSource.NameChange;
	public DateTime Created { get; set; } = DateTime.UnixEpoch;
}

public class NameFilterRejectConfiguration : IEntityTypeConfiguration<NameFilterReject>
{
	public void Configure(EntityTypeBuilder<NameFilterReject> builder)
	{
		builder.ToTable("name_filter_rejects");

		builder.HasKey(e => e.ID);

		builder.Property(e => e.ID).HasColumnName("id");
		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.AttemptedName).HasColumnName("attempted_name").HasColumnType("varchar(64)");
		builder.Property(e => e.Skeleton).HasColumnName("skeleton").HasColumnType("varchar(64)");
		builder.Property(e => e.RuleID).HasColumnName("rule_id");
		builder.Property(e => e.Action).HasColumnName("action").HasColumnType("tinyint(4)");
		builder.Property(e => e.Source).HasColumnName("source").HasColumnType("tinyint(4)");
		builder.Property(e => e.Created).HasColumnName("created");
	}
}

namespace Database
{
	public static class NameFilter
	{
		private static string Truncate(string strValue, int maxLength)
		{
			return strValue.Length <= maxLength ? strValue : strValue.Substring(0, maxLength);
		}

		// The accounts a scan row points at. The scan rows are used rather than users.
		// displayname_skeleton because they are the exact set the report was written about.
		public static async Task<List<Int64>> GetScanUserIDsBySkeleton(AppDbContext db, string strSkeleton)
		{
			try
			{
				return await db.NameFilterRejects
					.Where(r => r.Source == ENameRejectSource.Rescan && r.Skeleton == strSkeleton)
					.Select(r => r.UserID)
					.Distinct()
					.ToListAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] GetScanUserIDsBySkeleton failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return new List<Int64>();
			}
		}

		public static async Task<bool> IsNameOrSkeletonTaken(AppDbContext db, string strName, string strSkeleton)
		{
			try
			{
				return await db.Users
					.AnyAsync(u => u.DisplayName == strName || (strSkeleton != "" && u.DisplayNameSkeleton == strSkeleton));
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] IsNameOrSkeletonTaken failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return true;
			}
		}

		// Thousands of hits is not a list anybody reads. One skeleton usually accounts for hundreds
		// of accounts and one decision clears all of them, so group by rule and by skeleton.
		public static async Task<List<NameScanRuleGroup>> GetScanSummary(AppDbContext db, Dictionary<int, NameFilterRule> dictRules)
		{
			try
			{
				var groups = await db.NameFilterRejects
					.Where(r => r.Source == ENameRejectSource.Rescan)
					.GroupBy(r => r.RuleID)
					.Select(g => new
					{
						RuleID = g.Key,
						NumHits = g.Count(),
						NumSkeletons = g.Select(r => r.Skeleton).Distinct().Count()
					})
					.OrderByDescending(g => g.NumHits)
					.ToListAsync();

				List<NameScanRuleGroup> lstResult = new();

				foreach (var group in groups)
				{
					dictRules.TryGetValue(group.RuleID, out NameFilterRule? rule);

					lstResult.Add(new NameScanRuleGroup
					{
						RuleID = group.RuleID,
						Pattern = rule != null ? rule.Pattern : "(structural)",
						MatchType = rule != null ? rule.MatchType : ENameRuleMatch.Skeleton,
						Action = rule != null ? rule.Action : ENameRuleAction.Block,
						NumHits = group.NumHits,
						NumSkeletons = group.NumSkeletons
					});
				}

				return lstResult;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] GetScanSummary failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return new List<NameScanRuleGroup>();
			}
		}

		public static async Task<List<NameScanSkeletonGroup>> GetScanSkeletons(AppDbContext db, int? ruleID, int limit)
		{
			try
			{
				IQueryable<NameFilterReject> query = db.NameFilterRejects
					.Where(r => r.Source == ENameRejectSource.Rescan);

				if (ruleID.HasValue)
				{
					query = query.Where(r => r.RuleID == ruleID.Value);
				}

				return await query
					.GroupBy(r => r.Skeleton)
					.Select(g => new NameScanSkeletonGroup
					{
						Skeleton = g.Key,
						NumUsers = g.Count(),
						SampleName = g.Min(r => r.AttemptedName)
					})
					.OrderByDescending(g => g.NumUsers)
					.Take(limit)
					.ToListAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] GetScanSkeletons failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return new List<NameScanSkeletonGroup>();
			}
		}

		public static async Task<List<NameFilterReject>> GetScanHits(AppDbContext db, int? ruleID, int limit)
		{
			try
			{
				IQueryable<NameFilterReject> query = db.NameFilterRejects
					.Where(r => r.Source == ENameRejectSource.Rescan);

				if (ruleID.HasValue)
				{
					query = query.Where(r => r.RuleID == ruleID.Value);
				}

				return await query
					.OrderBy(r => r.RuleID)
					.ThenBy(r => r.Skeleton)
					.Take(limit)
					.AsNoTracking()
					.ToListAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] GetScanHits failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return new List<NameFilterReject>();
			}
		}

		// A rescan replaces the previous one, otherwise the report mixes decisions that have
		// already been acted on with the current state
		public static async Task ClearScanHits(AppDbContext db)
		{
			try
			{
				await db.NameFilterRejects
					.Where(r => r.Source == ENameRejectSource.Rescan)
					.ExecuteDeleteAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] ClearScanHits failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		// Keyset paged - there are six figures of users, so a rescan must never load them all
		public static async Task<List<User>> GetUsersWithDisplayName(AppDbContext db, Int64 afterUserID, int limit)
		{
			try
			{
				return await db.Users
					.Where(u => u.ID > afterUserID && u.DisplayName != null && u.DisplayName != "")
					.OrderBy(u => u.ID)
					.Take(limit)
					.AsNoTracking()
					.ToListAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] GetUsersWithDisplayName failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return new List<User>();
			}
		}

		public static async Task LogScanHits(AppDbContext db, List<NameFilterReject> lstHits)
		{
			try
			{
				if (lstHits.Count == 0)
				{
					return;
				}

				db.NameFilterRejects.AddRange(lstHits);
				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] LogScanHits failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		// Deliberately not SetDisplayName: the replacement is generated, not chosen, and must not
		// be refused by a rule or a skeleton collision.
		public static async Task<bool> ForceRename(AppDbContext db, Int64 userID, string newName)
		{
			try
			{
				string skeleton = NameSkeleton.Skeletonize(newName);

				int numUpdated = await db.Users
					.Where(u => u.ID == userID)
					.ExecuteUpdateAsync(setters => setters
						.SetProperty(u => u.DisplayName, newName)
						.SetProperty(u => u.DisplayNameSkeleton, skeleton)
					);

				return numUpdated > 0;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] ForceRename failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return false;
			}
		}

		public static async Task LogReject(
			AppDbContext db,
			Int64 userID,
			string strAttemptedName,
			string strSkeleton,
			int ruleID,
			ENameRuleAction action,
			ENameRejectSource source)
		{
			try
			{
				db.NameFilterRejects.Add(new NameFilterReject
				{
					UserID = userID,
					AttemptedName = Truncate(strAttemptedName, 64),
					Skeleton = Truncate(strSkeleton, 64),
					RuleID = ruleID,
					Action = action,
					Source = source,
					Created = DateTime.UtcNow
				});

				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] LogReject failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		public static async Task<bool> IsSkeletonTaken(AppDbContext db, Int64 userID, string strSkeleton)
		{
			try
			{
				if (String.IsNullOrEmpty(strSkeleton))
				{
					return false;
				}

				return await db.Users
					.AnyAsync(u => u.ID != userID && u.DisplayNameSkeleton == strSkeleton);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] IsSkeletonTaken failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return false;
			}
		}

		// backfills displayname_skeleton for rows that predate the column
		public static async Task<int> BackfillSkeletons(AppDbContext db, int maxRows)
		{
			int numUpdated = 0;

			try
			{
				// Only NULL is unfilled. An empty skeleton is a real answer for a name made of
				// decoration, and matching on it would hand the same rows back every pass.
				List<User> lstUsers = await db.Users
					.Where(u => u.DisplayName != null && u.DisplayName != "" && u.DisplayNameSkeleton == null)
					.Take(maxRows)
					.ToListAsync();

				foreach (User user in lstUsers)
				{
					user.DisplayNameSkeleton = NameSkeleton.Skeletonize(user.DisplayName ?? String.Empty);
					++numUpdated;
				}

				if (numUpdated > 0)
				{
					await db.SaveChangesAsync();
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] BackfillSkeletons failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}

			return numUpdated;
		}
	}
}
