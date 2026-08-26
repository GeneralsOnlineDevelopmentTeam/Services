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

namespace GenOnlineService.Controllers
{
	public class RouteHandler_GET_Rooms_Result : APIResult
	{
		public override Type GetReturnType() => GetType();

		public List<RoomData> rooms { get; set; } = [];
		public bool supports_moderation_commands { get; set; } = true;
		public bool supports_room_selection_results { get; set; } = true;
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class RoomsController : ControllerBase
	{
		[HttpGet(Name = "GetRooms")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher,Monitor")]
		public APIResult Get()
		{
			List<RoomData> rooms = RoomCatalog.Rooms.Select((room, index) => new RoomData
			{
				id = room.ID,
				name = room.Name,
				parent_id = room.ParentID,
				flags = index == 0
					? ERoomFlags.ROOM_FLAGS_SHOW_ALL_MATCHES
					: ERoomFlags.ROOM_FLAGS_NONE
			}).ToList();

			return new RouteHandler_GET_Rooms_Result { rooms = rooms };
		}
	}
}
