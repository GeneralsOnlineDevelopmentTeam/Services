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
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public class RouteHandler_GET_Rooms_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public List<RoomData>? rooms { get; set; } = null;
		public bool supports_moderation_commands { get; set; } = true;
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class RoomsController : ControllerBase
	{
		private readonly ILogger<RoomsController> _logger;

		public RoomsController(ILogger<RoomsController> logger)
		{
			_logger = logger;
		}

		[HttpGet(Name = "GetRooms")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher,Monitor")]
		public async Task<APIResult> Get()
		{
			RouteHandler_GET_Rooms_Result result = new RouteHandler_GET_Rooms_Result();

			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				};

				try
				{
					string strFileData = await System.IO.File.ReadAllTextAsync(Path.Combine("data", "rooms.json"));
					List<RoomData>? lstRooms = JsonSerializer.Deserialize<List<RoomData>>(strFileData, options);
					result.rooms = lstRooms;
				}
				catch
				{
					return result;
				}

				return result;
			}
		}
	}
}
