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

public class UserDevice
{
	public Int64 UserID { get; set; }

	public string HWID_0 { get; set; } = String.Empty;
	public string HWID_1 { get; set; } = String.Empty;
	public string HWID_2 { get; set; } = String.Empty;
	public string HWID_3 { get; set; } = String.Empty;
	public string HWID_4 { get; set; } = String.Empty;
	public string HWID_5 { get; set; } = String.Empty;

	public string IPAddress { get; set; } = String.Empty;
}

public class User
{
	public Int64 ID { get; set; }
	public EAccountType AccountType { get; set; } = EAccountType.Unknown;

	// Steam, only present if AccountType is Steam
	public Int64? SteamID { get; set; } = -1;

	// Discord, only present if AccountType is Discord
	public Int64? DiscordID { get; set; } = -1;
	public string? DiscordUsername { get; set; } = String.Empty;

	// GameReplays, only present if AccountType is GameReplays
	public Int64? GameReplaysID { get; set; } = -1;
	public string? GameReplaysUsername { get; set; } = String.Empty;


	public string? DisplayName { get; set; } = "";
	public DateTime LastLogin { get; set; } = DateTime.UnixEpoch;
	public string? LastIPAddress { get; set; } = String.Empty;
	public KnownClients.EKnownClients ClientID { get; set; } = KnownClients.EKnownClients.custom_third_party_client;

	// Gameplay Favorites
	public int FavoriteColor { get; set; } = -1;
	public int FavoriteSide { get; set; } = -1;
	public string? FavoriteMap { get; set; } = String.Empty;
	public int FavoriteStartingMoney { get; set; } = -1;
	public bool LimitSuperweapons { get; set; } = false;

	// User Permissions
	public bool IsAdmin { get; set; } = false;
	public bool IsBanned { get; set; } = false;

	// ELO
	public int EloRating { get; set; } = EloConfig.BaseRating;
	public int MonthlyEloRating { get; set; } = EloConfig.BaseRating;
	public int EloNumberOfMatches { get; set; } = 0;

	// Bans
	public string? BanReason { get; set; } = String.Empty;
	public string? BannedBy { get; set; } = String.Empty;
	public string? BanVerifiedBy { get; set; } = String.Empty;
	public string? BanAliases { get; set; } = String.Empty;
}

public class UserLobbyPreferences
{
	public int favorite_color = -1;
	public int favorite_side = -1;
	public string favorite_map = String.Empty;
	public int favorite_starting_money = -1;
	public bool favorite_limit_superweapons = false;
}

public sealed class UserBanStatus
{
	public bool IsBanned { get; set; }
	public string BanReason { get; set; } = String.Empty;
}

public class UserDevicesConfiguration : IEntityTypeConfiguration<UserDevice>
{
	public void Configure(EntityTypeBuilder<UserDevice> builder)
	{
		builder.ToTable("user_devices");

		// prim key
		builder.HasKey(e => new { e.UserID, e.HWID_0, e.HWID_1, e.HWID_2, e.IPAddress});

		// Lookups of other accounts that share a hardware ID or IP address (anti cheat / ban alias detection)
		builder.HasIndex(e => new { e.HWID_0, e.HWID_1, e.HWID_2, e.UserID })
			.HasDatabaseName("idx_user_devices_hwid_triplet_user");
		builder.HasIndex(e => new { e.HWID_0, e.UserID })
			.HasDatabaseName("idx_user_devices_hwid_0_user");
		builder.HasIndex(e => new { e.HWID_1, e.UserID })
			.HasDatabaseName("idx_user_devices_hwid_1_user");
		builder.HasIndex(e => new { e.HWID_2, e.UserID })
			.HasDatabaseName("idx_user_devices_hwid_2_user");
		builder.HasIndex(e => new { e.IPAddress, e.UserID })
			.HasDatabaseName("idx_user_devices_ip_user");

		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.HWID_0).HasColumnName("hwid_0").HasMaxLength(128);
		builder.Property(e => e.HWID_1).HasColumnName("hwid_1").HasMaxLength(128);
		builder.Property(e => e.HWID_2).HasColumnName("hwid_2").HasMaxLength(128);
		builder.Property(e => e.HWID_3).HasColumnName("hwid_3").HasMaxLength(50).IsRequired(false);
		builder.Property(e => e.HWID_4).HasColumnName("hwid_4").HasMaxLength(50).IsRequired(false);
		builder.Property(e => e.HWID_5).HasColumnName("hwid_5").HasMaxLength(50).IsRequired(false);
		builder.Property(e => e.IPAddress).HasColumnName("ip_addr").HasMaxLength(45);
	}
}

