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
		public async Task<APIResult> Post(
			[FromRoute] string environment,
			[FromRoute(Name = "contract_version")] string contractVersion)
		{
			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();

				POST_CheckLogin_Result result = (POST_CheckLogin_Result)await Post_InternalHandler(
					jsonData,
					IPHelpers.NormalizeIP(HttpContext.Connection.RemoteIpAddress?.ToString()),
					Program.BuildWebSocketUrl(Request, environment, contractVersion));
				return result;
			}
		}

		public async Task<APIResult> Post_InternalHandler(string jsonData, string ipAddr, string webSocketUrl, bool bIsMonitor = false)
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

							// in DEBUG, just accept any token for dev...
#if DEBUG
							{
								Int64 highestIDFound = 0;
								// which account should we use?
								var sessions = WebSocketManager.GetUserDataCache();
								foreach (var sessionDataByClient in sessions)
								{
									foreach (var sessionData in sessionDataByClient.Value)
									{
										UserSession sessIter = sessionData.Value;
										if (sessIter.m_UserID > highestIDFound)
										{
											highestIDFound = (Int64)sessIter.m_UserID;
										}
									}
								}

								// for dev, it wont have a user_id because it didnt go through the normal flow, so make one
								Int64 user_id = highestIDFound + 1;

								bool bTestSPOP = false;
								if (bTestSPOP)
								{
									user_id = 0;
								}
								string strDisplayName = String.Format("DEV_ACCOUNT_{0}", Math.Abs(user_id) - 1);


								// make user
								await Database.Users.CreateUserIfNotExists_DevAccount(db, user_id, strDisplayName);

								// for dev, just mark it as logged in, code further down will consume it
								PendingLoginManager.UpdatePendingLogin(gameCode, EPendingLoginState.LoginSuccess, user_id);
							}
#endif

							PendingLoginEntry loginEntry = PendingLoginManager.GetPendingLogin(gameCode);

							if (loginEntry != null)
							{
								if (loginEntry.state == EPendingLoginState.Waiting)
								{
									result.result = EPendingLoginState.Waiting;
								}
								else if (loginEntry.state == EPendingLoginState.LoginSuccess)
								{
									// consume
									PendingLoginManager.ConsumePendingLogin(gameCode);

									// create a session
									string? clientID = data["client_id"].GetString();

									if (clientID != null && Program.g_tokenGenerator != null)
									{
										Int64 user_id = loginEntry.user_id;

										// ban check
										bool bIsBanned = await Database.Users.IsUserBanned(db, user_id);
										if (bIsBanned)
										{
											await TokenRevocationManager.RevokeAllTokensForUser(user_id, "user is banned");

											result.result = EPendingLoginState.LoginFailed;
											Response.StatusCode = (int)HttpStatusCode.Locked;
											return result;
										}

										string strDisplayName = await Database.Users.GetDisplayName(db, user_id);

										bool bIsAdmin = await Database.Users.IsUserAdmin(db, user_id);

										// full login (known clients)
										// Enum.TryParse also accepts "unknown" and arbitrary numeric strings, neither of which is mapped.
										if (Enum.TryParse(clientID, ignoreCase: true, out KnownClients.EKnownClients knownClientID)
											&& KnownClients.KnownClientSessionTypes.TryGetValue(knownClientID, out EUserSessionType sessionType))
										{
											// Game clients should register the user device
											if (sessionType == EUserSessionType.GameClient)
											{
												string hwid_0 = data.ContainsKey("machine_guid") ? data["machine_guid"].ToString() : "NONE";
												string hwid_1 = data.ContainsKey("mac_addr") ? data["mac_addr"].ToString() : "NONE";
												string hwid_2 = data.ContainsKey("vol_serial") ? data["vol_serial"].ToString() : "NONE";
												await Database.UserDevices.RegisterUserDevice(db, user_id, hwid_0, hwid_1, hwid_2, ipAddr);
											}

											string exe_crc = data.ContainsKey("exe_crc") ? data["exe_crc"].ToString() : "NONE";
											Helpers.RegisterInitialPlayerExeCRC(user_id, exe_crc);

											var sessiontoken = Program.g_tokenGenerator.GenerateToken(strDisplayName, user_id, ipAddr, Program.JwtTokenGenerator.ETokenType.Session, knownClientID, sessionType, bIsAdmin);
											var refreshtoken = Program.g_tokenGenerator.GenerateToken(strDisplayName, user_id, ipAddr, Program.JwtTokenGenerator.ETokenType.Refresh, knownClientID, sessionType, false, out string refreshJti);

											// rotation: only this refresh token is accepted from now on
											await TokenRevocationManager.OnTokensIssued(user_id, sessionType, refreshJti);

											result.result = EPendingLoginState.LoginSuccess;
											result.session_token = sessiontoken;
											result.refresh_token = refreshtoken;
											result.user_id = user_id;
											result.display_name = strDisplayName;
											result.ws_uri = webSocketUrl;

											// clear cached data, its a new session and the client reconnects its
											// websocket using the ws_uri below - must be awaited so the teardown
											// can't race and destroy that new session
											await WebSocketManager.ClearDataFromUser(user_id, sessionType);
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


										return result;
									}
									else
									{
										result.result = EPendingLoginState.LoginFailed;
										Response.StatusCode = (int)HttpStatusCode.Forbidden;
										return result;
									}
								}
								else if (loginEntry.state == EPendingLoginState.LoginFailed)
								{
									// consume
									PendingLoginManager.ConsumePendingLogin(gameCode);

									result.result = EPendingLoginState.LoginFailed;
									Response.StatusCode = (int)HttpStatusCode.Forbidden;
								}
							}
							else
							{
								result.result = EPendingLoginState.LoginFailed;
								Response.StatusCode = (int)HttpStatusCode.Forbidden;
								return result;
							}
						}
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
			catch
			{
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return result;
			}

			return result;
		}
	}
}
