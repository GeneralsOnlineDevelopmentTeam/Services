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
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenOnlineService.Controllers
{
	[ApiController]
	[Authorize(Roles = "GameClient,Monitor")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class ServiceConfigController : ControllerBase
	{
		public ServiceConfigController()
		{
			
		}

		[HttpGet(Name = "GetServiceConfig")]

		public async Task<string?> Get()
		{
			try
			{
				string strFileData = await System.IO.File.ReadAllTextAsync(Path.Combine("data", "serviceconfig.json"));

				JsonNode? configNode = JsonNode.Parse(strFileData);
				if (configNode is JsonObject configObject)
				{
					// 0 = library default, 1 = native ICE, 2 = WebRTC
					int iceImplementation = Program.g_Config?.GetSection("Core").GetValue("ice_implementation", 2) ?? 2;

					if (Int64.TryParse(this.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out Int64 userId))
					{
						List<Int64> lstTesterIDs = Program.g_Config?.GetSection("Core").GetSection("ice_implementation_testers").Get<List<Int64>>() ?? new List<Int64>();
						if (lstTesterIDs.Contains(userId))
						{
							iceImplementation = Program.g_Config?.GetSection("Core").GetValue("ice_implementation_testers_value", 1) ?? 1;
						}
					}

					configObject["ice_implementation"] = iceImplementation;
					strFileData = configObject.ToJsonString();
				}

				Response.StatusCode = (int)HttpStatusCode.OK;
				return strFileData;
			}
			catch
			{
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return null;
			}
		}
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class AnticheatConfigController : ControllerBase
	{
		public AnticheatConfigController()
		{

		}

		[HttpGet(Name = "GetAnticheatConfig")]

		public async Task<string?> Get()
		{
			try
			{
				string strFileData = await System.IO.File.ReadAllTextAsync(Path.Combine("data", "anticheatconfig.dat"));

				// 0 = normal behavior
				// 1 = force goac
				// 2 = force eac

				Response.StatusCode = (int)HttpStatusCode.OK;
				return strFileData;
			}
			catch
			{
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return null;
			}
		}
	}
}
