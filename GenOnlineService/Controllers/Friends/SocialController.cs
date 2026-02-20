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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public class FriendEntry
	{
		public Int64 user_id { get; set; } = -1;
		public string display_name { get; set; } = String.Empty;
		public bool online { get; set; } = false;
		public string presence { get; set; } = String.Empty;
	}


	public class RouteHandler_GET_Friends_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public List<FriendEntry> friends { get; set; } = new();
		public List<FriendEntry> pending_requests { get; set; } = new();
	}

	public class RouteHandler_GET_Blocked_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public List<FriendEntry> blocked { get; set; } = new();
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class SocialController : ControllerBase
	{
		private readonly ILogger<SocialController> _logger;

		public SocialController(ILogger<SocialController> logger)
		{
			_logger = logger;
		}

		// Friends/Requests/<id>
		// PUT = send request
		// POST = accept request
		// DELETE = reject request

		private async Task HelperFunction_AcceptFriendRequest(Int64 source_user_id, Int64 target_user_id)
		{
            // target user does NOT need to be signed in
            UserSession? sourceData = WebSocketManager.GetDataFromUser(source_user_id);
			UserSession? targetData = WebSocketManager.GetDataFromUser(target_user_id);

			// remove the request from requestor (online version)
			// If source user is not online, we cannot proceed
			if (sourceData == null)
			{
				return;
			}


#pragma warning disable CS8602 // Dereference of a possibly null reference.
			sourceData.GetSocialContainer().PendingRequests.Remove(target_user_id);
#pragma warning restore CS8602 // Dereference of a possibly null reference.

            // remove the request from requestor (db)
            await Database.Functions.Auth.RemovePendingFriendRequest(GlobalDatabaseInstance.g_Database, source_user_id, target_user_id);

            // Add to both players friends list (online version and db)

            // SHARED db (we only have to add this once and it covers both players)
            await Database.Functions.Auth.CreateFriendship(GlobalDatabaseInstance.g_Database, source_user_id, target_user_id);

            // source player
            {
				// sess
				sourceData.GetSocialContainer().Friends.Add(target_user_id);
            }

            // target player
            {
                // sess
                if (targetData != null)
                {
					targetData.GetSocialContainer().Friends.Add(source_user_id);
                }
            }

			// notify the source player that the target player is online, if they are
			if (sourceData != null)
            {
                WebSocketMessage_Social_FriendStatusChanged friendStatusChangedEvent = new();
                friendStatusChangedEvent.msg_id = (int)EWebSocketMessageID.SOCIAL_FRIEND_ONLINE_STATUS_CHANGED;
                friendStatusChangedEvent.display_name = targetData.m_strDisplayName;
                friendStatusChangedEvent.online = true;
                byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(friendStatusChangedEvent));

                sourceData.QueueWebsocketSend(bytesJSON);
            }

            // notify the target player that the source player accepted their request
            if (targetData != null)
            {
                WebSocketMessage_Social_FriendRequestAccepted friendRequestAcceptedEvent = new();
                friendRequestAcceptedEvent.msg_id = (int)EWebSocketMessageID.SOCIAL_FRIEND_FRIEND_REQUEST_ACCEPTED_BY_TARGET;
                friendRequestAcceptedEvent.display_name = sourceData.m_strDisplayName;
                byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(friendRequestAcceptedEvent));

                targetData.QueueWebsocketSend(bytesJSON);
            }
        }

		// Accept a request
		[HttpPost("Friends/Requests/{target_user_id}")]
		[Authorize(Roles = "Player")]
		public async Task AcceptPendingRequest(Int64 target_user_id)
		{
			// source user must be signed in
			Int64 source_user_id = TokenHelper.GetUserID(this);
			if (source_user_id == -1 || WebSocketManager.GetDataFromUser(source_user_id) == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

			await HelperFunction_AcceptFriendRequest(source_user_id, target_user_id);

			UserSession? sourceSession = WebSocketManager.GetDataFromUser(source_user_id);
			if (sourceSession != null)
            {
				sourceSession.NotifyFriendslistDirty();
            }

			UserSession? targetSession = WebSocketManager.GetDataFromUser(target_user_id);
            if (targetSession != null)
            {
				targetSession.NotifyFriendslistDirty();
            }
        }

		// Reject a request
		[HttpDelete("Friends/Requests/{target_user_id}")]
		[Authorize(Roles = "Player")]
		public async Task RejectPendingRequest(Int64 target_user_id)
		{
			// source user must be signed in
			Int64 source_user_id = TokenHelper.GetUserID(this);
			if (source_user_id == -1 || WebSocketManager.GetDataFromUser(source_user_id) == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

			// remove the request from requestor (online version)
#pragma warning disable CS8602 // Dereference of a possibly null reference.
			UserSession? userData = WebSocketManager.GetDataFromUser(source_user_id);
			userData.GetSocialContainer().PendingRequests.Remove(target_user_id);
#pragma warning restore CS8602 // Dereference of a possibly null reference.

			// remove the request from requestor (db)
			// NOTE: Target and source are inverted here because the target is actually the person who sent the request, source is the person taking action on the friend request
			await Database.Functions.Auth.RemovePendingFriendRequest(GlobalDatabaseInstance.g_Database, target_user_id, source_user_id);


			if (userData != null)
            {
				userData.NotifyFriendslistDirty();
            }

			UserSession? targetSession = WebSocketManager.GetDataFromUser(target_user_id);
            if (targetSession != null)
            {
				targetSession.NotifyFriendslistDirty();
            }
        }

		// Remove a friend
		[HttpDelete("Friends/{target_user_id}")]
		[Authorize(Roles = "Player")]
		public async Task RemoveFriend(Int64 target_user_id)
		{
			// source user must be signed in
			Int64 source_user_id = TokenHelper.GetUserID(this);
			if (source_user_id == -1 || WebSocketManager.GetDataFromUser(source_user_id) == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

			// must be friends
#pragma warning disable CS8602 // Dereference of a possibly null reference.
			UserSession? userData = WebSocketManager.GetDataFromUser(source_user_id);
			if (!userData.GetSocialContainer().Friends.Contains(target_user_id))
			{
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				return;
			}
#pragma warning restore CS8602 // Dereference of a possibly null reference.

			// remove the request from requestor (online version)
			userData.GetSocialContainer().Friends.Remove(target_user_id);

			// if the other player is online, remove from them too
			UserSession? TargetUserData = WebSocketManager.GetDataFromUser(target_user_id);
			if (TargetUserData != null)
			{
				TargetUserData.GetSocialContainer().Friends.Remove(source_user_id);
			}

			// remove the request from requestor (db)
			await Database.Functions.Auth.RemoveFriendship(GlobalDatabaseInstance.g_Database, source_user_id, target_user_id);

			// TODO_SOCIAL: This tells the client to do a GET, we could just send them their friends list directly to reduce latency + calls to service
			if (userData != null)
            {
				userData.NotifyFriendslistDirty();
            }

			if (TargetUserData != null)
			{
				TargetUserData.NotifyFriendslistDirty();
            }
        }

        // Send a request
        [HttpPut("Friends/Requests/{target_user_id}")]
		[Authorize(Roles = "Player")]
		public async Task AddFriend(Int64 target_user_id)
		{
			// source user must be signed in
			Int64 requester_user_id = TokenHelper.GetUserID(this);
			if (requester_user_id == -1 || WebSocketManager.GetDataFromUser(requester_user_id) == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

			// too many friends?
			const int friendsLimit = 200;
			UserSession? userData = WebSocketManager.GetDataFromUser(requester_user_id);
			if (userData.GetSocialContainer().Friends.Count >= friendsLimit)
			{
				if (userData != null)
				{
					WebSocketMessage_Social_FriendsListFull friendsListFullEvent = new();
					friendsListFullEvent.msg_id = (int)EWebSocketMessageID.SOCIAL_CANT_ADD_FRIEND_LIST_FULL;
					byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(friendsListFullEvent));
					userData.QueueWebsocketSend(bytesJSON);
				}
            }

			// Check not already friends
#pragma warning disable CS8602 // Dereference of a possibly null reference.
			if (userData.GetSocialContainer().Friends.Contains(target_user_id))
			{
				Response.StatusCode = (int)HttpStatusCode.Conflict;
				return;
			}
#pragma warning restore CS8602 // Dereference of a possibly null reference.

			// the other user must be online, theres no way to add offline people in the client

			UserSession? TargetUserData = WebSocketManager.GetDataFromUser(target_user_id);
			if (TargetUserData == null)
			{
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				return;
			}

			// Check not already trying to friend this person
			if (TargetUserData.GetSocialContainer().PendingRequests.Contains(requester_user_id))
			{
				Response.StatusCode = (int)HttpStatusCode.UnprocessableContent;
				return;
			}

			// Check not blocked by target
			if (TargetUserData.GetSocialContainer().Blocked.Contains(requester_user_id))
			{
				Response.StatusCode = (int)HttpStatusCode.BadRequest;
				return;
			}

			// if blocked by the source user, unblock, they're choosing to friend the person now
			if (userData.GetSocialContainer().Blocked.Contains(target_user_id))
			{
				// - Remove from block list (cache)
				userData.GetSocialContainer().Blocked.Remove(target_user_id);

				// - Remove from block list (DB)
				await Database.Functions.Auth.RemoveBlock(GlobalDatabaseInstance.g_Database, requester_user_id, target_user_id);
			}

			// If the other user has a pending request to us, just accept it on both ends, they both want to be friends
			if (userData.GetSocialContainer().PendingRequests.Contains(target_user_id))
			{
				// accept their request
                await HelperFunction_AcceptFriendRequest(requester_user_id, target_user_id);
            }
            else
			{
				// add to list for target
				TargetUserData.GetSocialContainer().PendingRequests.Add(requester_user_id);

                // inform them via websocket
				if (TargetUserData != null)
				{
					WebSocketMessage_Social_NewFriendRequest socialInform = new WebSocketMessage_Social_NewFriendRequest();
					socialInform.msg_id = (int)EWebSocketMessageID.SOCIAL_NEW_FRIEND_REQUEST;
					socialInform.display_name = userData.m_strDisplayName;
					byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(socialInform));
					TargetUserData.QueueWebsocketSend(bytesJSON);
				}

                // add it to DB for target (if not already exists)
                await Database.Functions.Auth.AddPendingFriendRequest(GlobalDatabaseInstance.g_Database, requester_user_id, target_user_id);
            }

			if (userData != null)
			{
				userData.NotifyFriendslistDirty();
			}

            if (TargetUserData != null)
            {
				TargetUserData.NotifyFriendslistDirty();
            }
        }

		[HttpGet("Friends")]
		[Authorize(Roles = "Player,Monitor")]
		public async Task<APIResult> Get_FriendsAndRequests()
		{
			// TODO_ASP: Set error codes properly in all places (and use variable, not magic numbers)
			RouteHandler_GET_Friends_Result result = new RouteHandler_GET_Friends_Result();

			// source user must be signed in
			Int64 requester_user_id = TokenHelper.GetUserID(this);
			if (requester_user_id == -1 || WebSocketManager.GetDataFromUser(requester_user_id) == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return result;
			}

			// get websockets & data
			UserSession? sourceData = WebSocketManager.GetDataFromUser(requester_user_id);

#pragma warning disable CS8602 // Dereference of a possibly null reference.
			HashSet<Int64> setFriends = sourceData.GetSocialContainer().Friends;
#pragma warning restore CS8602 // Dereference of a possibly null reference.
			HashSet<Int64> setPendingRequests = sourceData.GetSocialContainer().PendingRequests;

			List<Int64> lstCombined = new List<Int64>();
			lstCombined.AddRange(setFriends);
			lstCombined.AddRange(setPendingRequests);

			Dictionary<Int64, string> dictDisplayNames = await Database.Functions.Auth.GetDisplayNameBulk(GlobalDatabaseInstance.g_Database, lstCombined);

			var options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			};

			try
			{
				if (this.User.IsInRole("Monitor"))
				{
                    result.friends.Add(new FriendEntry()
                    {
                        user_id = 123,
                        display_name = "Monitor Test Friend",
                        online = true,
                        presence = "Test Presence"
                    });

                    result.pending_requests.Add(new FriendEntry()
                    {
                        user_id = 456,
                        display_name = "Monitor Test Pending Request"
                    });
                }
				else
				{
					// friends
					foreach (Int64 friend_user_id in setFriends)
					{
						if (dictDisplayNames.ContainsKey(friend_user_id)) // no display name, they probably dont exist anymore, so dont return them
						{
							// are they online?
							UserSession? targetUserData = WebSocketManager.GetDataFromUser(friend_user_id);

							string strPresence = targetUserData != null ? UserPresence.DetermineUserStatus(targetUserData) : "Offline";
							
							result.friends.Add(new FriendEntry()
							{
								user_id = friend_user_id,
								display_name = dictDisplayNames[friend_user_id],
								online = targetUserData != null,
								presence = strPresence
							});
						}
					}

					// pending
					foreach (Int64 pending_friend_user_id in setPendingRequests)
					{
						if (dictDisplayNames.ContainsKey(pending_friend_user_id)) // no display name, they probably dont exist anymore, so dont return them
						{
							result.pending_requests.Add(new FriendEntry()
							{
								user_id = pending_friend_user_id,
								display_name = dictDisplayNames[pending_friend_user_id]
							});
						}
					}
				}

				return result;

			}
			catch
			{
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return result;
			}
		}

		[HttpGet("Blocked")]
		[Authorize(Roles = "Player,Monitor")]
		public async Task<APIResult> Get_Blocked()
		{
			// TODO_ASP: Set error codes properly in all places (and use variable, not magic numbers)
			RouteHandler_GET_Blocked_Result result = new RouteHandler_GET_Blocked_Result();

			// source user must be signed in
			Int64 requester_user_id = TokenHelper.GetUserID(this);
			if (requester_user_id == -1 || WebSocketManager.GetDataFromUser(requester_user_id) == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return result;
			}

			UserSession? sourceData = WebSocketManager.GetDataFromUser(requester_user_id);

#pragma warning disable CS8602 // Dereference of a possibly null reference.
			HashSet<Int64> setBlocked = sourceData.GetSocialContainer().Blocked;
#pragma warning restore CS8602 // Dereference of a possibly null reference.

			Dictionary<Int64, string> dictDisplayNames = await Database.Functions.Auth.GetDisplayNameBulk(GlobalDatabaseInstance.g_Database, setBlocked.ToList());

			var options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			};

			try
			{
				if (this.User.IsInRole("Monitor"))
				{
                    // Dummy result for monitor
                    result.blocked.Add(new FriendEntry()
                    {
                        user_id = 123,
                        display_name = "Monitor Test User"
                    });
                }
				else
				{
					foreach (Int64 blocked_user_id in setBlocked)
					{
						if (dictDisplayNames.ContainsKey(blocked_user_id)) // no display name, they probably dont exist anymore, so dont return them
						{
							result.blocked.Add(new FriendEntry()
							{
								user_id = blocked_user_id,
								display_name = dictDisplayNames[blocked_user_id]
							});
						}
					}
				}

				return result;

			}
			catch
			{
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return result;
			}
		}

		// Block user
		[HttpPut("Blocked/{target_user_id}")]
		[Authorize(Roles = "Player")]
		public async Task Add_Block(Int64 target_user_id)
		{
			// source user must be signed in
			Int64 requester_user_id = TokenHelper.GetUserID(this);
			if (requester_user_id == -1 || WebSocketManager.GetDataFromUser(requester_user_id) == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

			// Check not already blocked
			UserSession? sourceData = WebSocketManager.GetDataFromUser(requester_user_id);

#pragma warning disable CS8602 // Dereference of a possibly null reference.
			if (sourceData.GetSocialContainer().Blocked.Contains(target_user_id))
			{
				Response.StatusCode = (int)HttpStatusCode.Conflict;
				return;
			}

			// Target user cannot be an admin
			UserSession? targetData = WebSocketManager.GetDataFromUser(target_user_id);
			if (targetData != null)
			{
				if (targetData.IsAdmin())
				{
					Response.StatusCode = (int)HttpStatusCode.NotAcceptable;
					return;
				}
			}

#pragma warning restore CS8602 // Dereference of a possibly null reference.

			// We must:
			//// - Remove from source friends, DB (if present)
			//// - Remove from source friends, Cache (if present)
			//// - Remove from target friends, DB (if present)
			//// - Remove from target friends, cache (if present)
			//// - Add to block list (cache)
			//// - Add to block list (DB)

			// Remove from source friends, DB (if present)
			// Remove from target friends, DB (if present)
			await Database.Functions.Auth.RemoveFriendship(GlobalDatabaseInstance.g_Database, requester_user_id, target_user_id);

            // Remove from source friends, Cache (if present - Remove checks Contains)
            sourceData.GetSocialContainer().Friends.Remove(target_user_id);

            if (targetData != null) // user is online
			{
				// - Remove from target friends, cache  (if present - Remove checks Contains)
				targetData.GetSocialContainer().Friends.Remove(requester_user_id);
            }

			// Add to block list (cache)
			sourceData.GetSocialContainer().Blocked.Add(target_user_id);

			// Add to block list (db)
			await Database.Functions.Auth.AddBlock(GlobalDatabaseInstance.g_Database, requester_user_id, target_user_id);

			if (sourceData != null)
            {
				sourceData.NotifyFriendslistDirty();
            }

            if (targetData != null)
            {
				targetData.NotifyFriendslistDirty();
            }
        }

		// Unblock user
		[HttpDelete("Blocked/{target_user_id}")]
		[Authorize(Roles = "Player")]
		public async Task Remove_Block(Int64 target_user_id)
		{
			// We must:
			//// - Remove from block list (cache)
			//// - Remove from block list (DB)

			// source user must be signed in
			Int64 requester_user_id = TokenHelper.GetUserID(this);
			if (requester_user_id == -1 || WebSocketManager.GetDataFromUser(requester_user_id) == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

			UserSession? sourceData = WebSocketManager.GetDataFromUser(requester_user_id);

			// Check blocked
#pragma warning disable CS8602 // Dereference of a possibly null reference.
			if (!sourceData.GetSocialContainer().Blocked.Contains(target_user_id))
			{
				Response.StatusCode = (int)HttpStatusCode.Conflict;
				return;
			}
#pragma warning restore CS8602 // Dereference of a possibly null reference.

			// - Remove from block list (cache)
			sourceData.GetSocialContainer().Blocked.Remove(target_user_id);

			// - Remove from block list (DB)
			await Database.Functions.Auth.RemoveBlock(GlobalDatabaseInstance.g_Database, requester_user_id, target_user_id);

			// only the source user needs an update here
			if (sourceData != null)
            {
				sourceData.NotifyFriendslistDirty();
            }
        }
	}
}
