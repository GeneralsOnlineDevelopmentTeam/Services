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

public class AppDbContext : DbContext
{
	public DbSet<User> Users => Set<User>();
	public DbSet<UserDevice> UserDevices => Set<UserDevice>();
	public DbSet<DailyStat> DailyStats => Set<DailyStat>();
	public DbSet<LeaderboardDaily> LeaderboardDaily => Set<LeaderboardDaily>();
	public DbSet<LeaderboardMonthly> LeaderboardMonthly => Set<LeaderboardMonthly>();
	public DbSet<LeaderboardYearly> LeaderboardYearly => Set<LeaderboardYearly>();
	public DbSet<ServiceStat> ServiceStats => Set<ServiceStat>();
	public DbSet<PendingLogin> PendingLogins => Set<PendingLogin>();
	public DbSet<MatchHistoryEntry> MatchHistory => Set<MatchHistoryEntry>();
	public DbSet<UserStatsEntry> UserStats => Set<UserStatsEntry>();

	public DbSet<FriendEntry> Friends => Set<FriendEntry>();

	public DbSet<BlockedUserEntry> BlockedUsers => Set<BlockedUserEntry>();

	public DbSet<FriendRequestEntry> FriendRequests => Set<FriendRequestEntry>();
	public DbSet<ConnectionOutcome> ConnectionOutcomes => Set<ConnectionOutcome>();
	public DbSet<UserTokenState> UserTokenStates => Set<UserTokenState>();

	public DbSet<AcReviewCrc> AcReviewsCrc => Set<AcReviewCrc>();
	public DbSet<AcReviewModule> AcReviewsModules => Set<AcReviewModule>();
	public DbSet<AcReviewNewAccountGame> AcReviewsNewAccountGames => Set<AcReviewNewAccountGame>();
	public DbSet<AcReviewProbe> AcReviewsProbes => Set<AcReviewProbe>();

	public DbSet<NameFilterReject> NameFilterRejects => Set<NameFilterReject>();

	public AppDbContext(DbContextOptions<AppDbContext> options)
		: base(options)
	{
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		// Fluent configuration here
		base.OnModelCreating(modelBuilder);

		modelBuilder.ApplyConfiguration(new UserConfiguration());
		modelBuilder.ApplyConfiguration(new UserDevicesConfiguration());
		modelBuilder.ApplyConfiguration(new DailyStatsConfiguration());
		modelBuilder.ApplyConfiguration(new LeaderboardDailyConfiguration());
		modelBuilder.ApplyConfiguration(new LeaderboardMonthlyConfiguration());
		modelBuilder.ApplyConfiguration(new LeaderboardYearlyConfiguration());
		modelBuilder.ApplyConfiguration(new ServiceStatsConfiguration());
		modelBuilder.ApplyConfiguration(new PendingLoginConfiguration());
		modelBuilder.ApplyConfiguration(new MatchHistoryConfiguration());
		modelBuilder.ApplyConfiguration(new UserStatsConfiguration());
		modelBuilder.ApplyConfiguration(new FriendConfiguration());
		modelBuilder.ApplyConfiguration(new FriendRequestConfiguration());
		modelBuilder.ApplyConfiguration(new BlockedUserConfiguration());
		modelBuilder.ApplyConfiguration(new ConnectionOutcomeConfiguration());
		modelBuilder.ApplyConfiguration(new UserTokenStateConfiguration());
		modelBuilder.ApplyConfiguration(new AcReviewCrcConfiguration());
		modelBuilder.ApplyConfiguration(new AcReviewModuleConfiguration());
		modelBuilder.ApplyConfiguration(new AcReviewNewAccountGameConfiguration());
		modelBuilder.ApplyConfiguration(new AcReviewProbeConfiguration());
		modelBuilder.ApplyConfiguration(new NameFilterRejectConfiguration());
	}
}