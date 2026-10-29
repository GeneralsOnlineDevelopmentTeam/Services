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
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

public class LeaderboardDaily
{
	public long UserId { get; set; }
	public int? Points { get; set; }
	public int DayOfYear { get; set; }
	public int Year { get; set; }
	public int? Wins { get; set; }
	public int? Losses { get; set; }
}

public class LeaderboardMonthly
{
	public long UserId { get; set; }
	public int? Points { get; set; }
	public int MonthOfYear { get; set; }
	public int Year { get; set; }
	public int? Wins { get; set; }
	public int? Losses { get; set; }
}

public class LeaderboardYearly
{
	public long UserId { get; set; }
	public int? Points { get; set; }
	public int Year { get; set; }
	public int? Wins { get; set; }
	public int? Losses { get; set; }
}

public class LeaderboardDailyConfiguration : IEntityTypeConfiguration<LeaderboardDaily>
{
	public void Configure(EntityTypeBuilder<LeaderboardDaily> builder)
	{
		builder.ToTable("leaderboard_daily");

		builder.HasKey(x => new { x.UserId, x.DayOfYear, x.Year });

		builder.Property(x => x.UserId)
			.HasColumnName("user_id")
			.IsRequired();

		builder.Property(x => x.Points)
			.HasColumnName("points");

		builder.Property(x => x.DayOfYear)
			.HasColumnName("day_of_year")
			.IsRequired();

		builder.Property(x => x.Year)
			.HasColumnName("year")
			.IsRequired();

		builder.Property(x => x.Wins)
			.HasColumnName("wins");

		builder.Property(x => x.Losses)
			.HasColumnName("losses");
	}
}

public class LeaderboardMonthlyConfiguration : IEntityTypeConfiguration<LeaderboardMonthly>
{
	public void Configure(EntityTypeBuilder<LeaderboardMonthly> builder)
	{
		builder.ToTable("leaderboard_monthly");

		builder.HasKey(x => new { x.UserId, x.MonthOfYear, x.Year });

		builder.Property(x => x.UserId)
			.HasColumnName("user_id")
			.IsRequired();

		builder.Property(x => x.Points)
			.HasColumnName("points");

		builder.Property(x => x.MonthOfYear)
			.HasColumnName("month_of_year")
			.IsRequired();

		builder.Property(x => x.Year)
			.HasColumnName("year")
			.IsRequired();

		builder.Property(x => x.Wins)
			.HasColumnName("wins");

		builder.Property(x => x.Losses)
			.HasColumnName("losses");
	}
}

public class LeaderboardYearlyConfiguration : IEntityTypeConfiguration<LeaderboardYearly>
{
	public void Configure(EntityTypeBuilder<LeaderboardYearly> builder)
	{
		builder.ToTable("leaderboard_yearly");

		builder.HasKey(x => new { x.UserId, x.Year });

		builder.Property(x => x.UserId)
			.HasColumnName("user_id")
			.IsRequired();

		builder.Property(x => x.Points)
			.HasColumnName("points");

		builder.Property(x => x.Year)
			.HasColumnName("year")
			.IsRequired();

		builder.Property(x => x.Wins)
			.HasColumnName("wins");

		builder.Property(x => x.Losses)
			.HasColumnName("losses");
	}
}


namespace Database
{
	public static class Leaderboards
	{
		private static readonly ILogger s_log = AppLog.For(typeof(Leaderboards));


		public struct LeaderboardPoints
		{
			public int daily;
			public int daily_matches;
			public int monthly;
			public int monthly_matches;
			public int yearly;
			public int yearly_matches;
		}

		public sealed class LeaderboardRow
		{
			public long UserId { get; set; }
			public int Points { get; set; }
			public int Matches { get; set; }
		}

		public static class LeaderboardQueries
		{
			public static readonly Func<AppDbContext, List<long>, int, int, IAsyncEnumerable<LeaderboardRow>> DailyBulk =
		EF.CompileAsyncQuery((AppDbContext db, List<long> ids, int day, int year) =>
			db.LeaderboardDaily
				.AsNoTracking()
				.Where(x => ids.Contains(x.UserId)
						 && x.DayOfYear == day
						 && x.Year == year)
				.Select(x => new LeaderboardRow
				{
					UserId = x.UserId,
					Points = x.Points ?? 0,
					Matches = (x.Wins ?? 0) + (x.Losses ?? 0)
				})
		);

