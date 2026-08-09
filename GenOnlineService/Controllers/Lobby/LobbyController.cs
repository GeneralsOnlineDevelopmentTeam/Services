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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
public class LatencyEntry
{
	public Int64 user_id { get; set; }
	public int latency { get; set; }
}

public class MissingConnectionEntry
{
	public Int64 source_user_id { get; set; }
	public Int64 target_user_id { get; set; }
}


namespace GenOnlineService.Controllers
{

	public class RouteHandler_GET_Lobby_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public Lobby? lobby { get; set; } = null;
	}

	public class RouteHandler_DELETE_Lobby_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public bool success { get; set; } = false;
	}

	public class RouteHandler_POST_Outcome_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public string screenshot_url { get; set; } = String.Empty;
		public string replay_url { get; set; } = String.Empty;
	}

	public class RouteHandler_Get_MatchHistory_HighestMatchID_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public Int64 highest_match_id { get; set; } = -1;
	}

	public class RouteHandler_Get_MatchHistory_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public MatchHistoryCollection matches { get; set; } = new();
	}

	public class RouteHandler_POST_Lobby_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public bool success { get; set; } = false;
	}

    public enum ELobbyJoinability
    {
        Public,
		FriendsOnly
	};

    enum ELobbyUpdateField
	{
		LOBBY_MAP = 0,
		MY_SIDE = 1,
		MY_COLOR = 2,
		MY_START_POS = 3,
		MY_TEAM = 4,
		LOBBY_STARTING_CASH = 5,
		LOBBY_LIMIT_SUPERWEAPONS = 6,
		HOST_ACTION_FORCE_START = 7,
		LOCAL_PLAYER_HAS_MAP = 8,
		UNUSED = 9,
		UNUSED_2 = 10,
		HOST_ACTION_KICK_USER = 11,
		HOST_ACTION_SET_SLOT_STATE = 12,
		AI_SIDE = 13,
		AI_COLOR = 14,
		AI_TEAM = 15,
		AI_START_POS = 16,
		MAX_CAMERA_HEIGHT = 17,
        JOINABILITY = 18,
		HOST_ACTION_BULK_SLOT_UPDATE = 19
    };

	public class RouteHandler_PUT_Lobby_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public bool success { get; set; } = false;
		public string turn_username { get; set; } = String.Empty;
		public string turn_token { get; set; } = String.Empty;
	}


	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class LobbyController : ControllerBase
	{
		private readonly ILogger<LobbiesController> _logger;
		private readonly LobbyManager _lobbyManager;
		private readonly IDbContextFactory<AppDbContext> _dbFactory;

		public LobbyController(LobbyManager lobbyManager, IDbContextFactory<AppDbContext> dbFactory, ILogger<LobbiesController> logger)
		{
			_logger = logger;
			_lobbyManager = lobbyManager;
			_dbFactory = dbFactory;
		}

		[HttpGet("{lobby_id}")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher,Monitor")]
		public async Task<APIResult> Get(string lobby_id)
		{
			RouteHandler_GET_Lobby_Result result = new RouteHandler_GET_Lobby_Result();

			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				};

				try
				{
					// need a lobby ID
					if (Int64.TryParse(lobby_id, out Int64 lobbyID))
					{
						Lobby? lobby = _lobbyManager.GetLobby(lobbyID);
						result.lobby = lobby;
					}

				}
				catch
				{
					return result;
				}

				if (result.lobby == null)
				{
					// monitor cant handle a 404
					if (!this.User.IsInRole("Monitor"))
					{
						Response.StatusCode = (int)HttpStatusCode.NotFound;
					}
				}
			}

			return result;
		}

		[HttpDelete("{lobbyID}")]
		[Authorize(Roles = "GameClient")]
		public async Task<APIResult> Delete(Int64 lobbyID)
		{
			RouteHandler_DELETE_Lobby_Result result = new RouteHandler_DELETE_Lobby_Result();

			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				};

				try
				{
					// TODO: Dont let them join more than 1 lobby
					// need a lobby ID
					int leavingPersonSlot = -1;
					Int64 user_id = TokenHelper.GetUserID(this);
					EUserSessionType sessionType = TokenHelper.GetSessionType(this);
					if (user_id != -1 && SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.Gameplay))
					{
						Lobby? lobby = _lobbyManager.GetLobby(lobbyID);
						if (lobby != null)
						{
							foreach (var member in lobby.Members)
							{
								if (member.SlotState == EPlayerType.SLOT_PLAYER)
								{
									if (member.UserID == user_id)
									{
										leavingPersonSlot = member.SlotIndex;
									}
								}
							}
						}

						Console.WriteLine("[Source 1] User {0} Leave Any Lobby", user_id);
						_lobbyManager.LeaveAnyLobby(user_id);

						// cleanup TURN credentials
						TURNCredentialManager.DeleteCredentialsForUser(user_id);

						// clear our lobby ID
						UserSession? sourceData = WebSocketManager.GetSessionFromUser(user_id, sessionType);

						if (sourceData != null)
						{
							sourceData.UpdateSessionLobbyID(-1);
							// NOTE: We dont update the match history match ID here, that is done by the match history service
						}

						result.success = true;
					}

				}
				catch
				{
					return result;
				}
			}

			return result;
		}

		[HttpPost("Outcome")]
		[Authorize(Roles = "GameClient")]
		public async Task<APIResult?> PostOutcome()
		{
			RouteHandler_POST_Outcome_Result result = new RouteHandler_POST_Outcome_Result();

			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true,
					MaxDepth = 32
				};

				try
				{
					var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);

					if (data != null
						&& data.ContainsKey("match_id")
						&& data.ContainsKey("buildings_built")
						&& data.ContainsKey("buildings_killed")
						&& data.ContainsKey("buildings_lost")
						&& data.ContainsKey("units_built")
						&& data.ContainsKey("units_killed")
						&& data.ContainsKey("units_lost")
						&& data.ContainsKey("total_money")
						&& data.ContainsKey("won")
						&& data.ContainsKey("side")
						)
					{
						Int64 user_id = TokenHelper.GetUserID(this);
						EUserSessionType sessionType = TokenHelper.GetSessionType(this);
						if (user_id != -1 && SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.Gameplay))
						{
							UserSession? sourceData = WebSocketManager.GetSessionFromUser(user_id, sessionType);
							if (sourceData != null)
							{
								int buildings_built = data["buildings_built"].GetInt32();
								int buildings_killed = data["buildings_killed"].GetInt32();
								int buildings_lost = data["buildings_lost"].GetInt32();
								int units_built = data["units_built"].GetInt32();
								int units_killed = data["units_killed"].GetInt32();
								int units_lost = data["units_lost"].GetInt32();
								int total_money = data["total_money"].GetInt32();
								int side = data["side"].GetInt32();
								bool won = data["won"].GetBoolean();
								UInt64 match_id = data["match_id"].GetUInt64();

								// were they really in the match they claim to be in?
								if (!sourceData.WasPlayerInMatch(match_id, out int slotIndexInLobby, out int army, out DateTime LobbyCreationTime))
								{
									Response.StatusCode = (int)HttpStatusCode.Unauthorized;
									return null;
								}

								// register with daily stats
								DailyStatsManager.RegisterOutcome(army, won);

								// give them back signed URLs they need
								result.screenshot_url = await S3CredentialManager.GetPresignedURL(EMetadataFileType.FILE_TYPE_SCREENSHOT, EScreenshotType.SCREENSHOT_TYPE_SCORESCREEN, match_id, user_id, slotIndexInLobby, LobbyCreationTime) ?? String.Empty;
								result.replay_url = await S3CredentialManager.GetPresignedURL(EMetadataFileType.FILE_TYPE_REPLAY, EScreenshotType.NONE, match_id, user_id, slotIndexInLobby, LobbyCreationTime) ?? String.Empty;

								// store in DB
								await using var db = await _dbFactory.CreateDbContextAsync();
								await Database.MatchHistory.CommitPlayerOutcome(db, slotIndexInLobby, match_id, side,
										buildings_built, buildings_killed, buildings_lost, units_built, units_killed, units_lost, total_money, won);
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

		enum ELobbyUpdatePermissions
		{
			Anyone,
			LobbyOwner
		}

		private static ConcurrentDictionary<ELobbyUpdateField, ELobbyUpdatePermissions> g_dictLobbyUpdatePermissionsTable = new()
		{
			[ELobbyUpdateField.LOBBY_MAP] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.MY_SIDE] = ELobbyUpdatePermissions.Anyone,
			[ELobbyUpdateField.MY_COLOR] = ELobbyUpdatePermissions.Anyone,
			[ELobbyUpdateField.MY_START_POS] = ELobbyUpdatePermissions.Anyone,
			[ELobbyUpdateField.MY_TEAM] = ELobbyUpdatePermissions.Anyone,
			[ELobbyUpdateField.LOBBY_STARTING_CASH] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.LOBBY_LIMIT_SUPERWEAPONS] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.HOST_ACTION_FORCE_START] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.LOCAL_PLAYER_HAS_MAP] = ELobbyUpdatePermissions.Anyone,
			[ELobbyUpdateField.UNUSED] = ELobbyUpdatePermissions.Anyone,
			[ELobbyUpdateField.UNUSED_2] = ELobbyUpdatePermissions.Anyone,
			[ELobbyUpdateField.HOST_ACTION_KICK_USER] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.HOST_ACTION_SET_SLOT_STATE] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.AI_SIDE] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.AI_COLOR] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.AI_TEAM] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.AI_START_POS] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.MAX_CAMERA_HEIGHT] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.JOINABILITY] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.HOST_ACTION_BULK_SLOT_UPDATE] = ELobbyUpdatePermissions.LobbyOwner
		};


		[HttpPost("{lobbyID}")]
		[Authorize(Roles = "GameClient")]
		public async Task<APIResult> Post(Int64 lobbyID)
		{
			RouteHandler_POST_Lobby_Result result = new RouteHandler_POST_Lobby_Result();

			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				};

				try
				{
					// TODO: Dont let them join more than 1 lobby

					var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);

					if (data != null
						&& data.ContainsKey("field")
						)
					{
						// TODO_NGMP: For all things which modify lobbies, we should check if the player is really in the lobby
						// get requesting user
						Int64 user_id = TokenHelper.GetUserID(this);
						if (user_id != -1)
						{
							Lobby? lobby = _lobbyManager.GetLobby(lobbyID);

							if (lobby != null)
							{
								// check the user is in the lobby, otherwise bail
								LobbyMember? SourceMember = lobby.GetMemberFromUserID(user_id);

								if (SourceMember == null)
								{
									Response.StatusCode = (int)HttpStatusCode.Unauthorized;
									return result;
								}

								// TODO: Safety
								ELobbyUpdateField field = (ELobbyUpdateField)data["field"].GetInt32();

								// check permissions
								ELobbyUpdatePermissions updatePerms = g_dictLobbyUpdatePermissionsTable[field];

								if (updatePerms == ELobbyUpdatePermissions.LobbyOwner) // check owner
								{
									if (user_id != lobby.Owner)
									{
										Response.StatusCode = (int)HttpStatusCode.Unauthorized;
										result.success = false;
										return result;
									}
								}

								// reset everyones ready states when anything changes (minus dummy actions)
								if (field != ELobbyUpdateField.HOST_ACTION_FORCE_START
									&& field != ELobbyUpdateField.LOCAL_PLAYER_HAS_MAP
									&& field != ELobbyUpdateField.HOST_ACTION_KICK_USER)
								{
									lobby.ResetReadyStates();
								}

								if (field == ELobbyUpdateField.LOBBY_MAP)
								{
									if (data.ContainsKey("map")
										&& data.ContainsKey("map_path")
										&& data.ContainsKey("max_players")
										)
									{
										string? strMap = data["map"].GetString();
										string? strMapPath = data["map_path"].GetString();
										bool bOfficialMap = data["map_official"].GetBoolean();
										int maxPlayers = data["max_players"].GetInt32();

										if (strMap != null && strMapPath != null)
										{
											await using var db = await _dbFactory.CreateDbContextAsync();
											await lobby.UpdateMap(db, strMap, strMapPath, bOfficialMap, maxPlayers);
										}
									}
								}
								else if (field == ELobbyUpdateField.MY_SIDE)
								{
									if (data.ContainsKey("side")
										&& data.ContainsKey("start_pos")
										)
									{
										int side = data["side"].GetInt32();
										int start_pos = data["start_pos"].GetInt32();

										await using var db = await _dbFactory.CreateDbContextAsync();
										await SourceMember.UpdateSide(db, side, start_pos);
									}
								}
								else if (field == ELobbyUpdateField.MY_COLOR)
								{
									if (data.ContainsKey("color"))
									{
										int color = data["color"].GetInt32();

										await using var db = await _dbFactory.CreateDbContextAsync();
										await SourceMember.UpdateColor(db, color);
									}
								}
								else if (field == ELobbyUpdateField.MY_START_POS)
								{
									if (data.ContainsKey("startpos"))
									{
										int startpos = data["startpos"].GetInt32();
										SourceMember.UpdateStartPos(startpos);
									}
								}
								else if (field == ELobbyUpdateField.MY_TEAM)
								{
									if (data.ContainsKey("team"))
									{
										int team = data["team"].GetInt32();
										SourceMember.UpdateTeam(team);
									}
								}
								else if (field == ELobbyUpdateField.LOBBY_STARTING_CASH)
								{
									if (data.ContainsKey("startingcash"))
									{
										UInt32 startingCash = data["startingcash"].GetUInt32();

										await using var db = await _dbFactory.CreateDbContextAsync();
										await lobby.UpdateStartingCash(db, startingCash);
									}
								}
								else if (field == ELobbyUpdateField.LOBBY_LIMIT_SUPERWEAPONS)
								{
									if (data.ContainsKey("limit_superweapons"))
									{
										bool bLimitSuperweapons = data["limit_superweapons"].GetBoolean();

										await using var db = await _dbFactory.CreateDbContextAsync();
										await lobby.UpdateLimitSuperweapons(db, bLimitSuperweapons);
									}
								}
								else if (field == ELobbyUpdateField.HOST_ACTION_FORCE_START)
								{
									// dummy action... just force everyone ready
									lobby.ForceReady();
								}
								else if (field == ELobbyUpdateField.LOCAL_PLAYER_HAS_MAP)
								{
									if (data.ContainsKey("has_map"))
									{
										bool bHasMap = data["has_map"].GetBoolean();

										SourceMember.UpdateHasMap(bHasMap);
									}
								}
								else if (field == ELobbyUpdateField.HOST_ACTION_KICK_USER)
								{
									if (data.ContainsKey("userid"))
									{
										// TODO: we should communicate the kick to the user...
										Int64 KickedUserID = data["userid"].GetInt64();

										_lobbyManager.LeaveSpecificLobby(KickedUserID, lobbyID);

										// cleanup TURN credentials
										TURNCredentialManager.DeleteCredentialsForUser(KickedUserID);

										// clear our lobby ID
										UserSession? sourceData = WebSocketManager.GetSessionFromUser(KickedUserID, EUserSessionType.GameClient); // user being kicked must be a game client

										if (sourceData != null)
										{
											sourceData.UpdateSessionLobbyID(-1);
											// NOTE: We dont update the match history match ID here, that is done by the match history service
										}

										// we have to manually send to the kicked user... they won't get the dirty lobby update anymore
										await lobby.DirtyRetransmitToSingleMember(KickedUserID);
									}
								}
								else if (field == ELobbyUpdateField.HOST_ACTION_SET_SLOT_STATE)
								{
									UInt16 slot_index = data["slot_index"].GetUInt16();
									EPlayerType slot_state = (EPlayerType)data["slot_state"].GetUInt16();

									LobbyMember? TargetMember = lobby.GetMemberFromSlot(slot_index);
									if (TargetMember != null)
									{
										TargetMember.SetPlayerSlotState(slot_state);
									}
								}
								else if (field == ELobbyUpdateField.AI_SIDE)
								{
									if (data.ContainsKey("slot")
										&& data.ContainsKey("side")
										&& data.ContainsKey("start_pos")
										)
									{
										int slot = data["slot"].GetInt32();
										int side = data["side"].GetInt32();
										int start_pos = data["start_pos"].GetInt32();

										LobbyMember? TargetMember = lobby.GetMemberFromSlot(slot);
										if (TargetMember != null)
										{
											if (TargetMember.IsAI())
											{
												await using var db = await _dbFactory.CreateDbContextAsync();
												await TargetMember.UpdateSide(db, side, start_pos);
											}
										}
									}
								}
								else if (field == ELobbyUpdateField.AI_COLOR)
								{
									if (data.ContainsKey("slot")
										&& data.ContainsKey("color"))
									{
										int slot = data["slot"].GetInt32();
										int color = data["color"].GetInt32();

										LobbyMember? TargetMember = lobby.GetMemberFromSlot(slot);
										if (TargetMember != null)
										{
											if (TargetMember.IsAI())
											{
												await using var db = await _dbFactory.CreateDbContextAsync();
												await TargetMember.UpdateColor(db, color);
											}
										}
									}
								}
								else if (field == ELobbyUpdateField.AI_TEAM)
								{
									if (data.ContainsKey("slot")
										&& data.ContainsKey("team"))
									{
										int slot = data["slot"].GetInt32();
										int team = data["team"].GetInt32();

										LobbyMember? TargetMember = lobby.GetMemberFromSlot(slot);
										if (TargetMember != null)
										{
											if (TargetMember.IsAI())
											{
												TargetMember.UpdateTeam(team);
											}
										}
									}
								}
								else if (field == ELobbyUpdateField.AI_START_POS)
								{
									if (data.ContainsKey("slot")
										&& data.ContainsKey("start_pos"))
									{

										int slot = data["slot"].GetInt32();
										int start_pos = data["start_pos"].GetInt32();

										LobbyMember? TargetMember = lobby.GetMemberFromSlot(slot);
										if (TargetMember != null)
										{
											if (TargetMember.IsAI())
											{
												TargetMember.UpdateStartPos(start_pos);
											}
										}
									}
								}
								else if (field == ELobbyUpdateField.MAX_CAMERA_HEIGHT)
								{
									if (data.ContainsKey("max_camera_height"))
									{
										UInt16 maxCameraHeight = data["max_camera_height"].GetUInt16();
										lobby.UpdateMaxCameraHeight(maxCameraHeight);
									}
								}
								else if (field == ELobbyUpdateField.JOINABILITY)
								{
									ELobbyJoinability newLobbyJoinability = (ELobbyJoinability)data["joinability"].GetInt32();
									lobby.UpdateJoinability(newLobbyJoinability);
								}
								else if (field == ELobbyUpdateField.HOST_ACTION_BULK_SLOT_UPDATE)
								{
									if (data.ContainsKey("slots"))
									{
										await using var db = await _dbFactory.CreateDbContextAsync();
										foreach (JsonElement slotEntry in data["slots"].EnumerateArray())
										{
											try
											{
												if (!slotEntry.TryGetProperty("slot_index", out var slotIndexProp) ||
													!slotEntry.TryGetProperty("side", out var sideProp) ||
													!slotEntry.TryGetProperty("color", out var colorProp) ||
													!slotEntry.TryGetProperty("start_pos", out var startPosProp) ||
													!slotEntry.TryGetProperty("team", out var teamProp))
												{
													continue;
												}

												int slotIndex = slotIndexProp.GetInt32();
												int side = sideProp.GetInt32();
												int color = colorProp.GetInt32();
												int start_pos = startPosProp.GetInt32();
												int team = teamProp.GetInt32();

												LobbyMember? TargetMember = lobby.GetMemberFromSlot(slotIndex);
												if (TargetMember != null)
												{
													await TargetMember.UpdateSide(db, side, start_pos);
													await TargetMember.UpdateColor(db, color);
													TargetMember.UpdateStartPos(start_pos);
													TargetMember.UpdateTeam(team);
												}
											}
											catch
											{
												continue;
											}
										}
										lobby.DirtyRetransmit();
									}
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

		[HttpPut("{lobbyID}")]
		[Authorize(Roles = "GameClient")]
		public async Task<APIResult> Put(Int64 lobbyID)
		{
			RouteHandler_PUT_Lobby_Result result = new RouteHandler_PUT_Lobby_Result();

			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();

				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				};

				try
				{
					// TODO: Dont let them join more than 1 lobby

					var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);

					if (data != null
						&& data.ContainsKey("preferred_port")
						&& data.ContainsKey("anticheat_id")
						)
					{

						Lobby? lobby = _lobbyManager.GetLobby(lobbyID);

						if (lobby != null)
						{
							Int64 user_id = TokenHelper.GetUserID(this);
							EUserSessionType sessionType = TokenHelper.GetSessionType(this);
							if (user_id != -1 && SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.Gameplay))
							{
								UInt16 userPreferredPort = data["preferred_port"].GetUInt16();
								bool bHasMap = data["has_map"].GetBoolean();
								EKnownAnticheatID anticheatID = (EKnownAnticheatID)data["anticheat_id"].GetInt32();

								// does the lobby have a password?
								bool bLobbyPassworded = lobby.IsPassworded;
								bool bUserProvidedPassword = data.ContainsKey("password");

								// we need a password
								if (bLobbyPassworded)
								{
									if (bUserProvidedPassword)
									{
										// do the passwords match?
										string? strUserProvidedPassword = data["password"].GetString();

										// if it doesnt match, bail, otherwise just proceed as normal
										if (strUserProvidedPassword != lobby.Password)
										{
											Response.StatusCode = (int)HttpStatusCode.Unauthorized;
											result.success = false;
											return result;
										}
									}
									else
									{
										// no password, no access
										Response.StatusCode = (int)HttpStatusCode.Unauthorized;
										result.success = false;
										return result;
									}
								}

								// must be running same AC
								if (anticheatID != lobby.AnticheatID)
								{
									Response.StatusCode = (int)HttpStatusCode.ExpectationFailed;
									result.success = false;
									return result;
								}

								UserSession? playerSession = WebSocketManager.GetSessionFromUser(user_id, sessionType);

								if (playerSession != null)
								{
									// leave any lobby
									_lobbyManager.LeaveAnyLobby(user_id);

									await using var db = await _dbFactory.CreateDbContextAsync();
									string strDisplayName = await Database.Users.GetDisplayName(db, user_id);
									bool bJoinedSuccessfully = await _lobbyManager.JoinLobby(db, lobby, playerSession, strDisplayName, userPreferredPort, bHasMap);

									result.success = bJoinedSuccessfully;

									if (!bJoinedSuccessfully) // this basically means full, didnt find a slot in correct state
									{
										Response.StatusCode = (int)HttpStatusCode.NotAcceptable;
									}
									else
									{
										// TODO: What if this fails? just let them proceed? just means only direct connect people will be able to play
										// get some turn credentials
										TURNCredentialContainer? turnCredentials = await TURNCredentialManager.CreateCredentialsForUser(user_id);
										if (turnCredentials != null)
										{
											result.turn_username = turnCredentials.m_strUsername;
											result.turn_token = turnCredentials.m_strToken;
										}

										Response.StatusCode = (int)HttpStatusCode.OK;
									}
								}
								else
								{
									Response.StatusCode = (int)HttpStatusCode.Unauthorized;
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