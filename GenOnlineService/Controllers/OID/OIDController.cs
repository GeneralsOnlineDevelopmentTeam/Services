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
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Claims;

namespace GenOnlineService.Controllers.LoginWithToken
{

	public class POST_OID_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(POST_OID_Result);
		}
		
		public string user_id { get; set; } = null; // string provides max compat
		public string display_name { get; set; } = null;
		public List<string> roles { get; set; } = new();
		public KnownClients.EKnownClients client_id { get; set; } = KnownClients.EKnownClients.unknown;
	}

	[ApiController]
	[Authorize(Roles = "Player")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class OID : ControllerBase
	{

		public OID()
		{

		}

		[HttpPost(Name = "PostOID")]
		public async Task<APIResult> Post()
		{
			// if we reach here, the token was valid
			POST_OID_Result result = new POST_OID_Result();

			Int64 user_id = TokenHelper.GetUserID(this);
			if (user_id != -1)
			{
				string strDisplayName = TokenHelper.GetDisplayName(this);
				KnownClients.EKnownClients client_id = TokenHelper.GetClientID(this);

				result.user_id = user_id.ToString();
				result.display_name = strDisplayName;
				result.roles = TokenHelper.GetRoles(this);
				result.client_id = client_id;
			}

			return result;
		}
	}

	[ApiController]
	[Authorize(Roles = "Player")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class ProvideMWToken : ControllerBase
	{

		public ProvideMWToken()
		{

		}

		public static string? GetClaimValue(ClaimsPrincipal principal, string claimType)
		{
			return principal.FindFirst(claimType)?.Value;
		}

		public static byte[] Base64UrlDecode(string input)
		{
			return Base64UrlEncoder.DecodeBytes(input);
		}

		// One shared client for JWKS fetches. A new HttpClient per validation exhausts sockets and
		// makes this endpoint trivially DoS-able.
		private static readonly Lazy<HttpClient> s_httpClient = new Lazy<HttpClient>(() =>
		{
			var handler = new SocketsHttpHandler
			{
				PooledConnectionLifetime = TimeSpan.FromMinutes(5),
				PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
			};

			return new HttpClient(handler)
			{
				Timeout = TimeSpan.FromSeconds(10)
			};
		}, true);

		private static readonly SemaphoreSlim s_jwksLock = new SemaphoreSlim(1, 1);
		private static Jwks? s_cachedJwks = null;
		private static DateTime s_cachedJwksExpiry = DateTime.MinValue;
		private static readonly TimeSpan s_jwksCacheDuration = TimeSpan.FromMinutes(10);

		private static async Task<Jwks?> GetJwks(string endpoint, bool bForceRefresh)
		{
			if (!bForceRefresh && s_cachedJwks != null && DateTime.UtcNow < s_cachedJwksExpiry)
			{
				return s_cachedJwks;
			}

			await s_jwksLock.WaitAsync();
			try
			{
				// another caller may have refreshed while we waited
				if (!bForceRefresh && s_cachedJwks != null && DateTime.UtcNow < s_cachedJwksExpiry)
				{
					return s_cachedJwks;
				}

				Jwks? jwks = await s_httpClient.Value.GetFromJsonAsync<Jwks>(endpoint);
				if (jwks != null)
				{
					s_cachedJwks = jwks;
					s_cachedJwksExpiry = DateTime.UtcNow.Add(s_jwksCacheDuration);
				}

				return jwks;
			}
			finally
			{
				s_jwksLock.Release();
			}
		}


	public async Task<ClaimsPrincipal> ValidateEpicJwtAsync(string jwt)
	{
		var handler = new JwtSecurityTokenHandler();
		var token = handler.ReadJwtToken(jwt);

		var kid = token.Header.Kid;
		if (kid == null)
			throw new SecurityTokenException("JWT missing kid header");

		// load settings
		IConfigurationSection? middlewareSettings = Program.g_Config.GetSection("Middleware");

		if (middlewareSettings == null)
		{
			throw new Exception("Middleware section missing in config");
		}

		string? middleware_jwks_endpoint = middlewareSettings.GetValue<string>("jwks_endpoint");
		string? middleware_audience = middlewareSettings.GetValue<string>("audience");
		string? middleware_issuer = middlewareSettings.GetValue<string>("issuer");

		if (middleware_jwks_endpoint == null)
		{
			throw new Exception("middleware_jwks_endpoint missing in config");
		}

		if (middleware_audience == null)
		{
			throw new Exception("middleware_audience missing in config");
		}

		if (middleware_issuer == null)
		{
			throw new Exception("middleware_issuer missing in config");
		}

		// get JWKS (cached; refreshed on an unknown kid in case of key rotation)
		var jwks = await GetJwks(middleware_jwks_endpoint, false);

		var key = jwks?.Keys?.FirstOrDefault(k => k.Kid == kid);
		if (key == null)
		{
			jwks = await GetJwks(middleware_jwks_endpoint, true);
			key = jwks?.Keys?.FirstOrDefault(k => k.Kid == kid);
		}

		if (key == null)
			throw new SecurityTokenException($"No matching JWKS key for kid={kid}");

		// build RSA pub key
		var rsa = RSA.Create();
		rsa.ImportParameters(new RSAParameters
		{
			Modulus = Base64UrlDecode(key.N),
			Exponent = Base64UrlDecode(key.E)
		});

		var validationParameters = new TokenValidationParameters
		{
			ValidateIssuer = true,
			ValidIssuer = middleware_issuer,

			ValidateAudience = true,
			ValidAudience = middleware_audience,

			ValidateLifetime = true,
			ClockSkew = TimeSpan.FromMinutes(2),

			ValidateIssuerSigningKey = true,
			IssuerSigningKey = new RsaSecurityKey(rsa)
			{
				KeyId = key.Kid
			}
		};

		return handler.ValidateToken(jwt, validationParameters, out _);
	}

	public class Jwks
	{
		public List<Jwk> Keys { get; set; }
	}

	public class Jwk
	{
		public string Kid { get; set; }
		public string Kty { get; set; }
		public string N { get; set; }
		public string E { get; set; }
}


		[HttpPost(Name = "ProvideMWToken")]
		public async Task Post()
		{
			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				};

				string jsonData = await reader.ReadToEndAsync();
				var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);

				if (data != null && !data.ContainsKey("mw_token"))
				{
					Response.StatusCode = (int)HttpStatusCode.Unauthorized;
				}
				else
				{
					string mw_token = data["mw_token"].ToString();

					ClaimsPrincipal validatedClaims = await ValidateEpicJwtAsync(mw_token);

					if (validatedClaims != null)
					{
						// read from the validated principal, never by re-parsing the raw token
						string? mwUserID = GetClaimValue(validatedClaims, "sub");
						if (String.IsNullOrEmpty(mwUserID))
						{
							Response.StatusCode = (int)HttpStatusCode.Unauthorized;
							return;
						}

						Int64 user_id = TokenHelper.GetUserID(this);
						EUserSessionType sessionType = TokenHelper.GetSessionType(this);
						if (user_id != -1 && SessionHelpers.SessionTypeHasAccessTo(sessionType, ESessionAccessType.Gameplay)) // only game clients should be doing middleware login
						{
							UserSession? session = WebSocketManager.GetSessionFromUser(user_id, sessionType);
							if (session != null)
							{
								session.SetMiddlewareID(mwUserID);
							}
						}
					}
				}
			}
		}
	}
}