			public static readonly Func<AppDbContext, List<long>, int, int, IAsyncEnumerable<LeaderboardRow>> MonthlyBulk =
				EF.CompileAsyncQuery((AppDbContext db, List<long> ids, int month, int year) =>
					db.LeaderboardMonthly
						.AsNoTracking()
						.Where(x => ids.Contains(x.UserId)
								 && x.MonthOfYear == month
								 && x.Year == year)
						.Select(x => new LeaderboardRow
						{
							UserId = x.UserId,
							Points = x.Points ?? 0,
							Matches = (x.Wins ?? 0) + (x.Losses ?? 0)
						})
				);

			public static readonly Func<AppDbContext, List<long>, int, IAsyncEnumerable<LeaderboardRow>> YearlyBulk =
				EF.CompileAsyncQuery((AppDbContext db, List<long> ids, int year) =>
					db.LeaderboardYearly
						.AsNoTracking()
						.Where(x => ids.Contains(x.UserId)
								 && x.Year == year)
						.Select(x => new LeaderboardRow
						{
							UserId = x.UserId,
							Points = x.Points ?? 0,
							Matches = (x.Wins ?? 0) + (x.Losses ?? 0)
						})
				);
		}

		public static async Task CreateUserEntriesIfNotExists(AppDbContext db, long playerId)
		{
			try
			{
				int dayOfYear = DateTime.UtcNow.DayOfYear;
				int monthOfYear = DateTime.UtcNow.Month;
				int year = DateTime.UtcNow.Year;

				var daily = new LeaderboardDaily
				{
					UserId = playerId,
					Points = EloConfig.BaseRating,
					DayOfYear = dayOfYear,
					Year = year,
					Wins = 0,
					Losses = 0
				};

				var monthly = new LeaderboardMonthly
				{
					UserId = playerId,
					Points = EloConfig.BaseRating,
					MonthOfYear = monthOfYear,
					Year = year,
					Wins = 0,
					Losses = 0
				};

				var yearly = new LeaderboardYearly
				{
					UserId = playerId,
					Points = EloConfig.BaseRating,
					Year = year,
					Wins = 0,
					Losses = 0
				};

				db.Add(daily);
				db.Add(monthly);
				db.Add(yearly);

				try
				{
					await db.SaveChangesAsync();
				}
				catch (DbUpdateException ex)
				{
					// Ignore duplicate key errors (INSERT IGNORE behavior)
					if (!IsDuplicateKeyException(ex))
						throw;
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "CreateUserEntriesIfNotExists failed");
			}
		}

		private static bool IsDuplicateKeyException(DbUpdateException ex)
		{
			return ex.InnerException?.Message.Contains("Duplicate entry") == true;
		}


		private static async Task<List<LeaderboardRow>> MaterializeAsync(IAsyncEnumerable<LeaderboardRow> source)
		{
			var list = new List<LeaderboardRow>();

			await foreach (var item in source.ConfigureAwait(false))
				list.Add(item);

			return list;
		}


		public async static ValueTask<Dictionary<long, LeaderboardPoints>> GetBulkLeaderboardData(
   AppDbContext db,
   List<long> playerIDs,
   int dayOfYear,
   int monthOfYear,
   int year)
		{
			var results = new Dictionary<long, LeaderboardPoints>();

			if (playerIDs == null || playerIDs.Count == 0)
				return results;

			foreach (var id in playerIDs)
				results[id] = new LeaderboardPoints();

			try
			{
				// Queries must run sequentially — DbContext does not support concurrent operations.
				var daily = await MaterializeAsync(LeaderboardQueries.DailyBulk(db, playerIDs, dayOfYear, year));
				var monthly = await MaterializeAsync(LeaderboardQueries.MonthlyBulk(db, playerIDs, monthOfYear, year));
				var yearly = await MaterializeAsync(LeaderboardQueries.YearlyBulk(db, playerIDs, year));

				// DAILY
				foreach (var row in daily)
				{
					var entry = results[row.UserId];
					entry.daily = row.Points;
					entry.daily_matches = row.Matches;
					results[row.UserId] = entry;
				}

				// MONTHLY
				foreach (var row in monthly)
				{
					var entry = results[row.UserId];
					entry.monthly = row.Points;
					entry.monthly_matches = row.Matches;
					results[row.UserId] = entry;
				}

				// YEARLY
				foreach (var row in yearly)
				{
					var entry = results[row.UserId];
					entry.yearly = row.Points;
					entry.yearly_matches = row.Matches;
					results[row.UserId] = entry;
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetBulkLeaderboardData failed");
			}

			return results;
		}
	}
}