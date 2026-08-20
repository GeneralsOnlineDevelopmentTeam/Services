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

using Discord.Rest;
using GenOnlineService;
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
using static MatchmakingManager;

namespace GenOnlineService.Controllers
{
	[ApiController]
	[Authorize(Roles = "GameClient")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class MatchmakingController : ControllerBase
	{
		private readonly ILogger<MatchmakingController> _logger;

		public MatchmakingController(ILogger<MatchmakingController> logger)
		{
			_logger = logger;
		}

		[HttpPut]
		[Authorize(Roles = "GameClient")]
		public async Task<APIResult?> Put()
		{
			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				};

				try
				{
					var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);

					if (data != null
					&& data.ContainsKey("playlist")
					&& data.ContainsKey("maps")
					&& data.ContainsKey("exe_crc")
					&& data.ContainsKey("ini_crc")
					&& data.ContainsKey("anticheat_id")
					)
					{
						UInt16 playlistID = data["playlist"].GetUInt16();
						var array = data["maps"].EnumerateArray();
						List<int> mapIndices = array.Select(x => x.GetInt32()).ToList();
						UInt32 exe_crc = data["exe_crc"].GetUInt32();
						UInt32 ini_crc = data["ini_crc"].GetUInt32();
						EKnownAnticheatID anticheatID = (EKnownAnticheatID)data["anticheat_id"].GetUInt16();

						Int64 user_id = TokenHelper.GetUserID(this);
						EUserSessionType sessionType = TokenHelper.GetSessionType(this);
						if (user_id != -1 && SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.Gameplay))
						{
							UserSession? playerSession = WebSocketManager.GetSessionFromUser(user_id, sessionType);

							if (playerSession != null)
							{
								await MatchmakingManager.RegisterPlayer(playerSession, playlistID, mapIndices, exe_crc, ini_crc, anticheatID);
							}
						}
					}
				}
				catch
				{
					Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				}
			}

			return null;
		}

		[HttpPost("Widen")]
		[Authorize(Roles = "GameClient")]
		public void Put_Widen()
		{
			// TODO_QUICKMATCH: What if a user widens after already being matched? We should probably tell them no
			// widen the search
			Int64 user_id = TokenHelper.GetUserID(this);
			EUserSessionType sessionType = TokenHelper.GetSessionType(this);
			if (user_id != -1 && SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.Gameplay))
			{
				UserSession? playerSession = WebSocketManager.GetSessionFromUser(user_id, sessionType); ;

				if (playerSession != null)
				{
					MatchmakingManager.PlayerWidenSearch(playerSession);
				}
			}
		}

		[HttpDelete]
		[Authorize(Roles = "GameClient")]
		public async Task Delete()
		{
			Int64 user_id = TokenHelper.GetUserID(this);
			EUserSessionType sessionType = TokenHelper.GetSessionType(this);
			if (user_id != -1 && SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.Gameplay))
			{
				UserSession? playerSession = WebSocketManager.GetSessionFromUser(user_id, sessionType);

				if (playerSession != null)
				{
					await MatchmakingManager.DeregisterPlayer(playerSession);
				}
			}
		}

		public class RouteHandler_GET_Playlists_Result : APIResult
		{
			public override Type GetReturnType()
			{
				return this.GetType();
			}

			public Dictionary<UInt16, Playlist>? playlists { get; set; } = null;
		}

		// Get playlists
		[HttpGet("Playlists")]
		[Authorize(Roles = "GameClient")]
		public APIResult Get_Playlists()
		{
			RouteHandler_GET_Playlists_Result result = new RouteHandler_GET_Playlists_Result();

			Response.StatusCode = (int)HttpStatusCode.OK;
			result.playlists = MatchmakingManager.GetPlaylists();
			
			return result;
		}
	}
}
