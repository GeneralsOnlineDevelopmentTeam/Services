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
		private static readonly ILogger s_log = AppLog.For(typeof(ConnectionOutcomes));

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

				// Load existing row (if any)
				var existing = await db.ConnectionOutcomes
					.Where(c => c.DayOfYear == dayOfYear)
					.FirstOrDefaultAsync();

				// If no row exists → create one
				if (existing == null)
				{
					existing = new ConnectionOutcome
					{
						DayOfYear = dayOfYear,
						Ipv4Count = 0,
						Ipv6Count = 0,
						SuccessCount = 0,
						FailedCount = 0
					};

					db.ConnectionOutcomes.Add(existing);
				}

				// Increment protocol counters
				if (protocol == EIPVersion.IPV4)
					existing.Ipv4Count = (existing.Ipv4Count ?? 0) + 1;
				else if (protocol == EIPVersion.IPV6)
					existing.Ipv6Count = (existing.Ipv6Count ?? 0) + 1;

				// Increment outcome counters
				if (outcome == EConnectionState.CONNECTED_DIRECT ||
					outcome == EConnectionState.CONNECTED_RELAY)
				{
					existing.SuccessCount = (existing.SuccessCount ?? 0) + 1;
				}
				else if (outcome == EConnectionState.CONNECTION_FAILED)
				{
					existing.FailedCount = (existing.FailedCount ?? 0) + 1;
				}

				// Persist insert/update
				await db.SaveChangesAsync();

				// Cleanup: delete rows older than 30 days
				int cutoff = dayOfYear - 30;

				await db.ConnectionOutcomes
					.Where(c => c.DayOfYear < cutoff)
					.ExecuteDeleteAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "StoreConnectionOutcome failed");
			}
		}

	}
}