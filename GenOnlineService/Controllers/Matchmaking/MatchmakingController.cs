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
using MySqlX.XDevAPI.Common;
using Org.BouncyCastle.Tls;
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
	[Authorize(Roles = "Player")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class MatchmakingController : ControllerBase
	{
		private readonly ILogger<MatchmakingController> _logger;

		public MatchmakingController(ILogger<MatchmakingController> logger)
		{
			_logger = logger;
		}

		[HttpPut]
		[Authorize(Roles = "Player")]
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
					)
					{
						UInt16 playlistID = data["playlist"].GetUInt16();
						var array = data["maps"].EnumerateArray();
						List<int> mapIndices = array.Select(x => x.GetInt32()).ToList();
						UInt32 exe_crc = data["exe_crc"].GetUInt32();
						UInt32 ini_crc = data["ini_crc"].GetUInt32();

						Int64 user_id = TokenHelper.GetUserID(this);
						if (user_id != -1)
						{
							UserSession? playerSession = WebSocketManager.GetDataFromUser(user_id); ;

							if (playerSession != null)
							{
								await MatchmakingManager.RegisterPlayer(playerSession, playlistID, mapIndices, exe_crc, ini_crc);
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
		[Authorize(Roles = "Player")]
		public void Put_Widen()
		{
			try
			{
				// TODO_QUICKMATCH: What if a user widens after already being matched? We should probably tell them no
				// widen the search
				Int64 user_id = TokenHelper.GetUserID(this);
				if (user_id != -1)
				{
					UserSession? playerSession = WebSocketManager.GetDataFromUser(user_id); ;

					if (playerSession != null)
					{
						MatchmakingManager.PlayerWidenSearch(playerSession);
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in Put_Widen: {ex.Message}");
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
			}
		}

		[HttpDelete]
		[Authorize(Roles = "Player")]
		public void Delete()
		{
			try
			{
				Int64 user_id = TokenHelper.GetUserID(this);
				if (user_id != -1)
				{
					UserSession? playerSession = WebSocketManager.GetDataFromUser(user_id); ;

					if (playerSession != null)
					{
						MatchmakingManager.DeregisterPlayer(playerSession);
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in Delete: {ex.Message}");
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
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
		[Authorize(Roles = "Player")]
		public APIResult Get_Playlists()
		{
			try
			{
				RouteHandler_GET_Playlists_Result result = new RouteHandler_GET_Playlists_Result();

				Response.StatusCode = (int)HttpStatusCode.OK;
				result.playlists = MatchmakingManager.GetPlaylists();
				
				return result;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in Get_Playlists: {ex.Message}");
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return new RouteHandler_GET_Playlists_Result();
			}
		}
	}
}
