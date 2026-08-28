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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

// NOTE: Want to allow only a certain token type? use the below:
//		 [Authorize(AuthenticationSchemes = "Basic")]
//		 [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
// Or for a specific claim:
//		[Authorize(Roles = "Admin")] or [Authorize(Policy = "CustomPolicy")]

namespace GenOnlineService.Controllers
{
	public class GET_MonitorDatabase_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(GET_MonitorDatabase_Result);
		}

		public bool ok { get; set; } = false;
	}

	public class GET_Uptime_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(GET_Uptime_Result);
		}

		public string start_time { get; set; } = "";
		public string uptime { get; set; } = "";
	}

	public class GET_ActiveUsers_UserEntry
	{
		public string? name { get; set; }
		public string? status { get; set; }
		public KnownClients.EKnownClients? client_id { get; set; }
		public string? duration { get; set; }
	}

	public class GET_ActiveUsers_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(GET_ActiveUsers_Result);
		}

		public List<GET_ActiveUsers_UserEntry> active_users { get; set; } = new();
	}

	public class GET_BasicStats_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(GET_BasicStats_Result);
		}

		public int players { get; set; } = new();
		public int lobbies { get; set; } = new();
	}

	public class GET_MyUser_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(GET_MyUser_Result);
		}

		public Int64 user_id { get; set; } = -1;
		public string display_name { get; set; } = String.Empty;
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class MonitoringController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> _dbFactory;
		private readonly ILogger<MonitoringController> _logger;

		public MonitoringController(IDbContextFactory<AppDbContext> dbFactory, ILogger<MonitoringController> logger)
		{
			_logger = logger;
			_dbFactory = dbFactory;
		}

		[Route("ActiveUsers")]
		[Authorize(Policy = "MonitorOrApiKey")]
		[HttpGet]
		public APIResult Monitor_ActiveUsers()
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

			HashSet<Int64> setUsersAlreadyProcessed = new();
			ConcurrentDictionary<EUserSessionType, ConcurrentDictionary<Int64, UserSession>> allData = WebSocketManager.GetUserDataCache();
			foreach (var sessionDataPerClientType in allData)
			{
				foreach (var sessionData in sessionDataPerClientType.Value)
				{
					if (setUsersAlreadyProcessed.Contains(sessionData.Value.m_UserID))
					{
						continue;
					}
					setUsersAlreadyProcessed.Add(sessionData.Value.m_UserID);

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

		[Route("BasicStats")]
		[HttpGet]
		public APIResult Monitor_BaicStats()
		{
			GET_BasicStats_Result result = new GET_BasicStats_Result();

			var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();

			result.players = GenOnlineService.WebSocketManager.GetNumberOfUsersOnline();
			result.lobbies = lobbyManager.GetNumLobbies();

			return result;
		}

		[Route("Database")]
		[Authorize(Roles = "Monitor")]
		[HttpGet]
		public async Task<APIResult> Monitor_Database()
		{
			GET_MonitorDatabase_Result result = new GET_MonitorDatabase_Result();

			if (!this.User.IsInRole("Monitor"))
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
			}
			else
			{
				// db call
				try
				{
					await using var db = await _dbFactory.CreateDbContextAsync();
					string strDontCare = await Database.Users.GetDisplayName(db, 0);
					result.ok = true;
				}
				catch
				{
					result.ok = false;
				}
			}

			return result;
		}

		[Route("LoginWithToken")]
		[Authorize(Roles = "Monitor")]
		[HttpGet]
		public async Task<APIResult?> Monitor_LoginWithToken(
			[FromRoute] string environment,
			[FromRoute(Name = "contract_version")] string contractVersion)
		{
			if (!this.User.IsInRole("Monitor"))
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
			}
			else
			{
				// db call
				try
				{
					using var scope = ServiceLocator.Services.CreateScope();
					var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();

					GenOnlineService.Controllers.LoginWithToken.LoginWithToken loginWithTokenController = new GenOnlineService.Controllers.LoginWithToken.LoginWithToken(factory);
					GenOnlineService.Controllers.LoginWithToken.POST_LoginWithToken_Result internalResult = (GenOnlineService.Controllers.LoginWithToken.POST_LoginWithToken_Result)await loginWithTokenController.Post_InternalHandler("{\"challenge\": \"abc\", \"token\": \"iamatest\", \"client_id\": \"gen_online_60hz\"}", IPAddress.Loopback.ToString(), Program.BuildWebSocketUrl(Request, environment, contractVersion));
					return internalResult;
				}
				catch
				{
					Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				}
			}

			return null;
		}

		[Route("Uptime")]
		[HttpGet]
		public APIResult Monitor_Uptime()
		{
			GET_Uptime_Result result = new GET_Uptime_Result();

			result.start_time = Program.g_LastStartTime.ToString("yyyy-MM-dd HH:mm:ss");

			TimeSpan difference = DateTime.Now.Subtract(Program.g_LastStartTime);
			result.uptime = $"Days: {difference.Days}, Hours: {difference.Hours}, Minutes: {difference.Minutes}";

			return result;
		}

		[Route("VersionCheck")]
		[Authorize(Roles = "Monitor")]
		[HttpGet]
		// TODO: Undo all of these and make all flows use gethttpsize/head
#if !DEBUG
		public async Task<APIResult?> Monitor_VersionCheck()
#else
		public async Task<APIResult?> Monitor_VersionCheck()
#endif
		{
			if (!this.User.IsInRole("Monitor"))
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
			}
			else
			{
				// db call
				try
				{
					GenOnlineService.Controllers.VersionCheckController versionCheckController = new GenOnlineService.Controllers.VersionCheckController();
#if !DEBUG
				APIResult internalResult = await VersionHelper.Post_InternalHandler("{\"execrc\": 1234567890, \"ver\": 1, \"netver\": 2, \"servicesver\": 3}");
#else
					APIResult internalResult = await VersionHelper.Post_InternalHandler("{\"execrc\": 1234567890, \"ver\": 1, \"netver\": 2, \"servicesver\": 3}");
#endif

					return internalResult;
				}
				catch
				{
					Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				}
			}

			return null;
		}


		[Route("CheckLogin")]
		[Authorize(Roles = "Monitor")]
		[HttpGet]
		public async Task<APIResult?> Monitor_CheckLogin(
			[FromRoute] string environment,
			[FromRoute(Name = "contract_version")] string contractVersion)
		{
			if (!this.User.IsInRole("Monitor"))
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
			}
			else
			{
				try
				{
					GenOnlineService.Controllers.CheckLoginController checkLoginController = new GenOnlineService.Controllers.CheckLoginController(_dbFactory);
					APIResult internalResult = await checkLoginController.Post_InternalHandler("{\"challenge\": \"abc\", \"nonce\": \"def\", \"code\": \"iamatest\", \"client_id\": \"gen_online_30hz\"}", IPAddress.Loopback.ToString(), Program.BuildWebSocketUrl(Request, environment, contractVersion));
					return internalResult;
				}
				catch
				{
					Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				}
			}

			return null;
		}
	}
}
