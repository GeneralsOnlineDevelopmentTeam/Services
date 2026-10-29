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

public class DailyStat
{
	public DailyStat()
	{
		DayOfYear = DateTime.Now.DayOfYear;
		Stats = new();
	}

	public int DayOfYear { get; set; } = -1;
	public DailyStatsStructure Stats { get; set; } = null;
}

public class DailyStatsStructure
{
	public const int numSides = 12;
	public int[] matches { get; set; } = new int[12] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
	public int[] wins { get; set; } = new int[12] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
}

public class DailyStatsConfiguration : IEntityTypeConfiguration<DailyStat>
{
	public void Configure(EntityTypeBuilder<DailyStat> builder)
	{
		builder.ToTable("daily_stats");

		// prim key
		builder.HasKey(e => e.DayOfYear);

		builder.Property(e => e.DayOfYear).HasColumnName("day_of_year");

		// TODO_EFCORE: use column type json later (needs db update)

		builder.Property(e => e.Stats)
			.HasColumnName("stats_structure")
			.HasColumnType("longtext")
			.HasConversion(
				v => JsonSerializer.Serialize(v, (JsonSerializerOptions)null),
				v => JsonSerializer.Deserialize<DailyStatsStructure>(v, (JsonSerializerOptions)null)
			);
	}
}

public static class DailyStatsManager
{
	private static readonly ILogger s_log = AppLog.For(typeof(DailyStatsManager));

	public static DailyStat g_StatsContainer = new();

	public static async Task LoadFromDB(AppDbContext db)
	{
		try
		{
			int day_of_year = DateTime.Now.DayOfYear;
			g_StatsContainer = await db.DailyStats.FirstOrDefaultAsync(x => x.DayOfYear == day_of_year);

			// if null, instantiate, but dont save immediately, let the normal save timer handle it
			if (g_StatsContainer == null)
			{
				g_StatsContainer = new DailyStat();
			}
		}
		catch (Exception ex)
		{
			s_log.LogError(ex, "DailyStats.LoadFromDB failed");
			g_StatsContainer = new DailyStat();
		}
	}

	// TODO_EFCORE: This can be optimized
	public static async Task SaveToDB(AppDbContext db)
	{
		try
		{
			int day_of_year = DateTime.Now.DayOfYear;

			var entity = await db.DailyStats.AsTracking()
				.FirstOrDefaultAsync(x => x.DayOfYear == day_of_year);

			// Insert if new, otherwise update
			if (entity == null)
			{
				g_StatsContainer = new DailyStat();
				entity = g_StatsContainer;
				db.DailyStats.Add(entity);
			}
			else
			{
				entity.Stats = g_StatsContainer.Stats;
				db.DailyStats.Update(entity);
			}

			await db.SaveChangesAsync();
		}
		catch (Exception ex)
		{
			s_log.LogError(ex, "DailyStats.SaveToDB failed");
		}
	}

	public static void RegisterOutcome(int army, bool bWon)
	{
		try
		{
			int armyIndex = army - 2; // teams start at 2, so substract for array indices

			if (armyIndex >= 0 && armyIndex <= 11)
			{
				++g_StatsContainer.Stats.matches[armyIndex];

				if (bWon)
				{
					++g_StatsContainer.Stats.wins[armyIndex];
				}

				// clamp to a sane value, just incase (wins can never be more than matches)
				if (g_StatsContainer.Stats.wins[armyIndex] > g_StatsContainer.Stats.matches[armyIndex])
				{
					g_StatsContainer.Stats.wins[armyIndex] = g_StatsContainer.Stats.matches[armyIndex];

				}
			}
		}
		catch (Exception ex)
		{
			s_log.LogError(ex, "RegisterOutcome failed");
		}
	}
}