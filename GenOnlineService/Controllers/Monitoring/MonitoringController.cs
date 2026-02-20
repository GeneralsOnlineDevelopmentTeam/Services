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
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Security;
using System;
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
		public string? client_id { get; set; }
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
		private readonly ILogger<MonitoringController> _logger;

		public MonitoringController(ILogger<MonitoringController> logger)
		{
			_logger = logger;
		}

		[Route("ActiveUsers")]
		[Authorize(Policy = "MonitorOrApiKey")]
		[HttpGet]
		public APIResult Monitor_ActiveUsers()
		{
			try
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

				var allData = WebSocketManager.GetUserDataCache();
				foreach (var sessionData in allData)
				{
					GET_ActiveUsers_UserEntry userEntry = new();
					userEntry.name = sessionData.Value.m_strDisplayName;
					userEntry.status = UserPresence.DetermineUserStatus(sessionData.Value);
					userEntry.client_id = sessionData.Value.m_client_id;
					userEntry.duration = TimeSpanToHumanReadableString(sessionData.Value.GetDuration());

					result.active_users.Add(userEntry);
				}


				return result;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in Monitor_ActiveUsers: {ex.Message}");
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return new GET_ActiveUsers_Result();
			}
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
					string strDontCare = await Database.Functions.Auth.GetDisplayName(GlobalDatabaseInstance.g_Database, 0);
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
		public async Task<APIResult?> Monitor_LoginWithToken()
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
					GenOnlineService.Controllers.LoginWithToken.LoginWithToken loginWithTokenController = new GenOnlineService.Controllers.LoginWithToken.LoginWithToken();
					GenOnlineService.Controllers.LoginWithToken.POST_LoginWithToken_Result internalResult = (GenOnlineService.Controllers.LoginWithToken.POST_LoginWithToken_Result)await loginWithTokenController.Post_InternalHandler("{\"challenge\": \"abc\", \"token\": \"iamatest\", \"client_id\": \"gen_online_60hz\"}", IPAddress.Loopback.ToString(), true);
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
			try
			{
				GET_Uptime_Result result = new GET_Uptime_Result();

				result.start_time = Program.g_LastStartTime.ToString("yyyy-MM-dd HH:mm:ss");

				TimeSpan difference = DateTime.Now.Subtract(Program.g_LastStartTime);
				result.uptime = $"Days: {difference.Days}, Hours: {difference.Hours}, Minutes: {difference.Minutes}";

				return result;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in Monitor_Uptime: {ex.Message}");
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return new GET_Uptime_Result();
			}
		}

		[Route("VersionCheck")]
		[Authorize(Roles = "Monitor")]
		[HttpGet]
		// TODO: Undo all of these and make all flows use gethttpsize/head
#if !DEBUG
		public APIResult? Monitor_VersionCheck()
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
				APIResult internalResult = VersionHelper.Post_InternalHandler("{\"execrc\": 1234567890, \"ver\": 1, \"netver\": 2, \"servicesver\": 3}");
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
		public async Task<APIResult?> Monitor_CheckLogin()
		{
			if (!this.User.IsInRole("Monitor"))
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
			}
			else
			{
				try
				{
					GenOnlineService.Controllers.CheckLoginController checkLoginController = new GenOnlineService.Controllers.CheckLoginController();
					APIResult internalResult = await checkLoginController.Post_InternalHandler("{\"challenge\": \"abc\", \"nonce\": \"def\", \"code\": \"iamatest\", \"client_id\": \"gen_online_30hz\"}", IPAddress.Loopback.ToString(), true);
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
