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

using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.EntityFrameworkCore;
using MySqlX.XDevAPI;
using Org.BouncyCastle.Tls;
using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ZstdSharp.Unsafe;

namespace GenOnlineService
{
	public static class Constants
	{
		public const int GENERALS_ONLINE_VERSION = 1;
		public const int GENERALS_ONLINE_NET_VERSION = 1;
		public const int GENERALS_ONLINE_SERVICE_VERSION = 1;

		public const UInt16 g_DefaultCameraMaxHeight = 310;

		public const int OBSERVER_SIDE_VALUE = 1;
	}
	public class RoomMember
	{
		public RoomMember(Int64 a_UserID, string strName, bool admin)
		{
			UserID = a_UserID;
			Name = strName;
			IsAdmin = admin;
		}

		public Int64 UserID { get; set; } = -1;
		public String Name { get; set; } = String.Empty;
		public bool IsAdmin { get; set; } = false;
	}

	public enum EPendingLoginState
	{
		None = -1,
		Waiting = 0,
		LoginSuccess = 1,
		LoginFailed = 2
	};

	public enum EIPVersion
	{
		IPV4 = 0,
		IPV6
	}

	public enum EConnectionStateClient
	{
		NOT_CONNECTED,
		CONNECTING_DIRECT,
		FINDING_ROUTE,
		CONNECTED_DIRECT,
		CONNECTION_FAILED,
		CONNECTION_DISCONNECTED
	};

	public enum EConnectionState
	{
		NOT_CONNECTED,
		CONNECTING_DIRECT,
		CONNECTING_RELAY,
		CONNECTED_DIRECT,
		CONNECTED_RELAY,
		CONNECTION_FAILED
	};

	public enum ELobbyState
	{
		UNKNOWN = -1,
		GAME_SETUP,
		INGAME,
		COMPLETE
	}

	public enum ERoomFlags : int
	{
		ROOM_FLAGS_DEFAULT = 0,
		ROOM_FLAGS_SHOW_ALL_MATCHES = 1
	}
	public class RoomData
	{
		public int id { get; set; } = -1;
		public string name { get; set; } = "";
		public ERoomFlags flags { get; set; } = ERoomFlags.ROOM_FLAGS_DEFAULT;
	}

	public class UserSocialContainer
	{
		public HashSet<Int64> Friends { get; set; } = new HashSet<Int64>();
		public HashSet<Int64> PendingRequests { get; set; } = new HashSet<Int64>();
		public HashSet<Int64> Blocked { get; set; } = new HashSet<Int64>();
	}

	// NOTE: If you add to the below, make sure you initialize the dictionary
	public enum EUserSessionType
	{
		None = -1,
		GameClient = 0,
		ChatClient = 1,
		GameLauncher = 2
	}

	public static class SocialHelper
	{
		public static void NotifyFriendslistDirty(Int64 userID)
		{
			// serialize
			WebSocketMessage_Social_FriendsListDirty friendsListDirtyEvent = new();
			friendsListDirtyEvent.msg_id = (int)EWebSocketMessageID.SOCIAL_FRIENDS_LIST_DIRTY;
			byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(friendsListDirtyEvent));

