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
		public static async Task CommitStats(
			AppDbContext db,
			int day_of_year,
			int hour_of_day,
			int player_peak,
			int lobbies_peak)
		{
			try
			{
				// Atomic upsert (GREATEST semantics), safe against concurrent/repeated commits for the same day+hour
				await db.Database.ExecuteSqlInterpolatedAsync($@"
					INSERT INTO service_stats (day_of_year, hour_of_day, player_peak, lobbies_peak)
					VALUES ({day_of_year}, {hour_of_day}, {player_peak}, {lobbies_peak})
					ON DUPLICATE KEY UPDATE
						player_peak = GREATEST(player_peak, VALUES(player_peak)),
						lobbies_peak = GREATEST(lobbies_peak, VALUES(lobbies_peak))");

				// DELETE old rows
				int cutoff = day_of_year - 30;

				await db.ServiceStats
					.Where(s => s.DayOfYear < cutoff)
					.ExecuteDeleteAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] CommitStats failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

	}
}