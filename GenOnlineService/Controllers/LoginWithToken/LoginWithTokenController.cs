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
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MySqlX.XDevAPI.Common;
using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers.LoginWithToken
{

	public class POST_LoginWithToken_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(POST_LoginWithToken_Result);
		}

		public EPendingLoginState result { get; set; } = EPendingLoginState.None;
		public string session_token { get; set; } = "";
		public string refresh_token { get; set; } = "";
		public Int64 user_id { get; set; } = -1;
		public string display_name { get; set; } = "";

		public string ws_uri { get; set; } = "";
	}

	[ApiController]
	[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
	[RefreshTokenEndpoint]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class LoginWithToken : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> _dbFactory;

		public LoginWithToken(IDbContextFactory<AppDbContext> dbFactory)
		{
			_dbFactory = dbFactory;
		}

		[HttpPost(Name = "PostLoginWithToken")]
		//public async Task<APIResult> Post([FromHeader(Name = "CF-Connecting-IP")] string? ipAddress)
		public async Task<APIResult> Post()
		{
			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();

				bool bSecureWS = true;
				//if (ipCountry2LISO.ToLower() == "ru")
				{
					//bSecureWS = false;
				}

				POST_LoginWithToken_Result result = (POST_LoginWithToken_Result)await Post_InternalHandler(jsonData, IPHelpers.NormalizeIP(HttpContext.Connection.RemoteIpAddress?.ToString()), bSecureWS);
				return result;
			}
		}

		public async Task<APIResult> Post_InternalHandler(string jsonData, string ipAddr, bool bSecureWS, bool bWasMonitor = false)
		{
			if (bWasMonitor)
			{
				ipAddr = IPAddress.Loopback.ToString();
			}

			POST_LoginWithToken_Result result = new POST_LoginWithToken_Result();

			var options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			};

			try
			{
				var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);

				KnownClients.EKnownClients clientID = TokenHelper.GetClientID(this);
				if (clientID == KnownClients.EKnownClients.unknown)
				{
					result.result = EPendingLoginState.LoginFailed;
					Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				}
				else
				{
					byte[] respNonce = new byte[32];
					using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) { rng.GetBytes(respNonce); }

					// The refresh token that got us here was already checked against the current
					// stored jti during authentication; issuing below rotates it so the presented
					// token can't be used again.

					// If you reach here, the refresh token was valid because auth happens globally
					if (Program.g_tokenGenerator != null)
					{
						// start their session etc
						Int64 user_id = TokenHelper.GetUserID(this);
						EUserSessionType sessionType = TokenHelper.GetSessionType(this);

						await using var db = await _dbFactory.CreateDbContextAsync();

						// Game clients should register the user device
						if (sessionType == EUserSessionType.GameClient)
						{
							string hwid_0 = data.ContainsKey("reserved_0") ? data["reserved_0"].ToString() : "NONE";
							string hwid_1 = data.ContainsKey("reserved_1") ? data["reserved_1"].ToString() : "NONE";
							string hwid_2 = data.ContainsKey("reserved_2") ? data["reserved_2"].ToString() : "NONE";
							await Database.UserDevices.RegisterUserDevice(db, user_id, hwid_0, hwid_1, hwid_2, ipAddr);
						}

						// ban check
						bool bIsBanned = await Database.Users.IsUserBanned(db, user_id);
						if (bIsBanned)
						{
							// kill every token they hold, not just this request
							await TokenRevocationManager.RevokeAllTokensForUser(user_id, "user is banned");

							result.result = EPendingLoginState.LoginFailed;
							Response.StatusCode = (int)HttpStatusCode.Locked;
							return result;
						}

						string exe_crc = data.ContainsKey("exe_crc") ? data["exe_crc"].ToString() : "NONE";
						Helpers.RegisterInitialPlayerExeCRC(user_id, exe_crc);

						string strDisplayName = await Database.Users.GetDisplayName(db, user_id);
						await SessionHelpers.SetUsedLoggedIn(user_id, clientID, sessionType);

						bool bIsAdmin = await Database.Users.IsUserAdmin(db, user_id);
						EUserPriority userPriority = await Database.Users.GetUserPriority(db, user_id);

						result.result = EPendingLoginState.LoginSuccess;

						// extend token
						// TODO_TODAY_JWT: just get clientID from token
						var sessiontoken = Program.g_tokenGenerator.GenerateToken(strDisplayName, user_id, ipAddr, Program.JwtTokenGenerator.ETokenType.Session, clientID, sessionType, bIsAdmin, userPriority);
						var refreshtoken = Program.g_tokenGenerator.GenerateToken(strDisplayName, user_id, ipAddr, Program.JwtTokenGenerator.ETokenType.Refresh, clientID, sessionType, false, EUserPriority.None, out string refreshJti);

						// rotation: only this refresh token is accepted from now on
						await TokenRevocationManager.OnTokensIssued(user_id, sessionType, refreshJti);

						result.session_token = sessiontoken;
						result.refresh_token = refreshtoken;

						result.user_id = user_id;
						result.display_name = strDisplayName;

						result.ws_uri = Program.GetWebSocketAddress(bSecureWS);

						// This endpoint re-establishes a session, so tear down any state the previous
						// one left behind (lobby membership, matchmaking, cached session data). Must
						// be awaited - the response below hands the client a ws_uri and it will
						// reconnect immediately, so a fire-and-forget teardown can race that new
						// session and destroy it. Clients that only need to rotate an expiring token
						// must use the RefreshToken endpoint instead, which leaves state intact.
						await WebSocketManager.ClearDataFromUser(user_id, sessionType);
					}
					else
					{
						result.result = EPendingLoginState.LoginFailed;
						Response.StatusCode = (int)HttpStatusCode.Unauthorized;
						return result;
					}
				}
			}
			catch
			{
				return result;
			}

			return result;
		}
	}
}
