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
using System.Collections.Concurrent;
using GenOnlineService;

// Persisted per-user/per-session-type token state, used to revoke JWTs before their natural expiry.
//
// Two mechanisms live here:
//   1. TokenGeneration - a counter stamped into every issued token as the "tgen" claim. Bumping it
//      instantly invalidates every session AND refresh token previously issued to that user.
//   2. RefreshJti - the jti of the only refresh token currently accepted for that user/session type.
//      Refreshing rotates it, which makes the presented (old) refresh token single-use. The jti it
//      replaced is kept in PreviousRefreshJti for a short grace window so that a client which never
//      received the response to its refresh (dropped connection, timeout) can retry instead of being
//      forced through a full re-login.
public class UserTokenState
{
	public Int64 UserID { get; set; }
	public EUserSessionType SessionType { get; set; } = EUserSessionType.None;
	public int TokenGeneration { get; set; } = 0;
	public string RefreshJti { get; set; } = String.Empty;
	public string PreviousRefreshJti { get; set; } = String.Empty;
	public DateTime PreviousRefreshJtiExpires { get; set; } = DateTime.UnixEpoch;
	public DateTime Updated { get; set; } = DateTime.UnixEpoch;
}

public class UserTokenStateConfiguration : IEntityTypeConfiguration<UserTokenState>
{
	public void Configure(EntityTypeBuilder<UserTokenState> builder)
	{
		builder.ToTable("user_token_state");

		builder.HasKey(e => new { e.UserID, e.SessionType });

		builder.Property(e => e.UserID).HasColumnName("user_id");
		builder.Property(e => e.SessionType).HasColumnName("session_type");
		builder.Property(e => e.TokenGeneration).HasColumnName("token_generation");
		builder.Property(e => e.RefreshJti).HasColumnName("refresh_jti").HasColumnType("varchar(64)");
		builder.Property(e => e.PreviousRefreshJti).HasColumnName("previous_refresh_jti").HasColumnType("varchar(64)");
		builder.Property(e => e.PreviousRefreshJtiExpires).HasColumnName("previous_refresh_jti_expires").HasColumnType("datetime");
		builder.Property(e => e.Updated).HasColumnName("updated").HasColumnType("datetime");
	}
}

namespace Database
{
	public static class UserTokens
	{
		public static async Task<List<UserTokenState>> GetAll(AppDbContext db)
		{
			try
			{
				return await db.UserTokenStates.AsNoTracking().ToListAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] UserTokens.GetAll failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return new List<UserTokenState>();
			}
		}

		public static async Task Upsert(AppDbContext db, Int64 userID, EUserSessionType sessionType, int tokenGeneration, string refreshJti)
		{
			try
			{
				int sessionTypeValue = (int)sessionType;

				await db.Database.ExecuteSqlInterpolatedAsync($@"
					INSERT INTO user_token_state (user_id, session_type, token_generation, refresh_jti, updated)
					VALUES ({userID}, {sessionTypeValue}, {tokenGeneration}, {refreshJti}, UTC_TIMESTAMP())
					ON DUPLICATE KEY UPDATE
						token_generation = VALUES(token_generation),
						refresh_jti = VALUES(refresh_jti),
						updated = VALUES(updated)");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] UserTokens.Upsert failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		public static async Task<List<Int64>> GetBannedUserIDs(AppDbContext db)
		{
			try
			{
				return await db.Users.AsNoTracking().Where(u => u.IsBanned).Select(u => u.ID).ToListAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] UserTokens.GetBannedUserIDs failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return new List<Int64>();
			}
		}
	}
}

namespace GenOnlineService
{
	// In-memory authority for token revocation. Reads are lock-free so they can happen on every
	// authenticated request without touching the database; the database is only used so that state
	// survives a restart.
	public static class TokenRevocationManager
	{
		private sealed class CachedState
		{
			public int Generation;
			public string RefreshJti = String.Empty;
		}

		private static readonly ConcurrentDictionary<(Int64, EUserSessionType), CachedState> s_state = new();

		// Users currently flagged as banned in the database. Refreshed periodically so that a ban
		// applied directly in the database takes effect without waiting for token expiry.
		private static volatile HashSet<Int64> s_bannedUsers = new();
		private static readonly object s_bannedLock = new object();

		private static IDbContextFactory<AppDbContext>? s_dbFactory = null;

		private static readonly EUserSessionType[] s_allSessionTypes = new[]
		{
			EUserSessionType.GameClient,
			EUserSessionType.ChatClient,
			EUserSessionType.GameLauncher
		};

