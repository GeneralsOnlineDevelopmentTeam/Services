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
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public class RouteHandler_PUT_Lobbies_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public int result { get; set; } = 0;
		public Int64 lobby_id { get; set; } = -1;
		public string turn_username { get; set; } = String.Empty;
		public string turn_token { get; set; } = String.Empty;
	}

	public class RouteHandler_GET_Lobbies_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public List<Lobby>? lobbies { get; set; } = null;
		public List<int> latencies { get; set; } = new List<int>();

		public List<LatencyEntry> playerlatencies { get; set; } = new List<LatencyEntry>();
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class LobbiesController : ControllerBase
	{
		private readonly ILogger<LobbiesController> _logger;

		private readonly LobbyManager _lobbyManager;
		private readonly IDbContextFactory<AppDbContext> _dbFactory;


		public LobbiesController(LobbyManager lobbyManager, IDbContextFactory<AppDbContext> dbFactory, ILogger<LobbiesController> logger)
		{
			_logger = logger;
			_lobbyManager = lobbyManager;
			_dbFactory = dbFactory;
		}

		// FOR LATENCY ESTIMATIONS
		// Convert degrees to radians
		public static double ToRadians(double angleInDegrees)
		{
			return angleInDegrees * Math.PI / 180.0;
		}

		// Haversine formula for distance
		public static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
		{
			const double R = 6371; // Earth radius in kilometers

			double dLat = ToRadians(lat2 - lat1);
			double dLon = ToRadians(lon2 - lon1);

			double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
					   Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
					   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

			double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

			return R * c; // Distance in kilometers
		}

		// Estimate latency based on distance
		public static double EstimateLatency(double distanceKm)
		{
			// Speed of light in fiber ~200,000 km/s
			const double fiberSpeed = 200000.0;

			// Propagation delay (one-way)
			double propagationDelay = distanceKm / fiberSpeed;

			// Convert to milliseconds
			return propagationDelay * 1000;
		}
		// END LATENCY ESTIMATIONS

		[HttpGet(Name = "GetLobbies")]
		[Authorize(Policy = "AnyClientOrMonitorOrApiKey")]
		public async Task<APIResult> Get()
		{
			RouteHandler_GET_Lobbies_Result result = new RouteHandler_GET_Lobbies_Result();

			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				try
				{
					Int16 selectedRoomID = -1;
					bool bIncludeAllNetworkRooms = false;
					List<Lobby>? lstLobbies = null;
					List<int> lstLatencies = new();

					List<LatencyEntry> lstPlayerLatencies = new();

					Int64 user_id = TokenHelper.GetUserID(this);
					EUserSessionType sessionType = TokenHelper.GetSessionType(this);
					if (user_id != -1 && SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.ServerListReadOnly))
					{
						UserSession? sourceData = WebSocketManager.GetSessionFromUser(user_id, sessionType);

						if (sourceData != null)
						{
							selectedRoomID = sourceData.selectedNetworkRoomID;
						}
						else
						{
							bIncludeAllNetworkRooms = true;
						}

						lstLobbies = _lobbyManager.GetAllLobbies(selectedRoomID, true, true, false, false, bIncludeAllNetworkRooms);

						List<Lobby> lstLobbiesToRemove = new();

						// add latency to all
						// TODO_SOCIAL: Consider moving this to GetAllLobbies, same with joinability check
						foreach (Lobby lobby in lstLobbies)
						{
							// SOCIAL: If the lobby owner has source user blocked, remove the lobby
							SharedUserData? lobbyOwner = WebSocketManager.GetSharedDataForUser(lobby.Owner);

							if (lobbyOwner != null)
							{
								if (lobbyOwner.GetSocialContainer().Blocked.Contains(user_id))
								{
									lstLobbiesToRemove.Add(lobby);
									continue; // no need to calculate latency if we're removing it
								}

								// check joinability
								if (lobby.LobbyJoinability == ELobbyJoinability.FriendsOnly)
								{
                                    // If it's friends only, add to remove list if they aren't friends
                                    if (!lobbyOwner.GetSocialContainer().Friends.Contains(user_id))
                                    {
                                        lstLobbiesToRemove.Add(lobby);
                                        continue; // no need to calculate latency if we're removing it
                                    }
                                }
							}

							// calculate latency
							int largestLatency = 0;

							// per player latencies too
							foreach (LobbyMember member in lobby.Members)
							{
								if (sourceData != null && member.SlotState == EPlayerType.SLOT_PLAYER && member.UserID != sourceData.m_UserID) // dont need to ping ourselves...
								{
									if (member.GetSession().TryGetTarget(out UserSession? lobbyMemberSession))
									{
										double dDistance = HaversineDistance(sourceData.m_dLatitude, sourceData.m_dLongitude, lobbyMemberSession.m_dLatitude, lobbyMemberSession.m_dLongitude);
										double dEstimatedLatency = 2.0 * (EstimateLatency(dDistance) * 2.0);

										LatencyEntry latencyEntry = new();
										latencyEntry.user_id = member.UserID;
										latencyEntry.latency = Convert.ToInt32(dEstimatedLatency);

										largestLatency = Math.Max(largestLatency, latencyEntry.latency);

										lstPlayerLatencies.Add(latencyEntry);
									}
								}
							}

							// overall lobby latency (use the largest latency of all members, this is what the client will be bound by)
							lstLatencies.Add(largestLatency);
						}

						// SOCIAL: Now process the removes
						foreach (Lobby lobbyToRemove in lstLobbiesToRemove)
						{
							lstLobbies.Remove(lobbyToRemove);
						}
					}
					else if (this.User.IsInRole("Monitor"))
					{
						selectedRoomID = -1;
						bIncludeAllNetworkRooms = true;

						lstLobbies = _lobbyManager.GetAllLobbies(selectedRoomID, true, true, true, true, bIncludeAllNetworkRooms);
					}
					else
					{
						Response.StatusCode = (int)HttpStatusCode.InternalServerError;
					}


					result.lobbies = lstLobbies;
					result.latencies = lstLatencies;
					result.playerlatencies = lstPlayerLatencies;
				}
				catch
				{
					// TODO: Log this
					return result;
				}

				return result;
			}
		}

		[HttpPut(Name = "PutLobbies")]
		[Authorize(Roles = "GameClient")]
		public async Task<APIResult> Put()
		{
			RouteHandler_PUT_Lobbies_Result result = new RouteHandler_PUT_Lobbies_Result();

			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				};

				try
				{
					var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);

					if (data != null
					&& data.ContainsKey("name")
					&& data.ContainsKey("map_name")
					&& data.ContainsKey("map_path")
					&& data.ContainsKey("max_players")
					&& data.ContainsKey("preferred_port")
					&& data.ContainsKey("vanilla_teams")
					&& data.ContainsKey("track_stats")
					&& data.ContainsKey("starting_cash")
					&& data.ContainsKey("passworded")
					&& data.ContainsKey("password")
					&& data.ContainsKey("allow_observers")
					&& data.ContainsKey("max_cam_height")
					&& data.ContainsKey("exe_crc")
					&& data.ContainsKey("ini_crc")
					&& data.ContainsKey("anticheat_id")
					)
					{
						string? strName = data["name"].GetString();
						string? strMapName = data["map_name"].GetString();
						string? strMapPath = data["map_path"].GetString();
						bool bMapOfficial = data["map_official"].GetBoolean();
						int maxPlayers = data["max_players"].GetInt32();
						UInt16 hostPreferredPort = data["preferred_port"].GetUInt16();
						bool bVanillaTeamsOnly = data["vanilla_teams"].GetBoolean();
						bool bTrackStats = data["track_stats"].GetBoolean();
						UInt32 starting_cash = data["starting_cash"].GetUInt32();
						bool bPassworded = data["passworded"].GetBoolean();
						string? strPassword = data["password"].GetString();
						bool bAllowObservers = data["allow_observers"].GetBoolean();
						UInt16 maxCamHeight = Convert.ToUInt16(data["max_cam_height"].GetDouble()); // client sends this as a float...
						UInt32 exe_crc = data["exe_crc"].GetUInt32();
						UInt32 ini_crc = data["ini_crc"].GetUInt32();
						EKnownAnticheatID anticheatID = (EKnownAnticheatID)data["anticheat_id"].GetInt32();

						// Input validation
						if (strName != null && strName.Length > 255)
						{
							Response.StatusCode = (int)HttpStatusCode.BadRequest;
							return result;
						}
						if (strMapName != null && strMapName.Length > 255)
						{
							Response.StatusCode = (int)HttpStatusCode.BadRequest;
							return result;
						}
						if (strMapPath != null && strMapPath.Length > 512)
						{
							Response.StatusCode = (int)HttpStatusCode.BadRequest;
							return result;
						}
						if (strPassword != null && strPassword.Length > 128)
						{
							Response.StatusCode = (int)HttpStatusCode.BadRequest;
							return result;
						}



						// get requesting user data from session token
						Int64 user_id = TokenHelper.GetUserID(this);
						EUserSessionType sessionType = TokenHelper.GetSessionType(this);

						// check nullables also
						if (user_id != -1 && strName != null && strMapName != null && strMapPath != null && strPassword != null && SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.Gameplay))
						{
							// TODO: Handle failure here
							// TODO_ASP: Remove ip address from db, not needed
							string strIPAddr = "";

							UserSession playerSession = WebSocketManager.GetSessionFromUser(user_id, sessionType);

							if (playerSession != null)
							{
								// cleanup any zombie lobbies
								await _lobbyManager.CleanupUserLobbiesNotStarted(user_id);

								await using var db = await _dbFactory.CreateDbContextAsync();
								string strDisplayName = await Database.Users.GetDisplayName(db, user_id);

								Int64 newLobbyID = await _lobbyManager.CreateLobby(db, playerSession, strDisplayName, strName, strMapName, strMapPath, bMapOfficial, maxPlayers, strIPAddr,
									hostPreferredPort, bVanillaTeamsOnly, bTrackStats, starting_cash, bPassworded, strPassword, playerSession.networkRoomID, bAllowObservers, maxCamHeight, exe_crc, ini_crc, ELobbyType.CustomGame, anticheatID);

								if (newLobbyID >= 0)
								{
									result.result = 1;
									result.lobby_id = newLobbyID;

									// TODO: What if this fails? just let them proceed? just means only direct connect people will be able to play
									// get some turn credentials
									TURNCredentialContainer? turnCredentials = await TURNCredentialManager.CreateCredentialsForUser(user_id);
									if (turnCredentials != null)
									{
										result.turn_username = turnCredentials.m_strUsername;
										result.turn_token = turnCredentials.m_strToken;
									}
								}
								else
								{
									Response.StatusCode = (int)HttpStatusCode.InternalServerError;
								}
							}
						}
					}
				}
				catch
				{
					return result;
				}
			}

			return result;
		}
	}
}
