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
using MySqlX.XDevAPI.Common;
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

				result.user_id = user_id.ToString();
				result.display_name = strDisplayName;
			}

			return result;
		}
	}

	[ApiController]
	[Authorize(Roles = "Player")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class ProvideMWToken : ControllerBase
	{
		private static readonly HttpClient s_httpClient = new HttpClient 
		{ 
			Timeout = TimeSpan.FromSeconds(10) 
		};

		public ProvideMWToken()
		{

		}

		public static string GetClaimValue(string jwtToken, string claimType)
		{
			var handler = new JwtSecurityTokenHandler();
			var token = handler.ReadJwtToken(jwtToken); // Parses the token into JwtSecurityToken
			var claim = token.Claims.FirstOrDefault(c => c.Type == claimType);
			return claim?.Value;
		}

		public static byte[] Base64UrlDecode(string input)
		{
			return Base64UrlEncoder.DecodeBytes(input);
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

		// get JWKS
		var jwks = await s_httpClient.GetFromJsonAsync<Jwks>(middleware_jwks_endpoint);

		var key = jwks.Keys.FirstOrDefault(k => k.Kid == kid);
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
				else if (data != null && data.ContainsKey("mw_token"))
				{
					string mw_token = data["mw_token"].ToString();

					ClaimsPrincipal validatedClaims = await ValidateEpicJwtAsync(mw_token);

					if (validatedClaims != null)
					{
						string mwUserID = GetClaimValue(mw_token, "sub");

						Int64 user_id = TokenHelper.GetUserID(this);

						if (user_id != -1)
						{
							UserSession? session = WebSocketManager.GetDataFromUser(user_id);
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
