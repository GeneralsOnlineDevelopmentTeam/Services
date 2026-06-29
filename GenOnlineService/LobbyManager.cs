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
using Discord;
using GenOnlineService.Controllers;
using Microsoft.EntityFrameworkCore;
using MySqlX.XDevAPI;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace GenOnlineService
{
	public class Lobby
	{
		public Int64 LobbyID { get; private set; } = -1;
		public Int64 Owner { get; private set; } = -1;
		public string Name { get; private set; } = "";
		public ELobbyState State { get; private set; } = ELobbyState.UNKNOWN;
		public string MapName { get; private set; } = "";
		public string MapPath { get; private set; } = "";
		public bool IsMapOfficial { get; private set; } = false;
		public UInt64 MatchID { get; private set; } = 0;

		public DateTime TimeCreated { get; private set; } = DateTime.UtcNow;

		[JsonIgnore]
		public bool PendingFullMeshConnectivityChecks { get; private set; } = false;

		[JsonIgnore]
		public Int64 TimeStartFullMeshChecks { get; private set; } = -1;

		private const int MSToWaitForFullMeshChecks = 5; // really shouldnt take more than 5 seconds... this might even be too much

		[JsonIgnore]
		public ConcurrentDictionary<Int64, ConcurrentList<Int64>> FullMeshConnectivityChecks { get; set; } = new();

		public void StartFullMeshConnectivityCheck()
		{
			PendingFullMeshConnectivityChecks = true;
			FullMeshConnectivityChecks = new();
			TimeStartFullMeshChecks = Environment.TickCount64;
		}

		public async Task StoreFullMeshConnectivityResponse(Int64 sourceUser, List<Int64> connectivityMap)
		{
			FullMeshConnectivityChecks[sourceUser] = new ConcurrentList<Int64>(connectivityMap);

			// check again for being done
			await ProcessPendingFullMeshConnectivityChecks();
		}

		public async Task ProcessPendingFullMeshConnectivityChecks()
		{
			// TODO: Add a timeout to this
			if (PendingFullMeshConnectivityChecks)
			{
				bool bDoneChecks = false;
				int totalMapEntriesExpected = GetNumberOfHumans();
				//int numConnectionsExpectedPerUser = totalMapEntriesExpected - 1; // minus self

				// must have a connectivity map for each lobby member
				bDoneChecks = FullMeshConnectivityChecks.Count == totalMapEntriesExpected;

				// did we timeout?
				if (!bDoneChecks && (Environment.TickCount64 - TimeStartFullMeshChecks) >= MSToWaitForFullMeshChecks)
				{
					bDoneChecks = true;
				}

				List<MissingConnectionEntry> lstMissingConnections = new();

				if (bDoneChecks)
				{
					// now verify each user has provided data for all other users
					foreach (var userMap in FullMeshConnectivityChecks)
					{
						// foreach member in the lobby, check they are in userMap.Value
						foreach (LobbyMember member in Members)
						{
							if (member.IsHuman())
							{
								// useful for test
// 								if (member.UserID != userMap.Key && member.UserID == 1)
// 								{
// 									// register it
// 									MissingConnectionEntry missingConnectionEntry = new();
// 									missingConnectionEntry.source_user_id = userMap.Key;
// 									missingConnectionEntry.target_user_id = member.UserID;
// 									lstMissingConnections.Add(missingConnectionEntry);
// 								}

								// wont have a connection to ourself
								if (member.UserID != userMap.Key && !userMap.Value.Contains(member.UserID))
								{
									// register it
									MissingConnectionEntry missingConnectionEntry = new();
									missingConnectionEntry.source_user_id = userMap.Key;
									missingConnectionEntry.target_user_id = member.UserID;
									lstMissingConnections.Add(missingConnectionEntry);
								}
							}
						}
					}

					bool bDisableMeshCheck = false;
					if (Program.g_Config != null)
					{
						IConfiguration? coreSettings = Program.g_Config.GetSection("Core");

						if (coreSettings != null)
						{
							bDisableMeshCheck = coreSettings.GetValue<bool>("disable_full_mesh_check");
						}
					}

					

					// inform host that we are done
					// start full mesh connectivity checks
					WebSocketMessage_FullMeshConnectivityCheckOutcome outcome = new WebSocketMessage_FullMeshConnectivityCheckOutcome();
					outcome.msg_id = (int)EWebSocketMessageID.FULL_MESH_CONNECTIVITY_CHECK_RESPONSE_COMPLETE_TO_HOST;

					if (bDisableMeshCheck)
					{
						outcome.mesh_complete = true;
						outcome.missing_connections = new List<MissingConnectionEntry>();
					}
					else
					{
						outcome.mesh_complete = lstMissingConnections.Count == 0;
						outcome.missing_connections = lstMissingConnections;
					}

					// TODO_EFCORE: Later, these should really use lobby list instead of getting session from ID

					// send to host
					UserSession? hostSession = WebSocketManager.GetSessionFromUser(Owner, EUserSessionType.GameClient); // host should be a game client
					if (hostSession != null)
					{
						byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outcome));
						hostSession.QueueWebsocketSend(bytesJSON);
					}

					// reset state
					PendingFullMeshConnectivityChecks = false;
					TimeStartFullMeshChecks = -1;
				}
			}
		}

		public void AddPassword(string password)
		{
			Password = password;
			IsPassworded = true;
		}

		public void RemovePassword()
		{
			Password = String.Empty;
			IsPassworded = false;
		}

		public double GetLatitude() { return m_dHostLatitude; }
		public double GetLongitude() { return m_dHostLongitude; }

		public async Task SetMatchID(UInt64 a_matchID)
		{
#if DEBUG
			MatchID = 123456;
#else
			MatchID = a_matchID;
#endif

			// store on each player
			foreach (LobbyMember member in Members)
			{
				if (member.GetSession().TryGetTarget(out UserSession? session))
				{
					session.RegisterHistoricMatchID(MatchID, member.SlotIndex, member.Side, TimeCreated);
				}
			}

			DirtyRetransmit();
		}

		// NOTE: Do not use Members.Length on a lobby. It will include empty slots. Use NumCurrentPlayers instead
		public int NumCurrentPlayers
		{
			get
			{
				// TODO: More optimal to store this instead of looping for it every time
				int currentPlayers = 0;
				foreach (LobbyMember member in Members)
				{
					if (member.SlotState != EPlayerType.SLOT_CLOSED && member.SlotState != EPlayerType.SLOT_OPEN)
					{
						++currentPlayers;
					}
				}

				return currentPlayers;
			}
		}
		//public int MaxPlayers { get; private set; } = 0;
		public int MaxPlayers
		{
			get
			{
				// TODO: More optimal to store this instead of looping for it every time
				int maxPlayers = 0;
				foreach (LobbyMember member in Members)
				{
					if (member.SlotState != EPlayerType.SLOT_CLOSED)
					{
						++maxPlayers;
					}
				}

				return maxPlayers;
			}
		}

		public bool IsVanillaTeamsOnly { get; private set; } = false;
		public UInt32 StartingCash { get; private set; } = 0;
		public bool IsLimitSuperweapons { get; private set; } = false;
		public bool IsTrackingStats { get; private set; } = false;
		public bool IsPassworded { get; private set; } = false;

		[JsonIgnore] // never serialize the password, we only need it on the service
		public string Password { get; private set; } = String.Empty;

		public bool AllowObservers { get; private set; } = false;
		public UInt32 ExeCRC { get; private set; } = 0;
		public UInt32 IniCRC { get; private set; } = 0;

		public Int16 NetworkRoomID { get; private set; } = -1;

		public int RNGSeed { get; private set; } = -1;

		public ELobbyType LobbyType { get; private set; } = ELobbyType.CustomGame;

		public string Region { get; private set; } = "";
		public int EstimatedLatency { get; private set; } = 999999;

		public UInt16 MaximumCameraHeight { get; private set; } = GenOnlineService.Constants.g_DefaultCameraMaxHeight;

        [JsonIgnore] // This is not serialized as the client doesn't need to know, the service checks it
        public ELobbyJoinability LobbyJoinability { get; private set; } = ELobbyJoinability.Public; // public by default


        public const int maxLobbySize = 8;
		public LobbyMember[] Members { get; private set; } = new LobbyMember[maxLobbySize];

		public EKnownAnticheatID AnticheatID { get; private set; }  = EKnownAnticheatID.NONE;

		[JsonIgnore]
		public Dictionary<Int64, DateTime> TimeMemberLeft { get; private set; } = new();

		// Records the first time each player's in-game WebSocket connection dropped (i.e., when they first "quit"
		// while the match was in progress). Only the first disconnect is stored — reconnects do not reset it.
		// Used by DetermineLobbyWinnerIfNotPresent to find who abandoned first (= loser) vs last (= winner).
		[JsonIgnore]
		public Dictionary<Int64, DateTime> TimePlayerAbandonedIngame { get; private set; } = new();

		/// <summary>
		/// Records the moment a player's WebSocket dropped while the lobby was in INGAME state.
		/// Only the FIRST disconnect is stored; subsequent reconnect/disconnect cycles are ignored
		/// so that a player who briefly loses connection is not penalised more than the player who
		/// intentionally killed the game first.
		/// </summary>
		public void RecordPlayerIngameAbandon(Int64 userId)
		{
			if (!TimePlayerAbandonedIngame.ContainsKey(userId))
			{
				TimePlayerAbandonedIngame[userId] = DateTime.UtcNow;
				Console.WriteLine("[Lobby {0}] Recorded in-game abandon for user {1} at {2:O}", LobbyID, userId, TimePlayerAbandonedIngame[userId]);

			}
		}

		/// <summary>
		/// Removes the in-game abandon timestamp for a player who successfully reconnected.
		/// This ensures a future disconnect records the correct (later) quit time.
		/// </summary>
		public void ClearPlayerIngameAbandon(Int64 userId)
		{
			if (TimePlayerAbandonedIngame.Remove(userId))
			{
				Console.WriteLine("[Lobby {0}] Cleared in-game abandon record for reconnected user {1}", LobbyID, userId);
			}
		}


		private bool m_bIsDirty = false;

		[JsonIgnore]
		private Int64 m_LastInitialSync = Environment.TickCount64;

		[JsonIgnore]
		private int m_InitialSyncs = 0;

		[JsonIgnore]
		private Int64 m_NextProbe = 0;

		// used for ping calculation but never sent to clients
		[JsonIgnore]
		private double m_dHostLatitude = 0;

		[JsonIgnore]
		private double m_dHostLongitude = 0;

		public Lobby(Int64 lobby_id, UserSession owner, string name, ELobbyState state, string map_name, string map_path, bool vanilla_teams, UInt32 starting_cash, bool limit_superweapons,
			bool track_stats, bool passworded, string password, bool map_official, int rng_seed, Int16 network_room, bool allow_observers, UInt16 max_cam_height, UInt32 exe_crc, UInt32 ini_crc,
			int max_players, ELobbyType lobbyType, EKnownAnticheatID inAnticheatID)
		{
			LobbyID = lobby_id;
			Owner = owner.m_UserID;

			string strAnticheatName = "OTHER AC";
			if (inAnticheatID == EKnownAnticheatID.NONE)
			{
				strAnticheatName = "\u26C9";
			}
			else if (inAnticheatID == EKnownAnticheatID.GO_INTEGRATED_AC)
			{
				strAnticheatName = "\u26CA";
			}
			else if (inAnticheatID == EKnownAnticheatID.EASY_ANTICHEAT)
			{
				strAnticheatName = "\u26CA";
			}


			Name = String.Format("[{0}][{1}] {2}", owner.m_strContinent, strAnticheatName, name);
			Region = String.Format("{0}", owner.GetFullContinentName());
			m_dHostLatitude = owner.m_dLatitude;
			m_dHostLongitude = owner.m_dLongitude;
			State = state;
			MapName = map_name;
			MapPath = FixMapPathForGame(map_path);
			IsMapOfficial = map_official;
			IsVanillaTeamsOnly = vanilla_teams;
			StartingCash = starting_cash;
			IsLimitSuperweapons = limit_superweapons;
			IsTrackingStats = track_stats;
			IsPassworded = passworded;
			Password = password;
			AllowObservers = allow_observers;
			ExeCRC = exe_crc;
			IniCRC = ini_crc;
			NetworkRoomID = network_room;
			RNGSeed = rng_seed;
			MaximumCameraHeight = max_cam_height;
			LobbyType = lobbyType;
			Members = new LobbyMember[maxLobbySize];
			AnticheatID = inAnticheatID;

			// create default slots
			for (UInt16 i = 0; i < maxLobbySize; ++i)
			{
				LobbyMember placeholderMember = new LobbyMember(this, null, -1, String.Empty, String.Empty, 0, -1, -1, -1, i < max_players ? EPlayerType.SLOT_OPEN : EPlayerType.SLOT_CLOSED, i, true);
				Members[i] = placeholderMember;
			}
		}

		public event Action<Lobby>? OnLobbyNeedsDestroyed;

		public async Task OnAfterPlayerLeft(Int64 leavingUserID)
		{
			// NOTE: By the time this is called, the member is no longer in the members list
			bool bNeedsHostMigrate = Owner == leavingUserID;

			// we need human members, not real members
			int numHumanMembers = GetNumberOfHumans();

			if (numHumanMembers == 0)
			{
				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine("DeleteLobby: Source A");
				Console.ForegroundColor = ConsoleColor.Gray;

				OnLobbyNeedsDestroyed?.Invoke(this);
			}
			else
			{
				// Host migration is only meaningful in the lobby setup phase.
				// During an active game the P2P host is determined by the game engine,
				// and migrating the server-side owner causes confusing mid-game lobby
				// state broadcasts to surviving clients.
				if (bNeedsHostMigrate && State != ELobbyState.INGAME)
				{
					DoHostMigration();
				}
			}
		}

		public void CloseOpenSlots()
		{
			foreach (LobbyMember member in Members)
			{
				if (member.SlotState == EPlayerType.SLOT_OPEN)
				{
					member.SetPlayerSlotState(EPlayerType.SLOT_CLOSED);
				}
			}

			DirtyRetransmit();
		}

		public void DoHostMigration()
		{
			Int64 oldOwner = Owner;

			foreach (LobbyMember member in Members)
			{
				if (member.SlotState == EPlayerType.SLOT_PLAYER)
				{
					if (member.UserID != oldOwner)
					{
						// found a viable host
						UInt16 oldSlot = member.SlotIndex;

						// update owner
						Owner = member.UserID;

						// move them to slot 0 (host)
						member.UpdateSlotIndex(0);
						Members[0] = member;
						Members[oldSlot] = new LobbyMember(this, null, -1, String.Empty, String.Empty, 0, -1, -1, -1, EPlayerType.SLOT_OPEN, oldSlot, true);

						// mark as ready
						member.SetReadyState(true);

						// mark as dirty
						DirtyRetransmit();

						// we are done
						break;
					}
				}
			}
		}

		private void CalculateNextProbeTime(bool bIsFirstProbe)
		{
#if DEBUG
			m_NextProbe = Environment.TickCount64 + (bIsFirstProbe ? 5000 : 30000);
#else
			if (bIsFirstProbe) // 30s
			{
				m_NextProbe = Environment.TickCount64 + 30000;
			}
			else // 30 to 40 min
			{
				int nextProbeInterval = Random.Shared.Next(30, 40);
				m_NextProbe = Environment.TickCount64 + nextProbeInterval * 60000;
			}
#endif
		}

		public async Task Tick()
		{
			if (m_NextProbe != 0 && Environment.TickCount64 >= m_NextProbe)
			{
				// send probe
				{
					foreach (LobbyMember memberEntry in Members)
					{
						// per user endpoint
						string? strUploadURI = await S3CredentialManager.GetPresignedURL(EMetadataFileType.FILE_TYPE_SCREENSHOT, EScreenshotType.SCREENSHOT_TYPE_GAMEPLAY, MatchID, memberEntry.UserID, memberEntry.SlotIndex, TimeCreated);

						if (strUploadURI != null) // should never be null really
						{
							WebSocketMessage_Probe probe = new WebSocketMessage_Probe();
							probe.msg_id = (int)EWebSocketMessageID.PROBE;
							probe.url = strUploadURI;
							byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(probe));

							if (memberEntry.GetSession().TryGetTarget(out UserSession? session))
							{
								session.QueueWebsocketSend(bytesJSON);
							}
						}
					}
				}

				// calculate next probe time
				CalculateNextProbeTime(false);
			}

			if (m_InitialSyncs < 5 && Environment.TickCount64 - m_LastInitialSync > 200)
			{
				m_LastInitialSync = Environment.TickCount64;
				m_bIsDirty = true;
				++m_InitialSyncs;
			}

			if (m_bIsDirty)
			{
				WebSocketMessage_CurrentLobbyUpdate lobbyUpdate = new WebSocketMessage_CurrentLobbyUpdate();
				lobbyUpdate.msg_id = (int)EWebSocketMessageID.LOBBY_CURRENT_LOBBY_UPDATE;

				byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lobbyUpdate));

				foreach (LobbyMember memberEntry in Members)
				{
					if (memberEntry.GetSession().TryGetTarget(out UserSession? session))
					{
						UserSession? sess = WebSocketManager.GetSessionFromUser(session.m_UserID, session.GetSessionType());
						if (sess != null)
						{
							Console.WriteLine("[DIRTY LOBBY] Sending WS lobby update for lobby {0}", LobbyID);
							sess.QueueWebsocketSend(bytesJSON);
						}
					}
				}

				// transmit to those in network room
				//WebSocketManager.SendNewOrDeletedLobbyToAllNetworkRoomMembers(NetworkRoomID);

				m_bIsDirty = false;
			}
		}

		private readonly SemaphoreSlim g_SlotLock = new SemaphoreSlim(1, 1);
		public async Task<bool> AddMember(UserSession playerSession, string strDisplayName, UInt16 userPreferredPort, bool bHasMap, UserLobbyPreferences lobbyPrefs)
		{
			LobbyMember? existingMember = GetMemberFromUserID(playerSession.m_UserID);
			if (existingMember != null) // we're already in this lobby
			{
				return false;
			}

			// NOTE: AddMember is called async, so timing + slot determination could result in players being inserted in the same slot
			await g_SlotLock.WaitAsync();
			try
			{
				// find first open slot
				bool bFoundSlot = false;
				UInt16 slotIndex = 0;
				foreach (var memberEntry in Members)
				{
					if (memberEntry.SlotState == EPlayerType.SLOT_OPEN)
					{
						// found a gap, use this slot index
						bFoundSlot = true;
						break;
					}
					++slotIndex;
				}

				if (!bFoundSlot)
				{
					return false;
				}

                // Check social requirements (dont allow blocked in, and check friends only)
                // SOCIAL: If the lobby owner has source user blocked, remove the lobby
				// NOTE: Only check this for custom match, quick match checks it during matchmaking bucket stage
				if (LobbyType == ELobbyType.CustomGame)
				{
					SharedUserData? lobbyOwnerSharedData = WebSocketManager.GetSharedDataForUser(Owner); // owner must be a game client

					if (lobbyOwnerSharedData != null)
                    {
                        // dont allow join if blocked
                        if (lobbyOwnerSharedData.GetSocialContainer().Blocked.Contains(playerSession.m_UserID))
                        {
                            return false;
                        }

                        // check joinability
                        if (LobbyJoinability == ELobbyJoinability.FriendsOnly)
                        {
                            // If it's friends only, return false if they aren't friends
                            if (!lobbyOwnerSharedData.GetSocialContainer().Friends.Contains(playerSession.m_UserID))
                            {
                            return false;
                            }
                        }
                    }
                }

            // de dupe names
            string strOriginalDisplayName = strDisplayName;
			int dupesSeen = 0;
			string strNameLower = strDisplayName.ToLower();
			foreach (var memberEntry in Members)
			{
				if (memberEntry.DisplayNameNotDeduped.ToLower() == strNameLower)
				{
					++dupesSeen;
				}
			}

			if (dupesSeen > 0)
			{
				strDisplayName = String.Format("{0} ({1})", strDisplayName, dupesSeen);
			}

			// only apply lobby prefs if not QM
			LobbyMember? newMember = null;
			if (LobbyType == ELobbyType.CustomGame)
			{

				// if vanilla teams, dont apply favorite
				int sideToUse = lobbyPrefs.favorite_side;
				if (IsVanillaTeamsOnly)
				{
					sideToUse = -1;
				}

				// if our preferred color is already in use, revert to random
				int colorToUse = lobbyPrefs.favorite_color;
				foreach (var memberEntry in Members)
				{
					if (memberEntry.Color == lobbyPrefs.favorite_color)
					{
						colorToUse = -1;
						break;
					}
				}

				newMember = new LobbyMember(this, playerSession, playerSession.m_UserID, strDisplayName, strOriginalDisplayName, userPreferredPort, sideToUse, colorToUse, -1, EPlayerType.SLOT_PLAYER, slotIndex, bHasMap);
			}
			else
			{
				// NOTE: In quick match, we need to pick their team, client doesn't do it for us.
				int[] allowedTeams =
				[
					2, // USA
					3, // CHINA
					4, // GLA
					5, // USA Super Weapon
					6, // USA Laser
					7, // USA Airforce
					8, // China Tank
					9, // China Infantry
					10, // China Nuke
					11, // GLA Toxin
					12, // GLA Demo
					13 // GLA Stealth
				];

				int sideToUse = allowedTeams[Random.Shared.Next(0, allowedTeams.Length)];

				// team is random for now, matchmaker will assign teams on start
				newMember = new LobbyMember(this, playerSession, playerSession.m_UserID, strDisplayName, strOriginalDisplayName, userPreferredPort, sideToUse, -1, -1, EPlayerType.SLOT_PLAYER, slotIndex, bHasMap);
			}

			Members[slotIndex] = newMember;
			TimeMemberLeft[playerSession.m_UserID] = DateTime.UnixEpoch;

			// leave network room we were in
			playerSession.UpdateSessionNetworkRoom(-1);

			// store our lobby ID
			playerSession.UpdateSessionLobbyID(LobbyID);

			// START NETWORK SIGNALLING
			// send all existing player to new player and vice-versa
			foreach (LobbyMember memberEntry in Members)
			{
				if (memberEntry != newMember) // NOT us
				{
					if (memberEntry.GetSession().TryGetTarget(out UserSession? remoteSession))
					{
						if (playerSession != null)
						{
							// send signal start to joining player
							WebSocketMessage_NetworkStartSignalling joiningPlayerMsg = new WebSocketMessage_NetworkStartSignalling();
							joiningPlayerMsg.msg_id = (int)EWebSocketMessageID.NETWORK_CONNECTION_START_SIGNALLING;
							joiningPlayerMsg.lobby_id = LobbyID;
							joiningPlayerMsg.user_id = memberEntry.UserID;
							joiningPlayerMsg.preferred_port = memberEntry.Port;
							joiningPlayerMsg.middleware_id = memberEntry.MiddlewareUserID;
							playerSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(joiningPlayerMsg)));

							// send register player
							WebSocketMessage_ACRegisterPlayer joiningPlayerACMsg = new WebSocketMessage_ACRegisterPlayer();
							joiningPlayerACMsg.msg_id = (int)EWebSocketMessageID.AC_REGISTER_PLAYER;
							joiningPlayerACMsg.user_id = memberEntry.UserID;
							joiningPlayerACMsg.mwid = memberEntry.MiddlewareUserID;
							playerSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(joiningPlayerACMsg)));
						}

						if (remoteSession != null)
						{
							// send the reverse to the existing player
							WebSocketMessage_NetworkStartSignalling existingPlayerMsg = new WebSocketMessage_NetworkStartSignalling();
							existingPlayerMsg.msg_id = (int)EWebSocketMessageID.NETWORK_CONNECTION_START_SIGNALLING;
							existingPlayerMsg.lobby_id = LobbyID;
							existingPlayerMsg.user_id = playerSession.m_UserID;
							existingPlayerMsg.preferred_port = userPreferredPort;
							existingPlayerMsg.middleware_id = playerSession.GetMiddlewareID();
							remoteSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(existingPlayerMsg)));

							// send register player
							WebSocketMessage_ACRegisterPlayer existingPlayerACMsg = new WebSocketMessage_ACRegisterPlayer();
							existingPlayerACMsg.msg_id = (int)EWebSocketMessageID.AC_REGISTER_PLAYER;
							existingPlayerACMsg.user_id = playerSession.m_UserID;
							existingPlayerACMsg.mwid = playerSession.GetMiddlewareID();
							remoteSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(existingPlayerACMsg)));
						}
					}
				}
			}
			// END NETWORK SIGNALLING

			// also update the lobby for everyone inside of it
			DirtyRetransmit();

			Console.WriteLine("User {0} joined lobby {1}: {2} (Slot was {3})", playerSession.m_UserID, LobbyID, true, slotIndex);
			return true;
			}
			finally
			{
				g_SlotLock.Release();
			}
		}

		public async Task RemoveMember(LobbyMember member)
		{
			// TODO_LOBBY: Optimize this
			Int64 UserID = member.UserID;			

			Console.WriteLine("User {0} left lobby {1}", UserID, LobbyID);

			LobbyMember placeholderMember = new LobbyMember(this, null, -1, String.Empty, String.Empty, 0, -1, -1, -1, EPlayerType.SLOT_OPEN, member.SlotIndex, true);
			Members[member.SlotIndex] = placeholderMember;
			TimeMemberLeft[UserID] = DateTime.UtcNow;

			// AC dergister
			WebSocketMessage_ACDeregisterPlayer remotePlayerAcMsg = new WebSocketMessage_ACDeregisterPlayer();
			remotePlayerAcMsg.msg_id = (int)EWebSocketMessageID.AC_DEREGISTER_PLAYER;
			remotePlayerAcMsg.user_id = member.UserID;
			remotePlayerAcMsg.mwid = member.MiddlewareUserID;
			foreach (LobbyMember remoteMember in Members)
			{
				if (remoteMember.GetSession().TryGetTarget(out UserSession? remoteSession))
				{
					if (remoteSession != null)
					{
						Console.WriteLine("Sent AC deregister for user {0} to user {1}", member.UserID, remoteMember.UserID);
						remoteSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(remotePlayerAcMsg)));
					}
				}
			}

			// send signal to disconnect (only if not ingame, ingame we let the client handle it so a service disconnect doesnt end the game)
			if (State != ELobbyState.INGAME)
			{
				WebSocketMessage_NetworkDisconnectPlayer remotePlayerMsg = new WebSocketMessage_NetworkDisconnectPlayer();
				remotePlayerMsg.msg_id = (int)EWebSocketMessageID.NETWORK_CONNECTION_DISCONNECT_PLAYER;
				remotePlayerMsg.lobby_id = LobbyID;
				remotePlayerMsg.user_id = member.UserID;

				// START NETWORK DISCONNECT
				foreach (LobbyMember remoteMember in Members)
				{
					if (remoteMember.GetSession().TryGetTarget(out UserSession? remoteSession))
					{
						if (remoteSession != null)
						{
							Console.WriteLine("Sent network disconnect for user {0} to user {1}", member.UserID, remoteMember.UserID);
							remoteSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(remotePlayerMsg)));
						}
					}
				}
				// END NETWORK DISCONNECT
			}

			await OnAfterPlayerLeft(UserID);

			DirtyRetransmit();
		}

		public int GetNumberOfHumans()
		{
			int numHumanMembers = 0;
			foreach (LobbyMember memberEntry in Members)
			{
				if (memberEntry.SlotState == EPlayerType.SLOT_PLAYER) // we only care about human members, AI can't play alone
				{
					++numHumanMembers;
				}
			}

			return numHumanMembers;
		}

		public void GetParticipantBreakdown(out int numHumans, out int numAI, out int numOpen, out int numClosed)
		{
			numHumans = 0;
			numAI = 0;
			numOpen = 0;
			numClosed = 0;

			foreach (LobbyMember memberEntry in Members)
			{
				if (memberEntry.SlotState == EPlayerType.SLOT_OPEN)
				{
					++numOpen;
				}
				else if (memberEntry.SlotState == EPlayerType.SLOT_CLOSED)
				{
					++numClosed;
				}
				else if (memberEntry.SlotState == EPlayerType.SLOT_EASY_AI || memberEntry.SlotState == EPlayerType.SLOT_MED_AI || memberEntry.SlotState == EPlayerType.SLOT_BRUTAL_AI)
				{
					++numAI;
				}
				else if (memberEntry.SlotState == EPlayerType.SLOT_PLAYER)
				{
					++numHumans;
				}
			}
		}

		public void DirtyRetransmit()
		{
			m_bIsDirty = true;
		}

		public async Task DirtyRetransmitToSingleMember(Int64 targetUserID)
		{
			var session = WebSocketManager.GetSessionFromUser(targetUserID, EUserSessionType.GameClient); // lobby member must be a game client
			if (session != null)
			{
				Console.WriteLine("[DIRTY LOBBY] Sending WS lobby update for lobby {0}", LobbyID);

				WebSocketMessage_CurrentLobbyUpdate lobbyUpdate = new WebSocketMessage_CurrentLobbyUpdate();
				lobbyUpdate.msg_id = (int)EWebSocketMessageID.LOBBY_CURRENT_LOBBY_UPDATE;
				byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lobbyUpdate));

				session.QueueWebsocketSend(bytesJSON);
			}
		}

		public LobbyMember? GetMemberFromUserID(Int64 user_id)
		{
			foreach (LobbyMember memberEntry in Members)
			{
				if (memberEntry.UserID == user_id && memberEntry.SlotState == EPlayerType.SLOT_PLAYER)
				{
					return memberEntry;
				}
			}
			return null;
		}

		public LobbyMember? GetMemberFromSlot(int slotIndex)
		{
			if (slotIndex >= 0 && slotIndex < Members.Length)
			{
				return Members[slotIndex];
			}
			
			return null;
		}

		private static String FixMapPathForGame(string strMapPath)
		{
			strMapPath = String.Format(@"{0}\{1}", Path.GetFileNameWithoutExtension(strMapPath), strMapPath);
			return strMapPath;
		}

		public async Task UpdateMap(AppDbContext _db, string strMap, string strMapPath, bool bOfficialMap, int newMaxPlayers)
		{
			int oldMaxPlayers = MaxPlayers;
			MapName = strMap;
			MapPath = FixMapPathForGame(strMapPath);
			IsMapOfficial = bOfficialMap;

			// close any slots that were open and now below the max, open anything not already occupied, up to new max
			foreach (var slot in Members)
			{
				if (slot.SlotIndex < newMaxPlayers)
				{
					if (slot.SlotState == EPlayerType.SLOT_CLOSED)
					{
						slot.SetPlayerSlotState(EPlayerType.SLOT_OPEN);
					}
				}

				if (slot.SlotIndex >= newMaxPlayers)
				{
					if (slot.SlotState == EPlayerType.SLOT_OPEN)
					{
						slot.SetPlayerSlotState(EPlayerType.SLOT_CLOSED);
					}
				}
			}

			// only if official, since we cant guarantee if they log in on another machine that the map is installed
			if (bOfficialMap)
			{
				await Database.Users.SetFavorite_Map(_db, Owner, strMapPath);
			}

			DirtyRetransmit();
		}

		public async Task UpdateStartingCash(AppDbContext _db, UInt32 newStartingCash)
		{
			StartingCash = newStartingCash;

			await Database.Users.SetFavorite_StartingMoney(_db, Owner, (int)newStartingCash);

			DirtyRetransmit();
		}

		public async Task UpdateLimitSuperweapons(AppDbContext _db, bool bLimitSuperweapons)
		{
			IsLimitSuperweapons = bLimitSuperweapons;

			await Database.Users.SetFavorite_LimitSuperweapons(_db, Owner, bLimitSuperweapons);

			DirtyRetransmit();
		}

		public void ForceReady()
		{
			foreach (var member in Members)
			{
				member.SetReadyState(true);
			}

			DirtyRetransmit();
		}

		private int m_cachedAtStart_numHumans = -1;
		private int m_cachedAtStart_numOpen = -1;
		private int m_cachedAtStart_numClosed = -1;
		private int m_cachedAtStart_numAI = -1;

		// TODO: Really, client also shouldnt upload data we arent going to process in this situation, its wasteful
		public bool WasPVPAtStart()
		{
			// debug
#if DEBUG
			return true;
#endif

			// use cached data, we can call this after people left etc

			// We are a PVP lobby if we have > 1 human, and 0 AI
			bool bWasPVP = m_cachedAtStart_numHumans > 1 && m_cachedAtStart_numAI == 0;
			return bWasPVP;
		}

		public bool HadAIAtStart()
		{
			// debug
#if DEBUG
			return false;
#endif

			// use cached data, we can call this after people left etc
			return m_cachedAtStart_numAI > 0;
		}

		public async Task UpdateState(ELobbyState state)
		{
			State = state;

			// if start, init our AC probe
			if (state == ELobbyState.INGAME)
			{
				// cache starting data, we use this later in replay/ss upload
				GetParticipantBreakdown(out int numHumans, out int numAI, out int numOpen, out int numClosed);
				m_cachedAtStart_numHumans = numHumans;
				m_cachedAtStart_numOpen = numOpen;
				m_cachedAtStart_numClosed = numClosed;
				m_cachedAtStart_numAI = numAI;

				// lobby cant have AI and must have at least 2 human players at some point
				if (WasPVPAtStart() && !HadAIAtStart())
				{
					try
					{
						// create placeholder
						using var scope = ServiceLocator.Services.CreateScope();
						var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
						await using var db = await factory.CreateDbContextAsync();
						await Database.MatchHistory.CreatePlaceholderMatchHistory(db, this);
					}
					catch (Exception ex)
					{
						Console.WriteLine($"[ERROR] UpdateState placeholder creation failed: {ex.Message}");
						SentrySdk.CaptureException(ex);
					}

					// calculate first probe time
					CalculateNextProbeTime(true);
				}
				else
				{
					// disable probes
					m_NextProbe = 0;
				}

				
			}

			DirtyRetransmit();
		}

		public void UpdateJoinability(ELobbyJoinability newJoinability)
		{
			// must be a custom match
			if (LobbyType == ELobbyType.CustomGame)
			{
                LobbyJoinability = newJoinability;
            }
		}

		public void UpdateMaxCameraHeight(UInt16 maxCamHeight)
		{
			if (maxCamHeight >= 210 && maxCamHeight <= 1000)
			{
				MaximumCameraHeight = maxCamHeight;
				DirtyRetransmit();
			}
		}

		public void ResetReadyStates()
		{
			foreach (LobbyMember member in Members)
			{
				member.SetReadyState(false);
			}

			DirtyRetransmit();
		}

	}
	public class LobbyMember
	{
		public Int64 UserID { get; private set; } = -999999;
		public string DisplayName { get; private set; } = "";

		[JsonIgnore]
		public string DisplayNameNotDeduped { get; set; } = ""; // internal only, used for determining dedupe counts

		private bool m_Ready;
		public bool IsReady
		{
			get
			{
				// host is always ready
				if (SlotIndex == 0)
				{
					return true;
				}

				// AI is always ready
				if (IsAI())
				{
					return true;
				}

				return m_Ready;
			}
			set { m_Ready = value; }
		}

		public UInt16 Port { get; private set; } = 0;

		public void UpdateSlotIndex(UInt16 index)
		{
			SlotIndex = index;
		}
		public int Side { get; private set; } = 0;
		public int Team { get; private set; } = -1;
		public int Color { get; private set; } = 0;
		public int StartingPosition { get; private set; } = 0;
		public bool HasMap { get; private set; } = false;

		public EPlayerType SlotState { get; private set; } = 0;
		public UInt16 SlotIndex { get; private set; } = 0;
		public string Region { get; private set; } = "Unknown";
		public string MiddlewareUserID { get; private set; } = String.Empty;

		[JsonIgnore] // cant serialize refs
		private WeakReference<Lobby?> CurrentLobby = new(null);

		[JsonIgnore]
		private WeakReference<UserSession?> PlayerSession = new(null);

		public WeakReference<UserSession?> GetSession()
		{
			return PlayerSession;
		}

		public LobbyMember(Lobby owningLobby, UserSession? owningSession, Int64 UserID_in, string DisplayName_in, string strUndedupedDisplayName, UInt16 Port_in, int Side_in, int Color_in, int StartingPosition_in, EPlayerType SlotState_in, UInt16 SlotIndex_in, bool bHasMap_in)
		{
			CurrentLobby = new WeakReference<Lobby?>(owningLobby);
			PlayerSession = new WeakReference<UserSession?>(owningSession);

			UserID = UserID_in;
			DisplayName = DisplayName_in;
			DisplayNameNotDeduped = strUndedupedDisplayName;
			Port = Port_in;
			Side = Side_in;
			Color = Color_in;
			StartingPosition = StartingPosition_in;
			HasMap = bHasMap_in;
			SlotState = SlotState_in;
			SlotIndex = SlotIndex_in;

			// default slots are created with null
			if (owningSession != null)
			{
				MiddlewareUserID = owningSession.GetMiddlewareID();
			}
			else
			{
				MiddlewareUserID = String.Empty;
			}

			IsReady = false;
			Region = owningSession == null ? "Unknown" : owningSession.GetFullContinentName();
		}

		public bool IsHuman() {  return SlotState == EPlayerType.SLOT_PLAYER; }
		public bool IsAI() { return SlotState == EPlayerType.SLOT_EASY_AI || SlotState == EPlayerType.SLOT_MED_AI || SlotState == EPlayerType.SLOT_BRUTAL_AI; }

		private void DirtyRetransmit()
		{
			CurrentLobby.TryGetTarget(out Lobby? lobby);
			lobby?.DirtyRetransmit();
		}

		public void SetReadyState(bool bReady)
		{
			IsReady = bReady;

			DirtyRetransmit();
		}

		public void SetPlayerSlotState(EPlayerType newState)
		{
			SlotState = newState;

			// AI is always ready
			if (IsAI())
			{
				IsReady = true;
			}

			DirtyRetransmit();
		}

		public async Task UpdateSide(AppDbContext _db, int newSide, int start_pos)
		{
			Side = newSide;

			await Database.Users.SetFavorite_Side(_db, UserID, newSide);

			DirtyRetransmit();
		}

		public async Task UpdateColor(AppDbContext _db, int newColor)
		{
			Color = newColor;
			await Database.Users.SetFavorite_Color(_db, UserID, newColor);

			DirtyRetransmit();
		}

		public void UpdateStartPos(int startpos)
		{
			StartingPosition = startpos;

			DirtyRetransmit();
		}

		public void UpdateTeam(int team)
		{
			Team = team;

			DirtyRetransmit();
		}

		public void UpdateHasMap(bool bHasMap)
		{
			HasMap = bHasMap;

			DirtyRetransmit();
		}
	}

	public enum ELobbyType
	{
		CustomGame = 0,
		QuickMatch = 1
	}

	public enum EKnownAnticheatID
	{
		NONE = -1,
		GO_INTEGRATED_AC = 0,
		EASY_ANTICHEAT = 9481
	}


	public class LobbyManager
	{
		private ConcurrentDictionary<Int64, Lobby> m_dictLobbies = new();

		private Int64 m_NextLobbyID = 0;

		private readonly IServiceProvider _services;

		public LobbyManager(IServiceProvider services)
		{
			_services = services;
		}

		public async Task Cleanup()
		{
			// Remove any lobby that has 0 members and has been around for a bit (enough time for host to join)
			List<Lobby> lstLobbiesToRemove = new List<Lobby>();
			foreach (var kvPair in m_dictLobbies)
			{
				Lobby iterLobby = kvPair.Value;

				TimeSpan timeSinceCreated = DateTime.UtcNow.Subtract(iterLobby.TimeCreated);
				if (iterLobby.GetNumberOfHumans() == 0 && timeSinceCreated.TotalMinutes >= 1.0)
				{
                    Console.WriteLine("Garbage collecting lobby {0}", iterLobby.LobbyID);

					// mark for removal
					lstLobbiesToRemove.Add(iterLobby);
				}
			}

			foreach (Lobby lobbyToRemove in lstLobbiesToRemove)
			{
                // remove it, also commit it + update leaderboard
                await DeleteLobby(lobbyToRemove);
            }
		}

		private async void HandleLobbyNeedsDestroyed(Lobby lobby)
		{
			await DeleteLobby(lobby);
		}

		public async Task<Int64> CreateLobby(AppDbContext _db, UserSession owningSession, string strOwnerDisplayName, string strName, string strMapName, string strMapPath, bool bMapOfficial, int maxPlayers, string HostIPAddr,
			UInt16 hostPreferredPort, bool bVanillaTeams, bool bTrackStats, UInt32 default_starting_cash, bool bPassworded, String strPassword, Int16 parentNetworkRoom, bool bAllowObservers,
			UInt16 maxCamHeight, UInt32 exe_crc, UInt32 ini_crc, ELobbyType lobbyType, EKnownAnticheatID anticheatID)
		{
			Console.WriteLine("Created lobby");
			// cant own two lobbies at once, unless in gameplay
			await CleanupUserLobbiesNotStarted(owningSession.m_UserID);

			Console.WriteLine("[Source 3] User {0} Leave Any Lobby", owningSession.m_UserID);
			this.LeaveAnyLobby(owningSession.m_UserID);

			int rng_seed = new Random().Next();

			Int64 newLobbyID = m_NextLobbyID;
			++m_NextLobbyID;

			// load and apply user preferences (custom game only)
			bool bLimitSuperweapons = false;
			UInt32 starting_cash = default_starting_cash;
			if (lobbyType == ELobbyType.CustomGame)
			{
				UserLobbyPreferences? lobbyPrefs = await Database.Users.GetUserLobbyPreferences(_db, owningSession.m_UserID);
				bLimitSuperweapons = lobbyPrefs != null ? lobbyPrefs.favorite_limit_superweapons : false; // limit superweapons (NOTE: not present in clientside create lobby UI)

				// sane defaults
				if (lobbyPrefs != null && lobbyPrefs.favorite_starting_money > 0)
				{
					starting_cash = (uint)lobbyPrefs.favorite_starting_money;
				}
			}

			Lobby newLobby = new Lobby(newLobbyID, owningSession, strName, ELobbyState.GAME_SETUP, strMapName, strMapPath, bVanillaTeams, starting_cash, bLimitSuperweapons, bTrackStats, bPassworded, strPassword, bMapOfficial, rng_seed, parentNetworkRoom, bAllowObservers, maxCamHeight, exe_crc, ini_crc, maxPlayers, lobbyType, anticheatID);
			m_dictLobbies[newLobbyID] = newLobby;

			

			// subscribe for self-destruct event
			newLobby.OnLobbyNeedsDestroyed += HandleLobbyNeedsDestroyed;

			// and join
			if (lobbyType != ELobbyType.QuickMatch) // quickmatch requires a manual join, because the service creates the lobby for them, so the client knows nothing about it without a manual join
			{
				bool bJoined = await JoinLobby(_db, newLobby, owningSession, strOwnerDisplayName, hostPreferredPort, true);
			}

			newLobby.DirtyRetransmit();

			// inform
			await WebSocketManager.SendNewOrDeletedLobbyToAllNetworkRoomMembers(parentNetworkRoom);

			return newLobbyID;
		}

		public async Task Tick()
		{
			foreach (var kvPair in m_dictLobbies)
			{
				await kvPair.Value.Tick();
			}
		}

		public async Task<bool> JoinLobby(AppDbContext _db, Lobby lobby, UserSession playerSession, string strDisplayName, UInt16 userPreferredPort, bool bHasMap)
		{
			UserLobbyPreferences? lobbyPrefs = await Database.Users.GetUserLobbyPreferences(_db, playerSession.m_UserID);

			if (lobbyPrefs != null)
			{
				bool bAdded = await lobby.AddMember(playerSession, strDisplayName, userPreferredPort, bHasMap, lobbyPrefs);
				return bAdded;
			}

			return false;
		}

		public int GetNumLobbies()
		{
			return m_dictLobbies.Count;
		}

		public async Task CleanupUserLobbiesNotStarted(Int64 UserID)
		{
			List<Lobby> ownedLobbies = GetPlayerOwnedLobbies(UserID);
			foreach (Lobby ownedLobby in ownedLobbies)
			{
				if (ownedLobby.State == ELobbyState.GAME_SETUP) // only those in setup, in game games can continue
				{
					await DeleteLobby(ownedLobby);
				}
			}
		}

		public List<Lobby> GetAllLobbies(Int16 networkRoomID, bool bIncludePassword, bool bAllowInSetup, bool bAllowInGame, bool bAllowCompleted, bool bIncludeAllNetworkRooms)
		{
			List<Lobby> listLobbies = new List<Lobby>();
			foreach (var kvp in m_dictLobbies)
			{
				Lobby lobby = kvp.Value;
				if (!bIncludeAllNetworkRooms && lobby.NetworkRoomID != networkRoomID)
				{
					continue;
				}

				bool bMeetsCriteria = true;
				if (lobby.IsPassworded && !bIncludePassword)
				{
					bMeetsCriteria = false;
				}

				// don't allow quickmatch to show
				if (lobby.LobbyType == ELobbyType.QuickMatch)
				{
					bMeetsCriteria = false;
				}

				if (lobby.State == ELobbyState.GAME_SETUP && !bAllowInSetup)
				{
					bMeetsCriteria = false;
				}

				if (lobby.State == ELobbyState.INGAME && !bAllowInGame)
				{
					bMeetsCriteria = false;
				}

				if (lobby.State == ELobbyState.COMPLETE && !bAllowCompleted)
				{
					bMeetsCriteria = false;
				}

				if (bMeetsCriteria)
				{
					listLobbies.Add(lobby);
				}
			}

			return listLobbies;
		}

		public Lobby? GetLobby(Int64 lobbyID)
		{
			if (m_dictLobbies.TryGetValue(lobbyID, out Lobby? lobby))
			{
				return lobby;
			}

			return null;
		}

		public Lobby? GetLobbyFiltered(Int64 lobbyID, bool bIncludePassword, bool bAllowInSetup, bool bAllowInGame, bool bAllowCompleted)
		{
			if (m_dictLobbies.TryGetValue(lobbyID, out Lobby? lobby))
			{
				bool bMeetsCriteria = true;

				if (lobby.IsPassworded && !bIncludePassword)
				{
					bMeetsCriteria = false;
				}

				if (lobby.State == ELobbyState.GAME_SETUP && !bAllowInSetup)
				{
					bMeetsCriteria = false;
				}

				if (lobby.State == ELobbyState.INGAME && !bAllowInGame)
				{
					bMeetsCriteria = false;
				}

				if (lobby.State == ELobbyState.COMPLETE && !bAllowCompleted)
				{
					bMeetsCriteria = false;
				}

				if (bMeetsCriteria)
				{
					return lobby;
				}
				else
				{
					return null;
				}
			}
			return null;
		}

		public Lobby? GetPlayerParticipantLobby(Int64 userID)
		{
			// TODO_LOBBY: Optimize this, maintain a dictionary of userid
			foreach (Lobby lobbyInst in m_dictLobbies.Values)
			{
				if (lobbyInst.GetMemberFromUserID(userID) != null)
				{
					return lobbyInst;
				}
			}

			return null;
		}

		public List<Lobby> GetPlayerOwnedLobbies(Int64 userID)
		{
			// NOTE: This function doesnt account for games in progress, the callee must process those (the owner can have left and orphaned the session if in-game)
			// TODO_LOBBY: Optimize this, maintain a dictionary of userid
			List<Lobby> lstLobbies = new List<Lobby>();
			foreach (Lobby lobbyInst in m_dictLobbies.Values)
			{
				if (lobbyInst.Owner == userID)
				{
					lstLobbies.Add(lobbyInst);
				}
			}

			return lstLobbies;
		}

		public async Task LeaveSpecificLobby(Int64 userID, Int64 lobbyID)
		{
			Lobby? targetLobby = GetLobby(lobbyID);
			if (targetLobby != null)
			{
				LobbyMember? memberEntry = targetLobby.GetMemberFromUserID(userID);
				if (memberEntry != null)
				{
					Console.WriteLine("User {0} Leave Specific Lobby", userID);
					await targetLobby.RemoveMember(memberEntry);
				}
			}
		}

		public async Task LeaveAnyLobby(Int64 userID)
		{
			foreach (Lobby lobbyInst in m_dictLobbies.Values)
			{
				LobbyMember? member = lobbyInst.GetMemberFromUserID(userID);
				if (member != null)
				{
					Console.WriteLine("User {0} Leave Any Lobby", userID);
					await lobbyInst.RemoveMember(member);
				}
			}
		}

		public async Task<bool> DeleteLobby(Lobby lobby)
		{
			try
			{
				using var scope = _services.CreateScope();
				var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
				await using var db = await factory.CreateDbContextAsync();

				if (lobby.State != ELobbyState.COMPLETE)
				{
					// make done
					await lobby.UpdateState(ELobbyState.COMPLETE);

					// attempt to commit it
					await Database.MatchHistory.CommitLobbyToMatchHistory(db, lobby);
				}

				// delete
				bool bRemoved = m_dictLobbies.Remove(lobby.LobbyID, out _);
				await WebSocketManager.SendNewOrDeletedLobbyToAllNetworkRoomMembers(lobby.NetworkRoomID);

				// only do this once
				if (bRemoved)
				{
					// unsubscribe from self-destruct event
					lobby.OnLobbyNeedsDestroyed -= HandleLobbyNeedsDestroyed;

					// make sure we have a winner
					await Database.MatchHistory.DetermineLobbyWinnerIfNotPresent(db, lobby);

					// Post match result to external leaderboard API for every lobby type.
					// Only QuickMatch responses are expected to carry a ratings body.
					await ExternalLeaderboardsClient.PostMatchResultAsync(db, lobby);
				}

				return bRemoved;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] DeleteLobby failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return false;
			}
		}

		public bool IsUserInLobby(Lobby lobby, Int64 user_id)
		{
			LobbyMember? member = lobby.GetMemberFromUserID(user_id);
			return member != null;
		}
	}
}