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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

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

	public class POST_Livestreams_Ended_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public bool received { get; set; } = false;
		public string detail { get; set; } = String.Empty;
	}

	[ApiController]
	[Authorize(Roles = "GameClient")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
		public class LivestreamsController : ControllerBase
	{
		private readonly LobbyManager _lobbyManager;

		// Lobbies whose relay session has ended (the relay notified GO via POST /Livestreams/ended
		// that all sources left / the session was reaped). The relay owns stream liveness, so this
		// is the signal that removes a lobby from /livestreams and rejects /observe — even though
		// GO's own lobby may still be INGAME. Cleared when a fresh /register re-creates the session.
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<Int64, byte> s_endedStreams = new();

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
				if (lobby.State != ELobbyState.INGAME || !lobby.AllowObservers)
				{
					continue;
				}

				// The relay reported this stream ended — drop it from the menu even though GO's
				// lobby object is still INGAME.
				if (s_endedStreams.ContainsKey(lobby.LobbyID))
				{
					continue;
				}

				GET_Livestreams_LivestreamEntry entry = new GET_Livestreams_LivestreamEntry();
				entry.lobby_id = lobby.LobbyID;
				entry.name = lobby.Name;
				entry.map_name = lobby.MapName;
				entry.players = lobby.Members.Where(member => member.IsHuman()).Select(member => member.DisplayName).ToList();
				entry.delay_seconds = null;
				entry.age_seconds = Math.Max(0, (int)(DateTime.UtcNow - lobby.TimeCreated).TotalSeconds);

				result.livestreams.Add(entry);
			}

			return result;
		}

		[HttpPost("register", Name = "RegisterLivestream")]
		public async Task<APIResult> Register()
		{
			POST_Livestreams_Register_Result result = new POST_Livestreams_Register_Result();

			// The feature is off: refuse loudly rather than pretending a stream was set up.
			if (!RelayClient.IsEnabled())
			{
				Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
				result.detail = "Live streaming is not enabled on this server.";
				return result;
			}

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

			RelayLivestreamResponse? livestream = await RelayClient.CreateLivestreamAsync(lobby.LobbyID, user_id);
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

			// A fresh relay session was just created, so this lobby's stream is live again —
			// clear any earlier "stream ended" state from a previous session.
			s_endedStreams.TryRemove(lobby.LobbyID, out _);

			result.success = true;
			result.url = requesterUrl;
			result.member_urls = memberUrls;
			return result;
		}

		[HttpPost("observe/{lobby_id}", Name = "ObserveLivestream")]
		public async Task<APIResult> Observe(Int64 lobby_id)
		{
			POST_Livestreams_Observe_Result result = new POST_Livestreams_Observe_Result();

			// The feature is off: refuse loudly rather than handing out a broken watch URL.
			if (!RelayClient.IsEnabled())
			{
				Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
				result.detail = "Live streaming is not enabled on this server.";
				return result;
			}

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

			// The relay already told us this stream ended — reject before another relay round-trip.
			if (s_endedStreams.ContainsKey(lobby.LobbyID))
			{
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				result.detail = "That livestream has ended.";
				return result;
			}

			RelayWatchTicketResult ticket = await RelayClient.CreateWatchTicketAsync(lobby.LobbyID, user_id);
			if (ticket.Status == RelayWatchTicketStatus.StreamEnded)
			{
				// The relay session for this lobby is gone (all sources left / reaped): the
				// stream is over even though GO's lobby is still INGAME. Tell the client
				// "stream ended" (404) rather than a confusing 502, and remember it so the
				// lobby drops out of /livestreams.
				s_endedStreams[lobby.LobbyID] = 0;
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

		[HttpPost("ended", Name = "LivestreamEnded")]
		// The relay calls this (not a game client) and authenticates with its own credential
		// (Relay.ingress_api_key), so it must bypass the class-level GameClient JWT requirement.
		// [AllowAnonymous] opts this one route out; the key is validated manually below.
		[AllowAnonymous]
		public async Task<APIResult> Ended()
		{
			POST_Livestreams_Ended_Result result = new POST_Livestreams_Ended_Result();

			// The relay's credential is distinct from GO's key (Relay.api_key) that GO sends to
			// the relay. Missing/mismatched -> 401, matching the relay's /internal/* gate.
			if (!RelayClient.IsEnabled())
			{
				Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
				result.detail = "Relay integration is not enabled on this server.";
				return result;
			}

			IConfigurationSection relaySettings = Program.g_Config.GetSection("Relay");
			string? expectedKey = relaySettings.GetValue<string>("ingress_api_key");
			string? suppliedKey = Request.Headers["X-Relay-Key"].FirstOrDefault();

			if (string.IsNullOrEmpty(expectedKey) || suppliedKey != expectedKey)
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				result.detail = "Invalid or missing relay key.";
				return result;
			}

			Int64 lobby_id = -1;
			string reason = String.Empty;
			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
				var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);
				if (data != null)
				{
					if (data.TryGetValue("lobby_id", out JsonElement lobbyEl))
					{
						// The relay sends lobby_id as a decimal string; accept either for safety.
						if (lobbyEl.ValueKind == JsonValueKind.Number &&
							lobbyEl.TryGetInt64(out long parsedLobby))
						{
							lobby_id = parsedLobby;
						}
						else if (lobbyEl.ValueKind == JsonValueKind.String &&
							lobbyEl.GetString() != null &&
							Int64.TryParse(lobbyEl.GetString(), out long parsedStringLobby))
						{
							lobby_id = parsedStringLobby;
						}
					}
					if (data.TryGetValue("reason", out JsonElement reasonEl) &&
						reasonEl.ValueKind == JsonValueKind.String)
					{
						reason = reasonEl.GetString() ?? String.Empty;
					}
				}
			}

			if (lobby_id == -1)
			{
				Response.StatusCode = (int)HttpStatusCode.BadRequest;
				result.detail = "lobby_id required.";
				return result;
			}

			// The relay (the authority on stream liveness) says this stream is over. Record it
			// so the lobby drops out of /livestreams and /observe rejects — even though GO's
			// own lobby object may still be INGAME (the match can continue without a stream).
			s_endedStreams[lobby_id] = 0;

			Console.WriteLine($"[Livestream] Relay reported lobby {lobby_id} ended (reason: {reason}).");

			result.received = true;
			return result;
		}
	}
}
