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

using Discord;
using MaxMind.GeoIP2;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public class WebSocketController : ControllerBase
	{
		private readonly LobbyManager _lobbyManager;
		private readonly IDbContextFactory<AppDbContext> _dbFactory;

		public WebSocketController(LobbyManager lobbyManager, IDbContextFactory<AppDbContext> dbFactory)
		{
			_lobbyManager = lobbyManager;
			_dbFactory = dbFactory;
		}

		private static readonly JsonSerializerOptions JsonOpts = new()
		{
			PropertyNameCaseInsensitive = true,
			AllowOutOfOrderMetadataProperties = true
		};

		// GeoIP DB is designed to be reused; opening per request is expensive
		private static readonly DatabaseReader GeoIpReader = new("data/GeoLite2-City.mmdb");

		private struct WSMessageEnvelope
		{
			public int msg_id { get; set; }
		}

		[Route("/ws")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
		public async Task Get([FromHeader(Name = "is-reconnect")] bool bIsReconnect)
		{
			if (!HttpContext.WebSockets.IsWebSocketRequest)
			{
				HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
				return;
			}

			// create a session
			Int64 user_id = Convert.ToInt64(this.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);

			var firstEntryClientID = this.User.FindFirst("client_id");

			// client ID is mandatory
			if (firstEntryClientID == null)
			{
				// early out, dont accept WS
				HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
				return;
			}

			string ipAddress = IPHelpers.NormalizeIP(HttpContext.Connection.RemoteIpAddress?.ToString());
			string ipContinent = "NA";
			string ipCountry = "US";
			double dLongitude = 38.8977; // the whitehouse;
			double dLatitude = 77.0365f; // the whitehouse;

			try
			{
				var city = GeoIpReader.City(ipAddress);

				ipContinent = city.Continent.Code;
				ipCountry = city.Country.IsoCode;

				if (city.Location.Longitude != null)
				{
					dLongitude = (double)city.Location.Longitude;
				}

				if (city.Location.Latitude != null)
				{
					dLatitude = (double)city.Location.Latitude;
				}
			}
			catch
			{
				// keep defaults
			}

			bool bIsAdmin = HttpContext.User.IsInRole("Admin");

			KnownClients.EKnownClients client_id = KnownClients.EKnownClients.unknown;
			if (int.TryParse(firstEntryClientID.Value, out int clientIDInt32))
			{
				// Validate if the int corresponds to a defined enum value
				if (System.Enum.IsDefined(typeof(KnownClients.EKnownClients), clientIDInt32))
				{
					client_id = (KnownClients.EKnownClients)clientIDInt32;
				}
			}

			// if unknown, error
			if (client_id == KnownClients.EKnownClients.unknown)
			{
				HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
				return;
			}

			EUserSessionType sessType = TokenHelper.GetSessionType(this);

			await using var db = await _dbFactory.CreateDbContextAsync();
			UserWebSocketInstance wsSess = await WebSocketManager.CreateSession(
				db,
				sessType,
				bIsReconnect,
				user_id,
				client_id,
				ipAddress,
				ipContinent,
				ipCountry,
				dLatitude,
				dLongitude,
				bIsAdmin);

			// if null, it was probably a reconnect and they need to fully reconnect, so return an error instead
			if (wsSess == null)
			{
				HttpContext.Response.StatusCode = StatusCodes.Status205ResetContent;
				return;
			}

			// accept WS
			using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();

			// attach
			wsSess.AttachWebsocket(webSocket);

			var buffer = new byte[8196 * 4];
			WebSocketReceiveResult? receiveResult = null;

			// WebSocket messages can arrive split across multiple frames - they must be reassembled before parsing,
			// otherwise each fragment is parsed as if it were a whole message and the message is silently lost
			MemoryStream? fragmentBuffer = null;
			const int c_MaxReassembledMessageBytes = 8196 * 4 * 8;

			while (webSocket.State == WebSocketState.Open)
			{
				bool bDisconnectTest = false;
				if (bDisconnectTest)
				{
					await webSocket.CloseAsync(WebSocketCloseStatus.ProtocolError, "Disconnect Test", CancellationToken.None);
					break;
				}

				try
				{
					using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // timeout
					receiveResult = await webSocket.ReceiveAsync(
						new ArraySegment<byte>(buffer), cts.Token);
				}
				catch (OperationCanceledException)
				{
					// No message received in 30s � send a keep-alive pong and continue waiting
					await wsSess.SendPong();
					continue;
				}
				catch (Exception ex)
				{
					// Log unexpected errors
					Console.WriteLine($"WebSocket error: {ex}");
					SentrySdk.CaptureException(ex);
					break;
				}

				if (receiveResult.MessageType == WebSocketMessageType.Close)
				{
					using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // timeout
					await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cts.Token);
					break;
				}

				// slice only the valid part, no extra allocation
				ArraySegment<byte> segment;
				if (!receiveResult.EndOfMessage || fragmentBuffer != null)
				{
					fragmentBuffer ??= new MemoryStream();

					if (fragmentBuffer.Length + receiveResult.Count > c_MaxReassembledMessageBytes)
					{
						// a client streaming an unbounded message - drop the connection rather than buffer forever
						fragmentBuffer.Dispose();
						fragmentBuffer = null;

						using var ctsTooBig = new CancellationTokenSource(TimeSpan.FromSeconds(10));
						await webSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", ctsTooBig.Token);
						break;
					}

					fragmentBuffer.Write(buffer, 0, receiveResult.Count);

					if (!receiveResult.EndOfMessage)
					{
						// wait for the rest of the message
						continue;
					}

					segment = new ArraySegment<byte>(fragmentBuffer.ToArray());
					fragmentBuffer.Dispose();
					fragmentBuffer = null;
				}
				else
				{
					segment = new ArraySegment<byte>(buffer, 0, receiveResult.Count);
				}

				UserSession? sourceUserData = WebSocketManager.GetSessionFromUser(wsSess.m_UserID, wsSess.m_SessionType);

				// if we lost session data, close WS
				if (sourceUserData == null)
				{
					wsSess.CloseAsync(WebSocketCloseStatus.NormalClosure, "User signed in from another point of presence [B]");
					break;
				}

				await ProcessWSMessage(wsSess, sourceUserData, receiveResult, segment);
			}

			Console.ForegroundColor = ConsoleColor.Cyan;
			SharedUserData? sourceData = WebSocketManager.GetSharedDataForUser(user_id);
			Console.WriteLine("WEBSOCKET DISCONNECT FOR {0}", sourceData == null ? "NULL" : sourceData.m_strDisplayName);
			Console.ForegroundColor = ConsoleColor.Gray;

			// close the session
			if (wsSess != null)
			{
				// A closed websocket stopped watching - sweep its pending-observer subscriptions
				// so the count doesn't keep counting it.
				UserSession? closingSession = WebSocketManager.GetSessionFromUser(user_id, wsSess.m_SessionType);
				if (closingSession != null)
				{
					_lobbyManager.RemovePendingObserver(closingSession);
				}

				await WebSocketManager.DeleteSession(user_id, wsSess.m_SessionType, wsSess, false);
			}

			// do close (if in the correct state)
			if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived || webSocket.State == WebSocketState.CloseSent)
			{
				WebSocketCloseStatus closeStatus = WebSocketCloseStatus.PolicyViolation;
				string closeStatusDescription = "Protocol Error (Probably Disconnect)";
				if (receiveResult != null)
				{
					if (receiveResult.CloseStatus != null)
					{
						closeStatus = receiveResult.CloseStatus.Value;
						closeStatusDescription = receiveResult.CloseStatusDescription;
					}
				}

				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // timeout
				await webSocket.CloseAsync(closeStatus, closeStatusDescription, cts.Token);
			}
		}

		private async Task ProcessWSMessage(UserWebSocketInstance sourceWS, UserSession sourceUserSession, WebSocketReceiveResult receiveResult, ArraySegment<byte> buffer)
		{
			SharedUserData sourceUserData = WebSocketManager.GetSharedDataForUser(sourceUserSession.m_UserID);

			// shared data can legitimately be gone (session torn down concurrently) - dereferencing it below would
			// throw straight out of the receive loop and kill the connection
			if (sourceUserData == null)
			{
				return;
			}

			if (receiveResult.MessageType == WebSocketMessageType.Close)
			{
				await WebSocketManager.DeleteSession(sourceWS.m_UserID, sourceUserSession.GetSessionType(), sourceWS, false);
				return;
			}

			// we only process text or binary messages
			if (receiveResult.MessageType != WebSocketMessageType.Text &&
				receiveResult.MessageType != WebSocketMessageType.Binary)
			{
				return;
			}

			if (buffer.Array == null)
			{
				return;
			}

			ReadOnlySpan<byte> payload = buffer.AsSpan();

			WSMessageEnvelope envelope;
			try
			{
				envelope = JsonSerializer.Deserialize<WSMessageEnvelope>(payload, JsonOpts);
			}
			catch
			{
				// malformed
				return;
			}

			EWebSocketMessageID msgID = (EWebSocketMessageID)envelope.msg_id;

			// Only allocate a Dictionary when we actually need arbitrary fields
			Dictionary<string, JsonElement>? data = null;
			bool needsData =
				msgID == EWebSocketMessageID.NETWORK_ROOM_CHANGE_ROOM ||
				msgID == EWebSocketMessageID.NETWORK_ROOM_MARK_READY;

			if (needsData)
			{
				try
				{
					data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload, JsonOpts);
				}
				catch
				{
					data = null;
				}
			}

			try
			{
				if (msgID == EWebSocketMessageID.PING)
				{
					await sourceWS.SendPong();
				}
				else if (msgID == EWebSocketMessageID.SOCIAL_SUBSCRIBE_REALTIME_UPDATES)
				{
					sourceUserSession.SetSubscribedToRealtimeSocialUpdates(true);
				}
				else if (msgID == EWebSocketMessageID.SOCIAL_UNSUBSCRIBE_REALTIME_UPDATES)
				{
					sourceUserSession.SetSubscribedToRealtimeSocialUpdates(false);
				}
				else if (msgID == EWebSocketMessageID.SOCIAL_FRIEND_CHAT_MESSAGE_CLIENT_TO_SERVER)
				{
					WebSocketMessage_Social_FriendChatMessage_Inbound? chatMessage =
						JsonSerializer.Deserialize<WebSocketMessage_Social_FriendChatMessage_Inbound>(payload, JsonOpts);

					if (chatMessage != null)
					{
						// must be online & friends

						SharedUserData? targetUserData = WebSocketManager.GetSharedDataForUser(chatMessage.target_user_id);

						if (targetUserData != null)
						{
							if (sourceUserData.GetSocialContainer().Friends.Contains(chatMessage.target_user_id)
								&& targetUserData.GetSocialContainer().Friends.Contains(sourceUserSession.m_UserID))
							{
								// make websocket msg
								WebSocketMessage_Social_FriendChatMessage_Outbound outboundMsg = new();
								outboundMsg.msg_id = (int)EWebSocketMessageID.SOCIAL_FRIEND_CHAT_MESSAGE_SERVER_TO_CLIENT;
								outboundMsg.source_user_id = sourceWS.m_UserID;
								outboundMsg.target_user_id = chatMessage.target_user_id;
								outboundMsg.message = String.Format("{0}: {1}", sourceUserData.m_strDisplayName, chatMessage.message);
								byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsg));

								// send to both on all websockets
								WebsocketHelper.SendToAllSessionsOfUser(chatMessage.target_user_id, bytesJSON);
								WebsocketHelper.SendToAllSessionsOfUser(sourceWS.m_UserID, bytesJSON);
							}
						}
						else
						{
							// ok, they can chat, send the message to both of them
							WebSocketMessage_Social_FriendChatMessage_Outbound outboundMsg = new();
							outboundMsg.msg_id = (int)EWebSocketMessageID.SOCIAL_FRIEND_CHAT_MESSAGE_SERVER_TO_CLIENT;
							outboundMsg.source_user_id = sourceUserSession.m_UserID;
							outboundMsg.target_user_id = chatMessage.target_user_id;
							outboundMsg.message = String.Format("This user is not online. Offline messaging is not supported.");

							// send to source
							byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsg));

							await sourceWS.SendAsync(bytesJSON, WebSocketMessageType.Text);
						}
					}
				}
				else if (msgID == EWebSocketMessageID.NETWORK_ROOM_CHAT_FROM_CLIENT)
				{
					// must be in a room
					if (sourceUserSession.networkRoomID == -1)
					{
						return;
					}

					WebSocketMessage_NetworkRoomChatMessageInbound? chatMessage =
						JsonSerializer.Deserialize<WebSocketMessage_NetworkRoomChatMessageInbound>(payload, JsonOpts);

					if (chatMessage != null)
					{
						// response
						WebSocketMessage_NetworkRoomChatMessageOutbound outboundMsg = new WebSocketMessage_NetworkRoomChatMessageOutbound();
						outboundMsg.msg_id = (int)EWebSocketMessageID.NETWORK_ROOM_CHAT_FROM_SERVER;

						if (chatMessage.action)
						{
							outboundMsg.message = String.Format("{0} {1}", sourceUserData.m_strDisplayName, chatMessage.message);
							outboundMsg.admin = false; // dont care for actions
							outboundMsg.name_change = false;
						}
						else
						{
							if (sourceUserData.IsAdmin())
							{
								outboundMsg.message = String.Format("[\u2605\u2605GO STAFF\u2605\u2605]    [{0}] {1}", sourceUserData.m_strDisplayName, chatMessage.message);
								outboundMsg.admin = true;
								outboundMsg.name_change = false;
							}
							else
							{
								outboundMsg.message = String.Format("[{0}] {1}", sourceUserData.m_strDisplayName, chatMessage.message);
								outboundMsg.admin = false;
								outboundMsg.name_change = false;
							}
						}

						outboundMsg.action = chatMessage.action;

						// Serialize once before broadcasting
						byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsg));

						// send it to everyone in the same room
						foreach (var sessionDataByClient in WebSocketManager.GetUserDataCache())
						{
							foreach (var sessionData in sessionDataByClient.Value)
							{
								UserSession targetSess = sessionData.Value;
								if (targetSess.networkRoomID == sourceUserSession.networkRoomID)
								{
									SharedUserData? targetUserSharedData = WebSocketManager.GetSharedDataForUser(targetSess.m_UserID);

									if (targetUserSharedData != null)
									{
										// is it blocked by either side? dont deliver the chat
										bool bBlocked = targetUserSharedData.GetSocialContainer().Blocked.Contains(sourceUserSession.m_UserID) ||
											sourceUserData.GetSocialContainer().Blocked.Contains(targetSess.m_UserID);

										if (!bBlocked)
										{
											targetSess.QueueWebsocketSend(bytesJSON);
										}
									}
								}
							}
						}

						// send message to discord
						if (Program.g_Discord != null && chatMessage.message != null)
						{
							Program.g_Discord.SendNetworkRoomChat(sourceUserSession.networkRoomID, sourceUserSession.m_UserID, sourceUserData.m_strDisplayName, chatMessage.message);
						}
					}
				}
				else if (msgID == EWebSocketMessageID.NETWORK_ROOM_CHANGE_ROOM)
				{
					if (data != null && data.ContainsKey("room"))
					{
						Int16 roomID = data["room"].GetInt16();
						await sourceUserSession.UpdateSessionNetworkRoom(roomID);
					}
				}
				else if (msgID == EWebSocketMessageID.NETWORK_ROOM_MARK_READY)
				{
					if (data != null && data.ContainsKey("ready"))
					{
						bool bReady = data["ready"].GetBoolean();

						Lobby? lobby = _lobbyManager.GetLobby(sourceUserSession.currentLobbyID);
						if (lobby != null)
						{
							LobbyMember? member = lobby.GetMemberFromUserID(sourceUserSession.m_UserID);

							if (member != null)
							{
								member.SetReadyState(bReady);
							}
						}
					}
				}
				else if (msgID == EWebSocketMessageID.PLAYER_NAME_CHANGE)
				{
					// must be in a room
					if (sourceUserSession.networkRoomID == -1)
					{
						return;
					}

					WebSocketMessage_NameChange? nameChangeRequest =
						JsonSerializer.Deserialize<WebSocketMessage_NameChange>(payload, JsonOpts);

					if (nameChangeRequest != null)
					{
						// TODO: Move this to a file or DB
						List<string> lstProtectedNames = new List<string>()
						{
							"admin",
							"staff",
							"mass^",
							"mas^",
							"m4ss^",
							"m4s^",
							"moderator",
							"hitler",
							"h1tler",
							"h1tl3r",
							"hittler",
							"h1ttler",
							"h1ttl3r",
							"olda",
							"oldanalytics",
							"ibra",
							"x64",
							"ronin"
						};

						string strNameRequestLower = nameChangeRequest.name.ToLower();

						// dont allow protected names
						if (!sourceUserData.IsAdmin())
						{
							foreach (string strProtectedName in lstProtectedNames)
							{
								if (strNameRequestLower.Contains(strProtectedName))
								{
									// response back to user
									WebSocketMessage_NetworkRoomChatMessageOutbound outboundMsg = new WebSocketMessage_NetworkRoomChatMessageOutbound();
									outboundMsg.msg_id = (int)EWebSocketMessageID.NETWORK_ROOM_CHAT_FROM_SERVER;
									outboundMsg.message = String.Format("--NAME CHANGE-- The display name you tried to set contains a protected word/phrase ({0} - {1})", nameChangeRequest.name, strProtectedName);
									outboundMsg.admin = true; // dont care for actions
									outboundMsg.action = false;
									outboundMsg.name_change = true;
									byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsg));
									sourceUserSession.QueueWebsocketSend(bytesJSON);

									return;
								}
							}
						}

						if (strNameRequestLower.StartsWith(" ") || strNameRequestLower.EndsWith(" "))
						{
							// response back to user
							WebSocketMessage_NetworkRoomChatMessageOutbound outboundMsg = new WebSocketMessage_NetworkRoomChatMessageOutbound();
							outboundMsg.msg_id = (int)EWebSocketMessageID.NETWORK_ROOM_CHAT_FROM_SERVER;
							outboundMsg.message = String.Format("--NAME CHANGE-- Display names cannot begin or end with spaces ({0})", nameChangeRequest.name);
							outboundMsg.admin = true; // dont care for actions
							outboundMsg.action = false;
							outboundMsg.name_change = true;
							byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsg));
							sourceUserSession.QueueWebsocketSend(bytesJSON);

							return;
						}

						// dont allow numeric (X) endings, those are protected
						if (System.Text.RegularExpressions.Regex.IsMatch(nameChangeRequest.name, @"\((1[0-9]|20|[0-9])\)$"))
						{
							// Remove the protected numeric ending
							nameChangeRequest.name = System.Text.RegularExpressions.Regex.Replace(nameChangeRequest.name, @"\((1[0-9]|20|[0-9])\)$", "");
						}

						if (nameChangeRequest.name.Length >= 3 && nameChangeRequest.name.Length <= 16)
						{
							await using var db = await _dbFactory.CreateDbContextAsync();
							bool nameSet = await Database.Users.SetDisplayName(db, sourceUserSession.m_UserID, nameChangeRequest.name);
							if (nameSet)
							{
								// response
								WebSocketMessage_NetworkRoomChatMessageOutbound outboundMsg = new WebSocketMessage_NetworkRoomChatMessageOutbound();
								outboundMsg.msg_id = (int)EWebSocketMessageID.NETWORK_ROOM_CHAT_FROM_SERVER;

								outboundMsg.message = String.Format("--NAME CHANGE-- {0} has changed their display name to {1}", sourceUserData.m_strDisplayName, nameChangeRequest.name);
								outboundMsg.admin = true;
								outboundMsg.action = false;
								outboundMsg.name_change = true;

								// Serialize once before broadcasting
								byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsg));

								// send it to the person doing the name change and everyone in the room
								foreach (var sessionDataByClient in WebSocketManager.GetUserDataCache())
								{
									foreach (var sessionData in sessionDataByClient.Value)
									{
										UserSession targetSess = sessionData.Value;
										if (targetSess.networkRoomID == sourceUserSession.networkRoomID)
										{
											SharedUserData? targetUserSharedData = WebSocketManager.GetSharedDataForUser(targetSess.m_UserID);

											if (targetUserSharedData != null)
											{
												// is it blocked by either side? dont deliver the chat
												bool bBlocked = targetUserSharedData.GetSocialContainer().Blocked.Contains(sourceUserSession.m_UserID) ||
													sourceUserData.GetSocialContainer().Blocked.Contains(targetSess.m_UserID);

												if (!bBlocked)
												{
													targetSess.QueueWebsocketSend(bytesJSON);
												}
											}
										}
									}
								}

								sourceUserData.m_strDisplayName = nameChangeRequest.name;
								await WebSocketManager.MarkRoomMemberListAsDirty(sourceUserSession.networkRoomID);
							}
							else
							{
								// response back to user
								WebSocketMessage_NetworkRoomChatMessageOutbound outboundMsg = new WebSocketMessage_NetworkRoomChatMessageOutbound();
								outboundMsg.msg_id = (int)EWebSocketMessageID.NETWORK_ROOM_CHAT_FROM_SERVER;
								outboundMsg.message = String.Format("--NAME CHANGE-- The display name you tried to set is already in use by another user ({0})", nameChangeRequest.name);
								outboundMsg.admin = true; // dont care for actions
								outboundMsg.action = false;
								outboundMsg.name_change = true;
								byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsg));
								sourceUserSession.QueueWebsocketSend(bytesJSON);
							}
						}
					}	
				}
				else if (msgID == EWebSocketMessageID.LOBBY_CHANGE_PASSWORD)
				{
					// must be in a lobby
					Lobby? lobby = _lobbyManager.GetLobby(sourceUserSession.currentLobbyID);
					if (lobby != null)
					{
						// must be owner too
						if (lobby.Owner == sourceUserSession.m_UserID)
						{
							WebSocketMessage_LobbyPasswordChange? passwordChangeRequest =
								JsonSerializer.Deserialize<WebSocketMessage_LobbyPasswordChange>(payload, JsonOpts);

							if (passwordChangeRequest != null)
							{
								lobby.AddPassword(passwordChangeRequest.new_password);
							}
						}
					}
				}
				else if (msgID == EWebSocketMessageID.LOBBY_REMOVE_PASSWORD)
				{
					// must be in a lobby
					Lobby? lobby = _lobbyManager.GetLobby(sourceUserSession.currentLobbyID);
					if (lobby != null)
					{
						// must be owner too
						if (lobby.Owner == sourceUserSession.m_UserID)
						{
							lobby.RemovePassword();
						}
					}
				}
				else if (msgID == EWebSocketMessageID.LOBBY_ROOM_CHAT_FROM_CLIENT)
				{
					// must be in a lobby
					if (sourceUserSession.currentLobbyID == -1)
					{
						return;
					}

					WebSocketMessage_LobbyChatMessageInbound? chatMessage =
						JsonSerializer.Deserialize<WebSocketMessage_LobbyChatMessageInbound>(payload, JsonOpts);

					if (chatMessage != null)
					{
						// get lobby
						Lobby? playerLobby = _lobbyManager.GetLobby(sourceUserSession.currentLobbyID);

						if (playerLobby != null)
						{
							// response
							WebSocketMessage_LobbyChatMessageOutbound outboundMsg = new WebSocketMessage_LobbyChatMessageOutbound();
							outboundMsg.msg_id = (int)EWebSocketMessageID.LOBBY_CHAT_FROM_SERVER;
							outboundMsg.user_id = sourceUserSession.m_UserID;

							if (chatMessage.action)
							{
								outboundMsg.message = String.Format("{0} {1}", sourceUserData.m_strDisplayName, chatMessage.message);
							}
							else if (chatMessage.announcement)
							{
								outboundMsg.message = String.Format("{0}", chatMessage.message);
							}
							else
							{
								outboundMsg.message = String.Format("[{0}] {1}", sourceUserData.m_strDisplayName, chatMessage.message);
							}

							outboundMsg.action = chatMessage.action;
							outboundMsg.announcement = chatMessage.announcement;
							outboundMsg.show_announcement_to_host = chatMessage.show_announcement_to_host;

							// Serialize once before broadcasting
							byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsg));

							foreach (LobbyMember lobbyMember in playerLobby.Members)
							{
								if (lobbyMember != null)
								{
									// need to check announcement flag?
									if (outboundMsg.announcement && !outboundMsg.show_announcement_to_host)
									{
										// is it host?
										if (lobbyMember.UserID == sourceUserSession.m_UserID)
										{
											continue;
										}
									}

									if (lobbyMember.GetSession().TryGetTarget(out UserSession? sess))
									{
										if (sess != null)
										{
											sess.QueueWebsocketSend(bytesJSON);
										}
									}
								}
							}

							// Read-only watchers in the pre-game lobby view get the same bytes.
							// They are not members, so the announcement rules above cannot apply -
							// an observer reads the lobby chat exactly as the members see it.
							foreach (UserSession observerSess in playerLobby.PendingObservers.Keys)
							{
								observerSess.QueueWebsocketSend(bytesJSON);
							}
						}
					}
				}
				else if (msgID == EWebSocketMessageID.LOBBY_OBSERVER_SUBSCRIBE)
				{
					// Read-only registration: no membership, password, or lobby-state check.
					WebSocketMessage_LobbyObserverEvent? subscribeMsg =
						JsonSerializer.Deserialize<WebSocketMessage_LobbyObserverEvent>(payload, JsonOpts);

					if (subscribeMsg != null)
					{
						Lobby? observerLobby = _lobbyManager.GetLobby(subscribeMsg.lobby_id);
						if (observerLobby != null && observerLobby.PendingObservers.TryAdd(sourceUserSession, 0))
						{
							Console.WriteLine($"[OBSERVER] User {sourceUserSession.m_UserID} subscribed to pre-game lobby {observerLobby.LobbyID}");
							// The members can be read by this watcher from here on, so say so by
							// name: the count alone does not tell them who is listening. Other
							// observers in the same lobby see the line too - except the one who
							// just joined, who knows.
							observerLobby.BroadcastSystemChatToMembers(
								String.Format("Observer {0} joined the lobby", sourceUserData.m_strDisplayName),
								includeObservers: true, excludeObserverSession: sourceUserSession);

							// Tell the joining observer what they are in for: the broadcast delay
							// they will hold, or a straight join at match start. Priority viewers
							// skip the delay, and a lobby without a configured delay holds nobody.
							// The priority flag never reaches the client, so the server must pick
							// the message - same authoritative live re-read as the Observe endpoint.
							await using (var db = await _dbFactory.CreateDbContextAsync())
							{
								bool bIsPriority = await Database.Users.GetUserPriority(db, sourceUserSession.m_UserID) == EUserPriority.Viewer;

								string strJoinMessage;
								if (bIsPriority || observerLobby.StreamDelaySeconds == null || observerLobby.StreamDelaySeconds <= 0)
								{
									strJoinMessage = "Joining on match start.";
								}
								else
								{
									strJoinMessage = String.Format("Broadcast delay: {0}s - joining automatically when it ends", observerLobby.StreamDelaySeconds);
								}

								WebSocketMessage_LobbyChatMessageOutbound joinMsg = new WebSocketMessage_LobbyChatMessageOutbound();
								joinMsg.msg_id = (int)EWebSocketMessageID.LOBBY_CHAT_FROM_SERVER;
								joinMsg.user_id = -2;
								joinMsg.message = strJoinMessage;
								joinMsg.action = true;
								sourceUserSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(joinMsg)));
							}

							// Retransmit so members see the pending-observer count change too.
							observerLobby.DirtyRetransmit();
						}
					}
				}
				else if (msgID == EWebSocketMessageID.LOBBY_OBSERVER_UNSUBSCRIBE)
				{
					WebSocketMessage_LobbyObserverEvent? unsubscribeMsg =
						JsonSerializer.Deserialize<WebSocketMessage_LobbyObserverEvent>(payload, JsonOpts);

					if (unsubscribeMsg != null)
					{
						Lobby? observerLobby = _lobbyManager.GetLobby(unsubscribeMsg.lobby_id);
						if (observerLobby != null && observerLobby.PendingObservers.TryRemove(sourceUserSession, out _))
						{
							Console.WriteLine($"[OBSERVER] User {sourceUserSession.m_UserID} unsubscribed from pre-game lobby {observerLobby.LobbyID}");
							// The leaving session is already out of PendingObservers, so the
							// remaining observers and the members all get the line.
							observerLobby.BroadcastSystemChatToMembers(
								String.Format("Observer {0} left the lobby", sourceUserData.m_strDisplayName),
								includeObservers: true, excludeObserverSession: sourceUserSession);
							observerLobby.DirtyRetransmit();
						}
					}
				}
				else if (msgID == EWebSocketMessageID.LOBBY_OBSERVER_CHAT_FROM_CLIENT)
				{
					// Observer sends chat into a pre-game lobby. Own lane because the member
					// path is gated on currentLobbyID != -1 and an observer is not in a lobby.
					WebSocketMessage_LobbyObserverChatInbound? observerChatMsg =
						JsonSerializer.Deserialize<WebSocketMessage_LobbyObserverChatInbound>(payload, JsonOpts);

					if (observerChatMsg == null)
					{
						return;
					}

					Lobby? observerChatLobby = _lobbyManager.GetLobby(observerChatMsg.lobby_id);
					if (observerChatLobby == null)
					{
						return;
					}

					// Authorization: only sessions actually observing this lobby may post into
					// it. This is what stops someone from writing into an arbitrary lobby by
					// guessing an id; lobby_id selects which observed lobby this is for.
					if (!observerChatLobby.PendingObservers.ContainsKey(sourceUserSession))
					{
						return;
					}

					// Host kill switch. A stale client that still shows an enabled entry gets
					// an explanation rather than silence.
					if (!observerChatLobby.AllowObserverChat)
					{
						WebSocketMessage_LobbyChatMessageOutbound refusalMsg = new WebSocketMessage_LobbyChatMessageOutbound();
						refusalMsg.msg_id = (int)EWebSocketMessageID.LOBBY_CHAT_FROM_SERVER;
						refusalMsg.user_id = -2;
						refusalMsg.message = "Observer chat is disabled by the host.";
						refusalMsg.action = true;
						sourceUserSession.QueueWebsocketSend(
							Encoding.UTF8.GetBytes(JsonSerializer.Serialize(refusalMsg)));
						return;
					}

					string strText = observerChatMsg.message ?? String.Empty;
					strText = strText.Trim();
					if (strText.Length == 0)
					{
						return;
					}
					if (strText.Length > 200)
					{
						strText = strText.Substring(0, 200);
					}

					// Server-side rate gate (3000 ms, matching the client's network-room
					// slowmode): the client-side slowmode is only courtesy, a modded client
					// ignores it. The timestamp lives on the session and dies with the socket.
					long timeNow = Environment.TickCount64;
					if (sourceUserSession.m_timeLastObserverChatSent != -1 &&
						timeNow - sourceUserSession.m_timeLastObserverChatSent < 3000)
					{
						return;
					}
					sourceUserSession.m_timeLastObserverChatSent = timeNow;

					// Server-controlled formatting: exactly the member path's [Name] message,
					// but with the flags fixed so a client can never smuggle in an action or
					// announcement line through this lane.
					WebSocketMessage_LobbyChatMessageOutbound outboundMsg = new WebSocketMessage_LobbyChatMessageOutbound();
					outboundMsg.msg_id = (int)EWebSocketMessageID.LOBBY_CHAT_FROM_SERVER;
					outboundMsg.user_id = sourceUserSession.m_UserID;
					outboundMsg.message = String.Format("[{0}] {1}", sourceUserData.m_strDisplayName, strText);
					outboundMsg.action = false;
					outboundMsg.announcement = false;
					outboundMsg.show_announcement_to_host = false;

					byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsg));

					foreach (LobbyMember lobbyMember in observerChatLobby.Members)
					{
						if (lobbyMember != null && lobbyMember.GetSession().TryGetTarget(out UserSession? sess) && sess != null)
						{
							sess.QueueWebsocketSend(bytesJSON);
						}
					}

					// Observers get the same bytes; the sender's own copy comes back this way,
					// so no local echo is needed.
					foreach (UserSession observerSess in observerChatLobby.PendingObservers.Keys)
					{
						observerSess.QueueWebsocketSend(bytesJSON);
					}
				}
				else if (msgID == EWebSocketMessageID.LOBBY_OBSERVER_LIST_REQUEST)
				{
					// Host-only: the players already see join/leave lines as they happen, but
					// the host can ask for the current roster by name. The list is announced
					// into the lobby chat, so the whole game sees it.
					WebSocketMessage_LobbyObserverEvent? listMsg =
						JsonSerializer.Deserialize<WebSocketMessage_LobbyObserverEvent>(payload, JsonOpts);

					if (listMsg == null)
					{
						return;
					}

					Lobby? observerListLobby = _lobbyManager.GetLobby(listMsg.lobby_id);
					if (observerListLobby == null)
					{
						return;
					}

					if (observerListLobby.Owner != sourceUserSession.m_UserID)
					{
						return;
					}

					List<string> lstObserverNames = new List<string>();
					foreach (UserSession observerSession in observerListLobby.PendingObservers.Keys)
					{
						SharedUserData? observerData = WebSocketManager.GetSharedDataForUser(observerSession.m_UserID);
						lstObserverNames.Add(observerData != null ? observerData.m_strDisplayName : "Unknown");
					}

					string strMessage = lstObserverNames.Count == 0
						? "No observers are watching this lobby."
						: String.Format("Observers watching: {0}", String.Join(", ", lstObserverNames));

					observerListLobby.BroadcastSystemChatToMembers(strMessage);
				}
				else if (msgID == EWebSocketMessageID.START_GAME_COUNTDOWN_STARTED)
				{
					// must be in a lobby
					Lobby? lobbyInfo = null;
					if (sourceUserSession.currentLobbyID != -1)
					{
						// must be lobby owner too
						lobbyInfo = _lobbyManager.GetLobby(sourceUserSession.currentLobbyID);

						if (lobbyInfo == null || lobbyInfo.Owner != sourceUserSession.m_UserID)
						{
							return;
						}
					}

					if (lobbyInfo == null)
					{
						return;
					}

					// lock slots
					lobbyInfo.CloseOpenSlots();

					// Observers mirror this through the ordinary lobby-changed refetch; the
					// eager push below is just the instant cue.
					lobbyInfo.SetCountdownStarted(true);

					// Push it now so observers' countdown starts in sync with the lobby's,
					// rather than only on the START_GAME forward below (kept as a fallback
					// for observers that subscribed too late to catch this one).
					if (lobbyInfo.PendingObservers.Count > 0)
					{
						WebSocketMessage_LobbyObserverEvent observerEvent = new WebSocketMessage_LobbyObserverEvent();
						observerEvent.msg_id = (int)EWebSocketMessageID.LOBBY_OBSERVER_GAME_STARTING;
						observerEvent.lobby_id = lobbyInfo.LobbyID;
						byte[] observerBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(observerEvent));

						foreach (UserSession sess in lobbyInfo.PendingObservers.Keys)
						{
							sess.QueueWebsocketSend(observerBytes);
						}
					}
				}
				else if (msgID == EWebSocketMessageID.START_GAME)
				{
					// must be in a lobby
					Lobby? lobbyInfo = null;
					if (sourceUserSession.currentLobbyID != -1)
					{
						// must be lobby owner too
						lobbyInfo = _lobbyManager.GetLobby(sourceUserSession.currentLobbyID);

						if (lobbyInfo == null || lobbyInfo.Owner != sourceUserSession.m_UserID)
						{
							return;
						}
					}

					if (lobbyInfo == null)
					{
						return;
					}

					// start match + create placeholder match
					await lobbyInfo.UpdateState(ELobbyState.INGAME);

					// Not a cancel: UpdateState already flipped State to INGAME above, so
					// observers' next refetch reads "started", not "countdown cleared".
					lobbyInfo.SetCountdownStarted(false);

					// simple websocket msg, has no data, so dont even read anything


					foreach (LobbyMember lobbyMember in lobbyInfo.Members)
					{
						if (lobbyMember != null)
						{
							if (lobbyMember.GetSession().TryGetTarget(out UserSession? sess))
							{
								if (sess != null)
								{
									// response
									WebSocketMessage_StartMatch startCommand = new WebSocketMessage_StartMatch();
									startCommand.msg_id = (int)EWebSocketMessageID.START_GAME;
									startCommand.screenshot_url = await S3CredentialManager.GetPresignedURL(EMetadataFileType.FILE_TYPE_SCREENSHOT, EScreenshotType.SCREENSHOT_TYPE_LOADSCREEN, lobbyInfo.MatchID, lobbyMember.UserID, lobbyMember.SlotIndex, lobbyInfo.TimeCreated) ?? String.Empty;

									// Serialize once before broadcasting
									byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(startCommand));

									sess.QueueWebsocketSend(bytesJSON);
								}
							}
						}
					}

					// Tell pending observers to queue their join now - the delay rides along so
					// the client can show the broadcast-delay wait from the start.
					if (lobbyInfo.PendingObservers.Count > 0)
					{
						WebSocketMessage_LobbyObserverEvent observerEvent = new WebSocketMessage_LobbyObserverEvent();
						observerEvent.msg_id = (int)EWebSocketMessageID.LOBBY_OBSERVER_GAME_STARTED;
						observerEvent.lobby_id = lobbyInfo.LobbyID;
						observerEvent.delay_seconds = lobbyInfo.StreamDelaySeconds;
						byte[] observerBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(observerEvent));

						foreach (UserSession sess in lobbyInfo.PendingObservers.Keys)
						{
							sess.QueueWebsocketSend(observerBytes);
						}
					}
				}
				else if (msgID == EWebSocketMessageID.FULL_MESH_CONNECTIVITY_CHECK_HOST_REQUESTS_BEGIN)
				{
					// Host has requested this, as part of the start game flow

					// must be in a lobby
					Lobby? lobbyInfo = null;
					if (sourceUserSession.currentLobbyID != -1)
					{
						// must be lobby owner too
						lobbyInfo = _lobbyManager.GetLobby(sourceUserSession.currentLobbyID);

						if (lobbyInfo == null || lobbyInfo.Owner != sourceUserSession.m_UserID)
						{
							return;
						}
					}

					if (lobbyInfo == null)
					{
						return;
					}

					// lock slots (more people joining when we're already doing connectivity checks won't help the situation)
					lobbyInfo.CloseOpenSlots();

					// mark lobby as in progress of full mesh connectivity checks
					lobbyInfo.StartFullMeshConnectivityCheck();

					// start full mesh connectivity checks
					WebSocketMessage_Simple startCommand = new WebSocketMessage_Simple();
					startCommand.msg_id = (int)EWebSocketMessageID.FULL_MESH_CONNECTIVITY_CHECK_RESPONSE;

					// Serialize once before broadcasting
					byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(startCommand));

					foreach (LobbyMember lobbyMember in lobbyInfo.Members)
					{
						if (lobbyMember != null)
						{
							if (lobbyMember.GetSession().TryGetTarget(out UserSession? sess))
							{
								if (sess != null)
								{
									sess.QueueWebsocketSend(bytesJSON);
								}
							}
						}
					}
				}
				else if (msgID == EWebSocketMessageID.FULL_MESH_CONNECTIVITY_CHECK_RESPONSE)
				{
					// process a response from a user
					WebSocketMessage_FullMeshConnectivityCheckResponseFromUser? fullMeshMsg =
						JsonSerializer.Deserialize<WebSocketMessage_FullMeshConnectivityCheckResponseFromUser>(payload, JsonOpts);

					// store response
					if (fullMeshMsg != null)
					{
						Lobby? lobby = _lobbyManager.GetLobby(sourceUserSession.currentLobbyID);
						if (lobby != null)
						{
							await lobby.StoreFullMeshConnectivityResponse(sourceUserSession.m_UserID, fullMeshMsg.connectivity_map);
						}
					}
				}
				else if (msgID == EWebSocketMessageID.NETWORK_CONNECTION_CLIENT_REQUEST_SIGNALLING)
				{
					WebSocketMessage_RequestSignaling? signalingRequest =
						JsonSerializer.Deserialize<WebSocketMessage_RequestSignaling>(payload, JsonOpts);

					System.Diagnostics.Debug.WriteLine("Signal restart request received from {0}!", sourceUserSession.m_UserID);

					if (signalingRequest != null)
					{
						// Our protocol is just [payload]
						// And everything is in text.

						// find the dest players connection
						UserSession? targetSession = WebSocketManager.GetSessionFromUser(signalingRequest.target_user_id, EUserSessionType.GameClient); // signalling NEEDS a game client session
						if (targetSession != null)
						{
							Lobby? lobby = _lobbyManager.GetLobby(sourceUserSession.currentLobbyID);

							if (lobby != null)
							{
								LobbyMember? targetUser = lobby.GetMemberFromUserID(targetSession.m_UserID);
								LobbyMember? sourceUser = lobby.GetMemberFromUserID(sourceUserSession.m_UserID);

								if (sourceUser != null && targetUser != null)
								{
									// send signal start to source player
									WebSocketMessage_NetworkStartSignalling joiningPlayerMsg = new WebSocketMessage_NetworkStartSignalling();
									joiningPlayerMsg.msg_id = (int)EWebSocketMessageID.NETWORK_CONNECTION_START_SIGNALLING;
									joiningPlayerMsg.lobby_id = sourceUserSession.currentLobbyID;
									joiningPlayerMsg.user_id = targetUser.UserID;
									joiningPlayerMsg.preferred_port = targetUser.Port;
									joiningPlayerMsg.middleware_id = targetUser.MiddlewareUserID;
									sourceUserSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(joiningPlayerMsg)));

									// send the reverse to the target player
									WebSocketMessage_NetworkStartSignalling existingPlayerMsg = new WebSocketMessage_NetworkStartSignalling();
									existingPlayerMsg.msg_id = (int)EWebSocketMessageID.NETWORK_CONNECTION_START_SIGNALLING;
									existingPlayerMsg.lobby_id = sourceUserSession.currentLobbyID;
									existingPlayerMsg.user_id = sourceUser.UserID;
									existingPlayerMsg.preferred_port = sourceUser.Port;
									existingPlayerMsg.middleware_id = sourceUser.MiddlewareUserID;
									targetSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(existingPlayerMsg)));
								}
							}
						}
					}
				}
				else if (msgID == EWebSocketMessageID.NETWORK_SIGNAL)
				{
					WebSocketMessage_SignalBidirectional? signal =
						JsonSerializer.Deserialize<WebSocketMessage_SignalBidirectional>(payload, JsonOpts);
					//Console.WriteLine("Signal received: " + signal.signal);

					if (signal != null)
					{
						// Our protocol is just [payload]
						// And everything is in text.

						// find the dest players connection
						UserSession? targetSession = WebSocketManager.GetSessionFromUser(signal.target_user_id, EUserSessionType.GameClient); // network signals only goto game clients
						if (targetSession != null)
						{
							Lobby? lobby = _lobbyManager.GetLobby(sourceUserSession.currentLobbyID);

							if (lobby != null)
							{
								LobbyMember? targetUser = lobby.GetMemberFromUserID(targetSession.m_UserID);
								LobbyMember? sourceUser = lobby.GetMemberFromUserID(sourceUserSession.m_UserID);

								if (sourceUser != null && targetUser != null)
								{
									// now into json for our ws msg format
									// NOTE: outbound msg doesnt need sender ID, we only need that to determine target on the server, everything else is included in the payload
									WebSocketMessage_SignalBidirectional outboundSignal = new WebSocketMessage_SignalBidirectional();
									outboundSignal.msg_id = (int)EWebSocketMessageID.NETWORK_SIGNAL;
									outboundSignal.target_user_id = sourceUserSession.m_UserID; // user here is the person who sent it to us
									outboundSignal.payload = signal.payload;
									byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundSignal));

									targetSession.QueueWebsocketSend(bytesJSON);
									//Console.WriteLine("Signal out is: {0}", JsonSerializer.Serialize(outboundSignal));
									//Console.WriteLine("SIGNAL SENT ({0} bytes) (from user {1} to user {2})", bytesJSON.Length, wsSess.m_UserID, sess.m_UserID);
									//Console.WriteLine("MSG WAS: {0}", strMessage);
									//break;
								}
							}
						}
						else
						{
							return;
						}
					}
				}
				else if (msgID == EWebSocketMessageID.ANTICHEAT_MESSAGE)
				{
					WebSocketMessage_AnticheatMessage? acMsg = JsonSerializer.Deserialize<WebSocketMessage_AnticheatMessage>(payload, JsonOpts);

					if (acMsg != null)
					{
						// Our protocol is just [payload]
						// And everything is in text.

						// find the dest players connection
						UserSession? targetSession = WebSocketManager.GetSessionFromUser(acMsg.target_user_id, EUserSessionType.GameClient); // network signals only goto game clients
						if (targetSession != null)
						{
							Lobby? lobby = _lobbyManager.GetLobby(sourceUserSession.currentLobbyID);

							if (lobby != null)
							{
								LobbyMember? targetUser = lobby.GetMemberFromUserID(targetSession.m_UserID);
								LobbyMember? sourceUser = lobby.GetMemberFromUserID(sourceUserSession.m_UserID);

								if (sourceUser != null && targetUser != null)
								{
									// now into json for our ws msg format
									// NOTE: outbound msg doesnt need sender ID, we only need that to determine target on the server, everything else is included in the payload
									WebSocketMessage_AnticheatMessage outboundACMsg = new WebSocketMessage_AnticheatMessage();
									outboundACMsg.msg_id = (int)EWebSocketMessageID.ANTICHEAT_MESSAGE;
									outboundACMsg.target_user_id = sourceUserSession.m_UserID; // user here is the person who sent it to us
									outboundACMsg.payload = acMsg.payload;
									byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundACMsg));

									targetSession.QueueWebsocketSend(bytesJSON);
									//Console.WriteLine("Signal out is: {0}", JsonSerializer.Serialize(outboundSignal));
									//Console.WriteLine("SIGNAL SENT ({0} bytes) (from user {1} to user {2})", bytesJSON.Length, wsSess.m_UserID, sess.m_UserID);
									//Console.WriteLine("MSG WAS: {0}", strMessage);
									//break;
								}
							}
						}
						else
						{
							return;
						}
					}
				}
			}
			catch
			{
				// swallow per-message exceptions to avoid killing the loop
				// you can add Sentry logging here if desired
			}
		}
	}
}
