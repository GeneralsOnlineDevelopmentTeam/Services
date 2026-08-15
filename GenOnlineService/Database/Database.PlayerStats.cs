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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Text.Json;

// TODO_EFCORE: When updating this, make sure we preserve old ON DUPLICATE behavior, to overwrite the old data since day_of_year key will be re-used
public class UserStatsEntry
{
	public long UserId { get; set; }
	public string Stats { get; set; } = "{}";
}

// TODO_EFCORE: rename to user_stats
public class UserStatsConfiguration : IEntityTypeConfiguration<UserStatsEntry>
{
	public void Configure(EntityTypeBuilder<UserStatsEntry> builder)
	{
		builder.ToTable("user_stats_v2");

		builder.HasKey(x => x.UserId);

		builder.Property(x => x.UserId)
			.HasColumnName("user_id");

		builder.Property(x => x.Stats)
			.HasColumnName("stats")
			.HasColumnType("longtext")
			.UseCollation("utf8mb4_bin")     // matches your CREATE TABLE
			.IsRequired();

		// JSON validity constraint
		builder.HasCheckConstraint(
			"CK_user_stats_v2_stats_json_valid",
			"json_valid(`stats`)"
		);
	}
}


namespace Database
{
	public static class UserStats
	{
		private static readonly Func<AppDbContext, long, Task<string?>> _getUserStatsJson =
	EF.CompileAsyncQuery(
		(AppDbContext db, long userId) =>
			db.UserStats
			  .Where(s => s.UserId == userId)
			  .Select(s => s.Stats)
			  .FirstOrDefault()
	);


		private static readonly Func<AppDbContext, long, Task<string?>> _getUserStats =
	EF.CompileAsyncQuery(
		(AppDbContext db, long userId) =>
			db.UserStats
			  .Where(s => s.UserId == userId)
			  .Select(s => s.Stats)
			  .FirstOrDefault()
	);

		public static async Task<PlayerStats> GetPlayerStats(
	AppDbContext db,
	long userId)
		{
			EloData elo;
			try
			{
				// Load ELO (already EF-based)
				elo = await Database.Users.GetELOData(db, userId);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] GetPlayerStats failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return new PlayerStats(userId, EloConfig.BaseRating, 0, EloConfig.BaseRating);
			}

			PlayerStats ps = new PlayerStats(userId, elo.Rating, elo.NumMatches, elo.MonthlyRating);

			try
			{
				// Load stats JSON via EF
				string? json = await _getUserStatsJson(db, userId);

				if (string.IsNullOrEmpty(json))
					return ps; // no stats row → return ELO-only stats

				// Deserialize dictionary
				Dictionary<int, int>? dict =
					JsonSerializer.Deserialize<Dictionary<int, int>>(json);

				if (dict == null)
					return ps;

				// Feed into PlayerStats
				foreach (var kv in dict)
				{
					EStatIndex statId = (EStatIndex)kv.Key;
					int statValue = kv.Value;

					ps.ProcessFromDB(statId, statValue);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] GetPlayerStats failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}

			return ps;
		}


		public static async Task UpdatePlayerStat(
	AppDbContext db,
	long userId,
	int statId,
	int statVal)
		{
			try
			{
				// 1. Load existing JSON (if any)
				string? json = await _getUserStats(db, userId);

				Dictionary<string, int> stats;

				if (string.IsNullOrEmpty(json))
				{
					// No row exists → create new dictionary
					stats = new Dictionary<string, int>();
				}
				else
				{
					// Deserialize existing stats
					stats = JsonSerializer.Deserialize<Dictionary<string, int>>(json)
							?? new Dictionary<string, int>();
				}

				// 2. Update the stat
				stats[statId.ToString()] = statVal;

				// 3. Serialize back
				string updatedJson = JsonSerializer.Serialize(stats);

				// 4. Check if row exists
				bool exists = json != null;

				if (!exists)
				{
					// INSERT
					db.UserStats.Add(new UserStatsEntry
					{
						UserId = userId,
						Stats = updatedJson
					});

					await db.SaveChangesAsync();
					return;
				}

				// 5. UPDATE using ExecuteUpdateAsync (fast, no tracking)
				await db.UserStats
					.Where(s => s.UserId == userId)
					.ExecuteUpdateAsync(s => s
						.SetProperty(x => x.Stats, updatedJson));
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] UpdatePlayerStat failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

	}
}