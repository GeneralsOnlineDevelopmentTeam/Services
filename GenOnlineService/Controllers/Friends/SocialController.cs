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
using Discord.Commands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Net.NetworkInformation;
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
		private const int FriendsLimit = 200;

		private readonly IDbContextFactory<AppDbContext> _dbFactory;
		private readonly ILogger<SocialController> _logger;

		public SocialController(IDbContextFactory<AppDbContext> dbFactory, ILogger<SocialController> logger)
		{
			_logger = logger;
			_dbFactory = dbFactory;
		}

		// Friends/Requests/<id>
		// PUT = send request
		// POST = accept request
		// DELETE = reject request

		private bool RejectIfFriendsListFull(Int64 user_id, SharedUserData userData)
		{
			if (userData.GetSocialContainer().Friends.Count < FriendsLimit)
			{
				return false;
			}

			WebSocketMessage_Social_FriendsListFull friendsListFullEvent = new();
			friendsListFullEvent.msg_id = (int)EWebSocketMessageID.SOCIAL_CANT_ADD_FRIEND_LIST_FULL;
			byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(friendsListFullEvent));

			WebsocketHelper.SendToAllSessionsOfUser(user_id, bytesJSON);
			Response.StatusCode = (int)HttpStatusCode.Forbidden;
			return true;
		}

		private async Task<bool> HelperFunction_AcceptFriendRequest(Int64 source_user_id, Int64 target_user_id)
		{
			// NOTE: target user does NOT need to be signed in
			SharedUserData? sharedUserDataSource = GenOnlineService.WebSocketManager.GetSharedDataForUser(source_user_id);
			SharedUserData? sharedUserDataTarget = GenOnlineService.WebSocketManager.GetSharedDataForUser(target_user_id);

			if (sharedUserDataSource == null)
			{
				return false;
			}

			await using var db = await _dbFactory.CreateDbContextAsync();

			// a friendship adds an entry to both lists, so the target must have room too
			int targetFriendCount = sharedUserDataTarget != null
				? sharedUserDataTarget.GetSocialContainer().Friends.Count
				: await Database.Social.CountFriends(db, target_user_id);

			if (targetFriendCount >= FriendsLimit)
			{
				return false;
			}

			// remove the request from requestor (online version)
			sharedUserDataSource.GetSocialContainer().PendingRequests.Remove(target_user_id);

            // remove the request from requestor (db)
            await Database.Social.RemovePendingFriendRequest(db, source_user_id, target_user_id);

            // Add to both players friends list (online version and db)

            // SHARED db (we only have to add this once and it covers both players)
            await Database.Social.CreateFriendship(db, source_user_id, target_user_id);

            // source player
            {
				// sess
				sharedUserDataSource.GetSocialContainer().Friends.Add(target_user_id);
            }

            // target player
            {
                // sess
                if (sharedUserDataTarget != null)
                {
					sharedUserDataTarget.GetSocialContainer().Friends.Add(source_user_id);
                }
            }

			// notify the source player that the target player is online, if they are
			if (sharedUserDataTarget != null)
            {
                WebSocketMessage_Social_FriendStatusChanged friendStatusChangedEvent = new();
                friendStatusChangedEvent.msg_id = (int)EWebSocketMessageID.SOCIAL_FRIEND_ONLINE_STATUS_CHANGED;
                friendStatusChangedEvent.display_name = sharedUserDataTarget.m_strDisplayName;
                friendStatusChangedEvent.online = true;
                byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(friendStatusChangedEvent));

				// send to all sessions
				WebsocketHelper.SendToAllSessionsOfUser(source_user_id, bytesJSON);
            }

            // notify the target player that the source player accepted their request
            if (sharedUserDataTarget != null)
            {
                WebSocketMessage_Social_FriendRequestAccepted friendRequestAcceptedEvent = new();
                friendRequestAcceptedEvent.msg_id = (int)EWebSocketMessageID.SOCIAL_FRIEND_FRIEND_REQUEST_ACCEPTED_BY_TARGET;
                friendRequestAcceptedEvent.display_name = sharedUserDataSource.m_strDisplayName;
                byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(friendRequestAcceptedEvent));

				// send to all sessions
				WebsocketHelper.SendToAllSessionsOfUser(target_user_id, bytesJSON);
			}

			return true;
        }

		// Accept a request
		[HttpPost("Friends/Requests/{target_user_id}")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
		public async Task AcceptPendingRequest(Int64 target_user_id)
		{
			// source user must be signed in (anywhere)
			Int64 source_user_id = TokenHelper.GetUserID(this);
			SharedUserData? userData = WebSocketManager.GetSharedDataForUser(source_user_id);
			if (source_user_id == -1 || userData == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

			if (RejectIfFriendsListFull(source_user_id, userData))
			{
				return;
			}

			if (!await HelperFunction_AcceptFriendRequest(source_user_id, target_user_id))
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

			SocialHelper.NotifyFriendslistDirty(source_user_id);
			SocialHelper.NotifyFriendslistDirty(target_user_id);
		}

		// Reject a request
		[HttpDelete("Friends/Requests/{target_user_id}")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
		public async Task RejectPendingRequest(Int64 target_user_id)
		{
			// source user must be signed in
			Int64 source_user_id = TokenHelper.GetUserID(this);
			SharedUserData? userData = WebSocketManager.GetSharedDataForUser(source_user_id);
			if (source_user_id == -1 || userData == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

			// remove the request from requestor (online version)
			userData.GetSocialContainer().PendingRequests.Remove(target_user_id);

			// remove the request from requestor (db)
			// NOTE: Target and source are inverted here because the target is actually the person who sent the request, source is the person taking action on the friend request
			await using var db = await _dbFactory.CreateDbContextAsync();
			await Database.Social.RemovePendingFriendRequest(db, target_user_id, source_user_id);

			SocialHelper.NotifyFriendslistDirty(source_user_id);
			SocialHelper.NotifyFriendslistDirty(target_user_id);
        }

		// Remove a friend
		[HttpDelete("Friends/{target_user_id}")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
		public async Task RemoveFriend(Int64 target_user_id)
		{
			// source user must be signed in
			Int64 source_user_id = TokenHelper.GetUserID(this);
			SharedUserData? userData = WebSocketManager.GetSharedDataForUser(source_user_id);
			if (source_user_id == -1 || userData == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

			// must be friends
			if (!userData.GetSocialContainer().Friends.Contains(target_user_id))
			{
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				return;
			}

			// remove the request from requestor (online version)
			userData.GetSocialContainer().Friends.Remove(target_user_id);

			// if the other player is online, remove from them too
			SharedUserData? TargetUserData = WebSocketManager.GetSharedDataForUser(target_user_id);
			if (TargetUserData != null)
			{
				TargetUserData.GetSocialContainer().Friends.Remove(source_user_id);
			}

			// remove the request from requestor (db)
			await using var db = await _dbFactory.CreateDbContextAsync();
			await Database.Social.RemoveFriendship(db, source_user_id, target_user_id);

			// TODO_SOCIAL: This tells the client to do a GET, we could just send them their friends list directly to reduce latency + calls to service
			SocialHelper.NotifyFriendslistDirty(source_user_id);
			SocialHelper.NotifyFriendslistDirty(target_user_id);
		}

        // Send a request
        [HttpPut("Friends/Requests/{target_user_id}")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
		public async Task AddFriend(Int64 target_user_id)
		{
			// source user must be signed in
			Int64 requester_user_id = TokenHelper.GetUserID(this);
			SharedUserData? userData = WebSocketManager.GetSharedDataForUser(requester_user_id);
			if (requester_user_id == -1 || userData == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

			if (RejectIfFriendsListFull(requester_user_id, userData))
			{
				return;
			}

			// Check not already friends
			if (userData.GetSocialContainer().Friends.Contains(target_user_id))
			{
				Response.StatusCode = (int)HttpStatusCode.Conflict;
				return;
			}

			await using var db = await _dbFactory.CreateDbContextAsync();

			// the other user must be online, theres no way to add offline people in the client

			SharedUserData? TargetUserData = WebSocketManager.GetSharedDataForUser(target_user_id);
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
				await Database.Social.RemoveBlock(db, requester_user_id, target_user_id);
			}

			// If the other user has a pending request to us, just accept it on both ends, they both want to be friends
			if (userData.GetSocialContainer().PendingRequests.Contains(target_user_id))
			{
				// accept their request
                if (!await HelperFunction_AcceptFriendRequest(requester_user_id, target_user_id))
                {
                    Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    return;
                }
            }
            else
			{
				// add to list for target
				TargetUserData.GetSocialContainer().PendingRequests.Add(requester_user_id);

				// inform them via websocket
				WebSocketMessage_Social_NewFriendRequest socialInform = new WebSocketMessage_Social_NewFriendRequest();
				socialInform.msg_id = (int)EWebSocketMessageID.SOCIAL_NEW_FRIEND_REQUEST;
				socialInform.display_name = userData.m_strDisplayName;
				byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(socialInform));

				WebsocketHelper.SendToAllSessionsOfUser(target_user_id, bytesJSON);

                // add it to DB for target (if not already exists)
                await Database.Social.AddPendingFriendRequest(db, requester_user_id, target_user_id);
            }

			SocialHelper.NotifyFriendslistDirty(requester_user_id);
			SocialHelper.NotifyFriendslistDirty(target_user_id);
		}

		[HttpGet("Friends")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher,Monitor")]
		public async Task<APIResult> Get_FriendsAndRequests()
		{
			// TODO_ASP: Set error codes properly in all places (and use variable, not magic numbers)
			RouteHandler_GET_Friends_Result result = new RouteHandler_GET_Friends_Result();

			// source user must be signed in
			Int64 requester_user_id = TokenHelper.GetUserID(this);
			SharedUserData? sourceData = WebSocketManager.GetSharedDataForUser(requester_user_id);
			if (requester_user_id == -1 || sourceData == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return result;
			}

			HashSet<Int64> setFriends = sourceData.GetSocialContainer().Friends;
			HashSet<Int64> setPendingRequests = sourceData.GetSocialContainer().PendingRequests;

			List<Int64> lstCombined = new List<Int64>();
			lstCombined.AddRange(setFriends);
			lstCombined.AddRange(setPendingRequests);

			await using var db = await _dbFactory.CreateDbContextAsync();
			Dictionary<Int64, string> dictDisplayNames = await Database.Users.GetDisplayNameBulk(db, lstCombined);

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
							string strPresence = UserPresence.DetermineUserStatusFromAllSessions(friend_user_id, out bool isOnline);

							result.friends.Add(new FriendEntry()
							{
								user_id = friend_user_id,
								display_name = dictDisplayNames[friend_user_id],
								online = isOnline,
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
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher,Monitor")]
		public async Task<APIResult> Get_Blocked()
		{
			// TODO_ASP: Set error codes properly in all places (and use variable, not magic numbers)
			RouteHandler_GET_Blocked_Result result = new RouteHandler_GET_Blocked_Result();

			// source user must be signed in
			Int64 requester_user_id = TokenHelper.GetUserID(this);
			SharedUserData? sourceData = WebSocketManager.GetSharedDataForUser(requester_user_id);
			if (requester_user_id == -1 || sourceData == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return result;
			}

			HashSet<Int64> setBlocked = sourceData.GetSocialContainer().Blocked;

			await using var db = await _dbFactory.CreateDbContextAsync();
			Dictionary<Int64, string> dictDisplayNames = await Database.Users.GetDisplayNameBulk(db, setBlocked.ToList());

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
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
		public async Task Add_Block(Int64 target_user_id)
		{
			// source user must be signed in
			Int64 requester_user_id = TokenHelper.GetUserID(this);
			SharedUserData? sourceData = WebSocketManager.GetSharedDataForUser(requester_user_id);
			if (requester_user_id == -1 || sourceData == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

			// Check not already blocked
			if (sourceData.GetSocialContainer().Blocked.Contains(target_user_id))
			{
				Response.StatusCode = (int)HttpStatusCode.Conflict;
				return;
			}

			// Target user cannot be an admin
			SharedUserData? targetData = WebSocketManager.GetSharedDataForUser(target_user_id);
			if (targetData != null)
			{
				if (targetData.IsAdmin())
				{
					Response.StatusCode = (int)HttpStatusCode.NotAcceptable;
					return;
				}
			}

			// We must:
			//// - Remove from source friends, DB (if present)
			//// - Remove from source friends, Cache (if present)
			//// - Remove from target friends, DB (if present)
			//// - Remove from target friends, cache (if present)
			//// - Add to block list (cache)
			//// - Add to block list (DB)
			///
			await using var db = await _dbFactory.CreateDbContextAsync();

			// Remove from source friends, DB (if present)
			// Remove from target friends, DB (if present)
			await Database.Social.RemoveFriendship(db, requester_user_id, target_user_id);

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
			await Database.Social.AddBlock(db, requester_user_id, target_user_id);

			SocialHelper.NotifyFriendslistDirty(requester_user_id);
			SocialHelper.NotifyFriendslistDirty(target_user_id);
		}

		// Unblock user
		[HttpDelete("Blocked/{target_user_id}")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
		public async Task Remove_Block(Int64 target_user_id)
		{
			// We must:
			//// - Remove from block list (cache)
			//// - Remove from block list (DB)

			// source user must be signed in
			Int64 requester_user_id = TokenHelper.GetUserID(this);
			SharedUserData? sourceData = WebSocketManager.GetSharedDataForUser(requester_user_id);
			if (requester_user_id == -1 || sourceData == null)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

			// Check blocked
			if (!sourceData.GetSocialContainer().Blocked.Contains(target_user_id))
			{
				Response.StatusCode = (int)HttpStatusCode.Conflict;
				return;
			}

			// - Remove from block list (cache)
			sourceData.GetSocialContainer().Blocked.Remove(target_user_id);

			// - Remove from block list (DB)
			await using var db = await _dbFactory.CreateDbContextAsync();
			await Database.Social.RemoveBlock(db, requester_user_id, target_user_id);

			// only the source user needs an update here
			SocialHelper.NotifyFriendslistDirty(requester_user_id);
		}
	}
}
