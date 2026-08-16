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

using Amazon.S3.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public class RouteHandler_DELETE_User_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public bool success { get; set; } = false;
	}

	public class POST_User_SetPriority_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(POST_User_SetPriority_Result);
		}

		public bool success { get; set; } = false;
		public string detail { get; set; } = String.Empty;
		public Int64 user_id { get; set; } = -1;
		public string display_name { get; set; } = String.Empty;
		public int previous_priority { get; set; } = 0;
	}

	public class POST_User_LookupUser_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(POST_User_LookupUser_Result);
		}

		public bool success { get; set; } = false;
		public string detail { get; set; } = String.Empty;
		public List<UserLookupEntry> users { get; set; } = new List<UserLookupEntry>();
	}

	public class POST_User_SetPriorityBatch_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(POST_User_SetPriorityBatch_Result);
		}

		public bool success { get; set; } = false;
		public string detail { get; set; } = String.Empty;
		public int updated { get; set; } = 0;
		public List<PriorityBatchError> errors { get; set; } = new List<PriorityBatchError>();
	}

	public class PriorityBatchError
	{
		public Int64 user_id { get; set; } = -1;
		public string detail { get; set; } = String.Empty;
	}

	public class UserLookupEntry
	{
		public Int64 user_id { get; set; } = -1;
		public string display_name { get; set; } = String.Empty;
		public int priority { get; set; } = 0;
		public EAccountType account_type { get; set; } = EAccountType.Unknown;
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class UsersController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> _dbFactory;
		private readonly ILogger<UsersController> _logger;

		public UsersController(IDbContextFactory<AppDbContext> dbFactory, ILogger<UsersController> logger)
		{
			_logger = logger;
			_dbFactory = dbFactory;
		}

		[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
		[HttpGet("Me")]
		public async Task<APIResult> MyUser()
		{
			GET_MyUser_Result result = new GET_MyUser_Result();

			Int64 user_id = TokenHelper.GetUserID(this);

			if (user_id != -1)
			{
				await using var db = await _dbFactory.CreateDbContextAsync();
				string strDisplayName = await Database.Users.GetDisplayName(db, user_id);

				result.display_name = strDisplayName;
				result.user_id = user_id;
			}

			return result;
		}

		[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
		[HttpGet("Active")]
		public APIResult ActiveUsers()
		{
			GET_ActiveUsers_Result result = new GET_ActiveUsers_Result();

			string TimeSpanToHumanReadableString(TimeSpan timeSpan)
			{
				string humanReadable = $"{(timeSpan.Days > 0 ? $"{timeSpan.Days} days, " : "")}" +
					   $"{(timeSpan.Hours > 0 ? $"{timeSpan.Hours} hours, " : "")}" +
					   $"{(timeSpan.Minutes > 0 ? $"{timeSpan.Minutes} minutes, " : "")}" +
					   $"{timeSpan.Seconds} seconds";
				humanReadable = humanReadable.TrimEnd(',', ' ');
				return humanReadable;
			}

			// TODO_QUICKMATCH: We chekc maps are big enough, but the reverse needs checked too - dont let 8 playrs join a 6-8 ffa if only map is defcon6 for example

			ConcurrentDictionary<EUserSessionType, ConcurrentDictionary<Int64, UserSession>> allData = WebSocketManager.GetUserDataCache();
			foreach (var sessionDataPerClientType in allData)
			{
				foreach (var sessionData in sessionDataPerClientType.Value)
				{
					SharedUserData? userSharedData = WebSocketManager.GetSharedDataForUser(sessionData.Value.m_UserID);
					if (userSharedData != null)
					{
						GET_ActiveUsers_UserEntry userEntry = new();
						userEntry.name = userSharedData.m_strDisplayName;
						userEntry.status = UserPresence.DetermineUserStatusFromAllSessions(sessionData.Key, out bool isOnline);
						userEntry.client_id = sessionData.Value.m_client_id;
						userEntry.duration = TimeSpanToHumanReadableString(sessionData.Value.GetDuration());

						result.active_users.Add(userEntry);
					}
				}
			}


			return result;
		}

		// ---- Operator API for user_priority. Authenticated with a shared key
		// ("Authorization: Discord <WsBot:api_key>"), not a game-client JWT, so an external
		// scheduler can grant priority for the duration of an event and restore it afterwards.

		// Body: { "user_id": 12345, "priority": 2 }. previous_priority in the response is what
		// the caller stores to restore the earlier value later.
		[Authorize(AuthenticationSchemes = "Discord")]
		[HttpPost("SetPriority")]
		public async Task<APIResult> SetPriority()
		{
			POST_User_SetPriority_Result result = new POST_User_SetPriority_Result();

			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
				Dictionary<string, JsonElement>? data = null;
				try
				{
					data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);
				}
				catch
				{
					data = null;
				}

				if (data == null ||
					!data.TryGetValue("user_id", out JsonElement userIdEl) ||
					userIdEl.ValueKind != JsonValueKind.Number ||
					!userIdEl.TryGetInt64(out Int64 userId) ||
					!data.TryGetValue("priority", out JsonElement priorityEl) ||
					!priorityEl.TryGetInt32(out int priority))
				{
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					result.detail = "Body must be { \"user_id\": <int>, \"priority\": <0|1|2> }.";
					return result;
				}

				await using var db = await _dbFactory.CreateDbContextAsync();

				User? user = await Database.Users.GetUserById(db, userId);
				if (user == null)
				{
					Response.StatusCode = (int)HttpStatusCode.NotFound;
					result.detail = $"User {userId} does not exist.";
					return result;
				}

				if (priority < (int)EUserPriority.None || priority > (int)EUserPriority.Viewer)
				{
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					result.detail = $"Priority must be {(int)EUserPriority.None}, {(int)EUserPriority.Player} or {(int)EUserPriority.Viewer}.";
					return result;
				}

				int previous = (int)await Database.Users.GetUserPriority(db, userId);
				if (await Database.Users.SetUserPriority(db, userId, priority))
				{
					result.success = true;
					result.user_id = userId;
					result.display_name = user.DisplayName ?? String.Empty;
					result.previous_priority = previous;
				}
				else
				{
					Response.StatusCode = (int)HttpStatusCode.InternalServerError;
					result.detail = "Failed to update user_priority.";
				}
			}

			return result;
		}

		// Body: [ { "user_id": 12345, "priority": 2 }, ... ] - the whole roster in one call,
		// window-open (priority 2) or window-close (priority 0). Invalid entries are reported
		// per-user; valid ones all apply.
		[Authorize(AuthenticationSchemes = "Discord")]
		[HttpPost("SetPriorityBatch")]
		public async Task<APIResult> SetPriorityBatch()
		{
			POST_User_SetPriorityBatch_Result result = new POST_User_SetPriorityBatch_Result();

			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
				List<Dictionary<string, JsonElement>>? entries = null;
				try
				{
					entries = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(jsonData, options);
				}
				catch
				{
					entries = null;
				}

				if (entries == null || entries.Count == 0)
				{
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					result.detail = "Body must be a non-empty array of { \"user_id\": <int>, \"priority\": <0|1|2> }.";
					return result;
				}

				await using var db = await _dbFactory.CreateDbContextAsync();

				Dictionary<Int64, int> validById = new Dictionary<Int64, int>();
				foreach (Dictionary<string, JsonElement> entry in entries)
				{
					if (!entry.TryGetValue("user_id", out JsonElement userIdEl) ||
						userIdEl.ValueKind != JsonValueKind.Number ||
						!userIdEl.TryGetInt64(out Int64 userId) ||
						!entry.TryGetValue("priority", out JsonElement priorityEl) ||
						!priorityEl.TryGetInt32(out int priority))
					{
						result.errors.Add(new PriorityBatchError { user_id = -1, detail = "entry must be { \"user_id\": <int>, \"priority\": <0|1|2> }" });
						continue;
					}

					if (priority < (int)EUserPriority.None || priority > (int)EUserPriority.Viewer)
					{
						result.errors.Add(new PriorityBatchError { user_id = userId, detail = $"priority must be {(int)EUserPriority.None}, {(int)EUserPriority.Player} or {(int)EUserPriority.Viewer}" });
						continue;
					}

					validById[userId] = priority;
				}

				// One UPDATE per distinct priority: UPDATE users SET user_priority = @p
				// WHERE user_id IN (...). Nonexistent ids simply match nothing.
				foreach (IGrouping<int, KeyValuePair<Int64, int>> priorityGroup in validById.GroupBy(e => e.Value))
				{
					List<Int64> ids = priorityGroup.Select(e => e.Key).ToList();
					int updated = await db.Users
						.Where(u => ids.Contains(u.ID))
						.ExecuteUpdateAsync(setters => setters.SetProperty(u => u.UserPriority, priorityGroup.Key));

					result.updated += updated;

					// Report ids that did not exist so the bot can skip them when restoring.
					if (updated < ids.Count)
					{
						List<Int64> foundIds = await db.Users.AsNoTracking()
							.Where(u => ids.Contains(u.ID))
							.Select(u => u.ID)
							.ToListAsync();
						foreach (Int64 id in ids.Where(id => !foundIds.Contains(id)))
						{
							result.errors.Add(new PriorityBatchError { user_id = id, detail = "user does not exist" });
						}
					}
				}

				result.success = true;
			}

			return result;
		}

		// Body (one of):
		//   { "user_id": 12345 }              exact user-id match
		//   { "display_name": "x64" }         exact display-name match
		//   { "discord_id": 1234567890 }      users.discord_id (website Discord login)
		//   { "search_parts": ["bob", "x64"]} partial AND search, max 10 rows
		// All lookups are EF Core parameterised - arbitrary input cannot reach SQL.
		[Authorize(AuthenticationSchemes = "Discord")]
		[HttpPost("LookupUser")]
		public async Task<APIResult> LookupUser()
		{
			POST_User_LookupUser_Result result = new POST_User_LookupUser_Result();

			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
				Dictionary<string, JsonElement>? data = null;
				try
				{
					data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);
				}
				catch
				{
					data = null;
				}

				if (data == null)
				{
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					result.detail = "Body must be { \"display_name\" } or { \"user_id\" } or { \"discord_id\" } or { \"search_parts\" }.";
					return result;
				}

				await using var db = await _dbFactory.CreateDbContextAsync();

				List<User> users = new List<User>();

				if (data.TryGetValue("display_name", out JsonElement displayNameEl) &&
					displayNameEl.ValueKind == JsonValueKind.String)
				{
					User? exact = await Database.Users.GetUserByDisplayName(db, displayNameEl.GetString() ?? String.Empty);
					if (exact != null)
					{
						users.Add(exact);
					}
				}
				else if (data.TryGetValue("user_id", out JsonElement userIdEl) &&
					userIdEl.TryGetInt64(out Int64 lookupUserId))
				{
					User? byId = await Database.Users.GetUserById(db, lookupUserId);
					if (byId != null)
					{
						users.Add(byId);
					}
				}
				else if (data.TryGetValue("discord_id", out JsonElement discordIdEl) &&
					discordIdEl.TryGetInt64(out Int64 discordId))
				{
					User? byDiscord = await Database.Users.GetUserByDiscordID(db, discordId);
					if (byDiscord != null)
					{
						users.Add(byDiscord);
					}
				}
				else if (data.TryGetValue("search_parts", out JsonElement searchPartsEl) &&
					searchPartsEl.ValueKind == JsonValueKind.Array)
				{
					List<string> parts = new List<string>();
					foreach (JsonElement partEl in searchPartsEl.EnumerateArray())
					{
						if (partEl.ValueKind == JsonValueKind.String)
						{
							parts.Add(partEl.GetString() ?? String.Empty);
						}
					}

					if (parts.Count > 0)
					{
						users = await Database.Users.SearchUsersByDisplayName(db, parts, 10);
					}
				}
				else
				{
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					result.detail = "Body must be { \"display_name\" } or { \"user_id\" } or { \"discord_id\" } or { \"search_parts\" }.";
					return result;
				}

				result.success = true;
				foreach (User user in users)
				{
					result.users.Add(new UserLookupEntry
					{
						user_id = user.ID,
						display_name = user.DisplayName ?? String.Empty,
						priority = user.UserPriority,
						account_type = user.AccountType
					});
				}
			}

			return result;
		}
	}

	[ApiController]
	[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class UserController : ControllerBase
	{
		private readonly ILogger<UserController> _logger;

		public UserController(ILogger<UserController> logger)
		{
			_logger = logger;
		}

		[HttpDelete]
		public async Task<APIResult> Delete()
		{
			RouteHandler_DELETE_User_Result result = new RouteHandler_DELETE_User_Result();

			Int64 user_id = TokenHelper.GetUserID(this);

			EUserSessionType sessionType = TokenHelper.GetSessionType(this);
			if (user_id != -1 && SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.Authenticate))
			{
				// TODO_JWT: Add token used to a 'ban list'
				//string token = "";

				// end session
				UserSession? session = WebSocketManager.GetSessionFromUser(user_id, sessionType);
				if (session != null)
				{
					UserWebSocketInstance ws = await session.CloseWebsocket(WebSocketCloseStatus.NormalClosure, "User logged out");
					await WebSocketManager.DeleteSession(user_id, sessionType, ws, true);
				}
			}

			return result;
		}
	}
}
