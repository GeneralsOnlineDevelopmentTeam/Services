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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public class RouteHandler_GET_PlayerStats_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public PlayerStats? stats { get; set; } = null;
	}

    public class RouteHandler_GET_PlayerStatsBatch_Result : APIResult
    {
        public override Type GetReturnType()
        {
            return this.GetType();
        }

        public List<PlayerStats?> stats { get; set; } = null;
    }

    public class RouteHandler_GET_PlayerStatsBatch_Input
    {
        public List<Int64> user_ids { get; set; } = null;
    }

    public class RouteHandler_PUT_PlayerStats_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public PlayerStats? stats { get; set; } = null;
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class PlayerStatsController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> _dbFactory;
		private readonly ILogger<PlayerStatsController> _logger;

		public PlayerStatsController(IDbContextFactory<AppDbContext> dbFactory, ILogger<PlayerStatsController> logger)
		{
			_logger = logger;
			_dbFactory = dbFactory;
		}

		[HttpGet("{userID}")]
		[Authorize(Roles = "Player,Monitor")]
		public async Task<APIResult> Get(Int64 userID)
		{
			// TODO_ASP: Set error codes properly in all places (and use variable, not magic numbers)
			RouteHandler_GET_PlayerStats_Result result = new RouteHandler_GET_PlayerStats_Result();
			result.stats = new PlayerStats(userID, EloConfig.BaseRating, 0, EloConfig.BaseRating); // return 0s by default, incase client tries to use it

			var options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			};

			// get from cache (just get any user, all sessions will have stats stored against them)
			SharedUserData? userData = WebSocketManager.GetSharedDataForUser(userID);

			// if user is offline, hit DB, could be a friends list inspection for example
			if (userData == null)
			{
				await using var db = await _dbFactory.CreateDbContextAsync();
				PlayerStats playerStats = await Database.UserStats.GetPlayerStats(db, userID);

				if (playerStats == null)
				{
					Response.StatusCode = (int)HttpStatusCode.NotFound;
				}
				else
				{
					result.stats = playerStats;
				}

				return result;
			}
			else if (userData.GameStats == null) // if the session exists but no stats exist, this is a problem
			{
				Response.StatusCode = (int)HttpStatusCode.NotFound;
				return result;
			}

			result.stats = userData.GameStats;
			return result;
		}

        // Bulk endpoint
        [HttpPost("Batch")]
        [Authorize(Roles = "GameClient,ChatClient,GameLauncher,Monitor")]
        public async Task<APIResult> PostBatched()
        {
            RouteHandler_GET_PlayerStatsBatch_Result result = new RouteHandler_GET_PlayerStatsBatch_Result();
            result.stats = new List<PlayerStats>(); // return 0s by default, incase client tries to use it

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();

                RouteHandler_GET_PlayerStatsBatch_Input inputData = JsonSerializer.Deserialize<RouteHandler_GET_PlayerStatsBatch_Input>(jsonData, options);

                // process each user
                foreach (Int64 userID in inputData.user_ids)
                {
					// get all sessions for this user
					SharedUserData userData = WebSocketManager.GetSharedDataForUser(userID);

					// NOTE: Batch is only supported for ONLINE users, DB will never be looked up
					if (userData != null)
					{
						if (userData.GameStats != null)
						{
							result.stats.Add(userData.GameStats);

						}
					}
				}
            }

            return result;
        }

        [HttpPut]
		[Authorize(Roles = "GameClient")]
		public async Task<APIResult> Put()
		{
			RouteHandler_PUT_PlayerStats_Result result = new RouteHandler_PUT_PlayerStats_Result();

			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				};

				try
				{
					Int64 user_id = TokenHelper.GetUserID(this);

					EUserSessionType sessionType = TokenHelper.GetSessionType(this);
					if (user_id != -1 && SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.Gameplay))
					{
						List<JsonElement>? jsonReqData = JsonSerializer.Deserialize<List<JsonElement>>(jsonData, options);

						// TODO: Update client so this is an associative array and not just in order...
						if (jsonReqData != null)
						{
							int stat_id = 0;
							foreach (JsonElement elem in jsonReqData)
							{
								// TODO: do we care about the string stats? they dont seem relevant, its things like system spec
								try
								{
									if (elem.ValueKind != JsonValueKind.Null)
									{
										if (elem.TryGetInt32(out int statValInt))
										{
											// update cache too
											if (user_id != -1)
											{
												SharedUserData? userData = WebSocketManager.GetSharedDataForUser(user_id);

												if (userData != null)
												{
#pragma warning disable CS8602 // Dereference of a possibly null reference.
													userData.GameStats.ProcessFromDB((EStatIndex)stat_id, statValInt);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
												}
											}

											await using var db = await _dbFactory.CreateDbContextAsync();
											await Database.UserStats.UpdatePlayerStat(db, user_id, stat_id, statValInt);
											//Console.WriteLine("Stat {0} is valid and is {1}", (EStatIndex)stat_id, statValInt);
											// game tracks the progress, so these are full writes, not incremental

										}
									}
								}
								catch
								{
									//Console.WriteLine("Stat {0} is invalid and is {1}", (EStatIndex)stat_id, elem.ToString());
								}

								++stat_id;
							}
						}
						else
						{
							Response.StatusCode = (int)HttpStatusCode.InternalServerError;
						}
					}
					else
					{
						Response.StatusCode = (int)HttpStatusCode.InternalServerError;
					}
				}
				catch
				{
					return result;
				}
			}

			return result;
		}
	}
}