			// send it to all sessions that are subscribed for realtime updates
			WebSocketManager.GetAllDataFromUser(userID).ForEach(session =>
			{
				if (session.IsSubscribedToRealtimeSocialUpdates())
				{
					session.QueueWebsocketSend(bytesJSON);
				}
			});
		}
	}

	public static class WebsocketHelper
	{
		public static void SendToAllSessionsOfUser(Int64 userID, byte[] bytesData)
		{
			WebSocketManager.GetAllDataFromUser(userID).ForEach(session =>
			{
				session.QueueWebsocketSend(bytesData);
			});
		}
	}




	public static class KnownClients
	{
		public enum EKnownClients
		{
			unknown = -1,
			gen_online_30hz = 0,
			gen_online_60hz = 1,
			genhub = 2,
			communityoutpost_chat = 3,
			superhackers_community_patch_client = 4,
			custom_third_party_client = 5
		}

		public static ConcurrentDictionary<EKnownClients, EUserSessionType> KnownClientSessionTypes = new()
		{
			[EKnownClients.gen_online_30hz] = EUserSessionType.GameClient,
			[EKnownClients.gen_online_60hz] = EUserSessionType.GameClient,
			[EKnownClients.genhub] = EUserSessionType.GameLauncher,
			[EKnownClients.communityoutpost_chat] = EUserSessionType.ChatClient,
			[EKnownClients.superhackers_community_patch_client] = EUserSessionType.GameClient,
			[EKnownClients.custom_third_party_client] = EUserSessionType.GameClient
		};
	}



	// TODO
	static class WebSocketManager
	{
		public static int g_PeakConnectionCount = 0;
		public static async Task<UserWebSocketInstance> CreateSession(AppDbContext _db, EUserSessionType sessionType, bool bIsReconnect, Int64 ownerID, KnownClients.EKnownClients client_id, string ipAddr, string strContinent, string strCountry, double dLatitude, double dLongitude, bool bIsAdmin)
		{
			string strDisplayName = await Database.Users.GetDisplayName(_db, ownerID);

			// if we have cache data, that means its a reconnect, noraml connections go through login flows which reset cache data
			UserSession? userCacheData = WebSocketManager.GetSessionFromUser(ownerID, sessionType);
			if (bIsReconnect)
			{
				// this is a reconnect, re-use cache
				Console.WriteLine("--> WEBSOCKET RECONNECT");

				// if its a reconnect, and we dont have cache OR shared data, its probably a server restart, so return null
				if (userCacheData == null)
				{
					return null;
				}
				else
				{
					// depending on what our ref count was, we MAY need to recreate our shared data
					if (m_dictSharedUserData.TryGetValue(ownerID, out SharedUserData? sharedData))
					{
						// nothing to do here for shared user data, since the session was abandoned but not fully destroyed, it should still have user data
					}
					else
					{
						// get and cache social container
						UserSocialContainer socialContainer = new();
						socialContainer.Friends = await Database.Social.GetFriends(_db, ownerID);
						socialContainer.PendingRequests = await Database.Social.GetPendingFriendsRequests(_db, ownerID);
						socialContainer.Blocked = await Database.Social.GetBlocked(_db, ownerID);

						// get stats
						PlayerStats GameStats = await Database.UserStats.GetPlayerStats(_db, ownerID);

						m_dictSharedUserData[ownerID] = new SharedUserData(ownerID, socialContainer, strDisplayName, bIsAdmin, GameStats);
					}

					// clear abandoned flag
					userCacheData.MarkNotAbandoned();

					// NOTE: We intentionally do NOT call ClearPlayerIngameAbandon on reconnect.
					// RecordPlayerIngameAbandon only stores the FIRST disconnect time.  Clearing
					// it here was the root cause of the wrong-winner ELO bug:
					//   A kills game → RecordPlayerIngameAbandon(A) set
					//   A relaunches and reconnects → ClearPlayerIngameAbandon(A) wipes the record
					//   Fallback uses TimeMemberLeft[A] (set much later) > IngameAbandon[B]
					//   → A incorrectly wins
				}
			}
			else
			{
				Console.WriteLine("--> WEBSOCKET CONNECT");

				// how many other sessions do they have online?
				bool bIsFirstSessionForUser = WebSocketManager.GetAllDataFromUser(ownerID).Count == 0;

				// kill any existing sessions for this user of same session type
				if (m_dictWebsockets[sessionType].TryGetValue(ownerID, out UserWebSocketInstance? existingSession))
				{
					Console.WriteLine("Killing existing session for {0} ({1})", ownerID, strDisplayName);
					await DeleteSession(ownerID, sessionType, existingSession, !bIsReconnect);
				}

				// get and cache social container
				UserSocialContainer socialContainer = new();
				socialContainer.Friends = await Database.Social.GetFriends(_db, ownerID);
				socialContainer.PendingRequests = await Database.Social.GetPendingFriendsRequests(_db, ownerID);
				socialContainer.Blocked = await Database.Social.GetBlocked(_db, ownerID);

				// get stats
				PlayerStats GameStats = await Database.UserStats.GetPlayerStats(_db, ownerID);

				userCacheData = new UserSession(ownerID, sessionType, client_id, strContinent, strCountry, dLatitude, dLongitude);
				m_dictUserSessions[sessionType][ownerID] = userCacheData;
				
				// TODO_SOCIAL: Move this to a class
				// inform any friends who are online that this person just came online (if they had no other sessions prior)
				if (bIsFirstSessionForUser)
				{
					WebSocketMessage_Social_FriendStatusChanged friendStatusChangedEvent = new();
					friendStatusChangedEvent.msg_id = (int)EWebSocketMessageID.SOCIAL_FRIEND_ONLINE_STATUS_CHANGED;
					friendStatusChangedEvent.display_name = strDisplayName;
					friendStatusChangedEvent.online = true;
					byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(friendStatusChangedEvent));

					// friends are reciprocal so we can just iterate our friends
					foreach (Int64 friendID in socialContainer.Friends)
					{
						WebsocketHelper.SendToAllSessionsOfUser(friendID, bytesJSON);
					}
				}

				// TODO_EFCORE: check reconnect again, reconnect shouldnt increment ref count (nothing is done above)
				// create or increment shared data
				if (m_dictSharedUserData.TryGetValue(ownerID, out SharedUserData? sharedData))
				{
					// increment
					sharedData.IncrementRefCount();
				}
				else
				{
					m_dictSharedUserData[ownerID] = new SharedUserData(ownerID, socialContainer, strDisplayName, bIsAdmin, GameStats);
				}
			}

			// now create a websocket, we always do this whether its reconnect or not, only data is persistent
			// NOTE: on the reconnect path the previous websocket may still be physically open (e.g. its receive loop
			// is parked in a 30s ReceiveAsync). Overwriting the entry without closing it leaks a zombie connection
			// that keeps running and sending on a socket nobody owns any more.
			if (m_dictWebsockets[sessionType].TryRemove(ownerID, out UserWebSocketInstance? supersededSess) && supersededSess != null)
			{
				Console.WriteLine("Closing superseded websocket for {0} ({1})", ownerID, strDisplayName);
				await supersededSess.CloseAsync(WebSocketCloseStatus.NormalClosure, "Superseded by a newer connection");
			}

			UserWebSocketInstance newSess = new UserWebSocketInstance(sessionType, ownerID);
			m_dictWebsockets[sessionType][ownerID] = newSess;

			// update last login and last ip
			await Database.Users.UpdateLastLoginData(_db, ownerID, ipAddr);

			// TODO_EFCORE: Optimize this, dont iterate all the time
			int numSessions = WebSocketManager.GetNumberOfUsersOnline();

			if (numSessions > g_PeakConnectionCount)
			{
				g_PeakConnectionCount = numSessions;
			}

			Console.Title = String.Format("GenOnline - {0} players", numSessions);

			SharedUserData? sharedUserData = WebSocketManager.GetSharedDataForUser(ownerID);

			// inform the user of any pending friends activities
			{
				int numOnline = 0;
				int numPending = sharedUserData.GetSocialContainer().PendingRequests.Count;

				foreach (Int64 friendID in sharedUserData.GetSocialContainer().Friends)
				{
					if (WebSocketManager.GetSessionFromUser(friendID, sessionType) != null)
					{
						++numOnline;
					}
				}

				if (numOnline > 0 || numPending > 0)
				{
					WebSocketMessage_FriendsOverallStatusUpdate outboundMsg = new WebSocketMessage_FriendsOverallStatusUpdate();
					outboundMsg.msg_id = (int)EWebSocketMessageID.SOCIAL_FRIENDS_OVERALL_STATUS_UPDATE;
					outboundMsg.num_online = numOnline;
					outboundMsg.num_pending = numPending;
					byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsg));
					await newSess.SendAsync(bytesJSON, WebSocketMessageType.Text);
				}
			}

			return newSess;
		}

		public static async Task Tick()
		{
			// Give the entire tick a 20 ms deadline. All users drain concurrently via
			// Task.WhenAll, so a slow/stuck client cannot delay others. If the deadline
			// fires, the CancellationToken propagates into each in-flight SendAsync and
			// into the dequeue loop guard, so the stuck user is skipped and their unsent
			// messages stay in the queue for the next tick.
			using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
			await Task.WhenAll(m_dictUserSessions.Values.SelectMany(inner => inner.Values).Select(sess => sess.TickWebsocket(cts.Token)));
		}

		public static int GetNumberOfUsersOnline()
		{
			int numSessions = 0;
			foreach (var kvPair in m_dictUserSessions)
			{
				numSessions += kvPair.Value.Count;
			}

			return numSessions;
		}

		public static async Task CheckForTimeouts()
		{
			List<UserWebSocketInstance> lstSessionsToDestroy = new();
			foreach (var sessionDataByClient in m_dictWebsockets)
			{
				foreach (var sessionData in sessionDataByClient.Value)
				{
#if DEBUG
					const int timeoutVal = 60000 * 10;
#else
			const int timeoutVal = 20000;
#endif
					if (sessionData.Value.GetTimeSinceLastPing() >= timeoutVal)
					{
						lstSessionsToDestroy.Add(sessionData.Value);
					}
					else
					{
						await sessionData.Value.SendPong();
					}
				}
			}

			foreach (UserWebSocketInstance wsSess in lstSessionsToDestroy)
			{
				Console.WriteLine("Timing out WS session for {0}", wsSess.m_UserID);
				await DeleteSession(wsSess.m_UserID, wsSess.m_SessionType, wsSess, false);
			}

			// do we need to clear out cache entries?
			List<Tuple<Int64, EUserSessionType>> lstCacheEntriesToDestroy = new();
			foreach (var sessionDataPerClientType in m_dictUserSessions)
			{
				foreach (var sessionData in sessionDataPerClientType.Value)
				{
					if (sessionData.Value.IsAbandoned())
					{
						if (sessionData.Value.NeedsCleanup())
						{
							lstCacheEntriesToDestroy.Add(new Tuple<Int64, EUserSessionType>(sessionData.Key, sessionData.Value.GetSessionType()));
						}
					}
				}
			}

			foreach (Tuple<Int64, EUserSessionType> userData in lstCacheEntriesToDestroy)
			{
				await ClearDataFromUser(userData.Item1, userData.Item2);
			}
		}

		public static async Task DeleteSession(Int64 user_id, EUserSessionType sessionType, UserWebSocketInstance? oldWS, bool bShouldInvalidatePlayerCacheToBlockReconnect)
		{
			UserSession? sourceData = WebSocketManager.GetSessionFromUser(user_id, sessionType);
			SharedUserData? sourceSharedData = WebSocketManager.GetSharedDataForUser(user_id);

			bool bIsStaleSocket = false;

			if (oldWS != null)
			{
				// has this websocket already been superseded by a newer connection for the same user? if so it is a
				// stale/zombie socket and must not be allowed to abandon or tear down the live session
				if (m_dictWebsockets[sessionType].TryGetValue(user_id, out UserWebSocketInstance? currentWS))
				{
					bIsStaleSocket = !ReferenceEquals(currentWS, oldWS);
				}

				try
				{
					// dont remove by ID, user could have re-opened another websocket open via reconnection, remove by instance, if its not there, thats OK, it was already closed and the new instance is a reconnect
					var item = m_dictWebsockets[sessionType].First(kvp => kvp.Value == oldWS); // safe to lookup by sessionType here since we only ever remove old WS of the same type
					m_dictWebsockets[sessionType].Remove(item.Key, out UserWebSocketInstance? destroyedSess);

					if (destroyedSess != null)
					{
						await destroyedSess.CloseAsync(WebSocketCloseStatus.NormalClosure, "User signed in from another point of presence [A]");
					}
				}
				catch
				{

				}

				if (bIsStaleSocket)
				{
					// nothing else to do - the user is still connected on the newer socket
					await oldWS.CloseAsync(WebSocketCloseStatus.NormalClosure, "Superseded by a newer connection");
					return;
				}
			}

			if (bShouldInvalidatePlayerCacheToBlockReconnect)
			{
				// NOTE: ClearDataFromUser owns the shared data ref count decrement (and GC) for this path - doing it
				// again here would drop the count twice for a single disconnect and tear down the shared data while
				// the user still has other live sessions
				await WebSocketManager.ClearDataFromUser(user_id, sessionType);
			}
			else
			{
				// mark it as abandoned for now to start the expiration timer
				if (sourceData != null)
				{
					sourceData.MarkAbandoned();

					// If the player was in an active game when their connection dropped, record the
					// abandon time NOW (before any lobby-structure cleanup runs).  This timestamp is
					// the authoritative "who quit first" signal used by DetermineLobbyWinnerIfNotPresent,
					// and must be captured here rather than in RemoveMember, because RemoveMember may
					// execute much later (e.g. 30 s after the first abandonment) or at an unexpected
					// time (e.g. when the other player reconnects with a fresh session and their old
					// session is forcibly evicted, causing them to appear as "last to leave" even
					// though they stayed in the game longer than the opponent who quit first).
					try
					{
						var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();
						if (sourceData.currentLobbyID != -1)
						{
							Lobby? ingameLobby = lobbyManager.GetLobby(sourceData.currentLobbyID);
							if (ingameLobby != null && ingameLobby.State == ELobbyState.INGAME)
							{
								ingameLobby.RecordPlayerIngameAbandon(user_id);
							}
							else
							{
								Console.WriteLine($"[WARN] DeleteSession(false): skipped RecordPlayerIngameAbandon for user {user_id} — lobby={sourceData.currentLobbyID} found={ingameLobby != null} state={ingameLobby?.State}");
							}
						}
						else
						{
							Console.WriteLine($"[WARN] DeleteSession(false): skipped RecordPlayerIngameAbandon for user {user_id} — currentLobbyID==-1 (session has no lobby)");
						}
					}
					catch (Exception ex)
					{
						Console.WriteLine($"[WARN] Failed to record in-game abandon for user {user_id}: {ex.Message}");
					}
				}
			}

			// NOTE: They only went offline if ref count became 0, otherwise they're still online somewhere else
			if (sourceData != null && sourceSharedData != null && sourceSharedData.NeedsGC())
			{
				// TODO_SOCIAL: Move this to a class
				// inform any friends who are online that this person just came online
				WebSocketMessage_Social_FriendStatusChanged friendStatusChangedEvent = new();
				friendStatusChangedEvent.msg_id = (int)EWebSocketMessageID.SOCIAL_FRIEND_ONLINE_STATUS_CHANGED;
				friendStatusChangedEvent.display_name = sourceSharedData.m_strDisplayName;
				friendStatusChangedEvent.online = false;
				byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(friendStatusChangedEvent));

				if (sourceData != null)
				{
					// friends are reciprocal so we can just iterate our friends
					foreach (Int64 friendID in sourceSharedData.GetSocialContainer().Friends)
					{
						WebsocketHelper.SendToAllSessionsOfUser(friendID, bytesJSON);
					}
				}
			}

			int numSessions = WebSocketManager.GetNumberOfUsersOnline(); ;
			Console.Title = String.Format("GenOnline - {0} players", numSessions);

			try
			{
				if (oldWS != null)
				{
					// Close the WS directly, dont rely on session data as it may be linked to something else at this point
					await oldWS.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session being deleted");
				}
			}
			catch
			{

			}
		}

		/*
		public static ChatSession? GetWebSocketForUser(Int64 userID)
		{
			if (m_dictSessions.TryGetValue(userID, out ChatSession? retVal))
			{
				return retVal;
			}
			else
			{
				return null;
			}
		}
		*/

		// TODO_EFCORE: Just use a weakref to the websocket from the user session instead of lookups
		public static UserWebSocketInstance? GetWebSocketForSession(UserSession session)
		{
			if (m_dictWebsockets[session.GetSessionType()].TryGetValue(session.m_UserID, out UserWebSocketInstance? retVal))
			{
				return retVal;
			}
			else
			{
				return null;
			}
		}


		private static ConcurrentDictionary<EUserSessionType, ConcurrentDictionary<Int64, UserWebSocketInstance>> m_dictWebsockets = new()
		{
			// Initialize everything ahead of time so we don't have to keep doing lookups to see if it exists
			[EUserSessionType.GameClient] = new(),
			[EUserSessionType.GameLauncher] = new(),
			[EUserSessionType.ChatClient] = new(),
		};

		private static ConcurrentDictionary<EUserSessionType, ConcurrentDictionary<Int64, UserSession>> m_dictUserSessions = new()
		{
			// Initialize everything ahead of time so we don't have to keep doing lookups to see if it exists
			[EUserSessionType.GameClient] = new (),
			[EUserSessionType.GameLauncher] = new (),
			[EUserSessionType.ChatClient] = new (),
		};

		private static ConcurrentDictionary<Int64, SharedUserData> m_dictSharedUserData = new();

		public static ConcurrentDictionary<EUserSessionType, ConcurrentDictionary<Int64, UserSession>> GetUserDataCache()
		{
			return m_dictUserSessions;
		}

		public static SharedUserData? GetSharedDataForUser(string strDisplayName)
		{
			foreach (var kvPair in m_dictSharedUserData)
			{
				if (String.Equals(kvPair.Value.m_strDisplayName, strDisplayName, StringComparison.OrdinalIgnoreCase))
				{
					return kvPair.Value;
				}
			}

			return null;
		}


		public static SharedUserData? GetSharedDataForUser(Int64 userID)
		{
			if (m_dictSharedUserData.TryGetValue(userID, out SharedUserData? retVal))
			{
				return retVal;
			}
			else
			{
				return null;
			}
		}

		public static UserSession? GetSessionFromUser(Int64 userID, EUserSessionType sessionType)
		{
			if (m_dictUserSessions[sessionType].TryGetValue(userID, out UserSession? retVal))
			{
				return retVal;
			}
			else
			{
				return null;
			}
		}

		public static List<UserSession> GetAllDataFromUser(Int64 userID)
		{
			List<UserSession> lstRet = new();

			foreach (var sessionByClient in m_dictUserSessions)
			{
				if (sessionByClient.Value.TryGetValue(userID, out UserSession? sess))
				{
					lstRet.Add(sess);
				}
			}

			return lstRet;
		}

		public static async Task<bool> ClearDataFromUser(Int64 userID, EUserSessionType sessionType)
		{
			// NOTE: This is when a player is truly disconnected and we can destroy session, remove form lobby etc, websocket disconnect doesnt mean that because the clietn reconnects
			try
			{
				UserSession? userData = null;

				if (m_dictUserSessions[sessionType].ContainsKey(userID))
				{
					userData = m_dictUserSessions[sessionType][userID];
				}
				await SessionHelpers.FullyDestroyPlayerSession(userID, userData, true);

				// decrement ref count on shared data
				if (m_dictSharedUserData.TryGetValue(userID, out SharedUserData? sharedData))
				{
					sharedData.DecrementRefCount();
					if (sharedData.NeedsGC()) // cleanup if necessary
					{
						m_dictSharedUserData.Remove(userID, out var removedSharedData);
					}
				}
				else
				{
					Console.WriteLine("Error: Could not find shared data for user {0} when deleting session", userID);
				}
			}
			catch
			{

			}

			return m_dictUserSessions[sessionType].Remove(userID, out var itemRemoved);
		}


		// helpers
		public static async Task SendNewOrDeletedLobbyToAllNetworkRoomMembers(int networkRoomID)
		{
			if (networkRoomID != -1)
			{
				// need a member list update
				WebSocketMessage_CurrentNetworkRoomLobbyListUpdate lobbyListUpdate = new WebSocketMessage_CurrentNetworkRoomLobbyListUpdate();
				lobbyListUpdate.msg_id = (int)EWebSocketMessageID.NETWORK_ROOM_LOBBY_LIST_UPDATE;
				byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lobbyListUpdate));

				// populate list of everyone in the room
				foreach (var sessionDataByClient in m_dictUserSessions)
				{
					foreach (var sessionData in sessionDataByClient.Value)
					{
						if (sessionData.Value != null)
						{
							if (sessionData.Value.networkRoomID == networkRoomID || sessionData.Value.networkRoomID == 0)
							{
								sessionData.Value.QueueWebsocketSend(bytesJSON);
							}
						}
					}
				}
			}
		}

		private static ConcurrentList<int> g_lstDirtyNetworkRooms = new();
		public static async Task TickRoomMemberList()
		{
			foreach (int roomID in g_lstDirtyNetworkRooms)
			{
				
				// need a member list update
				WebSocketMessage_NetworkRoomMemberListUpdate memberListUpdate = new WebSocketMessage_NetworkRoomMemberListUpdate();
				memberListUpdate.msg_id = (int)EWebSocketMessageID.NETWORK_ROOM_MEMBER_LIST_UPDATE;
				memberListUpdate.members = new();

				Dictionary<EUserSessionType, SortedDictionary<Int64, bool>> usersAlreadyProcessed = new();
				// create base
				foreach (EUserSessionType sessionType in Enum.GetValues<EUserSessionType>())
				{
					usersAlreadyProcessed[sessionType] = new SortedDictionary<long, bool>();
				}

				List<Int64> lstUsersToSend = new();

				// populate list of everyone in the room
				foreach (var sessionDataByClient in m_dictUserSessions)
				{
					foreach (var sessionData in sessionDataByClient.Value)
					{
						UserSession sess = sessionData.Value;
						if (sess.networkRoomID == roomID)
						{
							EUserSessionType sessType = sessionData.Value.GetSessionType();
							if (!usersAlreadyProcessed[sessType].ContainsKey(sess.m_UserID))
							{
								usersAlreadyProcessed[sessType][sess.m_UserID] = true;

								SharedUserData? sharedUserData = WebSocketManager.GetSharedDataForUser(sess.m_UserID);
								if (sharedUserData != null)
								{
									// add to member list
									string strDisplayName = sharedUserData.IsAdmin() ? String.Format("[\u2605\u2605GO STAFF\u2605\u2605] {0}", sharedUserData.m_strDisplayName) : sharedUserData.m_strDisplayName;
									
									// append client, if not game
									if (sessType != EUserSessionType.GameClient)
									{
										if (sessType == EUserSessionType.GameLauncher)
										{
											if (sessionData.Value.m_client_id == KnownClients.EKnownClients.genhub)
											{
												strDisplayName += " [GENHUB]";
											}
											else
											{
												strDisplayName += " [LAUNCHER]";
											}
										}
										else if (sessType == EUserSessionType.ChatClient)
										{
											strDisplayName += " [WEBCHAT]";
										}
									}
									

									memberListUpdate.members.Add(new RoomMember(sess.m_UserID, strDisplayName, sharedUserData.IsAdmin()));

									// also add to list of users who need this update, since they were in there
									lstUsersToSend.Add(sess.m_UserID);
								}
							}
						}
					}
				}

				byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(memberListUpdate));

				// what if they have clients in different net rooms?

				// now send to everyone in the room
				foreach (Int64 user_id in lstUsersToSend)
				{
					// find all of their websockets, and send it to any who are in this network room
					foreach (UserSession sess in WebSocketManager.GetAllDataFromUser(user_id))
					{
						if (sess.networkRoomID == roomID)
						{
							sess.QueueWebsocketSend(bytesJSON);
						}
					}
				}
			}

			g_lstDirtyNetworkRooms.Clear();
		}

		public static async Task MarkRoomMemberListAsDirty(int roomID)
		{
			g_lstDirtyNetworkRooms.Add(roomID);
		}
	}

	// NOTE: only one instance for ALL websockets/sessions, and is destroyed when the last one of the former is destroyed
	public class SharedUserData
	{
		private int m_RefCount = 0;

		public void IncrementRefCount()
		{
			Interlocked.Increment(ref m_RefCount);
		}

		public void DecrementRefCount()
		{
			Interlocked.Decrement(ref m_RefCount);
		}

		public bool NeedsGC()
		{
			return m_RefCount <= 0;
		}

		public Int64 m_UserID = -1;
		public string m_strDisplayName = String.Empty;
		private bool m_bIsAdmin;

		// contains ELO too
		public PlayerStats? GameStats { get; private set; } = null;

		private UserSocialContainer m_socialContainer;

		public UserSocialContainer GetSocialContainer() { return m_socialContainer; }

		public bool IsAdmin() { return m_bIsAdmin; }

		public SharedUserData(Int64 ownerID, UserSocialContainer socialContainer, string strDisplayName, bool bIsAdmin, PlayerStats userStats)
		{
			m_strDisplayName = strDisplayName;
			m_bIsAdmin = bIsAdmin;

			m_UserID = ownerID;

			m_socialContainer = socialContainer;

			GameStats = userStats;

			// upon creation, immediately increment ref count
			IncrementRefCount();
		}
	}

	public class UserSession
	{
		public Int64 m_UserID = -1;
		
		public string m_strContinent;
		public string m_strCountry;
		public double m_dLatitude;
		public double m_dLongitude;
		
		private EUserSessionType m_sessionType = EUserSessionType.None;

		private string ACExeCRC = String.Empty;

		// Matchmaking data
		public UInt16 MatchmakingPlaylistID = 0;
		public ConcurrentList<int> MatchmakingMapIndicies = new();

		// NOTE: These are not set on login, only when in quickmatch!
		public UInt32 ExeCRC = 0;
		public UInt32 IniCRC = 0;
		public EKnownAnticheatID AnticheatID = EKnownAnticheatID.NONE;

		private ConcurrentList<UInt64> m_lstHistoricMatchIDs = new();
		private ConcurrentDictionary<UInt64, int> m_lstHistoricMatchIDToSlotIndexMap = new();
		private ConcurrentDictionary<UInt64, int> m_lstHistoricMatchIDToArmy = new();
		private ConcurrentDictionary<UInt64, DateTime> m_lstHistoricMatchIDToCreationTime = new();

		private Int64 m_timeAbandoned = -1;

		private string m_strMiddlewareUserID = String.Empty;

		public KnownClients.EKnownClients m_client_id = KnownClients.EKnownClients.unknown;
		DateTime m_CreateTime = DateTime.Now;
		public DateTime GetCreationTime()
		{
			return m_CreateTime;
		}

		public void SetMiddlewareID(string strMiddlewareUserID)
		{
			m_strMiddlewareUserID = strMiddlewareUserID;
		}

		public string GetMiddlewareID()
		{
			return m_strMiddlewareUserID;
		}

		public EUserSessionType GetSessionType()
		{
			return m_sessionType;
		}

		public UInt64 GetLatestMatchID()
		{
			UInt64 mostRecentMatchID = 0;
			if (m_lstHistoricMatchIDs.Count > 0)
			{
				mostRecentMatchID = m_lstHistoricMatchIDs[m_lstHistoricMatchIDs.Count - 1];
			}

			return mostRecentMatchID;
		}

		public TimeSpan GetDuration()
		{
			return DateTime.Now - m_CreateTime;
		}

		public UserSession(Int64 ownerID, EUserSessionType sessionType, KnownClients.EKnownClients client_id, string strContinent, string strCountry, double dLatitude, double dLongitude)
		{
			m_sessionType = sessionType;
			m_client_id = client_id;
			m_strContinent = strContinent;
			m_strCountry = strCountry;
			m_dLatitude = dLatitude;
			m_dLongitude = dLongitude;

			m_UserID = ownerID;

			// store the exe CRC (this is actually the .CODE section, for AC)
			if (Helpers.g_dictInitialExeCRCs.ContainsKey(ownerID))
			{
				ACExeCRC = Helpers.g_dictInitialExeCRCs[ownerID].ToUpper();
				Helpers.g_dictInitialExeCRCs.Remove(ownerID, out string removedCRC);
			}
		}

		public void MarkAbandoned()
		{
			m_timeAbandoned = Environment.TickCount64;
		}
		public void MarkNotAbandoned()
		{
			m_timeAbandoned = -1;
		}

		public bool IsAbandoned()
		{
			UserWebSocketInstance websocketForUser = WebSocketManager.GetWebSocketForSession(this);
			return m_timeAbandoned != -1 && websocketForUser == null;
		}

		// TODO_EFCORE: check all uses of QueueWebsocketSend, some might need to be SendToAllInstances
		public void QueueWebsocketSend(byte[] bytesJSON)
		{
			if (bytesJSON == null)
			{
				return;
			}

			// Always enqueue; the TickWebsocket drain loop is the sole sender,
			// ensuring WebSocket.SendAsync is never called concurrently.
			m_lstPendingWebsocketSends.Enqueue(bytesJSON);
		}

		public async Task<UserWebSocketInstance> CloseWebsocket(WebSocketCloseStatus reason, string strReason)
		{
			UserWebSocketInstance websocketForUser = WebSocketManager.GetWebSocketForSession(this);
			if (websocketForUser != null)
			{
				await websocketForUser.CloseAsync(reason, strReason);
			}

			return websocketForUser;
		}

		public async Task TickWebsocket(CancellationToken tickToken = default)
		{
			// Do we have a connection to send on?
			UserWebSocketInstance websocketForUser = WebSocketManager.GetWebSocketForSession(this);
			if (websocketForUser != null)
			{
				const int maxMessagesSendPerFrame = 50;
				int messagesSent = 0;
				// start dequeing and sending
				while (!tickToken.IsCancellationRequested && messagesSent < maxMessagesSendPerFrame && m_lstPendingWebsocketSends.TryDequeue(out byte[] packetData))
				{
					await websocketForUser.SendAsync(packetData, WebSocketMessageType.Text, tickToken);
					++messagesSent;
				}
			}
		}
		
		// TODO_CACHE: Size limit this?
		ConcurrentQueue<byte[]> m_lstPendingWebsocketSends = new ConcurrentQueue<byte[]>();

		public bool NeedsCleanup()
		{
			const Int64 timeBeforeConsideredAbandoned = 30000; // 5 minutes
			return Environment.TickCount64 - m_timeAbandoned >= timeBeforeConsideredAbandoned;
		}

		private bool m_bSubscribedToRealtimeSocialupdates = false;
		public void SetSubscribedToRealtimeSocialUpdates(bool bSubscribe)
		{
			m_bSubscribedToRealtimeSocialupdates = bSubscribe;
		}

		public bool IsSubscribedToRealtimeSocialUpdates()
		{
			return m_bSubscribedToRealtimeSocialupdates;
		}

		public async Task RegisterExeCRC(string strExeCRC)
		{
			if (ACExeCRC.ToUpper() != strExeCRC.ToUpper())
			{
				// Flag the account for review
				UInt64 mostRecentMatchID = 0;
				if (m_lstHistoricMatchIDs.Count > 0)
				{
					mostRecentMatchID = m_lstHistoricMatchIDs[m_lstHistoricMatchIDs.Count - 1];
				}

				using var scope = ServiceLocator.Services.CreateScope();
				var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
				await using var db = await factory.CreateDbContextAsync();
				await Database.AntiCheat.FlagAccountForReview(db, m_UserID, ACExeCRC, strExeCRC, mostRecentMatchID);
			}
		}

		public string GetFullCountryName()
		{
			RegionInfo ri = new RegionInfo(m_strCountry);
			return ri.EnglishName;
		}

		public string GetFullContinentName()
		{
			switch (m_strContinent)
			{
				case "AF": return "Africa";
				case "AN": return "Antartica";
				case "AS": return "Asia";
				case "EU": return "Europe";
				case "NA": return "North America";
				case "OC": return "Oceania";
				case "SA": return "South America";
				case "T1": return "Tor";
				default: return "Unknown";
			}
		}

		// Enough history to cover any realistic upload/outcome window; old entries just waste memory.
		private const int MaxHistoricMatches = 50;

		public void RegisterHistoricMatchID(UInt64 matchID, int slotIndex, int army, DateTime creationTime)
		{
			m_lstHistoricMatchIDs.Add(matchID);
			m_lstHistoricMatchIDToSlotIndexMap[matchID] = slotIndex;
			m_lstHistoricMatchIDToArmy[matchID] = army;
			m_lstHistoricMatchIDToCreationTime[matchID] = creationTime;

			// Trim oldest entries so the lists don't grow without bound over long sessions.
			while (m_lstHistoricMatchIDs.Count > MaxHistoricMatches)
			{
				UInt64 oldest = m_lstHistoricMatchIDs[0];
				m_lstHistoricMatchIDs.Remove(oldest);
				m_lstHistoricMatchIDToSlotIndexMap.TryRemove(oldest, out _);
				m_lstHistoricMatchIDToArmy.TryRemove(oldest, out _);
				m_lstHistoricMatchIDToCreationTime.TryRemove(oldest, out _);
				m_dictReportedMatchOutcomes.TryRemove(oldest, out _);
			}
		}

		private ConcurrentDictionary<UInt64, byte> m_dictReportedMatchOutcomes = new();

		// Returns true only the first time an outcome is reported for a given match, so a client cannot replay the
		// same result to inflate aggregate stats.
		public bool TryMarkMatchOutcomeReported(UInt64 matchID)
		{
			return m_dictReportedMatchOutcomes.TryAdd(matchID, 0);
		}

		public bool WasPlayerInMatch(UInt64 matchID, out int slotIndexInLobby, out int army, out DateTime lobbyCreationTime)
		{
			slotIndexInLobby = -1;
			army = -1;
			lobbyCreationTime = DateTime.UnixEpoch;

			bool bWasInMatch = m_lstHistoricMatchIDs.Contains(matchID);

			if (bWasInMatch)
			{
				// TODO: Optimize storage here
				slotIndexInLobby = m_lstHistoricMatchIDToSlotIndexMap[matchID];
				army = m_lstHistoricMatchIDToArmy[matchID];
				lobbyCreationTime = m_lstHistoricMatchIDToCreationTime[matchID];
			}

			return bWasInMatch;
		}

		public async Task UpdateSessionNetworkRoom(Int16 newRoomID)
		{
			Int16 oldRoom = networkRoomID;
			networkRoomID = newRoomID;

			// update the room roster they left
			if (oldRoom >= 0) // only if they werent in the dummy room before
			{
				await WebSocketManager.MarkRoomMemberListAsDirty(oldRoom);
			}

			// send update to joiner + everyone in new room already
			if (newRoomID >= 0) // only if they actually joined a room and weren't going to the dummy room
			{
				await WebSocketManager.MarkRoomMemberListAsDirty(newRoomID);
			}

			// make the client force refresh list too
			await WebSocketManager.SendNewOrDeletedLobbyToAllNetworkRoomMembers(this.networkRoomID);
		}

		public void UpdateSessionLobbyID(Int64 newLobbyID)
		{
			if (m_sessionType == EUserSessionType.GameClient)
			{
				currentLobbyID = newLobbyID;
			}
		}

		// network room
		public Int16 networkRoomID = -1;


		// lobby id
		public Int64 currentLobbyID = -1;
	}

	public class UserWebSocketInstance
	{
		// cached user data, useful
		public EUserSessionType m_SessionType = EUserSessionType.None;
		public Int64 m_UserID = -1;

		public Int64 m_lastPingTime = Environment.TickCount64; // last time we pinged this user, used to detect disconnects
		
		

		// TODO: Start using nullable for int values etc instead of doing 0 or -1
        public async Task SendPong()
		{
			OnPing();

			// send pong back
			WebSocketMessage_PONG outboundMsg = new WebSocketMessage_PONG();
			outboundMsg.msg_id = (int)EWebSocketMessageID.PONG;
			byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsg));
			await SendAsync(bytesJSON, WebSocketMessageType.Text);
		}
		

		private WebSocket? m_SockInternal = null;

		public UserWebSocketInstance(EUserSessionType sessionType, Int64 ownerID) : base()
		{
			m_SessionType = sessionType;
			m_UserID = ownerID;
		}

		public void AttachWebsocket(WebSocket sock)
		{
			m_SockInternal = sock;
		}

		public void OnPing()
		{
			m_lastPingTime = Environment.TickCount64;
		}

		public Int64 GetLastPingTime()
		{
			return m_lastPingTime;
		}

		public Int64 GetTimeSinceLastPing()
		{
			return Environment.TickCount64 - m_lastPingTime;
		}

		public async Task SendAsync(byte[] buffer, WebSocketMessageType messageType, CancellationToken externalToken = default)
		{
			if (m_SockInternal != null)
			{
				// WebSocket.SendAsync must never be called concurrently on the same socket or the frame stream gets
				// corrupted. Several paths (tick drain, pongs, direct sends) can send at the same time, so serialize here.
				await m_SendLock.WaitAsync();
				try
				{
					// should we chunked send?
					/*
					const int frameMax = 99999999;
					if (buffer.Length > frameMax)
					{
						int bytresRemaining = buffer.Length;
						int numFrames = (int)Math.Ceiling((float)buffer.Length / (float)frameMax);

						System.Diagnostics.Debug.WriteLine("[Websocket] sending {0} bytes in {1} chunks", bytresRemaining, numFrames);

						for (int i = 0; i < numFrames; ++i)
						{
							int bytesToSend = Math.Min(bytresRemaining, frameMax);
							bool bLastFrame = i == numFrames - 1;

							
							ArraySegment<byte> arrSegment = new ArraySegment<byte>(buffer, i * frameMax, bytesToSend);
							System.Diagnostics.Debug.WriteLine("[Websocket] send frame {0} with {1} bytes (last: {2})", i, bytesToSend, bLastFrame);
							await m_SockInternal.SendAsync(arrSegment, messageType, bLastFrame, CancellationToken.None);

							bytresRemaining -= bytesToSend;
						}

					}
					else // just send whole
					{
						await m_SockInternal.SendAsync(buffer, messageType, true, CancellationToken.None);
					}
					*/

					CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
					try
					{
						cts.CancelAfter(TimeSpan.FromMilliseconds(500));
						await m_SockInternal.SendAsync(buffer, messageType, true, cts.Token);
					}
					finally
					{
						try
						{
							// Disarm the pending CancelAfter timer before disposing to prevent a race condition
						// where the timer fires concurrently with Dispose(), causing ObjectDisposedException
						// in CancellationTokenSource.ExecuteCallbackHandlers.
						cts.CancelAfter(Timeout.InfiniteTimeSpan);
						cts.Dispose();
						}
						catch (ObjectDisposedException)
						{
							// The linked token may be disposed if the external token (parent CancellationTokenSource)
							// fires its timeout while we're disposing this instance. This is safe to ignore.
						}
					}
				}
				catch
				{

				}
				finally
				{
					m_SendLock.Release();
				}
			}
		}

		private readonly SemaphoreSlim m_SendLock = new SemaphoreSlim(1, 1);

		public async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription)
		{
			if (m_SockInternal != null)
			{
				try
				{
					// dont wait forever, certain situations can cause that in ASP.NET
					var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
					await m_SockInternal.CloseAsync(closeStatus, statusDescription, cts.Token);
				}
				catch
				{

				}
			}
		}
	}

	public enum ESessionAccessType
	{
		Authenticate, // log in and out
		Social, // friends lists
		ServerListReadOnly, // can read lobby list and players etc, but cannot join
		StatsReadOnly, // can read stats for any user, but not write anything
		Gameplay, // Create lobbies, Anticheat, Middleware login, Matchmaking, match screenshots, replays, join lobby, etc
	};
	public static class SessionHelpers
	{
		public static bool SessionTypeHasAccessTo(EUserSessionType sessType, ESessionAccessType accessType)
		{
			if (sessType == EUserSessionType.GameClient) // client can do anything
			{
				return true;
			}
			else if (sessType == EUserSessionType.ChatClient)
			{
				return false;
			}

			else if (sessType == EUserSessionType.GameLauncher)
			{
				return false;
			}

			return false;
		}

		public static async Task FullyDestroyPlayerSession(Int64 user_id, UserSession? userData, bool bMigrateLobbyIfPresent)
		{
			// NOTE: Dont assume userData is valid, use user_id for user id
			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.WriteLine("FullyDestroyPlayerSession for user {0}", user_id);
			Console.ForegroundColor = ConsoleColor.Gray;

			// invalidate any TURN credentials
			TURNCredentialManager.DeleteCredentialsForUser(user_id);

			// TODO: Implement single point of presence? gets dicey if multiple logins
			// TODO: Dont destroy this, just mark inactive/offline, we use this as a saved credential system

			// session tied to this token (keep other ones attached to user_id, could be other machines)
			// TODO_JWT: Remove table fully + set logged out
			//await m_Inst.Query("DELETE FROM sessions WHERE user_id={0} AND session_type={1};", user_id, (int)ESessionType.Game);

			// leave any lobby
			Console.WriteLine("[Source 2] User {0} Leave Any Lobby", user_id);

			var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();
			await lobbyManager.LeaveAnyLobby(user_id);

			await lobbyManager.CleanupUserLobbiesNotStarted(user_id);

			// remove from any matchmaking
			if (userData != null)
			{
				MatchmakingManager.DeregisterPlayer(userData);
			}

			// TODO: Client needs to handle this... itll start returning 404
		}

		public async static Task SetUsedLoggedIn(Int64 userID, KnownClients.EKnownClients clientID, EUserSessionType sessionType)
		{
			// TODO_EFCORE: website uses this index as 1 (60hz) to 0 (30hz), update it to use new enum + support new clients, also need to update DB to match
			// TODO_EFCORE: Move away from db for this and just have website login call endpoint on service
			//UInt16 clientID = clientIDStr == "gen_online_60hz" ? (UInt16)1 : (UInt16)0;

			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.WriteLine("StartSession deleing other sessions for user {0}", userID);
			Console.ForegroundColor = ConsoleColor.Gray;

			// kill any WS they had too, StartSession comes before WS connects
			// disconnect any other sessions with this ID
			UserSession? sess = GenOnlineService.WebSocketManager.GetSessionFromUser(userID, sessionType);
			if (sess != null)
			{
				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine("Found duplicate session for user {0}", userID);
				Console.ForegroundColor = ConsoleColor.Gray;

				UserWebSocketInstance? oldWS = GenOnlineService.WebSocketManager.GetWebSocketForSession(sess);
				await GenOnlineService.WebSocketManager.DeleteSession(userID, sessionType, oldWS, false);
			}
		}
	}

	public enum EAccountType
	{
		Unknown = -1,
		Steam = 0,
		Discord = 1,
		Ghost = 2,
		DevAccount = 3
	}

	public class PlayerStats
	{
		const int numGeneralsEntries = 15;

		public PlayerStats(Int64 inUserID, int inEloRating, int inEloMatches, int inMonthlyEloRating)
		{
			userID = inUserID;
			EloRating = inEloRating;
			EloMatches = inEloMatches;
			MonthlyEloRating = inMonthlyEloRating;

            // init arrays, rest are init'ed below
            for (int i = 0; i < numGeneralsEntries; ++i)
			{
				losses[i] = 0;
				games[i] = 0;
				duration[i] = 0;
				wins[i] = 0;
				unitsKilled[i] = 0;
				unitsLost[i] = 0;
				unitsBuilt[i] = 0;
				buildingsKilled[i] = 0;
				buildingsLost[i] = 0;
				buildingsBuilt[i] = 0;
				earnings[i] = 0;
				techCaptured[i] = 0;
				discons[i] = 0;
				desyncs[i] = 0;
				surrenders[i] = 0;
				gamesOf2p[i] = 0;
				gamesOf3p[i] = 0;
				gamesOf4p[i] = 0;
				gamesOf5p[i] = 0;
				gamesOf6p[i] = 0;
				gamesOf7p[i] = 0;
				gamesOf8p[i] = 0;
				customGames[i] = 0;
				QMGames[i] = 0;
			}
		}

		public Int64 userID { get; set; } = -1;
		public int EloRating { get; set; } = EloConfig.BaseRating;
		public int EloMatches { get; set; } = 0;
		public int MonthlyEloRating { get; set; } = EloConfig.BaseRating;

		public int[] wins { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] losses { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] games { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] duration { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] unitsKilled { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] unitsLost { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] unitsBuilt { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] buildingsKilled { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] buildingsLost { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] buildingsBuilt { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] earnings { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] techCaptured { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] discons { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] desyncs { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] surrenders { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf2p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf3p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf4p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf5p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf6p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf7p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] gamesOf8p { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] customGames { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] QMGames { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] customGamesPerGeneral { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int[] quickMatchesPerGeneral { get; set; } = new int[numGeneralsEntries] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
		public int locale { get; set; } = 0;
		public int gamesAsRandom { get; set; } = 0;
		public string options { get; set; } = String.Empty;
		public string systemSpec { get; set; } = String.Empty;
		public float lastFPS { get; set; } = 0F;
		public int lastGeneral { get; set; } = 0;
		public int gamesInRowWithLastGeneral { get; set; } = 0;
		public int challengeMedals { get; set; } = 0;
		public int battleHonors { get; set; } = 0;
		public int QMwinsInARow { get; set; } = 0;
		public int maxQMwinsInARow { get; set; } = 0;
		public int winsInARow { get; set; } = 0;
		public int maxWinsInARow { get; set; } = 0;
		public int lossesInARow { get; set; } = 0;
		public int maxLossesInARow { get; set; } = 0;
		public int disconsInARow { get; set; } = 0;
		public int maxDisconsInARow { get; set; } = 0;
		public int desyncsInARow { get; set; } = 0;
		public int maxDesyncsInARow { get; set; } = 0;
		public int builtParticleCannon { get; set; } = 0;
		public int builtNuke { get; set; } = 0;
		public int builtSCUD { get; set; } = 0;
		public int lastLadderPort { get; set; } = 0;
		public string lastLadderHost { get; set; } = String.Empty;

		public void ProcessFromDB(EStatIndex stat_id, int value)
		{
			int index = (int)stat_id % numGeneralsEntries;
			switch (stat_id)
			{
				case >= EStatIndex.WINS_PER_GENERAL_0 and <= EStatIndex.WINS_PER_GENERAL_14:
					wins[index] = value;
					break;
				case >= EStatIndex.LOSSES_PER_GENERAL_0 and <= EStatIndex.LOSSES_PER_GENERAL_14:
					losses[index] = value;
					break;
				case >= EStatIndex.GAMES_PER_GENERAL_0 and <= EStatIndex.GAMES_PER_GENERAL_14:
					games[index] = value;
					break;
				case >= EStatIndex.DURATION_PER_GENERAL_0 and <= EStatIndex.DURATION_PER_GENERAL_14:
					duration[index] = value;
					break;
				case >= EStatIndex.UNITSKILLED_PER_GENERAL_0 and <= EStatIndex.UNITSKILLED_PER_GENERAL_14:
					unitsKilled[index] = value;
					break;
				case >= EStatIndex.UNITSLOST_PER_GENERAL_0 and <= EStatIndex.UNITSLOST_PER_GENERAL_14:
					unitsLost[index] = value;
					break;
				case >= EStatIndex.UNITSBUILT_PER_GENERAL_0 and <= EStatIndex.UNITSBUILT_PER_GENERAL_14:
					unitsBuilt[index] = value;
					break;
				case >= EStatIndex.BUILDINGSKILLED_PER_GENERAL_0 and <= EStatIndex.BUILDINGSKILLED_PER_GENERAL_14:
					buildingsKilled[index] = value;
					break;
				case >= EStatIndex.BUILDINGSLOST_PER_GENERAL_0 and <= EStatIndex.BUILDINGSLOST_PER_GENERAL_14:
					buildingsLost[index] = value;
					break;
				case >= EStatIndex.BUILDINGSBUILT_PER_GENERAL_0 and <= EStatIndex.BUILDINGSBUILT_PER_GENERAL_14:
					buildingsBuilt[index] = value;
					break;
				case >= EStatIndex.EARNINGS_PER_GENERAL_0 and <= EStatIndex.EARNINGS_PER_GENERAL_14:
					earnings[index] = value;
					break;
				case >= EStatIndex.TECHCAPTURED_PER_GENERAL_0 and <= EStatIndex.TECHCAPTURED_PER_GENERAL_14:
					techCaptured[index] = value;
					break;
				case >= EStatIndex.DISCONS_PER_GENERAL_0 and <= EStatIndex.DISCONS_PER_GENERAL_14:
					discons[index] = value;
					break;
				case >= EStatIndex.DESYNCS_PER_GENERAL_0 and <= EStatIndex.DESYNCS_PER_GENERAL_14:
					desyncs[index] = value;
					break;
				case >= EStatIndex.SURRENDERS_PER_GENERAL_0 and <= EStatIndex.SURRENDERS_PER_GENERAL_14:
					surrenders[index] = value;
					break;
				case >= EStatIndex.GAMESOF2P_PER_GENERAL_0 and <= EStatIndex.GAMESOF2P_PER_GENERAL_14:
					gamesOf2p[index] = value;
					break;
				case >= EStatIndex.GAMESOF3P_PER_GENERAL_0 and <= EStatIndex.GAMESOF3P_PER_GENERAL_14:
					gamesOf3p[index] = value;
					break;
				case >= EStatIndex.GAMESOF4P_PER_GENERAL_0 and <= EStatIndex.GAMESOF4P_PER_GENERAL_14:
					gamesOf4p[index] = value;
					break;
				case >= EStatIndex.GAMESOF5P_PER_GENERAL_0 and <= EStatIndex.GAMESOF5P_PER_GENERAL_14:
					gamesOf5p[index] = value;
					break;
				case >= EStatIndex.GAMESOF6P_PER_GENERAL_0 and <= EStatIndex.GAMESOF6P_PER_GENERAL_14:
					gamesOf6p[index] = value;
					break;
				case >= EStatIndex.GAMESOF7P_PER_GENERAL_0 and <= EStatIndex.GAMESOF7P_PER_GENERAL_14:
					gamesOf7p[index] = value;
					break;
				case >= EStatIndex.GAMESOF8P_PER_GENERAL_0 and <= EStatIndex.GAMESOF8P_PER_GENERAL_14:
					gamesOf8p[index] = value;
					break;
				case >= EStatIndex.CUSTOMGAMES_PER_GENERAL_0 and <= EStatIndex.CUSTOMGAMES_PER_GENERAL_14:
					customGamesPerGeneral[index] = value;
					break;
				case >= EStatIndex.QUICKMATCHES_PER_GENERAL_0 and <= EStatIndex.QUICKMATCHES_PER_GENERAL_14:
					quickMatchesPerGeneral[index] = value;
					break;
				case EStatIndex.LOCALE:
					locale = value;
					break;
				case EStatIndex.GAMES_AS_RANDOM:
					gamesAsRandom = value;
					break;
				case EStatIndex.LASTFPS:
					lastFPS = value;
					break;
				case EStatIndex.LASTGENERAL:
					lastGeneral = value;
					break;
				case EStatIndex.GAMESINROWWITHLASTGENERAL:
					gamesInRowWithLastGeneral = value;
					break;
				case EStatIndex.CHALLENGEMEDALS:
					challengeMedals = value;
					break;
				case EStatIndex.BATTLEHONORS:
					battleHonors = value;
					break;
				case EStatIndex.QMWINSINAROW:
					QMwinsInARow = value;
					break;
				case EStatIndex.MAXQMWINSINAROW:
					maxQMwinsInARow = value;
					break;
				case EStatIndex.WINSINAROW:
					winsInARow = value;
					break;
				case EStatIndex.MAXWINSINAROW:
					maxWinsInARow = value;
					break;
				case EStatIndex.LOSSESINAROW:
					lossesInARow = value;
					break;
				case EStatIndex.MAXLOSSESINAROW:
					maxLossesInARow = value;
					break;
				case EStatIndex.DISCONSINAROW:
					disconsInARow = value;
					break;
				case EStatIndex.MAXDISCONSINAROW:
					maxDisconsInARow = value;
					break;
				case EStatIndex.DESYNCSINAROW:
					desyncsInARow = value;
					break;
				case EStatIndex.MAXDESYNCSINAROW:
					maxDesyncsInARow = value;
					break;
				case EStatIndex.BUILTPARTICLECANNON:
					builtParticleCannon = value;
					break;
				case EStatIndex.BUILTNUKE:
					builtNuke = value;
					break;
				case EStatIndex.BUILTSCUD:
					builtSCUD = value;
					break;
				case EStatIndex.LASTLADDERPORT:
					lastLadderPort = value;
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(stat_id), $"Unhandled stat_id: {stat_id}");
			}
		}
	}

	public enum EStatIndex : UInt16
	{
		WINS_PER_GENERAL_0,
		WINS_PER_GENERAL_1,
		WINS_PER_GENERAL_2,
		WINS_PER_GENERAL_3,
		WINS_PER_GENERAL_4,
		WINS_PER_GENERAL_5,
		WINS_PER_GENERAL_6,
		WINS_PER_GENERAL_7,
		WINS_PER_GENERAL_8,
		WINS_PER_GENERAL_9,
		WINS_PER_GENERAL_10,
		WINS_PER_GENERAL_11,
		WINS_PER_GENERAL_12,
		WINS_PER_GENERAL_13,
		WINS_PER_GENERAL_14,

		LOSSES_PER_GENERAL_0,
		LOSSES_PER_GENERAL_1,
		LOSSES_PER_GENERAL_2,
		LOSSES_PER_GENERAL_3,
		LOSSES_PER_GENERAL_4,
		LOSSES_PER_GENERAL_5,
		LOSSES_PER_GENERAL_6,
		LOSSES_PER_GENERAL_7,
		LOSSES_PER_GENERAL_8,
		LOSSES_PER_GENERAL_9,
		LOSSES_PER_GENERAL_10,
		LOSSES_PER_GENERAL_11,
		LOSSES_PER_GENERAL_12,
		LOSSES_PER_GENERAL_13,
		LOSSES_PER_GENERAL_14,

		GAMES_PER_GENERAL_0,
		GAMES_PER_GENERAL_1,
		GAMES_PER_GENERAL_2,
		GAMES_PER_GENERAL_3,
		GAMES_PER_GENERAL_4,
		GAMES_PER_GENERAL_5,
		GAMES_PER_GENERAL_6,
		GAMES_PER_GENERAL_7,
		GAMES_PER_GENERAL_8,
		GAMES_PER_GENERAL_9,
		GAMES_PER_GENERAL_10,
		GAMES_PER_GENERAL_11,
		GAMES_PER_GENERAL_12,
		GAMES_PER_GENERAL_13,
		GAMES_PER_GENERAL_14,

		DURATION_PER_GENERAL_0,
		DURATION_PER_GENERAL_1,
		DURATION_PER_GENERAL_2,
		DURATION_PER_GENERAL_3,
		DURATION_PER_GENERAL_4,
		DURATION_PER_GENERAL_5,
		DURATION_PER_GENERAL_6,
		DURATION_PER_GENERAL_7,
		DURATION_PER_GENERAL_8,
		DURATION_PER_GENERAL_9,
		DURATION_PER_GENERAL_10,
		DURATION_PER_GENERAL_11,
		DURATION_PER_GENERAL_12,
		DURATION_PER_GENERAL_13,
		DURATION_PER_GENERAL_14,

		UNITSKILLED_PER_GENERAL_0,
		UNITSKILLED_PER_GENERAL_1,
		UNITSKILLED_PER_GENERAL_2,
		UNITSKILLED_PER_GENERAL_3,
		UNITSKILLED_PER_GENERAL_4,
		UNITSKILLED_PER_GENERAL_5,
		UNITSKILLED_PER_GENERAL_6,
		UNITSKILLED_PER_GENERAL_7,
		UNITSKILLED_PER_GENERAL_8,
		UNITSKILLED_PER_GENERAL_9,
		UNITSKILLED_PER_GENERAL_10,
		UNITSKILLED_PER_GENERAL_11,
		UNITSKILLED_PER_GENERAL_12,
		UNITSKILLED_PER_GENERAL_13,
		UNITSKILLED_PER_GENERAL_14,

		UNITSLOST_PER_GENERAL_0,
		UNITSLOST_PER_GENERAL_1,
		UNITSLOST_PER_GENERAL_2,
		UNITSLOST_PER_GENERAL_3,
		UNITSLOST_PER_GENERAL_4,
		UNITSLOST_PER_GENERAL_5,
		UNITSLOST_PER_GENERAL_6,
		UNITSLOST_PER_GENERAL_7,
		UNITSLOST_PER_GENERAL_8,
		UNITSLOST_PER_GENERAL_9,
		UNITSLOST_PER_GENERAL_10,
		UNITSLOST_PER_GENERAL_11,
		UNITSLOST_PER_GENERAL_12,
		UNITSLOST_PER_GENERAL_13,
		UNITSLOST_PER_GENERAL_14,

		UNITSBUILT_PER_GENERAL_0,
		UNITSBUILT_PER_GENERAL_1,
		UNITSBUILT_PER_GENERAL_2,
		UNITSBUILT_PER_GENERAL_3,
		UNITSBUILT_PER_GENERAL_4,
		UNITSBUILT_PER_GENERAL_5,
		UNITSBUILT_PER_GENERAL_6,
		UNITSBUILT_PER_GENERAL_7,
		UNITSBUILT_PER_GENERAL_8,
		UNITSBUILT_PER_GENERAL_9,
		UNITSBUILT_PER_GENERAL_10,
		UNITSBUILT_PER_GENERAL_11,
		UNITSBUILT_PER_GENERAL_12,
		UNITSBUILT_PER_GENERAL_13,
		UNITSBUILT_PER_GENERAL_14,

		BUILDINGSKILLED_PER_GENERAL_0,
		BUILDINGSKILLED_PER_GENERAL_1,
		BUILDINGSKILLED_PER_GENERAL_2,
		BUILDINGSKILLED_PER_GENERAL_3,
		BUILDINGSKILLED_PER_GENERAL_4,
		BUILDINGSKILLED_PER_GENERAL_5,
		BUILDINGSKILLED_PER_GENERAL_6,
		BUILDINGSKILLED_PER_GENERAL_7,
		BUILDINGSKILLED_PER_GENERAL_8,
		BUILDINGSKILLED_PER_GENERAL_9,
		BUILDINGSKILLED_PER_GENERAL_10,
		BUILDINGSKILLED_PER_GENERAL_11,
		BUILDINGSKILLED_PER_GENERAL_12,
		BUILDINGSKILLED_PER_GENERAL_13,
		BUILDINGSKILLED_PER_GENERAL_14,

		BUILDINGSLOST_PER_GENERAL_0,
		BUILDINGSLOST_PER_GENERAL_1,
		BUILDINGSLOST_PER_GENERAL_2,
		BUILDINGSLOST_PER_GENERAL_3,
		BUILDINGSLOST_PER_GENERAL_4,
		BUILDINGSLOST_PER_GENERAL_5,
		BUILDINGSLOST_PER_GENERAL_6,
		BUILDINGSLOST_PER_GENERAL_7,
		BUILDINGSLOST_PER_GENERAL_8,
		BUILDINGSLOST_PER_GENERAL_9,
		BUILDINGSLOST_PER_GENERAL_10,
		BUILDINGSLOST_PER_GENERAL_11,
		BUILDINGSLOST_PER_GENERAL_12,
		BUILDINGSLOST_PER_GENERAL_13,
		BUILDINGSLOST_PER_GENERAL_14,

		BUILDINGSBUILT_PER_GENERAL_0,
		BUILDINGSBUILT_PER_GENERAL_1,
		BUILDINGSBUILT_PER_GENERAL_2,
		BUILDINGSBUILT_PER_GENERAL_3,
		BUILDINGSBUILT_PER_GENERAL_4,
		BUILDINGSBUILT_PER_GENERAL_5,
		BUILDINGSBUILT_PER_GENERAL_6,
		BUILDINGSBUILT_PER_GENERAL_7,
		BUILDINGSBUILT_PER_GENERAL_8,
		BUILDINGSBUILT_PER_GENERAL_9,
		BUILDINGSBUILT_PER_GENERAL_10,
		BUILDINGSBUILT_PER_GENERAL_11,
		BUILDINGSBUILT_PER_GENERAL_12,
		BUILDINGSBUILT_PER_GENERAL_13,
		BUILDINGSBUILT_PER_GENERAL_14,

		EARNINGS_PER_GENERAL_0,
		EARNINGS_PER_GENERAL_1,
		EARNINGS_PER_GENERAL_2,
		EARNINGS_PER_GENERAL_3,
		EARNINGS_PER_GENERAL_4,
		EARNINGS_PER_GENERAL_5,
		EARNINGS_PER_GENERAL_6,
		EARNINGS_PER_GENERAL_7,
		EARNINGS_PER_GENERAL_8,
		EARNINGS_PER_GENERAL_9,
		EARNINGS_PER_GENERAL_10,
		EARNINGS_PER_GENERAL_11,
		EARNINGS_PER_GENERAL_12,
		EARNINGS_PER_GENERAL_13,
		EARNINGS_PER_GENERAL_14,

		TECHCAPTURED_PER_GENERAL_0,
		TECHCAPTURED_PER_GENERAL_1,
		TECHCAPTURED_PER_GENERAL_2,
		TECHCAPTURED_PER_GENERAL_3,
		TECHCAPTURED_PER_GENERAL_4,
		TECHCAPTURED_PER_GENERAL_5,
		TECHCAPTURED_PER_GENERAL_6,
		TECHCAPTURED_PER_GENERAL_7,
		TECHCAPTURED_PER_GENERAL_8,
		TECHCAPTURED_PER_GENERAL_9,
		TECHCAPTURED_PER_GENERAL_10,
		TECHCAPTURED_PER_GENERAL_11,
		TECHCAPTURED_PER_GENERAL_12,
		TECHCAPTURED_PER_GENERAL_13,
		TECHCAPTURED_PER_GENERAL_14,

		DISCONS_PER_GENERAL_0,
		DISCONS_PER_GENERAL_1,
		DISCONS_PER_GENERAL_2,
		DISCONS_PER_GENERAL_3,
		DISCONS_PER_GENERAL_4,
		DISCONS_PER_GENERAL_5,
		DISCONS_PER_GENERAL_6,
		DISCONS_PER_GENERAL_7,
		DISCONS_PER_GENERAL_8,
		DISCONS_PER_GENERAL_9,
		DISCONS_PER_GENERAL_10,
		DISCONS_PER_GENERAL_11,
		DISCONS_PER_GENERAL_12,
		DISCONS_PER_GENERAL_13,
		DISCONS_PER_GENERAL_14,

		DESYNCS_PER_GENERAL_0,
		DESYNCS_PER_GENERAL_1,
		DESYNCS_PER_GENERAL_2,
		DESYNCS_PER_GENERAL_3,
		DESYNCS_PER_GENERAL_4,
		DESYNCS_PER_GENERAL_5,
		DESYNCS_PER_GENERAL_6,
		DESYNCS_PER_GENERAL_7,
		DESYNCS_PER_GENERAL_8,
		DESYNCS_PER_GENERAL_9,
		DESYNCS_PER_GENERAL_10,
		DESYNCS_PER_GENERAL_11,
		DESYNCS_PER_GENERAL_12,
		DESYNCS_PER_GENERAL_13,
		DESYNCS_PER_GENERAL_14,

		SURRENDERS_PER_GENERAL_0,
		SURRENDERS_PER_GENERAL_1,
		SURRENDERS_PER_GENERAL_2,
		SURRENDERS_PER_GENERAL_3,
		SURRENDERS_PER_GENERAL_4,
		SURRENDERS_PER_GENERAL_5,
		SURRENDERS_PER_GENERAL_6,
		SURRENDERS_PER_GENERAL_7,
		SURRENDERS_PER_GENERAL_8,
		SURRENDERS_PER_GENERAL_9,
		SURRENDERS_PER_GENERAL_10,
		SURRENDERS_PER_GENERAL_11,
		SURRENDERS_PER_GENERAL_12,
		SURRENDERS_PER_GENERAL_13,
		SURRENDERS_PER_GENERAL_14,

		GAMESOF2P_PER_GENERAL_0,
		GAMESOF2P_PER_GENERAL_1,
		GAMESOF2P_PER_GENERAL_2,
		GAMESOF2P_PER_GENERAL_3,
		GAMESOF2P_PER_GENERAL_4,
		GAMESOF2P_PER_GENERAL_5,
		GAMESOF2P_PER_GENERAL_6,
		GAMESOF2P_PER_GENERAL_7,
		GAMESOF2P_PER_GENERAL_8,
		GAMESOF2P_PER_GENERAL_9,
		GAMESOF2P_PER_GENERAL_10,
		GAMESOF2P_PER_GENERAL_11,
		GAMESOF2P_PER_GENERAL_12,
		GAMESOF2P_PER_GENERAL_13,
		GAMESOF2P_PER_GENERAL_14,

		GAMESOF3P_PER_GENERAL_0,
		GAMESOF3P_PER_GENERAL_1,
		GAMESOF3P_PER_GENERAL_2,
		GAMESOF3P_PER_GENERAL_3,
		GAMESOF3P_PER_GENERAL_4,
		GAMESOF3P_PER_GENERAL_5,
		GAMESOF3P_PER_GENERAL_6,
		GAMESOF3P_PER_GENERAL_7,
		GAMESOF3P_PER_GENERAL_8,
		GAMESOF3P_PER_GENERAL_9,
		GAMESOF3P_PER_GENERAL_10,
		GAMESOF3P_PER_GENERAL_11,
		GAMESOF3P_PER_GENERAL_12,
		GAMESOF3P_PER_GENERAL_13,
		GAMESOF3P_PER_GENERAL_14,

		GAMESOF4P_PER_GENERAL_0,
		GAMESOF4P_PER_GENERAL_1,
		GAMESOF4P_PER_GENERAL_2,
		GAMESOF4P_PER_GENERAL_3,
		GAMESOF4P_PER_GENERAL_4,
		GAMESOF4P_PER_GENERAL_5,
		GAMESOF4P_PER_GENERAL_6,
		GAMESOF4P_PER_GENERAL_7,
		GAMESOF4P_PER_GENERAL_8,
		GAMESOF4P_PER_GENERAL_9,
		GAMESOF4P_PER_GENERAL_10,
		GAMESOF4P_PER_GENERAL_11,
		GAMESOF4P_PER_GENERAL_12,
		GAMESOF4P_PER_GENERAL_13,
		GAMESOF4P_PER_GENERAL_14,

		GAMESOF5P_PER_GENERAL_0,
		GAMESOF5P_PER_GENERAL_1,
		GAMESOF5P_PER_GENERAL_2,
		GAMESOF5P_PER_GENERAL_3,
		GAMESOF5P_PER_GENERAL_4,
		GAMESOF5P_PER_GENERAL_5,
		GAMESOF5P_PER_GENERAL_6,
		GAMESOF5P_PER_GENERAL_7,
		GAMESOF5P_PER_GENERAL_8,
		GAMESOF5P_PER_GENERAL_9,
		GAMESOF5P_PER_GENERAL_10,
		GAMESOF5P_PER_GENERAL_11,
		GAMESOF5P_PER_GENERAL_12,
		GAMESOF5P_PER_GENERAL_13,
		GAMESOF5P_PER_GENERAL_14,

		GAMESOF6P_PER_GENERAL_0,
		GAMESOF6P_PER_GENERAL_1,
		GAMESOF6P_PER_GENERAL_2,
		GAMESOF6P_PER_GENERAL_3,
		GAMESOF6P_PER_GENERAL_4,
		GAMESOF6P_PER_GENERAL_5,
		GAMESOF6P_PER_GENERAL_6,
		GAMESOF6P_PER_GENERAL_7,
		GAMESOF6P_PER_GENERAL_8,
		GAMESOF6P_PER_GENERAL_9,
		GAMESOF6P_PER_GENERAL_10,
		GAMESOF6P_PER_GENERAL_11,
		GAMESOF6P_PER_GENERAL_12,
		GAMESOF6P_PER_GENERAL_13,
		GAMESOF6P_PER_GENERAL_14,

		GAMESOF7P_PER_GENERAL_0,
		GAMESOF7P_PER_GENERAL_1,
		GAMESOF7P_PER_GENERAL_2,
		GAMESOF7P_PER_GENERAL_3,
		GAMESOF7P_PER_GENERAL_4,
		GAMESOF7P_PER_GENERAL_5,
		GAMESOF7P_PER_GENERAL_6,
		GAMESOF7P_PER_GENERAL_7,
		GAMESOF7P_PER_GENERAL_8,
		GAMESOF7P_PER_GENERAL_9,
		GAMESOF7P_PER_GENERAL_10,
		GAMESOF7P_PER_GENERAL_11,
		GAMESOF7P_PER_GENERAL_12,
		GAMESOF7P_PER_GENERAL_13,
		GAMESOF7P_PER_GENERAL_14,

		GAMESOF8P_PER_GENERAL_0,
		GAMESOF8P_PER_GENERAL_1,
		GAMESOF8P_PER_GENERAL_2,
		GAMESOF8P_PER_GENERAL_3,
		GAMESOF8P_PER_GENERAL_4,
		GAMESOF8P_PER_GENERAL_5,
		GAMESOF8P_PER_GENERAL_6,
		GAMESOF8P_PER_GENERAL_7,
		GAMESOF8P_PER_GENERAL_8,
		GAMESOF8P_PER_GENERAL_9,
		GAMESOF8P_PER_GENERAL_10,
		GAMESOF8P_PER_GENERAL_11,
		GAMESOF8P_PER_GENERAL_12,
		GAMESOF8P_PER_GENERAL_13,
		GAMESOF8P_PER_GENERAL_14,

		CUSTOMGAMES_PER_GENERAL_0,
		CUSTOMGAMES_PER_GENERAL_1,
		CUSTOMGAMES_PER_GENERAL_2,
		CUSTOMGAMES_PER_GENERAL_3,
		CUSTOMGAMES_PER_GENERAL_4,
		CUSTOMGAMES_PER_GENERAL_5,
		CUSTOMGAMES_PER_GENERAL_6,
		CUSTOMGAMES_PER_GENERAL_7,
		CUSTOMGAMES_PER_GENERAL_8,
		CUSTOMGAMES_PER_GENERAL_9,
		CUSTOMGAMES_PER_GENERAL_10,
		CUSTOMGAMES_PER_GENERAL_11,
		CUSTOMGAMES_PER_GENERAL_12,
		CUSTOMGAMES_PER_GENERAL_13,
		CUSTOMGAMES_PER_GENERAL_14,

		QUICKMATCHES_PER_GENERAL_0,
		QUICKMATCHES_PER_GENERAL_1,
		QUICKMATCHES_PER_GENERAL_2,
		QUICKMATCHES_PER_GENERAL_3,
		QUICKMATCHES_PER_GENERAL_4,
		QUICKMATCHES_PER_GENERAL_5,
		QUICKMATCHES_PER_GENERAL_6,
		QUICKMATCHES_PER_GENERAL_7,
		QUICKMATCHES_PER_GENERAL_8,
		QUICKMATCHES_PER_GENERAL_9,
		QUICKMATCHES_PER_GENERAL_10,
		QUICKMATCHES_PER_GENERAL_11,
		QUICKMATCHES_PER_GENERAL_12,
		QUICKMATCHES_PER_GENERAL_13,
		QUICKMATCHES_PER_GENERAL_14,

		LOCALE,
		GAMES_AS_RANDOM,
		OPTIONS,
		SYSTEM_SPEC,
		LASTFPS,
		LASTGENERAL,
		GAMESINROWWITHLASTGENERAL,
		CHALLENGEMEDALS,
		BATTLEHONORS,
		QMWINSINAROW,
		MAXQMWINSINAROW,
		WINSINAROW,
		MAXWINSINAROW,
		LOSSESINAROW,
		MAXLOSSESINAROW,
		DISCONSINAROW,

		MAXDISCONSINAROW,
		DESYNCSINAROW,
		MAXDESYNCSINAROW,
		BUILTPARTICLECANNON,
		BUILTNUKE,
		BUILTSCUD,
		LASTLADDERPORT,
		LASTLADDERHOST
	}

	public class TURNCredentialContainer
	{
		public TURNCredentialContainer(string strUsername, string strToken)
		{
			m_strToken = strToken;
			m_strUsername = strUsername;
		}

		public string m_strUsername;
		public string m_strToken;
	}

	public class iceEntry
	{
		public List<string>? urls { get; set; } = null;
		public string? username { get; set; } = null;
		public string? credential { get; set; } = null;
	}

	public class TURNResponse
	{
		public List<iceEntry>? iceServers { get; set; }
	}

	public static class S3CredentialManager
	{
		private static AmazonS3Client m_s3client = null;
		private static bool s_uploadsEnabled = false;

		public static void Initialize()
		{
			if (Program.g_Config == null)
			{
				throw new Exception("Config not loaded");
			}

			s_uploadsEnabled = Program.g_Config.GetSection("MatchData").GetValue<bool?>("upload_match_data") ?? false;

			// When uploads are disabled, the S3 settings are intentionally optional.
			if (!s_uploadsEnabled)
			{
				Console.WriteLine("MatchData: upload_match_data is false, replay and screenshot uploads are disabled.");
				return;
			}

			GetS3Config(out int TTL, out string access_key, out string secret_key, out string bucket_name, out string client_endpoint);

			var config = new AmazonS3Config
			{
				ServiceURL = client_endpoint,
				ForcePathStyle = true
			};

			m_s3client = new AmazonS3Client(access_key, secret_key, config);
		}

		private static void GetS3Config(out int TTL, out string access_key, out string secret_key, out string bucket_name, out string endpoint)
		{
			TTL = -1;
			access_key = String.Empty;
			secret_key = String.Empty;
			bucket_name = String.Empty;
			endpoint = String.Empty;

			if (Program.g_Config == null)
			{
				throw new Exception("Config not loaded");
			}

			IConfigurationSection? turnSettings = Program.g_Config.GetSection("MatchData");

			if (turnSettings == null)
			{
				throw new Exception("MatchData section missing in config");
			}

			string? s3_access_key = turnSettings.GetValue<string>("s3_access_key");
			string? s3_secret_key = turnSettings.GetValue<string>("s3_secret_key");
			string? s3_bucket_name = turnSettings.GetValue<string>("s3_bucket_name");
			string? s3_endpoint = turnSettings.GetValue<string>("s3_endpoint");
			int? s3_url_ttl_minutes = turnSettings.GetValue<int>("s3_url_ttl_minutes");

			if (s3_access_key == null)
			{
				throw new Exception("s3_access_key missing in config");
			}

			if (s3_secret_key == null)
			{
				throw new Exception("s3_secret_key missing in config");
			}

			if (s3_bucket_name == null)
			{
				throw new Exception("s3_bucket_name missing in config");
			}

			if (s3_endpoint == null)
			{
				throw new Exception("s3_endpoint missing in config");
			}

			if (s3_url_ttl_minutes == null)
			{
				throw new Exception("s3_url_ttl_minutes missing in config");
			}

			TTL = (int)s3_url_ttl_minutes;
			access_key = s3_access_key;
			secret_key = s3_secret_key;
			bucket_name = s3_bucket_name;
			endpoint = s3_endpoint;
		}

		public static async Task<string?> GetPresignedURL(EMetadataFileType fileType, EScreenshotType screenshotTypeIfScreenshot, UInt64 matchID, Int64 userID, int slotIndex, DateTime matchStartTime)
		{
			if (!s_uploadsEnabled || m_s3client == null)
			{
				return null;
			}

			GetS3Config(out int TTL, out string access_key, out string secret_key, out string bucket_name, out string client_endpoint);
			TimeSpan expiresIn = TimeSpan.FromMinutes(TTL);

			DateTime utcNow = DateTime.UtcNow;
			int hour = utcNow.Hour;
			int minute = utcNow.Minute;

			string strPerMatchUserIDKey = Helpers.ComputeMD5Hash(String.Format("{0}_{1}", matchID, userID));
			string strFileName = null;

			string strContentType = String.Empty;
			string strFolderPrefix = String.Empty; ;


			if (fileType == EMetadataFileType.FILE_TYPE_SCREENSHOT)
			{
				strContentType = "image/jpeg";
				fileType = EMetadataFileType.FILE_TYPE_SCREENSHOT;

				string screenshotTypePrefix = "screenshot";
				if (screenshotTypeIfScreenshot == EScreenshotType.SCREENSHOT_TYPE_LOADSCREEN)
				{
					screenshotTypePrefix = "loadscreen";
				}
				else if (screenshotTypeIfScreenshot == EScreenshotType.SCREENSHOT_TYPE_GAMEPLAY)
				{
					screenshotTypePrefix = "gameplay";
				}
				else if (screenshotTypeIfScreenshot == EScreenshotType.SCREENSHOT_TYPE_SCORESCREEN)
				{
					screenshotTypePrefix = "scorescreen";
				}

				strFileName = String.Format("screenshot_{0}_{1}_{2}.jpg", screenshotTypePrefix, hour, minute);
				strFolderPrefix = "screenshots";
			}
			else if (fileType == EMetadataFileType.FILE_TYPE_REPLAY)
			{
				strContentType = "application/octet-stream";
				fileType = EMetadataFileType.FILE_TYPE_REPLAY;

				strFileName = String.Format("match_{0}_user_{1}_replay.rep", matchID, strPerMatchUserIDKey);
				strFolderPrefix = "replays";
			}

			if (strFileName == null)
			{
				return null;
			}

			string objectKey = String.Format("{3}/{4}/{5}/{6}/match_{0}/user_{1}/{2}", matchID, strPerMatchUserIDKey, strFileName, strFolderPrefix, matchStartTime.Year, matchStartTime.Month, matchStartTime.Day);

			var request = new GetPreSignedUrlRequest
			{
				BucketName = bucket_name,
				Key = objectKey,
				Verb = HttpVerb.PUT,
				Expires = DateTime.UtcNow.Add(expiresIn),
				ContentType = strContentType
			};

			// anytime we get an S3 uri, register the metadata
			using var scope = ServiceLocator.Services.CreateScope();
			var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
			await using var db = await factory.CreateDbContextAsync();

			await Database.MatchHistory.AttachMatchHistoryMetadata(db, matchID, slotIndex, strFileName, fileType);

			return await m_s3client.GetPreSignedURLAsync(request);
		}
	}


	public static class TURNCredentialManager
	{
		private static ConcurrentDictionary<Int64, string> g_DictTURNUsernames = new();

		private static void GetTURNConfig(out int TTL, out string token, out string key, out bool bShouldInvalidateTokensAutomatically)
		{
			TTL = -1;
			token = String.Empty;
			key = String.Empty;

			if (Program.g_Config == null)
			{
				throw new Exception("Config not loaded");
			}

			IConfigurationSection? turnSettings = Program.g_Config.GetSection("TURN");

			if (turnSettings == null)
			{
				throw new Exception("TURN section missing in config");
			}

			string? turn_key = turnSettings.GetValue<string>("key");
			string? turn_token = turnSettings.GetValue<string>("token");
			int? token_ttl = turnSettings.GetValue<int>("token_ttl");
			bool? automatic_token_invalidate = turnSettings.GetValue<bool>("automatic_token_invalidate");

			if (turn_key == null)
			{
				throw new Exception("turn_key missing in config");
			}

			if (turn_token == null)
			{
				throw new Exception("turn_token missing in config");
			}

			if (token_ttl == null)
			{
				throw new Exception("token_ttl missing in config");
			}

			if (automatic_token_invalidate == null)
			{
				throw new Exception("automatic_token_invalidate missing in config");
			}

			TTL = (int)token_ttl;
			token = turn_token;
			key = turn_key;
			bShouldInvalidateTokensAutomatically = (bool)automatic_token_invalidate;
		}

		public static async Task<TURNCredentialContainer?> CreateCredentialsForUser(Int64 userID)
		{
#if DEBUG
			TURNCredentialContainer fakeCreds = new("fake", "fake");
			await Task.Delay(1);
			return fakeCreds;
#endif
			GetTURNConfig(out int TurnTTL, out string TurnToken, out string TurnKey, out bool bShouldInvalidateTokensAutomatically);

			// we should only have 1 turn credential at a time... clean it up
			if (g_DictTURNUsernames.ContainsKey(userID))
			{
				await DeleteCredentialsForUser(userID);
			}

			// create new credential
			Dictionary<string, object> dictReqData = new();
			dictReqData.Add("ttl", TurnTTL); // 4 hours
			dictReqData.Add("go_user_id", userID); // go user id
			var jsonContent = JsonSerializer.Serialize(dictReqData);
			using var requestContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

			using (HttpClient client = new HttpClient(new SocketsHttpHandler()
			{
				ConnectCallback = async (context, cancellationToken) =>
				{
					// Use DNS to look up the IP addresses of the target host:
					// - IP v4: AddressFamily.InterNetwork
					// - IP v6: AddressFamily.InterNetworkV6
					// - IP v4 or IP v6: AddressFamily.Unspecified
					// note: this method throws a SocketException when there is no IP address for the host
					var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);

					// Open the connection to the target host/port
					var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

					// Turn off Nagle's algorithm since it degrades performance in most HttpClient scenarios.
					socket.NoDelay = true;

					try
					{
						await socket.ConnectAsync(entry.AddressList, context.DnsEndPoint.Port, cancellationToken);

						// If you want to choose a specific IP address to connect to the server
						// await socket.ConnectAsync(
						//    entry.AddressList[Random.Shared.Next(0, entry.AddressList.Length)],
						//    context.DnsEndPoint.Port, cancellationToken);

						// Return the NetworkStream to the caller
						return new NetworkStream(socket, ownsSocket: true);
					}
					catch
					{
						socket.Dispose();
						throw;
					}
				}
			}))
			{
				client.Timeout = TimeSpan.FromSeconds(10);
				client.DefaultRequestHeaders.Add("Authorization", String.Format("Bearer {0}", TurnToken));
				client.DefaultRequestHeaders.Add("Accept", "application/json");
				//client.DefaultRequestHeaders.Add("Content-Type", "application/json");
				try
				{
					Console.WriteLine("Start req turn credentials at {0}", Environment.TickCount);
					string strURI = String.Format("https://rtc.live.cloudflare.com/v1/turn/keys/{0}/credentials/generate-ice-servers", TurnKey);
					HttpResponseMessage response = await client.PostAsync(strURI, requestContent);

					if (response.IsSuccessStatusCode)
					{
						Console.WriteLine("Finish req turn credentials at {0}", Environment.TickCount);
						string responseBody = await response.Content.ReadAsStringAsync();
						TURNResponse? resp = JsonSerializer.Deserialize<TURNResponse>(responseBody);

						try
						{
							if (resp != null && resp.iceServers != null)
							{
								foreach (iceEntry? entry in resp.iceServers)
								{
									if (!string.IsNullOrEmpty(entry.username) && !string.IsNullOrEmpty(entry.credential))
									{
										TURNCredentialContainer creds = new(entry.username, entry.credential);
										g_DictTURNUsernames[userID] = entry.username;
										return creds;
									}
								}
							}

							return null;
						}
						catch
						{

						}

						return null;
					}
				}
				catch
				{

				}
			}

			return null;
		}

		public static async Task DeleteCredentialsForUser(Int64 userID)
		{
#if DEBUG
            await Task.Delay(1);
            return;
#endif

            GetTURNConfig(out int TurnTTL, out string TurnToken, out string TurnKey, out bool bShouldInvalidateTokensAutomatically);

			if (!bShouldInvalidateTokensAutomatically)
			{
				return;
			}

			if (g_DictTURNUsernames.ContainsKey(userID))
			{
				string strTURNUsername = g_DictTURNUsernames[userID];

				g_DictTURNUsernames.Remove(userID, out string? strUsername);

				// revoke credential
				Dictionary<string, object> dictReqData = new();
				var jsonContent = JsonSerializer.Serialize(dictReqData);
				using var requestContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

				using (HttpClient client = new HttpClient(new SocketsHttpHandler()
				{
					ConnectCallback = async (context, cancellationToken) =>
					{
						// Use DNS to look up the IP addresses of the target host:
						// - IP v4: AddressFamily.InterNetwork
						// - IP v6: AddressFamily.InterNetworkV6
						// - IP v4 or IP v6: AddressFamily.Unspecified
						// note: this method throws a SocketException when there is no IP address for the host
						var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);

						// Open the connection to the target host/port
						var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

						// Turn off Nagle's algorithm since it degrades performance in most HttpClient scenarios.
						socket.NoDelay = true;

						try
						{
							await socket.ConnectAsync(entry.AddressList, context.DnsEndPoint.Port, cancellationToken);

							// If you want to choose a specific IP address to connect to the server
							// await socket.ConnectAsync(
							//    entry.AddressList[Random.Shared.Next(0, entry.AddressList.Length)],
							//    context.DnsEndPoint.Port, cancellationToken);

							// Return the NetworkStream to the caller
							return new NetworkStream(socket, ownsSocket: true);
						}
						catch
						{
							socket.Dispose();
							throw;
						}
					}
				}))
				{
					client.Timeout = TimeSpan.FromSeconds(10);
					client.DefaultRequestHeaders.Add("Authorization", String.Format("Bearer {0}", TurnToken));
					client.DefaultRequestHeaders.Add("Accept", "application/json");
					try
					{
						string strURI = String.Format("https://rtc.live.cloudflare.com/v1/turn/keys/{0}/credentials/{1}/revoke", TurnKey, strTURNUsername);
						HttpResponseMessage response = await client.PostAsync(strURI, requestContent);
					}
					catch
					{

					}
				}
			}
		}
	}

	public enum EPlayerType
	{
		SLOT_OPEN,
		SLOT_CLOSED,
		SLOT_EASY_AI,
		SLOT_MED_AI,
		SLOT_BRUTAL_AI,
		SLOT_PLAYER
	}

	public enum EWebSocketMessageID
	{
		UNKNOWN = -1,
		NETWORK_ROOM_CHAT_FROM_CLIENT = 1,
		NETWORK_ROOM_CHAT_FROM_SERVER = 2,
		NETWORK_ROOM_CHANGE_ROOM = 3,
		NETWORK_ROOM_MEMBER_LIST_UPDATE = 4, // TODO: This could be more optimal, send a diff instead of full list?
		NETWORK_ROOM_MARK_READY = 5,
		LOBBY_CURRENT_LOBBY_UPDATE = 6,
		NETWORK_ROOM_LOBBY_LIST_UPDATE = 7,
		ANTICHEAT_MESSAGE = 8,
		PLAYER_NAME_CHANGE = 9,
		LOBBY_ROOM_CHAT_FROM_CLIENT = 10,
		LOBBY_CHAT_FROM_SERVER = 11,
		NETWORK_SIGNAL = 12,
		START_GAME = 13,
		PING = 14,
		PONG = 15,
		PROBE = 16,
		NETWORK_CONNECTION_START_SIGNALLING = 17,
		NETWORK_CONNECTION_DISCONNECT_PLAYER = 18,
		NETWORK_CONNECTION_CLIENT_REQUEST_SIGNALLING = 19,
		MATCHMAKING_ACTION_JOIN_PREARRANGED_LOBBY = 20,
		MATCHMAKING_ACTION_START_GAME = 21,
		MATCHMAKING_MESSAGE = 22,
		START_GAME_COUNTDOWN_STARTED = 23,
		LOBBY_REMOVE_PASSWORD = 24,
		LOBBY_CHANGE_PASSWORD = 25,
		FULL_MESH_CONNECTIVITY_CHECK_HOST_REQUESTS_BEGIN = 26,
		FULL_MESH_CONNECTIVITY_CHECK_RESPONSE = 27,
		FULL_MESH_CONNECTIVITY_CHECK_RESPONSE_COMPLETE_TO_HOST = 28,
		SOCIAL_NEW_FRIEND_REQUEST = 29,
		SOCIAL_FRIEND_CHAT_MESSAGE_CLIENT_TO_SERVER = 30,
		SOCIAL_FRIEND_CHAT_MESSAGE_SERVER_TO_CLIENT = 31,
		SOCIAL_FRIEND_ONLINE_STATUS_CHANGED = 32,
		SOCIAL_SUBSCRIBE_REALTIME_UPDATES = 33,
		SOCIAL_UNSUBSCRIBE_REALTIME_UPDATES = 34,
		SOCIAL_FRIENDS_OVERALL_STATUS_UPDATE = 35,
		SOCIAL_FRIEND_FRIEND_REQUEST_ACCEPTED_BY_TARGET = 36,
		SOCIAL_FRIENDS_LIST_DIRTY = 37,
        SOCIAL_CANT_ADD_FRIEND_LIST_FULL = 38,
		PROBE_RESP = 39,
		AC_REGISTER_PLAYER = 40,
		AC_DEREGISTER_PLAYER = 41,
		WS_KEEPALIVE = 42,
		WS_KEEPALIVE_CLIENT = 43
	};

	public static class UserPresence
	{
		public enum EPresencePriority
		{
			Highest = 2,
			Middle = 1,
			Lowest = 0
		}

		public static string DetermineUserStatusFromAllSessions(Int64 user_id, out bool IsOnline)
		{
			List<UserSession> lstUserSessions = WebSocketManager.GetAllDataFromUser(user_id);
			IsOnline = false;

			string strOverallPresence = "Offline";
			UserPresence.EPresencePriority overallPriority = UserPresence.EPresencePriority.Lowest;

			foreach (UserSession userData in lstUserSessions)
			{
				string strThisPresence = "Offline";
				EPresencePriority thisPriority = EPresencePriority.Lowest;

				if (userData == null)
				{
					thisPriority = EPresencePriority.Lowest;
					strThisPresence = "Offline";
				}

				IsOnline = true;

				if (userData.currentLobbyID == -1)
				{
					thisPriority = EPresencePriority.Middle;
					strThisPresence = "In Server List / Chat Room";
				}
				else
				{
					var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();
					Lobby? plrLobby = lobbyManager.GetLobby(userData.currentLobbyID);

					thisPriority = EPresencePriority.Highest;

					if (plrLobby == null)
					{
						strThisPresence = "In A Lobby";
					}
					else
					{
						if (plrLobby.State == ELobbyState.GAME_SETUP)
						{
							strThisPresence = String.Format("In lobby '{0}' - Waiting on game setup", plrLobby.Name);
						}
						else if (plrLobby.State == ELobbyState.INGAME)
						{
							strThisPresence = String.Format("In lobby '{0}' -  Match In Progress", plrLobby.Name);
						}
						else if (plrLobby.State == ELobbyState.COMPLETE)
						{
							strThisPresence = String.Format("In lobby '{0}' - Game Just Finished", plrLobby.Name);
						}
						else
						{
							strThisPresence = String.Format("In lobby '{0}'", plrLobby.Name);
						}
					}
				}

				// higher than our current priority?
				if (thisPriority > overallPriority)
				{
					strOverallPresence = strThisPresence;
					overallPriority = thisPriority;
				}
			}

			return strOverallPresence;
		}
	}


	public class WebSocketMessage_Simple : WebSocketMessage
	{

	}

	public class WebSocketMessage_Probe : WebSocketMessage
	{
		public string url { get; set; } = String.Empty;
	}

	public class WebSocketMessage_StartMatch : WebSocketMessage
	{
		public string screenshot_url { get; set; } = String.Empty;
	}

	public abstract class WebSocketMessage
	{
		public int msg_id { get; set; }
	}

	public class WebSocketMessage_NameChange : WebSocketMessage
	{
		public string name { get; set; } = String.Empty;
	}

	public class WebSocketMessage_FullMeshConnectivityCheckResponseFromUser : WebSocketMessage
	{
		public List<Int64> connectivity_map { get; set; } = new();
	}

	public class WebSocketMessage_Social_NewFriendRequest : WebSocketMessage
	{
		public string display_name { get; set; } = String.Empty;
	}

	public class WebSocketMessage_FullMeshConnectivityCheckOutcome: WebSocketMessage
	{
		public bool mesh_complete { get; set; }
		public List<MissingConnectionEntry> missing_connections { get; set; } = new();
	}

	public class WebSocketMessage_FullMeshConnectivityCheckOutcomeForHost : WebSocketMessage
	{
		public Dictionary<Int64, bool> connectivity_map { get; set; } = new();
	}

	public class WebSocketMessage_LobbyPasswordChange : WebSocketMessage
	{
		public string new_password { get; set; } = String.Empty;
	}
	
	public class WebSocketMessage_NetworkRoomChatMessageInbound : WebSocketMessage
	{
		public string? message { get; set; }
		public bool action { get; set; }
	}

	public class WebSocketMessage_Social_FriendChatMessage_Inbound : WebSocketMessage
	{
		public Int64 target_user_id { get; set; }
		public string? message { get; set; }
	}

	public class WebSocketMessage_Social_FriendChatMessage_Outbound : WebSocketMessage
	{
		public Int64 source_user_id { get; set; }
		public Int64 target_user_id { get; set; }
		public string? message { get; set; }
	}

	public class WebSocketMessage_Social_FriendStatusChanged : WebSocketMessage
	{
		public string display_name { get; set; } = String.Empty;
		public bool online { get; set; }
	}

    public class WebSocketMessage_Social_FriendsListDirty: WebSocketMessage
    {
    }

    public class WebSocketMessage_Social_FriendsListFull : WebSocketMessage
    {
    }

    public class WebSocketMessage_Social_FriendRequestAccepted : WebSocketMessage
	{
		public string display_name { get; set; } = String.Empty;
    }

	public class WebSocketMessage_MatchmakingMessage : WebSocketMessage
	{
		public string? message { get; set; }
	}

	public class WebSocketMessage_PONG : WebSocketMessage
	{
	}
	public class WebSocketMessage_NetworkRoomChatMessageOutbound : WebSocketMessage
	{
		public string? message { get; set; }
		public bool action { get; set; } = false;
		public bool admin { get; set; } = false;
		public bool name_change { get; set; } = false;
	}

	public class WebSocketMessage_NetworkStartSignalling : WebSocketMessage
	{
		public Int64 lobby_id{ get; set; }
		public Int64 user_id { get; set; }
		public Int64 preferred_port { get; set; }
		public string middleware_id { get; set; }
	}

	public class WebSocketMessage_FriendsOverallStatusUpdate : WebSocketMessage
	{
		public int num_online { get; set; } = 0;
		public int num_pending { get; set; } = 0;
	}

	public class WebSocketMessage_ACRegisterPlayer : WebSocketMessage
	{
		public Int64 user_id { get; set; }
		public string mwid { get; set; } = String.Empty;
	}

	public class WebSocketMessage_ACDeregisterPlayer : WebSocketMessage
	{
		public Int64 user_id { get; set; }
		public string mwid { get; set; } = String.Empty;
	}

	public class WebSocketMessage_NetworkDisconnectPlayer : WebSocketMessage
	{
		public Int64 lobby_id { get; set; }
		public Int64 user_id { get; set; }
	}

	public class WebSocketMessage_LobbyChatMessageInbound : WebSocketMessage
	{
		public string? message { get; set; }
		public bool action { get; set; }
		public bool announcement { get; set; }
		public bool show_announcement_to_host { get; set; }
	}

	public class WebSocketMessage_RequestSignaling : WebSocketMessage
	{
		public Int64 target_user_id { get; set; }
	}
	public class WebSocketMessage_SignalBidirectional : WebSocketMessage
	{
		public Int64 target_user_id { get; set; }
		public List<byte>? payload { get; set; }
	}

	public class WebSocketMessage_AnticheatMessage : WebSocketMessage
	{
		public Int64 target_user_id { get; set; }
		public List<byte>? payload { get; set; }
	}

	public class WebSocketMessage_LobbyChatMessageOutbound : WebSocketMessage
	{
		public Int64 user_id { get; set; }
		public string? message { get; set; }
		public bool action { get; set; }
		public bool announcement { get; set; }
		public bool show_announcement_to_host { get; set; } // TODO: Remove, client doesnt care
	}
	public class WebSocketMessage_RelayUpgradeInbound : WebSocketMessage
	{
		public Int64 target_user_id { get; set; }
	}

	public class WebSocketMessage_NetworkRoomMemberListUpdate : WebSocketMessage
	{
		public List<RoomMember> members { get; set; } = new();
	}

	public class WebSocketMessage_CurrentLobbyUpdate : WebSocketMessage
	{
	}

	public class WebSocketMessage_CurrentNetworkRoomLobbyListUpdate : WebSocketMessage
	{
	}

	public class WebSocketMessage_MatchmakerJoinLobby : WebSocketMessage
	{

		public Int64 lobby_id
		{
			get; set;
		}
	}

	public class WebSocketMessage_MatchmakerStartGame : WebSocketMessage
	{

	}

}