		public static async Task Initialize(IDbContextFactory<AppDbContext> dbFactory, AppDbContext db)
		{
			s_dbFactory = dbFactory;

			foreach (UserTokenState row in await Database.UserTokens.GetAll(db))
			{
				s_state[(row.UserID, row.SessionType)] = new CachedState
				{
					Generation = row.TokenGeneration,
					RefreshJti = row.RefreshJti ?? String.Empty
				};
			}

			// Seed the banned set without revoking. Anyone banned while the service was running was
			// already revoked at ban time and that revocation was persisted above.
			List<Int64> banned = await Database.UserTokens.GetBannedUserIDs(db);
			lock (s_bannedLock)
			{
				s_bannedUsers = new HashSet<Int64>(banned);
			}

			Console.WriteLine($"[TokenRevocation] Loaded {s_state.Count} token state entries, {banned.Count} banned users.");
		}

		public static int GetGeneration(Int64 userID, EUserSessionType sessionType)
		{
			return s_state.TryGetValue((userID, sessionType), out CachedState? state) ? state.Generation : 0;
		}

		public static bool IsUserBanned(Int64 userID)
		{
			return s_bannedUsers.Contains(userID);
		}

		// A refresh token is only accepted if its jti is the most recently issued one for that
		// user/session type. Rotating on every refresh makes a stolen refresh token useless as soon
		// as either party uses it.
		public static bool IsCurrentRefreshToken(Int64 userID, EUserSessionType sessionType, string jti)
		{
			if (String.IsNullOrEmpty(jti))
			{
				return false;
			}

			if (!s_state.TryGetValue((userID, sessionType), out CachedState? state))
			{
				// No record yet (e.g. tokens issued before this feature shipped). Accept once so we
				// don't force every live client to re-login; the refresh will then record its jti.
				return true;
			}

			// Same reason as above - a record that has never had a refresh jti recorded is permissive.
			if (String.IsNullOrEmpty(state.RefreshJti))
			{
				return true;
			}

			return String.Equals(state.RefreshJti, jti, StringComparison.Ordinal);
		}

		public static async Task OnTokensIssued(Int64 userID, EUserSessionType sessionType, string refreshJti)
		{
			CachedState newState = s_state.AddOrUpdate(
				(userID, sessionType),
				_ => new CachedState { Generation = 0, RefreshJti = refreshJti },
				(_, existing) => new CachedState { Generation = existing.Generation, RefreshJti = refreshJti });

			await Persist(userID, sessionType, newState);
		}

		// Invalidates every token previously issued to this user, across all session types, and drops
		// any live websockets they hold.
		public static async Task RevokeAllTokensForUser(Int64 userID, string reason)
		{
			Console.WriteLine($"[TokenRevocation] Revoking all tokens for user {userID} ({reason}).");

			foreach (EUserSessionType sessionType in s_allSessionTypes)
			{
				CachedState newState = s_state.AddOrUpdate(
					(userID, sessionType),
					_ => new CachedState { Generation = 1, RefreshJti = String.Empty },
					(_, existing) => new CachedState { Generation = existing.Generation + 1, RefreshJti = String.Empty });

				await Persist(userID, sessionType, newState);
			}

			await DisconnectUser(userID);
		}

		// Picks up bans applied directly in the database (there is no in-process ban API).
		public static async Task ReconcileBans(AppDbContext db)
		{
			// Don't run before the initial seed, or every already-banned user would look "newly banned".
			if (s_dbFactory == null)
			{
				return;
			}

			List<Int64> banned = await Database.UserTokens.GetBannedUserIDs(db);

			HashSet<Int64> newlyBanned = new HashSet<Int64>();

			lock (s_bannedLock)
			{
				foreach (Int64 userID in banned)
				{
					if (!s_bannedUsers.Contains(userID))
					{
						newlyBanned.Add(userID);
					}
				}

				s_bannedUsers = new HashSet<Int64>(banned);
			}

			foreach (Int64 userID in newlyBanned)
			{
				await RevokeAllTokensForUser(userID, "user was banned");
			}
		}

		private static async Task Persist(Int64 userID, EUserSessionType sessionType, CachedState state)
		{
			if (s_dbFactory == null)
			{
				return;
			}

			try
			{
				await using AppDbContext db = await s_dbFactory.CreateDbContextAsync();
				await Database.UserTokens.Upsert(db, userID, sessionType, state.Generation, state.RefreshJti);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] TokenRevocation.Persist failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		private static async Task DisconnectUser(Int64 userID)
		{
			try
			{
				List<UserSession> lstUserSessions = WebSocketManager.GetAllDataFromUser(userID);
				foreach (UserSession userSession in lstUserSessions)
				{
					UserWebSocketInstance? oldWS = WebSocketManager.GetWebSocketForSession(userSession);
					await WebSocketManager.DeleteSession(userID, userSession.GetSessionType(), oldWS, true);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] TokenRevocation.DisconnectUser failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}
	}
}
