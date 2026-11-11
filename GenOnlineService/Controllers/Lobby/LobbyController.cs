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
using System.Text.RegularExpressions;
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
		// Why a refused host action was refused, for the lobby chat. Empty on success.
		public string error { get; set; } = String.Empty;
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
		HOST_ACTION_BULK_SLOT_UPDATE = 19,
		HOST_ACTION_ARM_RESUME = 20,
		LOCAL_PLAYER_HAS_RESUME_REPLAY = 21,
		// Resume-from-replay: the host places every
		// slot (occupant, kind, faction, color, start position, team) the way the recording had it.
		HOST_ACTION_ALIGN_SLOTS = 22
    };

	public class RouteHandler_ResumeReplay_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public bool success { get; set; } = false;
		public long total { get; set; } = 0;
		public long from { get; set; } = 0;
		public string data { get; set; } = String.Empty;
	}

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
						await _lobbyManager.LeaveAnyLobby(user_id);

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
						&& data.ContainsKey("desynced")
						&& data.ContainsKey("duration")
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
								bool desynced = data["desynced"].GetBoolean();
								uint duration = data["duration"].GetUInt32();
								UInt64 match_id = data["match_id"].GetUInt64();

								// were they really in the match they claim to be in?
								if (!sourceData.WasPlayerInMatch(match_id, out int slotIndexInLobby, out int army, out DateTime LobbyCreationTime))
								{
									Response.StatusCode = (int)HttpStatusCode.Unauthorized;
									return null;
								}

								// Record on the lobby that this player reported, and what they claimed. The lobby
								// outlives a disconnected player's session, so this survives to DeleteLobby where the
								// winner is determined. MatchID is compared because WasPlayerInMatch accepts any
								// historic match of this session - without it a stale report could be attributed to
								// whatever lobby the player happens to be in now.
								Lobby? outcomeLobby = _lobbyManager.GetLobby(sourceData.currentLobbyID);
								if (outcomeLobby != null && outcomeLobby.MatchID == match_id)
								{
									outcomeLobby.RecordPlayerOutcome(user_id, won);
								}

								// register with daily stats
								// NOTE: only once per match - the outcome endpoint can be called repeatedly for the
								// same match_id, and these counters are unconditional increments
								bool bFirstOutcomeReportForMatch = sourceData.TryMarkMatchOutcomeReported(match_id);
								if (bFirstOutcomeReportForMatch)
								{
									DailyStatsManager.RegisterOutcome(army, won);
								}

								// give them back signed URLs they need
								result.screenshot_url = await S3CredentialManager.GetPresignedURL(EMetadataFileType.FILE_TYPE_SCREENSHOT, EScreenshotType.SCREENSHOT_TYPE_SCORESCREEN, match_id, user_id, slotIndexInLobby, LobbyCreationTime) ?? String.Empty;
								result.replay_url = await S3CredentialManager.GetPresignedURL(EMetadataFileType.FILE_TYPE_REPLAY, EScreenshotType.NONE, match_id, user_id, slotIndexInLobby, LobbyCreationTime) ?? String.Empty;

								// store in DB
								await using var db = await _dbFactory.CreateDbContextAsync();
								await Database.MatchHistory.CommitPlayerOutcome(db, slotIndexInLobby, match_id, side,
										buildings_built, buildings_killed, buildings_lost, units_built, units_killed, units_lost, total_money, won, desynced, duration);
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
			[ELobbyUpdateField.HOST_ACTION_BULK_SLOT_UPDATE] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.HOST_ACTION_ARM_RESUME] = ELobbyUpdatePermissions.LobbyOwner,
			[ELobbyUpdateField.LOCAL_PLAYER_HAS_RESUME_REPLAY] = ELobbyUpdatePermissions.Anyone,
			[ELobbyUpdateField.HOST_ACTION_ALIGN_SLOTS] = ELobbyUpdatePermissions.LobbyOwner
		};

		// Resume-from-replay source transfer (see HOST_ACTION_ARM_RESUME): the host posts the file in
		// base64 chunks, guests read it back in chunks. Replays are command logs, a long match is a few MB.
		private const int ResumeReplayMaxChunkBytes = 512 * 1024;
		private const int ResumeReplayMaxReadBytes = 256 * 1024;
		private const int ResumeReplayMaxChunkRequestBytes = ResumeReplayMaxChunkBytes * 4 / 3 + 4096; // base64 plus JSON framing
		private static readonly Regex ResumeReplayFileNamePattern = new Regex("^[0-9]{8}\\.rep$", RegexOptions.Compiled);

		// Why ApplyLobbyFieldUpdateAsync refused a host action, for the lobby chat (a controller lives for one request).
		private string m_strFieldUpdateError = String.Empty;


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

								if (!TryParseLobbyUpdateField(data["field"].GetInt32(), out ELobbyUpdateField field))
								{
									Response.StatusCode = (int)HttpStatusCode.BadRequest;
									return result;
								}

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

								// Every mutation below touches this lobby's Members, slot state, or ready state,
								// so the whole field-update dispatch runs as one atomic operation under the
								// per-lobby gate instead of each setter mutating state unguarded.
								// A kick can't finish the actual removal in here: RemoveMember acquires this
								// same lobby's gate, which is not reentrant, so ApplyLobbyFieldUpdateAsync only
								// validates the target and hands the user ID back for PerformKickAsync to
								// process once the gate below has been released.
								m_strFieldUpdateError = String.Empty;
								Int64? kickedUserID = await lobby.RunExclusiveAsync(() => ApplyLobbyFieldUpdateAsync(lobby, SourceMember, field, data));
								result.error = m_strFieldUpdateError;
								result.success = m_strFieldUpdateError.Length == 0 && Response.StatusCode == (int)HttpStatusCode.OK;

								if (kickedUserID.HasValue)
								{
									await PerformKickAsync(lobby, kickedUserID.Value);
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

		// Rejects any wire value that isn't a real EPlayerType member, so a malformed or
		// out-of-range slot_state from HOST_ACTION_SET_SLOT_STATE can't be cast into an enum value
		// the rest of the lobby code (SetPlayerSlotState, IsAI, serialization, ...) never expects.
		internal static bool TryParseSlotState(UInt16 rawValue, out EPlayerType slotState)
		{
			slotState = (EPlayerType)rawValue;
			// hosts can open, close or fill a slot with AI, but never mark it as a human
			return Enum.IsDefined(typeof(EPlayerType), slotState) && slotState != EPlayerType.SLOT_PLAYER;
		}

		// Rejects any wire value that isn't a real ELobbyUpdateField member, so an unknown/malformed
		// field can't be cast into an enum value that then gets indexed straight into
		// g_dictLobbyUpdatePermissionsTable (which previously threw KeyNotFoundException, silently
		// swallowed by Post's outer catch, for anything out of range).
		internal static bool TryParseLobbyUpdateField(int rawValue, out ELobbyUpdateField field)
		{
			field = (ELobbyUpdateField)rawValue;
			return Enum.IsDefined(typeof(ELobbyUpdateField), field);
		}

		// Runs the per-field lobby update dispatch. Must only be called from inside
		// lobby.RunExclusiveAsync: every branch here mutates this lobby's Members, slot
		// state/fields, or ready state, and previously ran completely unguarded.
		// Returns the kicked user's ID when HOST_ACTION_KICK_USER validated a real target, so the
		// caller can run the actual removal after releasing the gate; null for every other field
		// (including a kick request with no valid target).
		private async Task<Int64?> ApplyLobbyFieldUpdateAsync(Lobby lobby, LobbyMember SourceMember, ELobbyUpdateField field, Dictionary<string, JsonElement> data)
		{
			// reset everyones ready states when anything changes (minus dummy actions)
			if (field != ELobbyUpdateField.HOST_ACTION_FORCE_START
				&& field != ELobbyUpdateField.LOCAL_PLAYER_HAS_MAP
				&& field != ELobbyUpdateField.LOCAL_PLAYER_HAS_RESUME_REPLAY
				&& field != ELobbyUpdateField.HOST_ACTION_KICK_USER)
			{
				lobby.ResetReadyStates();
			}

			// Resume-from-replay: the recording fixes every slot's occupant, faction, color, start
			// position and team (the map, cash and superweapon handlers disarm inside the lobby), so
			// a slot change disarms. The host re-arms after fixing the lobby.
			//
			// Decided after the update, on whether the
			// roster actually changed, not on the field name. The arming client's own UI echoes the
			// slot it was just moved to (MY_SIDE / MY_COLOR / MY_START_POS with the values the
			// alignment set), and HOST_ACTION_ALIGN_SLOTS is the first half of arming itself; both
			// used to disarm the arm that had just been made, so the host had to arm twice
			// (2026-10-14: "Resume disarmed: the next start is a fresh game" right after every arm).
			bool bSlotField = field == ELobbyUpdateField.MY_SIDE
				|| field == ELobbyUpdateField.MY_COLOR
				|| field == ELobbyUpdateField.MY_START_POS
				|| field == ELobbyUpdateField.MY_TEAM
				|| field == ELobbyUpdateField.AI_SIDE
				|| field == ELobbyUpdateField.AI_COLOR
				|| field == ELobbyUpdateField.AI_TEAM
				|| field == ELobbyUpdateField.AI_START_POS
				|| field == ELobbyUpdateField.HOST_ACTION_SET_SLOT_STATE
				|| field == ELobbyUpdateField.HOST_ACTION_BULK_SLOT_UPDATE;
			string strRosterBefore = bSlotField ? lobby.RosterFingerprint() : String.Empty;

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

					// same limits as lobby creation
					if ((strMap != null && strMap.Length > 255) || (strMapPath != null && strMapPath.Length > 512))
					{
						Response.StatusCode = (int)HttpStatusCode.BadRequest;
						return null;
					}

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
					Int64 KickedUserID = data["userid"].GetInt64();

					// the target must actually be in THIS lobby, otherwise a host could wipe the
					// TURN credentials / lobby state of any arbitrary online player. Checked here,
					// under the gate, so it can't race a concurrent leave/kick of the same target;
					// the actual removal runs after the gate is released (see PerformKickAsync).
					if (lobby.GetMemberFromUserID(KickedUserID) != null)
					{
						return KickedUserID;
					}
				}
			}
			else if (field == ELobbyUpdateField.HOST_ACTION_SET_SLOT_STATE)
			{
				UInt16 slot_index = data["slot_index"].GetUInt16();

				if (!TryParseSlotState(data["slot_state"].GetUInt16(), out EPlayerType slot_state))
				{
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					return null;
				}

				// human slots are left alone; kicking is how a host removes a player
				LobbyMember? TargetMember = lobby.GetMemberFromSlot(slot_index);
				if (TargetMember != null && TargetMember.SlotState != EPlayerType.SLOT_PLAYER)
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
			else if (field == ELobbyUpdateField.HOST_ACTION_ALIGN_SLOTS)
			{
				// Resume-from-replay: the recording fixes who sat where, so the host sends the whole
				// arrangement in one request and the lobby is rebuilt to match. Every current human
				// must be placed exactly once and the owner must stay in slot 0; anything else is
				// refused with a reason the host can show, and the lobby is left as it was.
				if (!data.ContainsKey("slots") || data["slots"].ValueKind != JsonValueKind.Array)
				{
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					m_strFieldUpdateError = "no slots";
					return null;
				}
				List<LobbySlotSpec> specs = new List<LobbySlotSpec>();
				foreach (JsonElement slotEntry in data["slots"].EnumerateArray())
				{
					LobbySlotSpec spec = new LobbySlotSpec();
					if (!slotEntry.TryGetProperty("slot_index", out var idxProp) || !idxProp.TryGetInt32(out spec.SlotIndex)
						|| !slotEntry.TryGetProperty("slot_state", out var stateProp) || !stateProp.TryGetInt32(out int stateInt))
					{
						Response.StatusCode = (int)HttpStatusCode.BadRequest;
						m_strFieldUpdateError = "malformed slot entry";
						return null;
					}
					spec.SlotState = (EPlayerType)stateInt;
					spec.UserID = slotEntry.TryGetProperty("user_id", out var uidProp) && uidProp.TryGetInt64(out Int64 uid) ? uid : -1;
					spec.Side = slotEntry.TryGetProperty("side", out var sideProp) && sideProp.TryGetInt32(out int side) ? side : -1;
					spec.Color = slotEntry.TryGetProperty("color", out var colorProp) && colorProp.TryGetInt32(out int color) ? color : -1;
					spec.StartPos = slotEntry.TryGetProperty("start_pos", out var spProp) && spProp.TryGetInt32(out int sp) ? sp : -1;
					spec.Team = slotEntry.TryGetProperty("team", out var teamProp) && teamProp.TryGetInt32(out int team) ? team : -1;
					specs.Add(spec);
				}
				string? alignError = await lobby.AlignSlots(specs);
				if (alignError != null)
				{
					Console.WriteLine($"[Lobby {lobby.LobbyID}] Resume: ALIGN_SLOTS from user {SourceMember.UserID} refused: {alignError}");
					Response.StatusCode = (int)HttpStatusCode.Conflict;
					m_strFieldUpdateError = alignError;
					return null;
				}
			}
			else if (field == ELobbyUpdateField.HOST_ACTION_ARM_RESUME)
			{
				// Resume-from-replay: the host arms the resume source it uploaded (POST {lobbyID}/resume_replay).
				// The client replays it in lockstep up to handoff_frame on game start, so the lobby
				// must start from the replay's seed. handoff_frame 0 disarms. Every refusal is an
				// explicit error: a client that announced an arm the service dropped would have
				// every guest start a fresh match.
				if (data.ContainsKey("replay_file"))
				{
					// legacy field from clients that still name the file: only the recorder's own name is acceptable
					string? replayFile = data["replay_file"].ValueKind == JsonValueKind.String ? data["replay_file"].GetString() : null;
					if (replayFile == null || (replayFile.Length > 0 && !ResumeReplayFileNamePattern.IsMatch(replayFile)))
					{
						Response.StatusCode = (int)HttpStatusCode.BadRequest;
						m_strFieldUpdateError = "bad replay file name";
						return null;
					}
				}
				if (!data.ContainsKey("handoff_frame")
					|| data["handoff_frame"].ValueKind != JsonValueKind.Number
					|| !data["handoff_frame"].TryGetUInt32(out UInt32 handoffFrame))
				{
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					m_strFieldUpdateError = "no handoff frame";
					return null;
				}
				int rngSeed = 0;
				if (handoffFrame != 0
					&& (!data.ContainsKey("rng_seed")
						|| data["rng_seed"].ValueKind != JsonValueKind.Number
						|| !data["rng_seed"].TryGetInt32(out rngSeed)))
				{
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					m_strFieldUpdateError = "no rng seed";
					return null;
				}
				if (!lobby.UpdateResumeArm(handoffFrame, rngSeed))
				{
					// arming without an uploaded source
					Response.StatusCode = (int)HttpStatusCode.Conflict;
					m_strFieldUpdateError = "no resume source uploaded";
					return null;
				}
			}
			else if (field == ELobbyUpdateField.LOCAL_PLAYER_HAS_RESUME_REPLAY)
			{
				// whether this member holds a usable copy of the resume source (the way has_map works)
				if (data.ContainsKey("has_resume_replay") && (data["has_resume_replay"].ValueKind == JsonValueKind.True || data["has_resume_replay"].ValueKind == JsonValueKind.False))
				{
					SourceMember.UpdateHasResumeReplay(data["has_resume_replay"].GetBoolean());
				}
				else
				{
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					m_strFieldUpdateError = "bad has_resume_replay";
					return null;
				}
			}

			if (bSlotField && lobby.RosterFingerprint() != strRosterBefore)
			{
				lobby.ClearResumeArm($"slot change ({field}) by user {SourceMember.UserID}");
			}

			return null;
		}

		// Runs the parts of a kick that RemoveMember itself performs (leaving the lobby, which
		// acquires this same lobby's gate) plus TURN/session cleanup - all after
		// ApplyLobbyFieldUpdateAsync's gate has been released, never from inside it.
		private async Task PerformKickAsync(Lobby lobby, Int64 kickedUserID)
		{
			// TODO: we should communicate the kick to the user...
			await _lobbyManager.LeaveSpecificLobby(kickedUserID, lobby.LobbyID);

			// cleanup TURN credentials
			TURNCredentialManager.DeleteCredentialsForUser(kickedUserID);

			// clear our lobby ID
			UserSession? sourceData = WebSocketManager.GetSessionFromUser(kickedUserID, EUserSessionType.GameClient); // user being kicked must be a game client

			if (sourceData != null)
			{
				sourceData.UpdateSessionLobbyID(-1);
				// NOTE: We dont update the match history match ID here, that is done by the match history service
			}

			// we have to manually send to the kicked user... they won't get the dirty lobby update anymore
			await lobby.DirtyRetransmitToSingleMember(kickedUserID);
		}

		/// <summary>
		/// Resume-from-replay source upload (lobby owner only). The file arrives in base64 chunks with an
		/// offset; offset 0 starts a new file and replaces whatever the lobby held. The service keeps it
		/// in memory with the lobby until the lobby is deleted.
		/// </summary>
		[HttpPost("{lobbyID}/resume_replay")]
		[Authorize(Roles = "GameClient")]
		[RequestSizeLimit(ResumeReplayMaxChunkRequestBytes)]
		public async Task<APIResult> PostResumeReplay(Int64 lobbyID)
		{
			RouteHandler_ResumeReplay_Result result = new RouteHandler_ResumeReplay_Result();

			Int64 user_id = TokenHelper.GetUserID(this);
			Lobby? lobby = _lobbyManager.GetLobby(lobbyID);
			if (lobby == null)
			{
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				return result;
			}
			if (user_id == -1 || lobby.Owner != user_id)
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				return result;
			}

			long offset;
			long total;
			byte[] bytes;
			try
			{
				using var reader = new StreamReader(HttpContext.Request.Body);
				string jsonData = await reader.ReadToEndAsync();
				var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
				if (data == null || !data.ContainsKey("offset") || !data.ContainsKey("total") || !data.ContainsKey("data")
					|| !data["offset"].TryGetInt64(out offset) || !data["total"].TryGetInt64(out total))
				{
					Response.StatusCode = (int)HttpStatusCode.BadRequest;
					return result;
				}
				bytes = Convert.FromBase64String(data["data"].GetString() ?? String.Empty);
			}
			catch
			{
				Response.StatusCode = (int)HttpStatusCode.BadRequest;
				return result;
			}

			if (bytes.Length == 0 || bytes.Length > ResumeReplayMaxChunkBytes || offset < 0 || total <= 0 || total > Lobby.ResumeReplayMaxBytes)
			{
				Response.StatusCode = (int)HttpStatusCode.BadRequest;
				return result;
			}

			// the first chunk must open like a replay (RecorderClass writes the GENREP tag first)
			if (offset == 0 && !Lobby.LooksLikeReplay(bytes))
			{
				Response.StatusCode = (int)HttpStatusCode.BadRequest;
				return result;
			}

			// under the lobby gate: offset 0 disarms, and an arm request must not interleave with that
			bool bAppended = false;
			long currentLength = 0;
			bool bOverCapacity = false;
			await lobby.RunExclusiveAsync(() =>
			{
				bAppended = lobby.AppendResumeReplay(offset, total, bytes, out currentLength, out bOverCapacity);
				return Task.CompletedTask;
			});
			if (!bAppended)
			{
				if (bOverCapacity)
				{
					// the service-wide budget for held resume replays is spent
					Response.StatusCode = (int)HttpStatusCode.InsufficientStorage;
					return result;
				}
				// offset disagreement (lost reply, duplicate send): tell the uploader where we really are
				Response.StatusCode = (int)HttpStatusCode.Conflict;
				result.total = currentLength;
				return result;
			}

			result.success = true;
			result.total = currentLength;
			return result;
		}

		/// <summary>
		/// Resume-from-replay source download (lobby members only), from the given byte offset.
		/// </summary>
		[HttpGet("{lobbyID}/resume_replay")]
		[Authorize(Roles = "GameClient")]
		public APIResult GetResumeReplay(Int64 lobbyID, [FromQuery] long from = 0)
		{
			RouteHandler_ResumeReplay_Result result = new RouteHandler_ResumeReplay_Result();

			Int64 user_id = TokenHelper.GetUserID(this);
			Lobby? lobby = _lobbyManager.GetLobby(lobbyID);
			if (lobby == null)
			{
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				return result;
			}
			if (user_id == -1 || lobby.GetMemberFromUserID(user_id) == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				return result;
			}
			if (from < 0)
			{
				Response.StatusCode = (int)HttpStatusCode.BadRequest;
				return result;
			}

			byte[] bytes = lobby.ReadResumeReplay(from, ResumeReplayMaxReadBytes, out long total);
			if (total == 0)
			{
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				return result;
			}

			result.success = true;
			result.total = total;
			result.from = from;
			if (bytes.Length > 0)
			{
				result.data = Convert.ToBase64String(bytes);
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
								if (lobby.LobbyType == ELobbyType.QuickMatch && !lobby.MatchedUserIDs.Contains(user_id))
								{
									Response.StatusCode = (int)HttpStatusCode.Forbidden;
									result.success = false;
									return result;
								}

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
									await _lobbyManager.LeaveAnyLobby(user_id);

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
