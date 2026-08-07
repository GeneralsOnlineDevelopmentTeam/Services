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

using Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MySqlX.XDevAPI.Common;
using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{

	public class POST_CheckLogin_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(POST_CheckLogin_Result);
		}

		public EPendingLoginState result { get; set; } = EPendingLoginState.None;
		public string session_token { get; set; } = "";
		public string refresh_token { get; set; } = "";
		public Int64 user_id { get; set; } = -1;
		public string display_name { get; set; } = "";

		public string ws_uri { get; set; } = "";
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class CheckLoginController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> _dbFactory;

		public CheckLoginController(IDbContextFactory<AppDbContext> dbFactory)
		{
			_dbFactory = dbFactory;
		}

		[HttpPost]
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

				POST_CheckLogin_Result result = (POST_CheckLogin_Result)await Post_InternalHandler(jsonData, IPHelpers.NormalizeIP(HttpContext.Connection.RemoteIpAddress?.ToString()), bSecureWS);
				return result;
			}
		}

		public async Task<APIResult> Post_InternalHandler(string jsonData, string ipAddr, bool bSecureWS, bool bIsMonitor = false)
		{
			POST_CheckLogin_Result result = new POST_CheckLogin_Result();

			// Must have an IP...
			if (bIsMonitor)
			{
				ipAddr = IPAddress.Loopback.ToString();
			}

			var options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			};

			try
			{
				var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);

				if (data != null && !data.ContainsKey("client_id"))
				{
					result.result = EPendingLoginState.LoginFailed;
					Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				}
				else
				{
					if (data != null && data.ContainsKey("code"))
					{
						if (data != null && data.ContainsKey("code") && data.ContainsKey("client_id"))
						{
							await using var db = await _dbFactory.CreateDbContextAsync();

							//byte[] respNonce = new byte[32];
							//using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) { rng.GetBytes(respNonce); }

							string? gameCode = data["code"].GetString();

							if (gameCode == null)
							{
								Response.StatusCode = (int)HttpStatusCode.InternalServerError;
								return result;
							}
#if DEBUG
							// Dev/test login: every CheckLogin attempt gets a fresh, unique random
							// user so multiple test clients can hold distinct sessions on the same
							// service instance at the same time. Skips the pending_login table.
							EPendingLoginState state = EPendingLoginState.LoginSuccess;
							UInt32 user_id = 0;
							string strDisplayName = String.Empty;

							byte[] randBytes = new byte[4];
							using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
							{
								rng.GetBytes(randBytes);
							}
							user_id = BitConverter.ToUInt32(randBytes, 0) & 0x7FFFFFFF;
							strDisplayName = String.Format("DEV_ACCOUNT_{0}", user_id);

							// make user
							await Database.Users.CreateUserIfNotExists_DevAccount(db, user_id, strDisplayName);

							bool bIsAdmin = await Database.Users.IsUserAdmin(db, user_id);
#else
							EPendingLoginState? loginState = await Database.PendingLogins.GetPendingLoginState(db, gameCode.ToUpper());

							if (loginState != null)
							{
									EPendingLoginState state = loginState.Value;

									Int64 user_id = await Database.PendingLogins.GetUserIDFromPendingLogin(db, gameCode);
									string strDisplayName = await Database.Users.GetDisplayName(db, user_id);

								bool bIsAdmin = await Database.Users.IsUserAdmin(db, user_id);
#endif

							if (state == EPendingLoginState.Waiting)
							{
								result.result = EPendingLoginState.Waiting;
							}
							else if (state == EPendingLoginState.LoginSuccess)
							{
								// create a session
								string? clientID = data["client_id"].GetString();

								if (clientID != null && Program.g_tokenGenerator != null)
								{
									// ban check
									bool bIsBanned = await Database.Users.IsUserBanned(db, user_id);
									if (bIsBanned)
									{
										result.result = EPendingLoginState.LoginFailed;
										Response.StatusCode = (int)HttpStatusCode.Locked;
										return result;
									}

									// full login (known clients)
									if (Enum.TryParse(typeof(KnownClients.EKnownClients), clientID, ignoreCase: true, out object knownClientIDObj))
									{
										KnownClients.EKnownClients knownClientID = (KnownClients.EKnownClients)knownClientIDObj;
										EUserSessionType sessionType = KnownClients.KnownClientSessionTypes[knownClientID];

										// Game clients should register the user device
										if (sessionType == EUserSessionType.GameClient)
										{
											string hwid_0 = data.ContainsKey("reserved_0") ? data["reserved_0"].ToString() : "NONE";
											string hwid_1 = data.ContainsKey("reserved_1") ? data["reserved_1"].ToString() : "NONE";
											string hwid_2 = data.ContainsKey("reserved_2") ? data["reserved_2"].ToString() : "NONE";
											await Database.UserDevices.RegisterUserDevice(db, user_id, hwid_0, hwid_1, hwid_2, ipAddr);
										}

										string exe_crc = data.ContainsKey("exe_crc") ? data["exe_crc"].ToString() : "NONE";
										Helpers.RegisterInitialPlayerExeCRC(user_id, exe_crc);

										var sessiontoken = Program.g_tokenGenerator.GenerateToken(strDisplayName, user_id, ipAddr, Program.JwtTokenGenerator.ETokenType.Session, knownClientID, sessionType, bIsAdmin);
										var refreshtoken = Program.g_tokenGenerator.GenerateToken(strDisplayName, user_id, ipAddr, Program.JwtTokenGenerator.ETokenType.Refresh, knownClientID, sessionType, false);

										result.result = EPendingLoginState.LoginSuccess;
										result.session_token = sessiontoken;
										result.refresh_token = refreshtoken;
										result.user_id = user_id;
										result.display_name = strDisplayName;
										result.ws_uri = Program.GetWebSocketAddress(bSecureWS);

										// clear cached data, its a refresh websocket connection
										WebSocketManager.ClearDataFromUser(user_id, sessionType);
									}
									else // limited login (auth partners)
									{
										result.result = EPendingLoginState.LoginSuccess;
										result.session_token = null;
										result.refresh_token = null;
										result.user_id = user_id;
										result.display_name = strDisplayName;
										result.ws_uri = null;
									}

									await Database.PendingLogins.CleanupPendingLogin(db, gameCode);

									return result;
								}
								else
								{
									result.result = EPendingLoginState.LoginFailed;
									Response.StatusCode = (int)HttpStatusCode.Forbidden;
									return result;
								}
							}
							else if (state == EPendingLoginState.LoginFailed)
							{
								result.result = EPendingLoginState.LoginFailed;
								Response.StatusCode = (int)HttpStatusCode.Forbidden;
								await Database.PendingLogins.CleanupPendingLogin(db, gameCode);
							}
#if !DEBUG
							}
#endif
						}
						else
						{
							// TODO: Log this
							result.result = EPendingLoginState.LoginFailed;
							Response.StatusCode = (int)HttpStatusCode.Forbidden;
							return result;
						}
					}
				}
			}
			catch
			{
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return result;
			}

			return result;
		}
	}
}
