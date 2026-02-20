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
		[Authorize(Roles = "Player")]
		public async Task Get([FromHeader(Name = "is-reconnect")] bool bIsReconnect)
		{
			if (!HttpContext.WebSockets.IsWebSocketRequest)
			{
				HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
				return;
			}

			// create a session
			var userIdClaim = this.User.FindFirst(ClaimTypes.NameIdentifier);
			if (userIdClaim == null || !Int64.TryParse(userIdClaim.Value, out Int64 user_id))
			{
				HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
				return;
			}

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

			string client_id = firstEntryClientID.Value;
			UserWebSocketInstance wsSess = await WebSocketManager.CreateSession(
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
				catch (OperationCanceledException ex)
				{
					// send a ping
					wsSess.SendPong();

					{
						// log it to sentry
						var customEvent = new SentryEvent
						{
							Message = "Websocket Disconnect A:" + ex.ToString(),
							Level = SentryLevel.Error
						};

						// Add custom tags
						customEvent.SetTag("websocket", "error_1");
						customEvent.SetTag("user_id", wsSess.m_UserID.ToString());

						// Add extra data
						customEvent.SetExtra("user_id_tag", wsSess.m_UserID);

						// Capture the event
						SentrySdk.CaptureEvent(customEvent);

						// flush
						await SentrySdk.FlushAsync();
					}

					break;
				}
				catch (Exception ex)
				{
					// Log unexpected errors
					Console.WriteLine($"WebSocket error: {ex}");

					{
						// log it to sentry
						var customEvent = new SentryEvent
						{
							Message = "Websocket Disconnect B: " + ex.ToString(),
							Level = SentryLevel.Error
						};

						// Add custom tags
						customEvent.SetTag("websocket", "error_2");
						customEvent.SetTag("user_id", wsSess.m_UserID.ToString());

						// Add extra data
						customEvent.SetExtra("user_id_tag", wsSess.m_UserID);

						// Capture the event
						SentrySdk.CaptureEvent(customEvent);

						// flush
						await SentrySdk.FlushAsync();
					}

					break;
				}

				if (receiveResult.MessageType == WebSocketMessageType.Close)
				{
					using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // timeout
					await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cts.Token);
					break;
				}

				// slice only the valid part, no extra allocation
				var segment = new ArraySegment<byte>(buffer, 0, receiveResult.Count);

				UserSession? sourceUserData = WebSocketManager.GetDataFromUser(wsSess.m_UserID);
				await ProcessWSMessage(wsSess, sourceUserData, receiveResult, segment);
			}

			Console.ForegroundColor = ConsoleColor.Cyan;
			UserSession? sourceData = WebSocketManager.GetDataFromUser(user_id);
			Console.WriteLine("WEBSOCKET DISCONNECT FOR {0}", sourceData == null ? "NULL" : sourceData.m_strDisplayName);
			Console.ForegroundColor = ConsoleColor.Gray;

			// close the session
			if (wsSess != null)
			{
				await WebSocketManager.DeleteSession(user_id, wsSess, false);
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
			if (receiveResult.MessageType == WebSocketMessageType.Close)
			{
				await WebSocketManager.DeleteSession(sourceWS.m_UserID, sourceWS, false);
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
						UserSession? targetSession = WebSocketManager.GetDataFromUser(chatMessage.target_user_id);

						if (targetSession != null)
						{
							if (sourceUserSession.GetSocialContainer().Friends.Contains(chatMessage.target_user_id)
								&& targetSession.GetSocialContainer().Friends.Contains(sourceUserSession.m_UserID))
							{
								// ok, they can chat, send the message to both of them
								WebSocketMessage_Social_FriendChatMessage_Outbound outboundMsg = new();
								outboundMsg.msg_id = (int)EWebSocketMessageID.SOCIAL_FRIEND_CHAT_MESSAGE_SERVER_TO_CLIENT;
								outboundMsg.source_user_id = sourceWS.m_UserID;
								outboundMsg.target_user_id = targetSession.m_UserID;
								outboundMsg.message = String.Format("{0}: {1}", sourceUserSession.m_strDisplayName, chatMessage.message);

								// send to both
								byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsg));

								await sourceWS.SendAsync(bytesJSON, WebSocketMessageType.Text);

								targetSession.QueueWebsocketSend(bytesJSON);
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
							outboundMsg.message = String.Format("{0} {1}", sourceUserSession.m_strDisplayName, chatMessage.message);
							outboundMsg.admin = false; // dont care for actions
						}
						else
						{
							if (sourceUserSession.IsAdmin())
							{
								outboundMsg.message = String.Format("[\u2605\u2605GO STAFF\u2605\u2605]    [{0}] {1}", sourceUserSession.m_strDisplayName, chatMessage.message);
								outboundMsg.admin = true;
							}
							else
							{
								outboundMsg.message = String.Format("[{0}] {1}", sourceUserSession.m_strDisplayName, chatMessage.message);
								outboundMsg.admin = false;
							}
						}

						outboundMsg.action = chatMessage.action;

						// Serialize once before broadcasting
						byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsg));

						// send it to everyone in the same room
						foreach (KeyValuePair<Int64, UserSession> sessionData in WebSocketManager.GetUserDataCache())
						{
							UserSession targetSess = sessionData.Value;
							if (targetSess.networkRoomID == sourceUserSession.networkRoomID)
							{
								// is it blocked by either side? dont deliver the chat
								bool bBlocked = targetSess.GetSocialContainer().Blocked.Contains(sourceUserSession.m_UserID) ||
									sourceUserSession.GetSocialContainer().Blocked.Contains(targetSess.m_UserID);

								if (!bBlocked)
								{
									targetSess.QueueWebsocketSend(bytesJSON);
								}
							}
						}

						// send message to discord
						if (Program.g_Discord != null && chatMessage.message != null)
						{
							Program.g_Discord.SendNetworkRoomChat(sourceUserSession.networkRoomID, sourceUserSession.m_UserID, sourceUserSession.m_strDisplayName, chatMessage.message);
						}
					}
				}
				else if (msgID == EWebSocketMessageID.UNUSED_PLACEHOLDER)
				{
					// no-op
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

						Lobby? lobby = LobbyManager.GetLobby(sourceUserSession.currentLobbyID);
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
						// dont allow admin or staff
						if (nameChangeRequest.name.ToLower().Contains("admin") || nameChangeRequest.name.ToLower().Contains("staff"))
						{
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
							await Database.Functions.Lobby.UpdateDisplayName(GlobalDatabaseInstance.g_Database, sourceUserSession.m_UserID, nameChangeRequest.name);
							sourceUserSession.m_strDisplayName = nameChangeRequest.name;
							await WebSocketManager.MarkRoomMemberListAsDirty(sourceUserSession.networkRoomID);
						}
					}
				}
				else if (msgID == EWebSocketMessageID.LOBBY_CHANGE_PASSWORD)
				{
					// must be in a lobby
					Lobby? lobby = LobbyManager.GetLobby(sourceUserSession.currentLobbyID);
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
					Lobby? lobby = LobbyManager.GetLobby(sourceUserSession.currentLobbyID);
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
						Lobby? playerLobby = LobbyManager.GetLobby(sourceUserSession.currentLobbyID);

						if (playerLobby != null)
						{
							// response
							WebSocketMessage_LobbyChatMessageOutbound outboundMsg = new WebSocketMessage_LobbyChatMessageOutbound();
							outboundMsg.msg_id = (int)EWebSocketMessageID.LOBBY_CHAT_FROM_SERVER;
							outboundMsg.user_id = sourceUserSession.m_UserID;

							if (chatMessage.action)
							{
								outboundMsg.message = String.Format("{0} {1}", sourceUserSession.m_strDisplayName, chatMessage.message);
							}
							else if (chatMessage.announcement)
							{
								outboundMsg.message = String.Format("{0}", chatMessage.message);
							}
							else
							{
								outboundMsg.message = String.Format("[{0}] {1}", sourceUserSession.m_strDisplayName, chatMessage.message);
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
						}
					}
				}
				else if (msgID == EWebSocketMessageID.START_GAME_COUNTDOWN_STARTED)
				{
					// must be in a lobby
					Lobby? lobbyInfo = null;
					if (sourceUserSession.currentLobbyID != -1)
					{
						// must be lobby owner too
						lobbyInfo = LobbyManager.GetLobby(sourceUserSession.currentLobbyID);

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
				}
				else if (msgID == EWebSocketMessageID.START_GAME)
				{
					// must be in a lobby
					Lobby? lobbyInfo = null;
					if (sourceUserSession.currentLobbyID != -1)
					{
						// must be lobby owner too
						lobbyInfo = LobbyManager.GetLobby(sourceUserSession.currentLobbyID);

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

					// simple websocket msg, has no data, so dont even read anything

					// response
					WebSocketMessage_Simple startCommand = new WebSocketMessage_Simple();
					startCommand.msg_id = (int)EWebSocketMessageID.START_GAME;

					// Serialize once before broadcasting
					byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(startCommand));

					foreach (KeyValuePair<Int64, UserSession> sessionData in WebSocketManager.GetUserDataCache())
					{
						UserSession sess = sessionData.Value;
						if (sess.currentLobbyID == sourceUserSession.currentLobbyID)
						{
							sess.QueueWebsocketSend(bytesJSON);
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
						lobbyInfo = LobbyManager.GetLobby(sourceUserSession.currentLobbyID);

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

					foreach (KeyValuePair<Int64, UserSession> sessionData in WebSocketManager.GetUserDataCache())
					{
						UserSession sess = sessionData.Value;
						if (sess.currentLobbyID == sourceUserSession.currentLobbyID)
						{
							sess.QueueWebsocketSend(bytesJSON);
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
						Lobby? lobby = LobbyManager.GetLobby(sourceUserSession.currentLobbyID);
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
						UserSession? targetSession = WebSocketManager.GetDataFromUser(signalingRequest.target_user_id);
						if (targetSession != null)
						{
							Lobby? lobby = LobbyManager.GetLobby(sourceUserSession.currentLobbyID);

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
									sourceUserSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(joiningPlayerMsg)));

									// send the reverse to the target player
									WebSocketMessage_NetworkStartSignalling existingPlayerMsg = new WebSocketMessage_NetworkStartSignalling();
									existingPlayerMsg.msg_id = (int)EWebSocketMessageID.NETWORK_CONNECTION_START_SIGNALLING;
									existingPlayerMsg.lobby_id = sourceUserSession.currentLobbyID;
									existingPlayerMsg.user_id = sourceUser.UserID;
									existingPlayerMsg.preferred_port = sourceUser.Port;
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
						UserSession? targetSession = WebSocketManager.GetDataFromUser(signal.target_user_id);
						if (targetSession != null)
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
