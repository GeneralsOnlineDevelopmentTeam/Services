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

public class ConnectionOutcome
{
	public int DayOfYear { get; set; }

	public int? Ipv4Count { get; set; }
	public int? Ipv6Count { get; set; }
	public int? SuccessCount { get; set; }
	public int? FailedCount { get; set; }
}

public class ConnectionOutcomeConfiguration : IEntityTypeConfiguration<ConnectionOutcome>
{
	public void Configure(EntityTypeBuilder<ConnectionOutcome> builder)
	{
		builder.ToTable("connection_outcomes");

		builder.HasKey(x => x.DayOfYear);

		builder.Property(x => x.DayOfYear)
			.HasColumnName("day_of_year");

		builder.Property(x => x.Ipv4Count)
			.HasColumnName("ipv4_count");

		builder.Property(x => x.Ipv6Count)
			.HasColumnName("ipv6_count");

		builder.Property(x => x.SuccessCount)
			.HasColumnName("success_count");

		builder.Property(x => x.FailedCount)
			.HasColumnName("failed_count");
	}
}



namespace Database
{
	public static class ConnectionOutcomes
	{
		public static async Task StoreConnectionOutcome(
	AppDbContext db,
	EIPVersion protocol,
	EConnectionState outcome)
		{
			// Only track these states
			if (outcome != EConnectionState.CONNECTED_DIRECT &&
				outcome != EConnectionState.CONNECTED_RELAY &&
				outcome != EConnectionState.CONNECTION_FAILED)
				return;

			try
			{
				int dayOfYear = DateTime.UtcNow.DayOfYear;

				// Atomic upsert: the day_of_year row is created on first use and incremented in the database, so concurrent
				// reports (e.g. at midnight, when the row doesn't exist yet) can't collide on the primary key or lose counts
				int ipv4Inc = protocol == EIPVersion.IPV4 ? 1 : 0;
				int ipv6Inc = protocol == EIPVersion.IPV6 ? 1 : 0;
				int successInc = (outcome == EConnectionState.CONNECTED_DIRECT || outcome == EConnectionState.CONNECTED_RELAY) ? 1 : 0;
				int failedInc = outcome == EConnectionState.CONNECTION_FAILED ? 1 : 0;

				await db.Database.ExecuteSqlInterpolatedAsync($@"
					INSERT INTO connection_outcomes (day_of_year, ipv4_count, ipv6_count, success_count, failed_count)
					VALUES ({dayOfYear}, {ipv4Inc}, {ipv6Inc}, {successInc}, {failedInc})
					ON DUPLICATE KEY UPDATE
						ipv4_count = COALESCE(ipv4_count, 0) + VALUES(ipv4_count),
						ipv6_count = COALESCE(ipv6_count, 0) + VALUES(ipv6_count),
						success_count = COALESCE(success_count, 0) + VALUES(success_count),
						failed_count = COALESCE(failed_count, 0) + VALUES(failed_count)");

				// Cleanup: delete rows older than 30 days
				int cutoff = dayOfYear - 30;

				await db.ConnectionOutcomes
					.Where(c => c.DayOfYear < cutoff)
					.ExecuteDeleteAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] StoreConnectionOutcome failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

	}
}