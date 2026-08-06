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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public class GET_Livestreams_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public List<GET_Livestreams_LivestreamEntry> livestreams { get; set; } = new();
	}

	public class GET_Livestreams_LivestreamEntry
	{
		public Int64 lobby_id { get; set; } = -1;
		public string name { get; set; } = String.Empty;
		public string map_name { get; set; } = String.Empty;
		public List<string> players { get; set; } = new();
		public int? delay_seconds { get; set; } = null;
		public int observer_count { get; set; } = 0;
		public int? age_seconds { get; set; } = null;
	}

	public class POST_Livestreams_Register_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public bool success { get; set; } = false;
		public string url { get; set; } = String.Empty;
		public Dictionary<Int64, string> member_urls { get; set; } = new();
		public string detail { get; set; } = String.Empty;
	}

	public class POST_Livestreams_Observe_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public string url { get; set; } = String.Empty;
		public string detail { get; set; } = String.Empty;
	}

	public class POST_Livestreams_Observers_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public bool received { get; set; } = false;
		public string detail { get; set; } = String.Empty;
	}

	public class POST_Livestreams_Observers_Entry
	{
		public string lobby_id { get; set; } = String.Empty;
		public int observer_count { get; set; } = 0;
		public bool is_live { get; set; } = true;
	}

	// The relay is optional: when it is not configured (Relay.enabled off / missing keys) the
	// livestream POST endpoints must refuse loudly rather than pretending a stream was set up.
	// Applied per-endpoint so the GET /livestreams menu can keep its deliberate empty-list
	// behaviour when the feature is not deployed.
	public class RequireRelayAttribute : ActionFilterAttribute
	{
		public override void OnActionExecuting(ActionExecutingContext context)
		{
			if (!RelayClient.IsEnabled())
			{
				context.Result = new ObjectResult(new { detail = "Live streaming is not enabled on this server." })
				{
					StatusCode = (int)HttpStatusCode.ServiceUnavailable
				};
			}
		}
	}

	[ApiController]
	[Authorize(Roles = "GameClient")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
		public class LivestreamsController : ControllerBase
	{
		private readonly LobbyManager _lobbyManager;

		public LivestreamsController(LobbyManager lobbyManager)
		{
			_lobbyManager = lobbyManager;
		}

		[HttpGet(Name = "GetLivestreams")]
		public APIResult Get()
		{
			GET_Livestreams_Result result = new GET_Livestreams_Result();

			// Relay not configured -> no livestreams exist to list. An empty list is the right
			// shape here: the observer menu just shows nothing, exactly as if nobody is
			// streaming. No 5xx — there is no error, the feature is simply not deployed.
			if (!RelayClient.IsEnabled())
			{
				return result;
			}

			foreach (Lobby lobby in _lobbyManager.GetAllLobbies())
			{
				if (lobby.State != ELobbyState.INGAME || !lobby.AllowObservers || !lobby.IsStreaming)
				{
					continue;
				}

				GET_Livestreams_LivestreamEntry entry = new GET_Livestreams_LivestreamEntry();
				entry.lobby_id = lobby.LobbyID;
				entry.name = lobby.Name;
				entry.map_name = lobby.MapName;
				entry.players = lobby.Members.Where(member => member.IsHuman()).Select(member => member.DisplayName).ToList();
				entry.delay_seconds = lobby.StreamDelaySeconds;
				entry.observer_count = lobby.ObserverCount;
				entry.age_seconds = Math.Max(0, (int)(DateTime.UtcNow - lobby.TimeCreated).TotalSeconds);

				result.livestreams.Add(entry);
			}

			return result;
		}

		[HttpPost("register", Name = "RegisterLivestream")]
		[RequireRelay]
		public async Task<APIResult> Register()
		{
			POST_Livestreams_Register_Result result = new POST_Livestreams_Register_Result();

			Int64 user_id = TokenHelper.GetUserID(this);
			EUserSessionType sessionType = TokenHelper.GetSessionType(this);

			if (user_id == -1 || !SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.Gameplay))
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				result.detail = "Invalid or missing game client session.";
				return result;
			}

			Lobby? lobby = _lobbyManager.GetPlayerParticipantLobby(user_id);
			if (lobby == null || lobby.State != ELobbyState.INGAME)
			{
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				result.detail = "You are not in an in-progress match.";
				return result;
			}

			if (lobby.GetMemberFromUserID(user_id) == null && lobby.Owner != user_id)
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				result.detail = "You are not a member of this lobby.";
				return result;
			}

			// The host reports the relay stream delay in seconds when starting the stream.
			int? delaySeconds = null;
			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				if (!String.IsNullOrEmpty(jsonData))
				{
					var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
					var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);
					if (data != null && data.TryGetValue("delay_seconds", out JsonElement delayEl) &&
						delayEl.ValueKind == JsonValueKind.Number &&
						delayEl.TryGetInt32(out int parsedDelay))
					{
						delaySeconds = Math.Max(0, parsedDelay);
					}
				}
			}

			RelayLivestreamResponse? livestream = await RelayClient.CreateLivestreamAsync(lobby.LobbyID, user_id, delaySeconds);
			if (livestream == null || String.IsNullOrEmpty(livestream.base_url))
			{
				Response.StatusCode = (int)HttpStatusCode.BadGateway;
				result.detail = "Relay failed to create a livestream session.";
				return result;
			}

			Dictionary<Int64, string> memberUrls = new Dictionary<Int64, string>();
			string? requesterUrl = null;

			foreach (LobbyMember member in lobby.Members)
			{
				if (!member.IsHuman())
				{
					continue;
				}

				RelayTokenResponse? tokenResponse = await RelayClient.CreateStreamTokenAsync(lobby.LobbyID, member.UserID);
				if (tokenResponse == null || String.IsNullOrEmpty(tokenResponse.url))
				{
					Console.WriteLine($"[ERROR] Relay stream token mint failed for lobby {lobby.LobbyID} user {member.UserID}");
					continue;
				}

				memberUrls[member.UserID] = tokenResponse.url;

				if (member.UserID == user_id)
				{
					requesterUrl = tokenResponse.url;
				}
			}

			if (requesterUrl == null)
			{
				Response.StatusCode = (int)HttpStatusCode.BadGateway;
				result.detail = "Relay failed to mint a stream token for you.";
				return result;
			}

			// Refresh the lobby lists in the network room so the in-progress game shows up in the livestreams menu.
			await WebSocketManager.SendNewOrDeletedLobbyToAllNetworkRoomMembers(lobby.NetworkRoomID);

			// A fresh relay session was just created — mark this lobby as actively streaming
			// so it shows up in /livestreams and is accepted by /observe.
			lobby.SetStreaming(true, delaySeconds, 0);

			result.success = true;
			result.url = requesterUrl;
			result.member_urls = memberUrls;
			return result;
		}

		[HttpPost("observe/{lobby_id}", Name = "ObserveLivestream")]
		[RequireRelay]
		public async Task<APIResult> Observe(Int64 lobby_id)
		{
			POST_Livestreams_Observe_Result result = new POST_Livestreams_Observe_Result();

			Int64 user_id = TokenHelper.GetUserID(this);
			EUserSessionType sessionType = TokenHelper.GetSessionType(this);

			if (user_id == -1 || !SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.ServerListReadOnly))
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				result.detail = "Invalid or missing game client session.";
				return result;
			}

			Lobby? lobby = _lobbyManager.GetLobby(lobby_id);
			if (lobby == null || lobby.State != ELobbyState.INGAME || !lobby.AllowObservers)
			{
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				result.detail = "No watchable live game found for that lobby_id.";
				return result;
			}

			// No active relay stream for this lobby — reject before another relay round-trip.
			if (!lobby.IsStreaming)
			{
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				result.detail = "That livestream has ended.";
				return result;
			}

			RelayWatchTicketResult ticket = await RelayClient.CreateWatchTicketAsync(lobby.LobbyID, user_id);
			if (ticket.Status == RelayWatchTicketStatus.StreamEnded)
			{
				// The relay session for this lobby is gone (all sources left / reaped): the
				// stream is over even though GO's lobby is still INGAME. Deregister it so the
				// lobby drops out of /livestreams on the next refresh, and tell the client
				// "stream ended" (404) rather than a confusing 502.
				lobby.SetStreaming(false);
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				result.detail = "That livestream has ended.";
				return result;
			}

			if (ticket.Status != RelayWatchTicketStatus.Ok ||
				ticket.Token == null || String.IsNullOrEmpty(ticket.Token.url))
			{
				Response.StatusCode = (int)HttpStatusCode.BadGateway;
				result.detail = "Relay failed to issue a watch ticket.";
				return result;
			}

			result.url = ticket.Token.url;
			return result;
		}

		[HttpPost("observers", Name = "LivestreamObservers")]
		[RequireRelay]
		// The relay calls this (not a game client) with its own credential
		// (Relay.ingress_api_key), so it must bypass the class-level GameClient JWT requirement.
		[AllowAnonymous]
		public async Task<APIResult> Observers([FromHeader(Name = "X-Relay-Key")] string? relayKey)
		{
			POST_Livestreams_Observers_Result result = new POST_Livestreams_Observers_Result();

			if (!RelayClient.ValidateIngressKey(relayKey))
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				result.detail = "Invalid or missing relay key.";
				return result;
			}

			// The relay batches all lobbies whose livestream state changed into one request as an
			// array of {lobby_id, observer_count, is_live} entries, always containing at least one
			// update. is_live=false means the relay closed the stream (it owns stream liveness),
			// so the lobby is deregistered.
			List<POST_Livestreams_Observers_Entry>? updates = null;
			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
				updates = JsonSerializer.Deserialize<List<POST_Livestreams_Observers_Entry>>(jsonData, options);
			}

			if (updates == null || updates.Count == 0)
			{
				Response.StatusCode = (int)HttpStatusCode.BadRequest;
				result.detail = "livestream state updates must be an array.";
				return result;
			}

			// The relay (the authority on who is watching and stream liveness) reports the
			// current livestream state. is_live=false deregisters the stream so the lobby
			// drops out of /livestreams and /observe rejects — even though GO's own lobby
			// object may still be INGAME (the match can continue without a stream).
			foreach (POST_Livestreams_Observers_Entry update in updates)
			{
				if (!Int64.TryParse(update.lobby_id, out Int64 entryLobby))
				{
					continue;
				}

				Lobby? observedLobby = _lobbyManager.GetLobby(entryLobby);
				if (observedLobby == null)
				{
					continue;
				}

				if (update.is_live)
				{
					if (observedLobby.IsStreaming)
					{
						observedLobby.SetStreaming(true, observerCount: Math.Max(0, update.observer_count));
					}
				}
				else
				{
					observedLobby.SetStreaming(false, observerCount: 0);
				}
			}

			result.received = true;
			return result;
		}
	}
}
