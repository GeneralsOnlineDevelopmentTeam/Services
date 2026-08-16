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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
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
		public int state { get; set; } = 1;
		public bool passworded { get; set; } = false;
		public int pending_observer_count { get; set; } = 0;

		// Highest EUserPriority among the lobby's human members (0/1/2). The client
		// highlights rows with value 1 (a priority Player is in the match).
		public int user_priority { get; set; } = 0;

		// Priority-player match (lobby latched when a users.user_priority = Player creates or
		// joins): sorts the row to the top of the Watch Live browser.
		public bool priority { get; set; } = false;

		// Per-viewer directive: 0 observe (pre-game, enter the lobby view), 1 wait (not live
		// yet, or held behind the delay - enter the lobby view and wait), 2 join (connect now).
		public int watch_action { get; set; } = 2;

		// Remaining broadcast-delay hold in seconds for this viewer (null when not held).
		public int? delay_remaining_seconds { get; set; } = null;
	}

	public class POST_Livestreams_Register_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public bool success { get; set; } = false;
		public string url { get; set; } = String.Empty;
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

		// 423 (delay gate): seconds left before this viewer's ticket may be minted.
		public int? delay_remaining_seconds { get; set; } = null;

		// 200: GO owns the broadcast delay (the ticket was only minted after it elapsed), so
		// the client must not hold playback itself.
		public bool server_held { get; set; } = false;
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

	// Per-endpoint rather than class-wide: an unconfigured relay must refuse the POSTs, but GET
	// /livestreams should still answer with its ordinary empty list.
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
		private readonly IDbContextFactory<AppDbContext> _dbFactory;

		// How long a started-but-never-streamed game stays listed before Watch Live drops it -
		// streamers register within seconds of match start, so 60s is generous.
		private const int NeverStreamedGraceSeconds = 60;

		public LivestreamsController(LobbyManager lobbyManager, IDbContextFactory<AppDbContext> dbFactory)
		{
			_lobbyManager = lobbyManager;
			_dbFactory = dbFactory;
		}

		[HttpGet(Name = "GetLivestreams")]
		public async Task<APIResult> Get()
		{
			GET_Livestreams_Result result = new GET_Livestreams_Result();

			// Relay not configured -> no livestreams exist to list. An empty list is the right
			// shape here: the observer menu just shows nothing, exactly as if nobody is
			// streaming. No 5xx - there is no error, the feature is simply not deployed.
			if (!RelayClient.IsEnabled())
			{
				return result;
			}

			Int64 user_id = TokenHelper.GetUserID(this);
			// A priority viewer (admin, or user_priority = Viewer re-read live from the DB
			// like Observe does) is never held behind the broadcast delay below.
			bool isPriority = TokenHelper.IsAdmin(this) || TokenHelper.GetUserPriority(this) == EUserPriority.Viewer;
			if (!isPriority)
			{
				await using var db = await _dbFactory.CreateDbContextAsync();
				if (await Database.Users.GetUserPriority(db, user_id) == EUserPriority.Viewer)
				{
					isPriority = true;
				}
			}

			foreach (Lobby lobby in _lobbyManager.GetAllLobbies())
			{
				// AllowObservers governs in-game observer slots, a different feature - a
				// livestream is gated only by IsStreaming (did the host start one).
				bool isPregame = lobby.State == ELobbyState.GAME_SETUP;
				bool isLive = lobby.State == ELobbyState.INGAME && lobby.IsStreaming;
				bool isWaiting = lobby.State == ELobbyState.INGAME && !lobby.IsStreaming;
				// A pre-game lobby is only watchable when the host allowed streamers at
				// creation: without that, no stream can ever come, and parking observers on
				// it would only lead to an endless wait. Live rows are already streaming, so
				// they are always listed.
				if (!isLive && !lobby.AllowStreamers)
				{
					continue;
				}
				if (!isPregame && !isLive && !isWaiting)
				{
					continue;
				}

				// A started game with no stream is only listed for a grace period: after
				// that the stream is simply not coming (or has ended), so the row would
				// only strand observers on a wait that can never end. The browser picks
				// the drop up on its next 5s refresh.
				if (isWaiting && lobby.TimeMatchStarted != null &&
					(DateTime.UtcNow - lobby.TimeMatchStarted.Value).TotalSeconds > NeverStreamedGraceSeconds)
				{
					continue;
				}

				// 0 = observe (pre-game), 1 = wait (stream not live yet, or this viewer is
				// held behind the broadcast delay), 2 = join (stream live, ticket mints now).
				int watchAction = isPregame ? 0 : 1;
				int? delayRemainingSeconds = null;
				// The hold clock only means something for lobbies that actually have a
				// stream: a never-streamed game would otherwise show a countdown for a
				// hold that can never expire.
				if (lobby.State == ELobbyState.INGAME && lobby.IsStreaming &&
					lobby.TimeMatchStarted != null && lobby.StreamDelaySeconds > 0)
				{
					delayRemainingSeconds = Math.Max(0, lobby.StreamDelaySeconds.Value -
						(int)(DateTime.UtcNow - lobby.TimeMatchStarted.Value).TotalSeconds);
				}
				if (isLive && (isPriority || delayRemainingSeconds == null || delayRemainingSeconds == 0))
				{
					watchAction = 2;
				}

				GET_Livestreams_LivestreamEntry entry = new GET_Livestreams_LivestreamEntry();
				entry.lobby_id = lobby.LobbyID;
				entry.name = lobby.Name;
				entry.map_name = lobby.MapName;
				entry.players = lobby.Members.Where(member => member.IsHuman()).Select(member => member.DisplayName).ToList();
				entry.delay_seconds = lobby.StreamDelaySeconds;
				entry.observer_count = lobby.ObserverCount;
				entry.age_seconds = Math.Max(0, (int)(DateTime.UtcNow - lobby.TimeCreated).TotalSeconds);
				entry.state = isLive ? 1 : 0;
				entry.passworded = lobby.IsPassworded;
				entry.pending_observer_count = lobby.PendingObserverCount;
				entry.priority = lobby.IsPriority;
				entry.watch_action = watchAction;
				entry.delay_remaining_seconds = delayRemainingSeconds;

				result.livestreams.Add(entry);
			}

			// Watch Live order: priority-player matches first, then join -> wait -> pre-game.
			// Stable, so equal rows keep their insertion order.
			result.livestreams = result.livestreams
				.OrderByDescending(e => e.priority)
				.ThenByDescending(e => e.watch_action)
				.ToList();

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

			// Runs once per streaming client, not once per lobby: each gets its own single-use
			// relay token, and minting one for a member who hasn't asked for it just burns it.
			bool isHost = lobby.Owner == user_id;

			// Only the host may set the delay (their spoiler window), but it's a lobby
			// property - if a member registers first (host's own streaming still off), the
			// session still needs the host's already-chosen delay, not the relay default.
			int? delaySeconds = lobby.StreamDelaySeconds;
			if (isHost)
			{
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
			}

			// lobby.Owner, not the caller - any member may register first, but the relay must
			// record the same owner either way.
			RelayLivestreamResponse? livestream = await RelayClient.CreateLivestreamAsync(lobby.LobbyID, lobby.Owner, delaySeconds);
			if (livestream == null || String.IsNullOrEmpty(livestream.base_url))
			{
				Response.StatusCode = (int)HttpStatusCode.BadGateway;
				result.detail = "Relay failed to create a livestream session.";
				return result;
			}

			RelayTokenResponse? tokenResponse = await RelayClient.CreateStreamTokenAsync(lobby.LobbyID, user_id);
			if (tokenResponse == null || String.IsNullOrEmpty(tokenResponse.url))
			{
				Console.WriteLine($"[ERROR] Relay stream token mint failed for lobby {lobby.LobbyID} user {user_id}");
				Response.StatusCode = (int)HttpStatusCode.BadGateway;
				result.detail = "Relay failed to mint a stream token for you.";
				return result;
			}

			// Registering does not make the lobby live - nothing has streamed into the relay
			// session yet. Observers() below flips IsStreaming once the relay reports is_live.
			lobby.SetStreamDelay(delaySeconds);

			result.success = true;
			result.url = tokenResponse.url;
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

			// As in Get: watching a livestream is not the same as taking an in-game observer
			// slot, so AllowObservers has no say here. IsStreaming below is the gate.
			Lobby? lobby = _lobbyManager.GetLobby(lobby_id);
			if (lobby == null || lobby.State != ELobbyState.INGAME)
			{
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				result.detail = "No watchable live game found for that lobby_id.";
				return result;
			}

			// No active relay stream for this lobby - reject before another relay round-trip.
			if (!lobby.IsStreaming)
			{
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				result.detail = "That livestream has ended.";
				return result;
			}

			// Admin or user_priority = Viewer skips the password and broadcast-delay gates
			// below - ticket mints instantly.
			bool isPriority = TokenHelper.IsAdmin(this) || TokenHelper.GetUserPriority(this) == EUserPriority.Viewer;
			if (!isPriority)
			{
				await using var db = await _dbFactory.CreateDbContextAsync();

				// Re-read live rather than trusting only the login-time claim: a mid-session
				// grant (bot timed grant, Discord !setpriority) must not wait for a re-login.
				if (await Database.Users.GetUserPriority(db, user_id) == EUserPriority.Viewer)
				{
					isPriority = true;
				}
			}

			// A livestream inherits its lobby's password; the read-only pre-game lobby view
			// itself stays password-free. Checked before the ticket mint so a bad password
			// never burns a relay ticket.
			string? strProvidedPassword = null;
			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				if (!String.IsNullOrWhiteSpace(jsonData))
				{
					try
					{
						var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
						var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);
						if (data != null && data.ContainsKey("password") && data["password"].ValueKind == JsonValueKind.String)
							strProvidedPassword = data["password"].GetString();
					}
					catch { /* unreadable body = no password supplied; the gate below decides */ }
				}
			}

			if (!isPriority && lobby.IsPassworded && strProvidedPassword != lobby.Password)
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				result.detail = "This livestream is password protected.";
				return result;
			}

			// A normal viewer is held until the match has run for the host's delay, clocked
			// from TimeMatchStarted (GO's own state, not the relay's liveness report).
			if (!isPriority && lobby.StreamDelaySeconds > 0 && lobby.TimeMatchStarted != null)
			{
				int remainingSeconds = lobby.StreamDelaySeconds.Value -
					(int)(DateTime.UtcNow - lobby.TimeMatchStarted.Value).TotalSeconds;
				if (remainingSeconds > 0)
				{
					// 423 so the client's retry keeps working - nothing was minted, so there's
					// no ticket to burn.
					Response.StatusCode = (int)HttpStatusCode.Locked;
					result.detail = "This stream starts after its broadcast delay.";
					result.delay_remaining_seconds = remainingSeconds;
					return result;
				}
			}

			RelayWatchTicketResult ticket = await RelayClient.CreateWatchTicketAsync(lobby.LobbyID, user_id, isPriority);
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
			result.server_held = true;
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

			// One request batches every lobby whose state changed: array of
			// {lobby_id, observer_count, is_live}, at least one entry.
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

			// The relay is the authority on stream liveness, not GO's own lobby state - a match
			// can stay INGAME with nobody streaming it, so is_live=false here still deregisters.
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
					bool wasStreaming = observedLobby.IsStreaming;

					// On the first transition, seed the observer count with the pre-game
					// watchers who were parked in the lobby view: they join within moments, so
					// without the seed the count would dip to ~0 then climb. The relay's own
					// periodic reporting takes over from here.
					int seededObserverCount = Math.Max(0, update.observer_count) + observedLobby.PendingObserverCount;
					observedLobby.SetStreaming(true, observerCount: seededObserverCount);

					if (!wasStreaming)
					{
						// The stream just became watchable. Tell the read-only observers so
						// they can fetch a watch ticket and join immediately.
						if (observedLobby.PendingObservers.Count > 0)
						{
							WebSocketMessage_LobbyObserverEvent observerEvent = new WebSocketMessage_LobbyObserverEvent();
							observerEvent.msg_id = (int)EWebSocketMessageID.LOBBY_OBSERVER_STREAM_LIVE;
							observerEvent.lobby_id = observedLobby.LobbyID;
							byte[] observerBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(observerEvent));

							foreach (UserSession sess in observedLobby.PendingObservers.Keys)
							{
								sess.QueueWebsocketSend(observerBytes);
							}
						}

						// Refresh the lobby lists in the network room so the in-progress game
						// appears in the livestreams menu.
						await WebSocketManager.SendNewOrDeletedLobbyToAllNetworkRoomMembers(observedLobby.NetworkRoomID);
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