public class UserConfiguration : IEntityTypeConfiguration<User>
{
	public void Configure(EntityTypeBuilder<User> builder)
	{
		builder.ToTable("users");

		// prim key
		builder.HasKey(e => e.ID);
		builder.HasIndex(e => e.IsBanned)
			.HasDatabaseName("ix_users_banned");

		builder.Property(e => e.ID).HasColumnName("user_id").ValueGeneratedOnAdd();

		builder.Property(e => e.AccountType).HasColumnName("account_type");
		builder.Property(e => e.SteamID).HasColumnName("steam_id");
		builder.Property(e => e.DiscordID).HasColumnName("discord_id");
		builder.Property(e => e.DiscordUsername).HasColumnName("discord_username").HasMaxLength(32);
		builder.Property(e => e.GameReplaysID).HasColumnName("gamereplays_id");
		builder.Property(e => e.GameReplaysUsername).HasColumnName("gamereplays_username").HasMaxLength(32);
		builder.Property(e => e.DisplayName).HasColumnName("displayname").HasMaxLength(32);
		builder.Property(e => e.LastLogin).HasColumnName("lastlogin");
		builder.Property(e => e.LastIPAddress).HasColumnName("last_ip").HasMaxLength(45).IsRequired();
		builder.Property(e => e.ClientID).HasColumnName("client_id");
		builder.Property(e => e.FavoriteColor).HasColumnName("favorite_color");
		builder.Property(e => e.FavoriteSide).HasColumnName("favorite_side");
		builder.Property(e => e.FavoriteMap).HasColumnName("favorite_map").HasMaxLength(128);
		builder.Property(e => e.FavoriteStartingMoney).HasColumnName("favorite_starting_money");
		builder.Property(e => e.LimitSuperweapons).HasColumnName("favorite_limit_superweapons");
		builder.Property(e => e.IsAdmin).HasColumnName("admin");
		builder.Property(e => e.IsBanned).HasColumnName("banned");
		builder.Property(e => e.EloRating).HasColumnName("elo_rating");
		builder.Property(e => e.MonthlyEloRating).HasColumnName("monthly_elo_rating");
		builder.Property(e => e.EloNumberOfMatches).HasColumnName("elo_num_matches");
		builder.Property(e => e.BanReason).HasColumnName("ban_reason").HasMaxLength(128);
		builder.Property(e => e.BannedBy).HasColumnName("banned_by").HasMaxLength(50);
		builder.Property(e => e.BanVerifiedBy).HasColumnName("ban_verified_by").HasMaxLength(50);
		builder.Property(e => e.BanAliases).HasColumnName("ban_aliases").HasMaxLength(50);

		// Columns owned by the website / admin tooling that shares this database. This service never reads or writes
		// them, so they are shadow properties: they only exist so the schema (and therefore a fresh install) has them.
		builder.Property<string?>("GoogleId").HasColumnName("google_id").HasMaxLength(50);
		builder.Property<string?>("GoogleEmail").HasColumnName("google_email").HasMaxLength(255);
		builder.Property<int?>("PermissionsLevel").HasColumnName("permissionslevel").HasDefaultValue(0);
	}
}

// A login session of the website. The table belongs to the website, this service never reads or writes it and only
// owns its schema so that a fresh install has the table the website expects.
public class WebSession
{
	public string SessionId { get; set; } = String.Empty;
	public Int64 UserId { get; set; }
	public DateTime Created { get; set; }
	public DateTime Expiration { get; set; }
	public DateTime LastUsed { get; set; }
	public string IpAddress { get; set; } = String.Empty;
	public string Os { get; set; } = String.Empty;
	public string Browser { get; set; } = String.Empty;
}

public class WebSessionConfiguration : IEntityTypeConfiguration<WebSession>
{
	public void Configure(EntityTypeBuilder<WebSession> builder)
	{
		builder.ToTable("web_sessions");

		builder.HasKey(e => e.SessionId);
		builder.HasIndex(e => e.UserId)
			.HasDatabaseName("user_id");

		builder.Property(e => e.SessionId).HasColumnName("session_id").HasMaxLength(64);
		builder.Property(e => e.UserId).HasColumnName("user_id");
		builder.Property(e => e.Created).HasColumnName("created");
		builder.Property(e => e.Expiration).HasColumnName("expiration");
		builder.Property(e => e.LastUsed).HasColumnName("last_used");
		builder.Property(e => e.IpAddress).HasColumnName("ip_addr").HasMaxLength(45);
		builder.Property(e => e.Os).HasColumnName("os").HasMaxLength(64);
		builder.Property(e => e.Browser).HasColumnName("browser").HasMaxLength(256);
	}
}

