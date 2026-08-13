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
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace GenOnlineService.Controllers.RefreshToken
{
	public class POST_RefreshToken_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(POST_RefreshToken_Result);
		}

		public EPendingLoginState result { get; set; } = EPendingLoginState.None;
		public string session_token { get; set; } = "";
		public string refresh_token { get; set; } = "";
		public Int64 user_id { get; set; } = -1;
		public string display_name { get; set; } = "";
	}

	// Pure token rotation. Unlike LoginWithToken this does NOT establish a session - the caller's
	// websocket, lobby membership and matchmaking state are left completely untouched, so a client
	// can renew an expiring session token mid-lobby or mid-match without being treated as having
	// disconnected. Clients that are (re)connecting from cold should still use LoginWithToken.
	[ApiController]
	[Authorize(Roles = "GameClient,ChatClient,GameLauncher")]
	[RefreshTokenEndpoint]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class RefreshTokenController : ControllerBase
	{
		private readonly IDbContextFactory<AppDbContext> _dbFactory;

		public RefreshTokenController(IDbContextFactory<AppDbContext> dbFactory)
		{
			_dbFactory = dbFactory;
		}

		[HttpPost(Name = "PostRefreshToken")]
		public async Task<APIResult> Post()
		{
			return await Post_InternalHandler(IPHelpers.NormalizeIP(HttpContext.Connection.RemoteIpAddress?.ToString()));
		}

		public async Task<APIResult> Post_InternalHandler(string ipAddr)
		{
			POST_RefreshToken_Result result = new POST_RefreshToken_Result();

			try
			{
				// The refresh token that got us here was already fully validated by the global auth
				// pipeline (type, endpoint, ban, generation and jti rotation checks).
				KnownClients.EKnownClients clientID = TokenHelper.GetClientID(this);
				if (clientID == KnownClients.EKnownClients.unknown)
				{
					result.result = EPendingLoginState.LoginFailed;
					Response.StatusCode = (int)HttpStatusCode.Unauthorized;
					return result;
				}

				if (Program.g_tokenGenerator == null)
				{
					result.result = EPendingLoginState.LoginFailed;
					Response.StatusCode = (int)HttpStatusCode.Unauthorized;
					return result;
				}

				Int64 user_id = TokenHelper.GetUserID(this);
				EUserSessionType sessionType = TokenHelper.GetSessionType(this);

				if (user_id == -1 || sessionType == EUserSessionType.None)
				{
					result.result = EPendingLoginState.LoginFailed;
					Response.StatusCode = (int)HttpStatusCode.Unauthorized;
					return result;
				}

				await using var db = await _dbFactory.CreateDbContextAsync();

				// re-check the ban on every rotation so a ban applied since the last refresh takes
				// effect immediately rather than waiting for the periodic reconcile
				bool bIsBanned = await Database.Users.IsUserBanned(db, user_id);
				if (bIsBanned)
				{
					// kill every token they hold, not just this request
					await TokenRevocationManager.RevokeAllTokensForUser(user_id, "user is banned");

					result.result = EPendingLoginState.LoginFailed;
					Response.StatusCode = (int)HttpStatusCode.Locked;
					return result;
				}

				string strDisplayName = await Database.Users.GetDisplayName(db, user_id);
				bool bIsAdmin = await Database.Users.IsUserAdmin(db, user_id);

				var sessiontoken = Program.g_tokenGenerator.GenerateToken(strDisplayName, user_id, ipAddr, Program.JwtTokenGenerator.ETokenType.Session, clientID, sessionType, bIsAdmin);
				var refreshtoken = Program.g_tokenGenerator.GenerateToken(strDisplayName, user_id, ipAddr, Program.JwtTokenGenerator.ETokenType.Refresh, clientID, sessionType, false, out string refreshJti);

				// rotation: only this refresh token is accepted from now on
				await TokenRevocationManager.OnTokensIssued(user_id, sessionType, refreshJti);

				result.result = EPendingLoginState.LoginSuccess;
				result.session_token = sessiontoken;
				result.refresh_token = refreshtoken;
				result.user_id = user_id;
				result.display_name = strDisplayName;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] RefreshToken failed: {ex.Message}");
				SentrySdk.CaptureException(ex);

				result.result = EPendingLoginState.LoginFailed;
				Response.StatusCode = (int)HttpStatusCode.InternalServerError;
			}

			return result;
		}
	}
}
