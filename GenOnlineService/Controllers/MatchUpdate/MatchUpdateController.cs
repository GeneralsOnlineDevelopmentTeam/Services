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

using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public class RouteHandler_PUT_MatchUpdate_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}
	}

	public class MatchHistoryCollection : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public List<MatchHistory_Entry> matches { get; private set; } = new List<MatchHistory_Entry>();
	}

	public class MatchHistory_Entry
	{
		public MatchHistory_Entry(
		Int64 match_id,
		Int64 owner_user_id,
		string lobby_name,
		bool is_finished,
		string time_started,
		string time_finished,
		string map_name,
		string map_path,
		string match_roster_type,
		ELobbyType? lobby_type,
		bool is_official_map,
		bool vanilla_teams_only,
		UInt32 starting_cash,
		bool is_limit_superweapons,
		bool is_tracking_stats,
		bool allow_observers,
		UInt16 max_camera_height,
		UInt32 exe_crc,
		UInt32 ini_crc
		)
		{
			this.match_id = match_id;
			this.owner_user_id = owner_user_id;
			this.lobby_name = lobby_name;
			this.is_finished = is_finished;
			this.time_started = time_started;
			this.time_finished = time_finished;
			this.map_name = map_name;
			this.map_path = map_path;
			this.match_roster_type = match_roster_type;
			this.lobby_type = lobby_type;
			this.is_official_map = is_official_map;
			this.vanilla_teams_only = vanilla_teams_only;
			this.starting_cash = starting_cash;
			this.is_limit_superweapons = is_limit_superweapons;
			this.is_tracking_stats = is_tracking_stats;
			this.allow_observers = allow_observers;
			this.max_camera_height = max_camera_height;
			this.exe_crc = exe_crc;
			this.ini_crc = ini_crc;
		}

		public Int64 match_id { get; set; } = -1;
		public Int64 owner_user_id { get; set; } = -1;
		public string lobby_name { get; set; } = String.Empty;
		public bool is_finished { get; set; } = false;
		public string time_started { get; set; } = String.Empty;
		public string time_finished { get; set; } = String.Empty;
		public string map_name { get; set; } = String.Empty;
		public string map_path { get; set; } = String.Empty;

		public string match_roster_type { get; set; } = String.Empty;
		public ELobbyType? lobby_type { get; set; } = null;
		public bool is_official_map { get; set; } = false;
		public bool vanilla_teams_only { get; set; } = false;
		public UInt32 starting_cash { get; set; } = 0;
		public bool is_limit_superweapons { get; set; } = false;
		public bool is_tracking_stats { get; set; } = false;
		public bool allow_observers { get; set; } = false;
		public UInt16 max_camera_height { get; set; } = 0;
		public UInt32 exe_crc { get; set; } = 0;
		public UInt32 ini_crc { get; set; } = 0;

		public List<MatchdataMemberModel?> members { get; private set; } = new();
	}

	// Match history list API
	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/MatchHistory")]
	public class API_MatchHistoryController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> _dbFactory;
		public API_MatchHistoryController(IDbContextFactory<AppDbContext> dbFactory)
		{
			_dbFactory = dbFactory;
		}

		[HttpGet("{startingMatchID}")]
		// TODO: Move to Authorize for this
		public async Task<APIResult> GetHistorySinceMatchID([FromHeader(Name = "X-Api-Key")] string apiKey, Int64 startingMatchID)
		{
			RouteHandler_Get_MatchHistory_Result result = new RouteHandler_Get_MatchHistory_Result();

			if (string.IsNullOrEmpty(apiKey))
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				return result;
			}

			if (!APIKeyHelpers.ValidateKey(apiKey, EApiKeyType.PartnerKey))
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return result;
			}

			const Int64 maxLobbiesPerRequest = 99; // actually 100, but query is <= 

			await using var db = await _dbFactory.CreateDbContextAsync();
			result.matches = await Database.MatchHistory.GetMatchesInRange(db, startingMatchID, startingMatchID + maxLobbiesPerRequest);

			return result;
		}

		[HttpGet("since/{since}")]
		// TODO: Move to Authorize for this
		public async Task<APIResult> GetHistorySinceDateTime([FromHeader(Name = "X-Api-Key")] string apiKey, DateTime since)
		{
			RouteHandler_Get_MatchHistory_Result result = new RouteHandler_Get_MatchHistory_Result();

			if (string.IsNullOrEmpty(apiKey))
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				return result;
			}

			if (!APIKeyHelpers.ValidateKey(apiKey, EApiKeyType.PartnerKey))
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return result;
			}

			const int maxLobbiesPerRequest = 500;

			await using var db = await _dbFactory.CreateDbContextAsync();
			result.matches = await Database.MatchHistory.GetMatchesSince(db, since, maxLobbiesPerRequest);

			return result;
		}

		[HttpGet]
		// TODO: Move to Authorize for this
		public async Task<APIResult> GetHighestMatchID([FromHeader(Name = "X-Api-Key")] string apiKey)
		{
			RouteHandler_Get_MatchHistory_HighestMatchID_Result result = new RouteHandler_Get_MatchHistory_HighestMatchID_Result();

			if (string.IsNullOrEmpty(apiKey))
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				return result;
			}

			if (!APIKeyHelpers.ValidateKey(apiKey, EApiKeyType.PartnerKey))
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return result;
			}

			await using var db = await _dbFactory.CreateDbContextAsync();
			result.highest_match_id = await Database.MatchHistory.GetHighestMatchID(db);
			return result;
		}
	}
}