namespace Database
{
	public static class UserDevices
	{
		private static readonly ILogger s_log = AppLog.For(typeof(UserDevices));

		public static readonly Func<AppDbContext, long, string, string, string, Task<UserDevice?>> FindDevice =
				EF.CompileAsyncQuery(
					(AppDbContext db, long userId, string h0, string h1, string h2) =>
						db.UserDevices.FirstOrDefault(d =>
							d.UserID == userId &&
							d.HWID_0 == h0 &&
							d.HWID_1 == h1 &&
							d.HWID_2 == h2)
				);

		public static async Task RegisterUserDevice(
			AppDbContext db,
			long userId,
			string hwid_0,
			string hwid_1,
			string hwid_2,
			string ipAddr)
		{
			try
			{
				// raw versions
				string hwid_3 = hwid_0.ToUpper();
				string hwid_4 = hwid_1.ToUpper();
				string hwid_5 = hwid_2.ToUpper();

				// hashed versions
				string h0 = Helpers.ComputeMD5Hash(hwid_0).ToUpper();
				string h1 = Helpers.ComputeMD5Hash(hwid_1).ToUpper();
				string h2 = Helpers.ComputeMD5Hash(hwid_2).ToUpper();

			// basic checks and registration
			if (!hwid_3.EndsWith("YY"))
			{
				await Database.AntiCheat.FlagAccountForReview_SuspectProbes(db, userId, 0, "Logged in with potentially spoofed HWID 0");
			}

			if (!hwid_4.EndsWith("ZZ"))
			{
				await Database.AntiCheat.FlagAccountForReview_SuspectProbes(db, userId, 0, "Logged in with potentially spoofed HWID 1");
			}

			if (!hwid_5.EndsWith("ZZ"))
			{
				await Database.AntiCheat.FlagAccountForReview_SuspectProbes(db, userId, 0, "Logged in with potentially spoofed HWID 2");
			}

				// check if exists (precompiled query)
				var existing = await FindDevice(db, userId, h0, h1, h2);
				if (existing != null)
					return;

				// insert new (if doesnt exist)
				var device = new UserDevice
				{
					UserID = userId,
					HWID_0 = h0,
					HWID_1 = h1,
					HWID_2 = h2,
					HWID_3 = hwid_3,
					HWID_4 = hwid_4,
					HWID_5 = hwid_5,
					IPAddress = ipAddr
				};

				db.UserDevices.Add(device);
				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "RegisterUserDevice failed");
			}
		}
	}


	public static class Users
	{
		private static readonly ILogger s_log = AppLog.For(typeof(Users));

		private static readonly Func<AppDbContext, long, Task<EloData?>> GetEloData =
				EF.CompileAsyncQuery(
					(AppDbContext db, long userId) =>
						db.Users
						  .Where(u => u.ID == userId)
						  .Select(u => new EloData(u.EloRating, u.MonthlyEloRating, u.EloNumberOfMatches))
						  .FirstOrDefault()
				);

		private static readonly Func<AppDbContext, long, Task<bool>> _isUserAdminQuery =
			EF.CompileAsyncQuery((AppDbContext db, long userId) =>
				db.Users
				  .AsNoTracking()
				  .Where(u => u.ID == userId)
				  .Select(u => u.IsAdmin)
				  .FirstOrDefault());

		private static readonly Func<AppDbContext, long, Task<bool>> _isUserBannedQuery =
			EF.CompileAsyncQuery((AppDbContext db, long userId) =>
				db.Users
				  .AsNoTracking()
				  .Where(u => u.ID == userId)
				  .Select(u => u.IsBanned)
				  .FirstOrDefault());

		private static readonly Func<AppDbContext, long, Task<UserBanStatus?>> _getUserBanStatusQuery =
			EF.CompileAsyncQuery((AppDbContext db, long userId) =>
				db.Users
				  .AsNoTracking()
				  .Where(u => u.ID == userId)
				  .Select(u => new UserBanStatus
				  {
					  IsBanned = u.IsBanned,
					  BanReason = u.BanReason ?? String.Empty
				  })
				  .FirstOrDefault());

		private static readonly Func<AppDbContext, long, Task<string?>> _getDisplayNameQuery =
				EF.CompileAsyncQuery((AppDbContext db, long userId) =>
					db.Users
					  .AsNoTracking()
					  .Where(u => u.ID == userId)
					  .Select(u => u.DisplayName)
					  .FirstOrDefault()
				);

		private static readonly Func<AppDbContext, List<long>, IAsyncEnumerable<User>> _getUsersByIds =
		EF.CompileAsyncQuery(
			(AppDbContext db, List<long> ids) =>
				db.Users
				  .Where(u => ids.Contains(u.ID))
				  .Select(u => new User
				  {
					  ID = u.ID,
					  DisplayName = u.DisplayName
				  })
		);

		private static readonly Func<AppDbContext, long, Task<UserLobbyPreferences?>> _getUserLobbyPreferencesQuery =
			EF.CompileAsyncQuery((AppDbContext db, long userId) =>
				db.Users
				  .AsNoTracking()
				  .Where(u => u.ID == userId)
				  .Select(u => new UserLobbyPreferences
				  {
					  favorite_color = u.FavoriteColor,
					  favorite_side = u.FavoriteSide,
					  favorite_map = u.FavoriteMap ?? String.Empty,
					  favorite_starting_money = u.FavoriteStartingMoney,
					  favorite_limit_superweapons = u.LimitSuperweapons
				  })
				  .FirstOrDefault()
			);

		private static readonly Func<AppDbContext, List<long>, IAsyncEnumerable<User>> _compiledBulkQuery =
			EF.CompileAsyncQuery(
				(AppDbContext db, List<long> ids) =>
					db.Users.Where(u => ids.Contains(u.ID))
			);


		public static async Task<Dictionary<long, EloData>> GetBulkELOData(
	AppDbContext db, List<long> userIds)
		{
			Dictionary<long, EloData> results = new();

			if (userIds == null || userIds.Count == 0)
				return results;

			try
			{
				// Execute compiled query
				await foreach (var u in _compiledBulkQuery(db, userIds))
				{
					results[u.ID] = new EloData(u.EloRating, u.MonthlyEloRating, u.EloNumberOfMatches);
				}

				// Fill missing users with defaults
				foreach (var id in userIds)
				{
					if (!results.ContainsKey(id))
						results[id] = new EloData(EloConfig.BaseRating, EloConfig.BaseRating, 0);
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetBulkELOData failed");
			}

			return results;
		}


#if DEBUG
		public static readonly Func<AppDbContext, long, Task<bool>> UserExists =
        EF.CompileAsyncQuery(
            (AppDbContext db, long userId) =>
                db.Users.Any(u => u.ID == userId)
        );

		internal static async Task CreateUserIfNotExists_DevAccount(
		AppDbContext db, long userId, string displayName)
		{
			// Normalize input
			displayName = displayName?.Trim();

			// Fast existence check
			bool exists = await UserExists(db, userId);

			if (!exists)
			{
				// needs try catch because debug client spams this
				try
				{
					db.Users.Add(new User
					{
						ID = userId,
						AccountType = EAccountType.DevAccount,
						DisplayName = displayName
					});

					await db.SaveChangesAsync();
				}
				catch
				{

				}
			}
		}

#endif

		public static async Task<bool> IsUserAdmin(AppDbContext db, long userId)
		{
			try
			{
				return await _isUserAdminQuery(db, userId);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "IsUserAdmin failed");
				return false;
			}
		}

		public static async Task<Dictionary<long, string>> GetDisplayNameBulk(AppDbContext db, List<long> lstUserIDs)
		{
			var dict = new Dictionary<long, string>(lstUserIDs.Count);

			try
			{
				await foreach (var user in _getUsersByIds(db, lstUserIDs))
				{
					if (user.DisplayName is not null)
					{
						dict[user.ID] = user.DisplayName;
					}
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetDisplayNameBulk failed");
			}

			return dict;
		}

		public static async Task<bool> IsUserBanned(AppDbContext db, long userId)
		{
			try
			{
				return await _isUserBannedQuery(db, userId);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "IsUserBanned failed");
				return false;
			}
		}

		public static async Task<UserBanStatus?> GetUserBanStatus(AppDbContext db, long userId)
		{
			try
			{
				return await _getUserBanStatusQuery(db, userId);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetUserBanStatus failed");
				return null;
			}
		}


		public static async Task<string> GetDisplayName(AppDbContext db, long userId)
		{
			try
			{
				return await _getDisplayNameQuery(db, userId) ?? string.Empty;
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetDisplayName failed");
				return string.Empty;
			}
		}

		public static async Task<UserLobbyPreferences?> GetUserLobbyPreferences(AppDbContext db, long userId)
		{
			try
			{
				return await _getUserLobbyPreferencesQuery(db, userId);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetUserLobbyPreferences failed");
				return null;
			}
		}

		// TODO_EFCORE: check all queries, determine which ones should be moved to precompiled query
		public static async Task SetFavorite_LimitSuperweapons(
			AppDbContext db,
			long userId,
			bool bLimitSuperweapons)
		{
			try
			{
				// TODO_EFCORE: Check all sets, some may want to be execute update instead of db.SaveChangesAsync(); as this requires a lookup first
				await db.Users.Where(u => u.ID == userId).ExecuteUpdateAsync(setters => setters.SetProperty(u => u.LimitSuperweapons, bLimitSuperweapons));
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "SetFavorite_LimitSuperweapons failed");
			}
		}

		public static async Task<EloData> GetELOData(AppDbContext db, long userId)
		{
			try
			{
				// Try fetching from the external API
				var apiElo = await ExternalLeaderboardsClient.GetEloFromApi(userId);
				if (apiElo != null)
				{
					// Persist retrieved rating to DB asynchronously for fallback
					await SaveELOData(db, userId, apiElo);
					return apiElo;
				}
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetELOData API call failed");
			}

			try
			{
				// Fall back to database ELO
				var result = await GetEloData(db, userId);
				if (result != null)
					return result;
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "GetELOData fallback failed");
			}

			return new EloData(EloConfig.BaseRating, EloConfig.BaseRating, 0);
		}


		public static async Task SetFavorite_Map(
			AppDbContext db,
			long userId,
			string strMap)
		{
			try
			{
				await db.Users.Where(u => u.ID == userId).ExecuteUpdateAsync(setters => setters.SetProperty(u => u.FavoriteMap, strMap));
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "SetFavorite_Map failed");
			}
		}

		public static async Task UpdateLastLoginData(AppDbContext db, long userId, string ipAddr)
		{
			try
			{
				await db.Users
					.Where(u => u.ID == userId)
					.ExecuteUpdateAsync(setters => setters
						.SetProperty(u => u.LastLogin, DateTime.UtcNow)
						.SetProperty(u => u.LastIPAddress, ipAddr)
					);
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "UpdateLastLoginData failed");
			}
		}


		public static async Task SetFavorite_StartingMoney(
			AppDbContext db,
			long userId,
			int startingMoney)
		{
			try
			{
				await db.Users.Where(u => u.ID == userId).ExecuteUpdateAsync(setters => setters.SetProperty(u => u.FavoriteStartingMoney, startingMoney));
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "SetFavorite_StartingMoney failed");
			}
		}

		public static async Task SetFavorite_Side(
			AppDbContext db,
			long userId,
			int side)
		{
			try
			{
				await db.Users.Where(u => u.ID == userId).ExecuteUpdateAsync(setters => setters.SetProperty(u => u.FavoriteSide, side));
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "SetFavorite_Side failed");
			}
		}

		public static async Task<bool> SetDisplayName(AppDbContext db, long userId, string newName)
		{
			try
			{
				bool nameTaken = await db.Users
					.AnyAsync(u => u.ID != userId && u.DisplayName.ToLower() == newName.ToLower());

				if (nameTaken)
					return false;

				await db.Users
					.Where(u => u.ID == userId)
					.ExecuteUpdateAsync(setters => setters
						.SetProperty(u => u.DisplayName, newName)
					);

				return true;
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "SetDisplayName failed");
				return false;
			}
		}

		public static async Task SetFavorite_Color(
			AppDbContext db,
			long userId,
			int color)
		{
			try
			{
				await db.Users.Where(u => u.ID == userId).ExecuteUpdateAsync(setters => setters.SetProperty(u => u.FavoriteColor, color));
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "SetFavorite_Color failed");
			}
		}

		public static async Task SaveELOData(
			AppDbContext db,
			long userId,
			EloData newEloData,
			CancellationToken cancellationToken = default)
		{
			try
			{
				await db.Users
					.Where(u => u.ID == userId)
					.ExecuteUpdateAsync(setters => setters
						.SetProperty(u => u.EloRating, newEloData.Rating)
						.SetProperty(u => u.MonthlyEloRating, newEloData.MonthlyRating)
						.SetProperty(u => u.EloNumberOfMatches, newEloData.NumMatches),
						cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				s_log.LogError(ex, "SaveELOData failed");
			}
		}

		public static async Task<bool> SaveExternalELOData(
			AppDbContext db,
			long userId,
			EloData newEloData,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(db);
			ArgumentNullException.ThrowIfNull(newEloData);

			int updated = await db.Users
				.Where(u => u.ID == userId)
				.ExecuteUpdateAsync(setters => setters
					.SetProperty(u => u.EloRating, newEloData.Rating)
					.SetProperty(u => u.MonthlyEloRating, newEloData.MonthlyRating)
					.SetProperty(u => u.EloNumberOfMatches, newEloData.NumMatches),
					cancellationToken);

			return updated == 1;
		}

	}
}
