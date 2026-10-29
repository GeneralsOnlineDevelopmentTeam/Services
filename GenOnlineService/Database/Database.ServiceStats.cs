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
public class ServiceStat
{
	public ServiceStat()
	{
		DayOfYear = DateTime.Now.DayOfYear;
		HourOfDay = DateTime.Now.Hour;
	}

	public int DayOfYear { get; set; } = -1;
	public int HourOfDay { get; set; } = -1;
	public int PlayerPeak { get; set; } = -1;
	public int LobbiesPeak { get; set; } = -1;
}

public class ServiceStatsConfiguration : IEntityTypeConfiguration<ServiceStat>
{
	public void Configure(EntityTypeBuilder<ServiceStat> builder)
	{
		builder.ToTable("service_stats");

		// prim key
		builder.HasKey(e => new { e.DayOfYear, e.HourOfDay });

		builder.Property(e => e.DayOfYear).HasColumnName("day_of_year");
		builder.Property(e => e.HourOfDay).HasColumnName("hour_of_day");
		builder.Property(e => e.PlayerPeak).HasColumnName("player_peak");
		builder.Property(e => e.LobbiesPeak).HasColumnName("lobbies_peak");
	}
}

namespace Database
{
	public static class ServiceStats
	{
		private static readonly ILogger s_log = AppLog.For(typeof(ServiceStats));

		public static readonly Func<AppDbContext, int, int, Task<ServiceStat?>> FindStatTracked =
		EF.CompileAsyncQuery(
			(AppDbContext db, int day, int hour) =>
				db.ServiceStats.AsTracking().FirstOrDefault(s =>
					s.DayOfYear == day &&
					s.HourOfDay == hour)
		);

		public static readonly Func<AppDbContext, int, IAsyncEnumerable<ServiceStat>> FindOldStats =
			EF.CompileAsyncQuery(
				(AppDbContext db, int cutoff) =>
					db.ServiceStats.Where(s => s.DayOfYear < cutoff)
		);

		public static async Task CommitStats(
			AppDbContext db,
			int day_of_year,
			int hour_of_day,
			int player_peak,
			int lobbies_peak)
		{
			try
			{
				// UPSERT logic using precompiled query
				var existing = await FindStatTracked(db, day_of_year, hour_of_day);

				if (existing == null)
				{
					// Insert new
					var stat = new ServiceStat
					{
						DayOfYear = day_of_year,
						HourOfDay = hour_of_day,
						PlayerPeak = player_peak,
						LobbiesPeak = lobbies_peak
					};

					db.ServiceStats.Add(stat);
				}
				else
				{
					// Update using GREATEST() semantics
					existing.PlayerPeak = Math.Max(existing.PlayerPeak, player_peak);
					existing.LobbiesPeak = Math.Max(existing.LobbiesPeak, lobbies_peak);
				}

				// NOTE: duplicate, unnecessary
				//await db.SaveChangesAsync();

				// DELETE old rows (precompiled)
				int cutoff = day_of_year - 30;

				await foreach (var old in FindOldStats(db, cutoff))
					db.ServiceStats.Remove(old);

				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "CommitStats failed");
			}
		}

	}
}