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
		private readonly ILogger<PlayerStatsController> _logger;

		public PlayerStatsController(ILogger<PlayerStatsController> logger)
		{
			_logger = logger;
		}

		[HttpGet("{userID}")]
		[Authorize(Roles = "Player,Monitor")]
		public async Task<APIResult> Get(Int64 userID)
		{
			try
			{
				// TODO_ASP: Set error codes properly in all places (and use variable, not magic numbers)
				RouteHandler_GET_PlayerStats_Result result = new RouteHandler_GET_PlayerStats_Result();
				result.stats = new PlayerStats(userID, EloConfig.BaseRating, 0); // return 0s by default, incase client tries to use it

				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				};

				// get from cache
				UserSession? userSession = WebSocketManager.GetDataFromUser(userID);

				// if user is offline, hit DB, could be a friends list inspection for example
				if (userSession == null)
				{
					PlayerStats playerStats = await Database.Functions.Auth.GetPlayerStats(GlobalDatabaseInstance.g_Database, userID);

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
				else if (userSession.GameStats == null) // if the session exists but no stats exist, this is a problem
				{
					Response.StatusCode = (int)HttpStatusCode.NotFound;
					return result;
				}

				result.stats = userSession.GameStats;
				return result;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in PlayerStats Get: {ex.Message}");
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return new RouteHandler_GET_PlayerStats_Result() { stats = new PlayerStats(userID, EloConfig.BaseRating, 0) };
			}
		}

        // Bulk endpoint
        [HttpPost("Batch")]
        [Authorize(Roles = "Player,Monitor")]
        public async Task<APIResult> PostBatched()
        {
			try
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
						// get from cache
						UserSession? userSession = WebSocketManager.GetDataFromUser(userID);

						// NOTE: Batch is only supported for ONLINE users, DB will never be looked up
						if (userSession != null)
						{
							if (userSession.GameStats != null)
							{
								result.stats.Add(userSession.GameStats);

							}
						}
					}
				}

				return result;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in PostBatched: {ex.Message}");
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				return new RouteHandler_GET_PlayerStatsBatch_Result() { stats = new List<PlayerStats>() };
			}
        }

        [HttpPut]
		[Authorize(Roles = "Player")]
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

					if (user_id != -1)
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
												UserSession? sourceSession = WebSocketManager.GetDataFromUser(user_id);

												if (sourceSession != null)
												{
#pragma warning disable CS8602 // Dereference of a possibly null reference.
													sourceSession.GameStats.ProcessFromDB((EStatIndex)stat_id, statValInt);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
												}
											}

											await Database.Functions.Auth.UpdatePlayerStat(GlobalDatabaseInstance.g_Database, user_id, stat_id, statValInt);
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
