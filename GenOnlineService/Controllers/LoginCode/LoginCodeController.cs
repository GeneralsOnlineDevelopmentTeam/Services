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

using GenOnlineService;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MySqlX.XDevAPI.Common;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public class PendingLoginEntry
{
	public EPendingLoginState state { get; set; } = EPendingLoginState.Waiting;
	public Int64 user_id { get; set; } = -1;
	public DateTime created_at { get; set; } = DateTime.UtcNow;

	public bool IsExpired(DateTime utcNow)
	{
		return (utcNow - created_at) >= PendingLoginManager.PendingLoginLifetime;
	}
}

public static class PendingLoginManager
{
	// a login code is only valid for this long after it was issued
	public static readonly TimeSpan PendingLoginLifetime = TimeSpan.FromMinutes(5);

	private static ConcurrentDictionary<string, PendingLoginEntry> g_dictPendingLogins = new();

	public static bool AddPendingLogin(string strLoginCode)
	{
		// if an expired entry is squatting on this code, drop it first so the code can be reused
		RemoveIfExpired(strLoginCode);

		// we try add, if its already in there, dont nuke it, could be abused
		return g_dictPendingLogins.TryAdd(strLoginCode, new PendingLoginEntry());
	}

	public static PendingLoginEntry GetPendingLogin(string strLoginCode)
	{
		if (g_dictPendingLogins.TryGetValue(strLoginCode, out PendingLoginEntry pendingLoginInst))
		{
			if (pendingLoginInst.IsExpired(DateTime.UtcNow))
			{
				ConsumePendingLogin(strLoginCode);
				return null;
			}

			return pendingLoginInst;
		}

		return null;
	}

	public static bool ConsumePendingLogin(string strLoginCode)
	{
		return g_dictPendingLogins.TryRemove(strLoginCode, out PendingLoginEntry removedInst);
	}

	public static bool UpdatePendingLogin(string strLoginCode, EPendingLoginState newState, Int64 userIDIfComplete = -1)
	{
		if (g_dictPendingLogins.TryGetValue(strLoginCode, out PendingLoginEntry pendingLoginInst))
		{
			// expired codes can't be completed
			if (pendingLoginInst.IsExpired(DateTime.UtcNow))
			{
				ConsumePendingLogin(strLoginCode);
				return false;
			}

			// must be pending
			if (pendingLoginInst.state == EPendingLoginState.Waiting)
			{
				pendingLoginInst.state = newState;
				pendingLoginInst.user_id = userIDIfComplete;
				return true;
			}

			return false;
		}

		return false;
	}

	public static int CleanupExpiredLogins()
	{
		DateTime utcNow = DateTime.UtcNow;
		int numRemoved = 0;

		foreach (var kvp in g_dictPendingLogins)
		{
			if (kvp.Value.IsExpired(utcNow))
			{
				if (g_dictPendingLogins.TryRemove(kvp))
				{
					++numRemoved;
				}
			}
		}

		return numRemoved;
	}

	private static void RemoveIfExpired(string strLoginCode)
	{
		if (g_dictPendingLogins.TryGetValue(strLoginCode, out PendingLoginEntry pendingLoginInst))
		{
			if (pendingLoginInst.IsExpired(DateTime.UtcNow))
			{
				g_dictPendingLogins.TryRemove(new KeyValuePair<string, PendingLoginEntry>(strLoginCode, pendingLoginInst));
			}
		}
	}
}

namespace GenOnlineService.Controllers
{

	public class GET_LoginCode_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(GET_LoginCode_Result);
		}

		public bool success { get; set; } = false;
		public string login_code { get; set; } = "";
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class LoginCode : ControllerBase
	{

		[HttpPost]
		public async Task Post([FromHeader(Name = "X-Api-Key")] string apiKey)
		{
			if (string.IsNullOrEmpty(apiKey))
			{
				Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				return;
			}

			if (!APIKeyHelpers.ValidateKey(apiKey, EApiKeyType.WebServerKey))
			{
				Response.StatusCode = (int)HttpStatusCode.Forbidden;
				return;
			}

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

					if (data != null && data.ContainsKey("env") && data.ContainsKey("code") && data.ContainsKey("user_id") && data.ContainsKey("success"))
					{
						try
						{
							string loginEnv = data["env"].GetString();
							string gameCode = data["code"].GetString();
							Int64 user_id = data["user_id"].GetInt64();
							bool success = data["success"].GetBoolean();

							bool bUpdated = false;

							// update it, CheckLogin will consume it
							// NOTE: we dont care about env here, because we are only running in one env by the time the server starts running. Env is passed for development workflow purposes only.
							if (success)
							{
								bUpdated = PendingLoginManager.UpdatePendingLogin(gameCode, EPendingLoginState.LoginSuccess, user_id);
							}
							else
							{
								bUpdated = PendingLoginManager.UpdatePendingLogin(gameCode, EPendingLoginState.LoginFailed);
							}

							if (!bUpdated)
							{
								Response.StatusCode = (int)HttpStatusCode.NotFound;
							}

						}
						catch
						{
							Response.StatusCode = (int)HttpStatusCode.PreconditionFailed;
						}
					}
					else
					{
						Response.StatusCode = (int)HttpStatusCode.Unauthorized;
					}
				}
				catch
				{
					Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				}
			}
		}

		[HttpGet]
		public async Task<APIResult> Get()
		{
			GET_LoginCode_Result result = new GET_LoginCode_Result();

			result.success = true;
			result.login_code = GenerateLoginCode();

			PendingLoginManager.AddPendingLogin(result.login_code);

			return result;
		}

		private static string GenerateLoginCode()
		{
			const int loginCodeLength = 32;
			const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
			var bytes = new byte[loginCodeLength];
			var result = new StringBuilder(loginCodeLength);

			using (var rng = RandomNumberGenerator.Create())
			{
				rng.GetBytes(bytes);
			}

			foreach (var b in bytes)
			{
				result.Append(chars[b % chars.Length]);
			}

			return result.ToString().ToUpper();
		}
	}
}
