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

using Google.Protobuf.WellKnownTypes;
using MaxMind.GeoIP2;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Sentry;
using System.Collections.Concurrent;
using System.Configuration;
using System.Drawing;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using System.Xml;

namespace GenOnlineService
{
	public static class IPHelpers
	{
		public static string NormalizeIP(string? ipAddress)
		{
			if (string.IsNullOrEmpty(ipAddress))
			{
				return "unknown";
			}

			if (System.Net.IPAddress.TryParse(ipAddress, out System.Net.IPAddress? addr))
			{
				// Convert IPv6-mapped IPv4 (::ffff:127.0.0.1) to IPv4 (127.0.0.1)
				if (addr.IsIPv4MappedToIPv6)
				{
					return addr.MapToIPv4().ToString();
				}

				// Treat all localhost addresses as 127.0.0.1
				if (System.Net.IPAddress.IsLoopback(addr))
				{
					return "127.0.0.1";
				}

				return addr.ToString();
			}

			return ipAddress;
		}
	}

	public static class SecretComparer
	{
		// Constant-time string comparison so an attacker can't learn a secret from response timing.
		public static bool FixedTimeEquals(string? a, string? b)
		{
			if (a == null || b == null)
			{
				return false;
			}

			byte[] bytesA = Encoding.UTF8.GetBytes(a);
			byte[] bytesB = Encoding.UTF8.GetBytes(b);

			return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
		}
	}

	public static class APIKeyHelpers
	{
		private static List<string>? s_cachedApiKeys = null;
		private static readonly object s_cacheLock = new object();

		public static bool ValidateKey(string strKey)
		{
			if (Program.g_Config == null)
			{
				return false;
			}

			if (s_cachedApiKeys == null)
			{
				lock (s_cacheLock)
				{
					if (s_cachedApiKeys == null)
					{
						IConfiguration? apiSettings = Program.g_Config.GetSection("API");
						if (apiSettings == null)
						{
							return false;
						}

						List<string>? api_keys = apiSettings.GetSection("keys").Get<List<string>>();
						if (api_keys == null)
						{
							return false;
						}

						s_cachedApiKeys = api_keys.Select(k => k.ToUpperInvariant()).ToList();
					}
				}
			}

			string strKeyUpper = strKey.ToUpperInvariant();

			// Compare against every key without short-circuiting, so timing doesn't leak which key
			// (or how much of a key) matched.
			bool bMatched = false;
			foreach (string strKnownKey in s_cachedApiKeys)
			{
				bMatched |= SecretComparer.FixedTimeEquals(strKnownKey, strKeyUpper);
			}

			return bMatched;
		}
	}

	// Shared-key credential for the priority scheduler (config WsBot:api_key, presented as
	// "Authorization: Discord <key>").
	public static class WsBotKeyValidator
	{
		public static bool ValidateKey(string? suppliedKey)
		{
			if (string.IsNullOrEmpty(suppliedKey) || Program.g_Config == null)
			{
				return false;
			}

			string? expectedKey = Program.g_Config.GetSection("WsBot").GetValue<string>("api_key");
			if (string.IsNullOrEmpty(expectedKey))
			{
				return false;
			}

			// Fixed-time so a wrong key doesn't leak how many leading bytes were right.
			return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
				Encoding.UTF8.GetBytes(suppliedKey),
				Encoding.UTF8.GetBytes(expectedKey));
		}
	}

	// Its own scheme because the caller is a back-end service, not a player, and carries no game-client JWT.
	public class DiscordAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
	{
		public DiscordAuthenticationHandler(
			IOptionsMonitor<AuthenticationSchemeOptions> options,
			ILoggerFactory logger,
			UrlEncoder encoder,
			TimeProvider timeProvider)
			: base(options, logger, encoder) { }

		protected override Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			if (!Request.Headers.ContainsKey("Authorization"))
				return Task.FromResult(AuthenticateResult.Fail("Missing Authorization Header"));

			try
			{
				string? authHeader = Request.Headers["Authorization"].FirstOrDefault();
				if (authHeader == null || !authHeader.StartsWith("Discord ", StringComparison.OrdinalIgnoreCase))
				{
					return Task.FromResult(AuthenticateResult.Fail("Invalid Authorization Header"));
				}

				string suppliedKey = authHeader.Substring("Discord ".Length).Trim();
				if (!WsBotKeyValidator.ValidateKey(suppliedKey))
				{
					return Task.FromResult(AuthenticateResult.Fail("Invalid Discord key"));
				}

				var claims = new[] { new Claim(ClaimTypes.Name, "wsbot"), new Claim(ClaimTypes.Role, "WsBot") };
				var identity = new ClaimsIdentity(claims, Scheme.Name);
				var principal = new ClaimsPrincipal(identity);
				var ticket = new AuthenticationTicket(principal, Scheme.Name);

				return Task.FromResult(AuthenticateResult.Success(ticket));
			}
			catch
			{
				return Task.FromResult(AuthenticateResult.Fail("Invalid Authorization Header"));
			}
		}
	}
	public static class CertHelpers
	{
		public static X509Certificate2 LoadPemWithPrivateKey(string certPath, string keyPath)
		{
			using (var certReader = new StreamReader(certPath))
			using (var keyReader = new StreamReader(keyPath))
			{
				var pemCertReader = new PemReader(certReader);
				var pemKeyReader = new PemReader(keyReader);

				var certificate = (Org.BouncyCastle.X509.X509Certificate)pemCertReader.ReadObject();
				var privateKey = (AsymmetricKeyParameter)pemKeyReader.ReadObject();

				//var store = new Pkcs12Store();
				var store = new Pkcs12StoreBuilder().Build(); // Fix for CS1729  
				var certEntry = new X509CertificateEntry(certificate);
				store.SetCertificateEntry("cert", certEntry);
				store.SetKeyEntry("key", new AsymmetricKeyEntry(privateKey), new[] { certEntry });

				using (var ms = new MemoryStream())
				{
					store.Save(ms, new char[0], new SecureRandom());
#pragma warning disable SYSLIB0057 // Type or member is obsolete
					return new X509Certificate2(ms.ToArray(), string.Empty);
#pragma warning restore SYSLIB0057 // Type or member is obsolete
				}
			}
		}
	}


	public class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
	{
		public BasicAuthenticationHandler(
			IOptionsMonitor<AuthenticationSchemeOptions> options,
			ILoggerFactory logger,
			UrlEncoder encoder,
			TimeProvider timeProvider)
			: base(options, logger, encoder) { }

		protected override Task<AuthenticateResult> HandleAuthenticateAsync()
		{
			if (!Request.Headers.ContainsKey("Authorization"))
				return Task.FromResult(AuthenticateResult.Fail("Missing Authorization Header"));

			try
			{
				var authType = Request.Headers["Authorization"].FirstOrDefault()?.Split(" ").First();
				var token = Request.Headers["Authorization"].FirstOrDefault()?.Split(" ").Last();

				if (authType != null && token != null)
				{
					// monitor (basic auth)
					if (authType.ToLower() == "basic")
					{
						var decodedBytes = Convert.FromBase64String(token);
						var decodedCredentials = Encoding.UTF8.GetString(decodedBytes);

						var parts = decodedCredentials.Split(':', 2);
						if (parts.Length == 2)
						{
							string strUsername = parts[0];
							string strPassword = parts[1];

							IConfigurationSection? monitorSettings = Program.g_Config.GetSection("Monitor");

							if (monitorSettings == null)
							{
								throw new Exception("Monitor section missing in config");
							}

							string? monitorUsername = monitorSettings.GetValue<string>("username");
							string? monitorPassword = monitorSettings.GetValue<string>("password");

							if (monitorUsername == null)
							{
								throw new Exception("Monitor Username missing in config");
							}

							if (monitorPassword == null)
							{
								throw new Exception("Monitor Password missing in config");
							}

							if (SecretComparer.FixedTimeEquals(monitorUsername, strUsername)
								& SecretComparer.FixedTimeEquals(monitorPassword, strPassword))
							{
								var claims = new[] { new Claim(ClaimTypes.Name, strUsername), new Claim(ClaimTypes.Role, "Monitor") };
								var identity = new ClaimsIdentity(claims, "MonitorToken");
								var principal = new ClaimsPrincipal(identity);
								var ticket = new AuthenticationTicket(principal, Scheme.Name);

								return Task.FromResult(AuthenticateResult.Success(ticket));
							}
							else
							{
								Response.StatusCode = 401;
								return Task.FromResult(AuthenticateResult.Fail("Invalid Authorization Header"));
							}
						}
					}

					// shouldnt get here
					Response.StatusCode = 401;
					return Task.FromResult(AuthenticateResult.Fail("Authorization Failed"));
				}
				else
				{
					Response.StatusCode = 401;
					return Task.FromResult(AuthenticateResult.Fail("Invalid Authorization Data"));
				}
			}
			catch
			{
				Response.StatusCode = 401;
				return Task.FromResult(AuthenticateResult.Fail("Invalid Authorization Header"));
			}
		}
	}

	public static class StatsTracker
	{
		public static async Task Update(int numLobbies, int numPlayers)
		{
			int hourOfDay = DateTime.Now.Hour;
			// store stats

			using var scope = ServiceLocator.Services.CreateScope();
			var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
			await using var db = await factory.CreateDbContextAsync();
			await Database.ServiceStats.CommitStats(db, DateTime.Now.DayOfYear, hourOfDay, numPlayers, numLobbies);
		}
	}

	// Marks the endpoints that accept refresh tokens (LoginWithToken and RefreshToken). Used instead
	// of matching on the request path so a future route whose path happens to contain "loginwithtoken"
	// can't silently accept them.
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
	public sealed class RefreshTokenEndpointAttribute : Attribute
	{
	}

	public static class TokenHelper
	{
		public static Int64 GetUserID(ControllerBase controller)
		{
			if (controller.User.IsInRole("Monitor"))
			{
				return -1;
			}

			var claim = controller.User.FindFirst(ClaimTypes.NameIdentifier);
			if (claim == null || !Int64.TryParse(claim.Value, out Int64 userId))
				return -1;

			return userId;
		}

		public static List<string> GetRoles(ControllerBase controller)
		{
			var roles = controller.User.Claims.Where(c => c.Type == ClaimTypes.Role || c.Type == "role").Select(c => c.Value).ToList();
			return roles;
		}

		public static bool IsAdmin(ControllerBase controller)
		{
			return controller.User.IsInRole("Admin");
		}

		// Signed at login from users.user_priority - a client cannot forge it.
		public static EUserPriority GetUserPriority(ControllerBase controller)
		{
			var claim = controller.User.FindFirst("priority");
			if (claim != null && Int32.TryParse(claim.Value, out int value))
			{
				return (EUserPriority)value;
			}

			return EUserPriority.None;
		}

		public static KnownClients.EKnownClients GetClientID(ControllerBase controller)
		{
			var first = controller.User.FindFirst("client_id");

			if (first == null)
				return KnownClients.EKnownClients.unknown;

			if (int.TryParse(first.Value, out int clientIDInt32))
			{
				// Validate if the int corresponds to a defined enum value
				if (System.Enum.IsDefined(typeof(KnownClients.EKnownClients), clientIDInt32))
				{
					KnownClients.EKnownClients knownClientID = (KnownClients.EKnownClients)clientIDInt32;
					return knownClientID;
				}
			}

			return KnownClients.EKnownClients.unknown;
		}

		public static EUserSessionType GetSessionType(ControllerBase controller)
		{
			var first = controller.User.FindFirst("session_type");

			if (first == null)
				return EUserSessionType.None;

			if (int.TryParse(first.Value, out int sessionTypeInt32))
			{
				// Validate if the int corresponds to a defined enum value
				if (System.Enum.IsDefined(typeof(EUserSessionType), sessionTypeInt32))
				{
					EUserSessionType sessionType = (EUserSessionType)sessionTypeInt32;
					return sessionType;
				}
			}

			return EUserSessionType.None;
		}

		public static string GetDisplayName(ControllerBase controller)
		{
			// TODO: Handle not finding claims, it is a critical error
			var first = controller.User.FindFirst(JwtRegisteredClaimNames.Name);
			return first != null ? first.Value : String.Empty;
		}

		public static string GetIPAddress(ControllerBase controller)
		{
			// TODO: Handle not finding claims, it is a critical error
			var first = controller.User.FindFirst(JwtRegisteredClaimNames.Address);
			return first != null ? first.Value : String.Empty;
		}
	}

	public class Program
	{
		public static IConfiguration? g_Config = null;
		public static DiscordBot? g_Discord = null;

		// TODO_EFCORE: Do this regularly
		static async Task DoCleanup(AppDbContext db, bool bStartup)
		{
			await Database.PendingLogins.Cleanup(db, bStartup);
		}

		private static async Task InitializeDatabase(WebApplicationBuilder builder)
		{
			// TODO_EFCORE: Check connection immediately like old impl
			if (Program.g_Config == null)
			{
				throw new Exception("Config is null. Check config file exists.");
			}

			IConfiguration? dbSettings = Program.g_Config.GetSection("Database");

			if (dbSettings == null)
			{
				throw new Exception("Database section in config is null / not set in config");
			}

			string? hostname = dbSettings.GetValue<string>("db_host");
			string? dbname = dbSettings.GetValue<string>("db_name");
			string? username = dbSettings.GetValue<string>("db_username");
			string? password = dbSettings.GetValue<string>("db_password");
			UInt16? port = dbSettings.GetValue<UInt16>("db_port");

			int? db_min_poolsize = dbSettings.GetValue<int>("db_min_poolsize");
			int? db_max_poolsize = dbSettings.GetValue<int>("db_max_poolsize");
			bool? db_use_pooling = dbSettings.GetValue<bool>("db_use_pooling");
			bool? db_conn_reset = dbSettings.GetValue<bool>("db_conn_reset");
			int? db_connect_timeout = dbSettings.GetValue<int>("db_connect_timeout");
			int? db_command_timeout = dbSettings.GetValue<int>("db_command_timeout");

			if (hostname == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}

			if (dbname == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}

			if (username == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}

			if (password == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}

			if (port == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}

			// TODO_EFCORE: Log exceptions to disk again
			if (!Directory.Exists("Exceptions"))
			{
				Directory.CreateDirectory("Exceptions");
			}

			// EFCore connect
			{
				//var builder = WebApplication.CreateBuilder(args);

				var csb = new MySql.Data.MySqlClient.MySqlConnectionStringBuilder
				{
					Server = hostname,
					Port = (uint)port,
					Database = dbname,
					UserID = username,
					Password = password,
					ConnectionTimeout = (uint)db_connect_timeout,
					DefaultCommandTimeout = (uint)db_command_timeout,
					SslMode = MySql.Data.MySqlClient.MySqlSslMode.Preferred
				};

				// TODO_EFCORE: Consider use of ExecuteDeleteAsync and options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
				// TODO_EFCORE: Move to AddPooledDbContextFactory instead and use private readonly IDbContextFactory<AppDbContext> _factory;
				builder.Services.AddPooledDbContextFactory<AppDbContext>(options =>
				{
					options.UseMySql(
						csb.ConnectionString,
						ServerVersion.AutoDetect(csb.ConnectionString));

					options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

#if RELEASE
					options.UseLoggerFactory(LoggerFactory.Create(builder => { })); // Empty logger
					options.EnableSensitiveDataLogging(false); // Ensure sensitive data is not logged
					options.EnableDetailedErrors(false);      // Disable detailed error messages
#endif

				});
			}
		}

		// The signing key is the only thing standing between a user and a forged token, so refuse to
		// start with a placeholder or a key too short for HS256.
		private static void ValidateSigningKey(string strKey)
		{
			int keyBytes = Encoding.UTF8.GetByteCount(strKey);

			if (keyBytes < 32)
			{
				throw new Exception($"JwtSettings:Key is only {keyBytes} bytes; HS256 requires at least 32 bytes of key material.");
			}

			if (strKey.Contains("TODO", StringComparison.OrdinalIgnoreCase))
			{
				throw new Exception("JwtSettings:Key is still set to the placeholder value. Set a real secret before starting the service.");
			}
		}

		private static bool IsRefreshTokenEndpoint(HttpContext httpContext)
		{
			Endpoint? endpoint = httpContext.GetEndpoint();
			if (endpoint != null)
			{
				return endpoint.Metadata.GetMetadata<RefreshTokenEndpointAttribute>() != null;
			}

			// Routing hasn't resolved an endpoint yet. Fall back to an exact final-segment match
			// rather than a substring search over the whole path.
			string path = httpContext.Request.Path.ToString().TrimEnd('/');
			int lastSlash = path.LastIndexOf('/');
			string lastSegment = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;
			return String.Equals(lastSegment, "loginwithtoken", StringComparison.OrdinalIgnoreCase)
				|| String.Equals(lastSegment, "refreshtoken", StringComparison.OrdinalIgnoreCase);
		}

		private static Task AdditionalValidation(TokenValidatedContext context)
		{
			try
			{
				if (context.Principal == null || context.Principal.Claims == null)
				{
					context.Fail("Failed Validation #1");
					return Task.CompletedTask;
				}

				Claim? userIdClaim = context.Principal.FindFirst(ClaimTypes.NameIdentifier);
				if (userIdClaim == null || !Int64.TryParse(userIdClaim.Value, out Int64 userID))
				{
					context.Fail("Failed Validation #2");
					return Task.CompletedTask;
				}

				Claim? nameClaim = context.Principal.FindFirst(JwtRegisteredClaimNames.Name);
				if (nameClaim == null || String.IsNullOrEmpty(nameClaim.Value))
				{
					context.Fail("Failed Validation #3");
					return Task.CompletedTask;
				}

				// refresh tokens are only valid on endpoints marked [RefreshTokenEndpoint]
				Claim? firstType = context.Principal.FindFirst(JwtRegisteredClaimNames.Typ);

				if (firstType == null || String.IsNullOrEmpty(firstType.Value))
				{
					context.Fail("Failed Validation #4");
					return Task.CompletedTask;
				}

				if (!int.TryParse(firstType.Value, out int tokenTypeValue)
					|| !System.Enum.IsDefined(typeof(JwtTokenGenerator.ETokenType), tokenTypeValue))
				{
					context.Fail("Failed Validation #10 - Unknown token type");
					return Task.CompletedTask;
				}

				JwtTokenGenerator.ETokenType tokenType = (JwtTokenGenerator.ETokenType)tokenTypeValue;

				bool bIsRefreshEndpoint = IsRefreshTokenEndpoint(context.HttpContext);

				if (tokenType == JwtTokenGenerator.ETokenType.Refresh)
				{
					if (!bIsRefreshEndpoint)
					{
						context.Fail("Failed Validation #5 - Refresh token used on non-refresh endpoint");
						return Task.CompletedTask;
					}
				}
				else if (tokenType == JwtTokenGenerator.ETokenType.Session)
				{
					if (bIsRefreshEndpoint)
					{
						context.Fail("Failed Validation #6 - Session token used on refresh endpoint");
						return Task.CompletedTask;
					}
				}

				Claim? addressClaim = context.Principal.FindFirst(JwtRegisteredClaimNames.Address);
				if (addressClaim == null)
				{
					context.Fail("Failed Validation #7");
					return Task.CompletedTask;
				}

				EUserSessionType sessionType;
				{
					Claim? sessionTypeClaim = context.Principal.FindFirst("session_type");
					if (sessionTypeClaim == null
						|| !int.TryParse(sessionTypeClaim.Value, out int sessionTypeValue)
						|| !System.Enum.IsDefined(typeof(EUserSessionType), sessionTypeValue))
					{
						context.Fail("Failed Validation #11 - Missing or invalid session type");
						return Task.CompletedTask;
					}

					sessionType = (EUserSessionType)sessionTypeValue;
				}

				// Revocation checks. All in-memory, no database access per request.
				if (TokenRevocationManager.IsUserBanned(userID))
				{
					context.Fail("Failed Validation #12 - User is banned");
					return Task.CompletedTask;
				}

				{
					// Tokens issued before this claim existed are treated as generation 0, so a deploy
					// doesn't force every live client to re-login. They still get rejected once that
					// user's generation is bumped by a revocation.
					int tokenGeneration = 0;

					Claim? generationClaim = context.Principal.FindFirst(JwtTokenGenerator.TokenGenerationClaim);
					if (generationClaim != null && !int.TryParse(generationClaim.Value, out tokenGeneration))
					{
						context.Fail("Failed Validation #13 - Invalid token generation");
						return Task.CompletedTask;
					}

					if (tokenGeneration != TokenRevocationManager.GetGeneration(userID, sessionType))
					{
						context.Fail("Failed Validation #14 - Token has been revoked");
						return Task.CompletedTask;
					}
				}

				// Refresh tokens are single use - only the most recently issued one is accepted.
				if (tokenType == JwtTokenGenerator.ETokenType.Refresh)
				{
					Claim? jtiClaim = context.Principal.FindFirst(JwtRegisteredClaimNames.Jti);
					if (jtiClaim == null || !TokenRevocationManager.IsCurrentRefreshToken(userID, sessionType, jtiClaim.Value))
					{
						context.Fail("Failed Validation #15 - Refresh token has been superseded");
						return Task.CompletedTask;
					}
				}

				if (Program.g_Config != null)
				{
					IConfiguration? jwtSettings = Program.g_Config.GetSection("JwtSettings");

					if (jwtSettings != null)
					{
						if (jwtSettings.GetValue<bool>("enforce_ip_match"))
						{
							string strExpectedIP = addressClaim.Value;
							string currentIP = IPHelpers.NormalizeIP(context.HttpContext.Connection.RemoteIpAddress?.ToString());
							if (strExpectedIP != currentIP)
							{
								context.Fail("Failed Validation #8 - IP mismatch");
								return Task.CompletedTask;
							}
						}
					}
				}
			}
			catch
			{
				context.Fail("Failed Validation #9");
			}

			return Task.CompletedTask;
		}

		public class JwtTokenGenerator
		{
			private readonly IConfiguration _configuration;

			public JwtTokenGenerator(IConfiguration configuration)
			{
				_configuration = configuration;
			}

			public enum ETokenType
			{
				Session,
				Refresh
			}

			public const string TokenGenerationClaim = "tgen";

			public string GenerateToken(string displayname, Int64 userID, string ipAddr, ETokenType tokenType, KnownClients.EKnownClients knownClientID, EUserSessionType sessionType, bool bIsAdmin, EUserPriority userPriority = EUserPriority.None)
			{
				return GenerateToken(displayname, userID, ipAddr, tokenType, knownClientID, sessionType, bIsAdmin, userPriority, out _);
			}

			public string GenerateToken(string displayname, Int64 userID, string ipAddr, ETokenType tokenType, KnownClients.EKnownClients knownClientID, EUserSessionType sessionType, bool bIsAdmin, EUserPriority userPriority, out string jti)
			{
				var jwtSettings = _configuration.GetSection("JwtSettings");

				if (jwtSettings == null)
				{
					throw new Exception("JWT Settings not found in configuration");
				}

				string? strKey = jwtSettings["Key"];
				if (strKey == null)
				{
					throw new Exception("JWT Key not found in configuration");
				}

				var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(strKey));
				var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

				jti = Guid.NewGuid().ToString();

				List<Claim> claims = new List<Claim>
				{
					new Claim(JwtRegisteredClaimNames.Sub, userID.ToString()),
					new Claim(JwtRegisteredClaimNames.Jti, jti),
					new Claim(JwtRegisteredClaimNames.Name, displayname),
					new Claim(JwtRegisteredClaimNames.Address, ipAddr),
					new Claim(JwtRegisteredClaimNames.Typ, ((int)tokenType).ToString()),
					new Claim("client_id", ((int)knownClientID).ToString()),
					new Claim("session_type", ((int)sessionType).ToString()),

					// Token generation, checked against the revocation manager on every request so
					// that bans/logouts can invalidate tokens before they naturally expire.
					new Claim(TokenGenerationClaim, TokenRevocationManager.GetGeneration(userID, sessionType).ToString())
				};

				// everyone gets the player role
				claims.Add(new Claim(ClaimTypes.Role, "Player"));

				if (sessionType == EUserSessionType.GameClient)
				{
					claims.Add(new Claim(ClaimTypes.Role, "GameClient"));
				}
				else if (sessionType == EUserSessionType.ChatClient)
				{
					claims.Add(new Claim(ClaimTypes.Role, "ChatClient"));
				}
				else if (sessionType == EUserSessionType.GameLauncher)
				{
					claims.Add(new Claim(ClaimTypes.Role, "GameLauncher"));
				}
				else
				{
					throw new Exception("Unhandled session type: " + sessionType);
				}

				if (bIsAdmin)
				{
					claims.Add(new Claim(ClaimTypes.Role, "Admin"));
				}

				// Signed like every other claim - a client cannot alter it without breaking
				// the HMAC signature.
				claims.Add(new Claim("priority", ((int)userPriority).ToString()));


				var token = new JwtSecurityToken(
					issuer: jwtSettings["Issuer"],
					audience: jwtSettings["Audience"],
					claims: claims,
					expires: DateTime.Now.AddMinutes(Convert.ToDouble(tokenType == ETokenType.Session ? jwtSettings["ExpiresInMinutes_Session"] : jwtSettings["ExpiresInMinutes_Refresh"])),
					signingCredentials: credentials
				);

				return new JwtSecurityTokenHandler().WriteToken(token);
			}
		}

		public static string GetWebSocketAddress(bool bSecure)
		{
			if (Program.g_Config == null)
			{
				throw new Exception("g_Config is null.");
			}

			IConfiguration? coreSettings = Program.g_Config.GetSection("Core");

			if (coreSettings == null)
			{
				throw new Exception("Core section of config is null.");
			}


			string configKey = bSecure ? "ws_address" : "ws_address_insecure";

			string? ws_address = coreSettings.GetValue<string>(configKey);

			if (ws_address == null)
			{
				throw new Exception(String.Format("{0} in Core section of config is null.", configKey));
			}

			return ws_address;
		}

		public static async Task Main(string[] args)
		{
#if !DEBUG
			AppDomain.CurrentDomain.UnhandledException += GlobalExceptionHandler;
#endif

			// Configure thread pool for better performance under load
			ThreadPool.SetMinThreads(200, 200);

			var builder = WebApplication.CreateBuilder(args);

			// Add services to the container.

			g_Config = builder.Configuration;

			ShowLogo();

			IConfigurationSection? sentrySettings = Program.g_Config.GetSection("Sentry");

			if (sentrySettings == null)
			{
				throw new Exception("Sentry section missing in config");
			}

			bool? sentry_enabled = sentrySettings.GetValue<bool>("enabled");
			string? sentry_dsn = sentrySettings.GetValue<string>("dsn");
			string? sentry_env = sentrySettings.GetValue<string>("environment");

			if (sentry_enabled == null)
			{
				throw new Exception("sentry_enabled missing in config");
			}

			if (sentry_dsn == null)
			{
				throw new Exception("sentry_dsn missing in config");
			}

			if (sentry_env == null)
			{
				sentry_env = "production";
			}

			if ((bool)sentry_enabled)
			{
				// init sentry
				SentrySdk.Init(options =>
				{
					// A Sentry Data Source Name (DSN) is required.
					// See https://docs.sentry.io/product/sentry-basics/dsn-explainer/
					// You can set it in the SENTRY_DSN environment variable, or you can set it in code here.
					options.Dsn = sentry_dsn;

					// When debug is enabled, the Sentry client will emit detailed debugging information to the console.
					// This might be helpful, or might interfere with the normal operation of your application.
					// We enable it here for demonstration purposes when first trying Sentry.
					// You shouldn't do this in your applications unless you're troubleshooting issues with Sentry.
					options.Debug = false;

					// This option is recommended. It enables Sentry's "Release Health" feature.
					options.AutoSessionTracking = true;

					options.Environment = sentry_env;

					options.Release = "generalsonline-services@081326";
				});
			}

			S3CredentialManager.Initialize();


			// create discord?
			var discordSettings = Program.g_Config.GetSection("Discord");
			bool bEnableDiscord = discordSettings.GetValue<bool>("enable_discord");
			if (bEnableDiscord)
			{
				g_Discord = new DiscordBot();
			}

			// A misconfigured optional integration must not take the server down - warn and
			// continue, same as Discord's own startup check.
			var relaySettings = Program.g_Config.GetSection("Relay");
			if (relaySettings.GetValue<bool>("enabled"))
			{
				string? relayBaseUrl = relaySettings.GetValue<string>("base_url");
				string? relayApiKey = relaySettings.GetValue<string>("api_key");
				string? relayIngressKey = relaySettings.GetValue<string>("ingress_api_key");
				if (string.IsNullOrEmpty(relayBaseUrl) || string.IsNullOrEmpty(relayApiKey) || string.IsNullOrEmpty(relayIngressKey))
				{
					Console.WriteLine($"[WARNING] Relay is enabled in config but base_url, api_key and/or ingress_api_key are missing - " +
						$"livestream endpoints will return 503 until the Relay section is completed.");
				}
			}

			builder.Services.AddSingleton<LobbyManager>();

			var rateLimitingSettings = Program.g_Config.GetSection("RateLimiting");
			bool bUseBuiltinRateLimiter = rateLimitingSettings.GetValue<bool>("use_builtin_ratelimiter"); // use built in Kestrel/dotnet rate limiting if you do not have a reverse proxy or other rate limiter in front of service

			if (bUseBuiltinRateLimiter)
			{
				builder.Services.AddRateLimiter(options =>
				{
					options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
					{
						// Use authenticated user ID or fallback to IP address
						var userKey = httpContext.User.Identity?.IsAuthenticated == true
							? httpContext.User.Identity.Name
							: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

						return RateLimitPartition.GetTokenBucketLimiter(userKey, _ => new TokenBucketRateLimiterOptions
						{
							TokenLimit = 50, // max burst
							TokensPerPeriod = 10, // refill rate
							ReplenishmentPeriod = TimeSpan.FromSeconds(5),
							AutoReplenishment = true,
							QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
							QueueLimit = 10
						});
					});
				});
			}

			builder.Services.AddCors(options =>
			{
				options.AddDefaultPolicy(policy =>
				{
					policy.WithOrigins(
						"https://localhost:9000",
						"http://localhost:9001",
						"https://*.playgenerals.online"
					)
					.AllowAnyHeader()
					.AllowAnyMethod()
					.AllowCredentials();
				});
			});


			var jwtSettings = builder.Configuration.GetSection("JwtSettings");
			builder.Services.AddAuthentication(options =>
			{
				options.DefaultScheme = "JwtOrBasic"; // Custom policy scheme
			})
			.AddPolicyScheme("JwtOrBasic", "JWT or Basic", options =>
			{
				options.ForwardDefaultSelector = context =>
				{
					var authHeader = context.Request.Headers["Authorization"].ToString();
					if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
						return JwtBearerDefaults.AuthenticationScheme;
					else if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
						return "Basic";
					return JwtBearerDefaults.AuthenticationScheme; // fallback
				};
			})
			.AddJwtBearer(options =>
			{
				var jwtSettings = builder.Configuration.GetSection("JwtSettings");

				string? strKey = jwtSettings["Key"];
				if (strKey == null)
				{
					throw new Exception("JWT Key not found in configuration");
				}

				ValidateSigningKey(strKey);

				options.TokenValidationParameters = new TokenValidationParameters
				{
					ValidateIssuer = true,
					ValidateAudience = true,
					ValidateLifetime = true,
					ValidateIssuerSigningKey = true,
					ValidIssuer = jwtSettings["Issuer"],
					ValidAudience = jwtSettings["Audience"],
					IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(strKey)),

					// Pin the algorithm so only tokens signed the way we sign them are accepted.
					ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },

					// Default is 5 minutes, which keeps expired tokens usable for that long.
					ClockSkew = TimeSpan.FromSeconds(30)
				};

				options.Events = new JwtBearerEvents
				{
					OnTokenValidated = AdditionalValidation
				};
			}).AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>("Basic", null)
			.AddScheme<AuthenticationSchemeOptions, DiscordAuthenticationHandler>("Discord", null);

			builder.Services.AddAuthorization(options =>
			{
				options.AddPolicy("MonitorOrApiKey", policy =>
					policy.RequireAssertion(context =>
					{
						// Check role
						if (context.User.IsInRole("Monitor"))
							return true;

						// Check x-api-key header
						var httpContext = context.Resource as HttpContext;
						if (httpContext?.Request.Headers.TryGetValue("x-api-key", out var apiKey) == true)
						{
							return APIKeyHelpers.ValidateKey(apiKey);
						}

						return false;
					}));

				options.AddPolicy("AnyClientOrMonitorOrApiKey", policy =>
					policy.RequireAssertion(context =>
					{
						// Check roles
						if (context.User.IsInRole("GameClient"))
							return true;

						if (context.User.IsInRole("ChatClient"))
							return true;

						if (context.User.IsInRole("GameLauncher"))
							return true;

						if (context.User.IsInRole("Monitor"))
							return true;

						// Check x-api-key header
						var httpContext = context.Resource as HttpContext;
						if (httpContext?.Request.Headers.TryGetValue("x-api-key", out var apiKey) == true)
						{
							return APIKeyHelpers.ValidateKey(apiKey);
						}

						return false;
					}));
			});

			// Add in-memory caching for performance optimization
			builder.Services.AddMemoryCache(options =>
			{
				options.SizeLimit = 10000; // Limit cache entries
			});

			// Add response compression for bandwidth optimization (60-80% reduction)
			builder.Services.AddResponseCompression(options =>
			{
				options.EnableForHttps = true;
				options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
				options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
			});

			builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(options =>
			{
				options.Level = System.IO.Compression.CompressionLevel.Fastest; // Balance speed vs compression
			});

			builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(options =>
			{
				options.Level = System.IO.Compression.CompressionLevel.Fastest;
			});

			// JSON options needed to avoid ASP.NET lower casing everything
			builder.Services.AddControllers().AddJsonOptions(options =>
			{
				options.JsonSerializerOptions
				.PropertyNamingPolicy = null;
			});
			// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
			//builder.Services.AddOpenApi();

			X509Certificate2? X509Certificate2 = null;

			var coreSettings = Program.g_Config.GetSection("Core");

			if (coreSettings == null)
			{
				Console.WriteLine("FATAL ERROR: Core sections of config file is null");
				Console.ReadKey(true);
				return;
			}

			bool use_os_cert_store = coreSettings.GetValue<bool>("use_os_cert_store");
			string? cert_pem_path = coreSettings.GetValue<string>("cert_pem_path");
			string? cert_key_path = coreSettings.GetValue<string>("cert_key_path");

			if (use_os_cert_store == null)
			{
				Console.WriteLine("FATAL ERROR: use_os_cert_store is not set in the config");
				Console.ReadKey(true);
				return;
			}

			if (!use_os_cert_store) // if not using the cert store, we need a pem and key
			{
				if (cert_pem_path == null)
				{
					Console.WriteLine("FATAL ERROR: cert_pem_path is not set in the config");
					Console.ReadKey(true);
					return;
				}

				if (cert_key_path == null)
				{
					Console.WriteLine("FATAL ERROR: cert_key_path is not set in the config");
					Console.ReadKey(true);
					return;
				}
			}


			//UInt16 port = coreSettings.GetValue<UInt16>("port");

			bool bShouldUseOSCertSTore = (bool)use_os_cert_store;
			if (!bShouldUseOSCertSTore)
			{
				if (String.IsNullOrEmpty(cert_pem_path) || String.IsNullOrEmpty(cert_key_path))
				{
					Console.WriteLine("FATAL ERROR: use_os_cert_store is set to false, but cert_pem_path and/or cert_key_path were not provided / null!");
					Console.ReadKey(true);
					return;
				}
				else
				{
					//X509Certificate2 = CertHelpers.LoadPemWithPrivateKey(cert_pem_path, cert_key_path);

					X509Certificate2 = X509Certificate2.CreateFromPemFile(cert_pem_path, cert_key_path);


					if (X509Certificate2 == null)
					{
						Console.WriteLine("FATAL ERROR: Failed to load the provided certificate!");
						Console.ReadKey(true);
						return;
					}
				}
			}

			var kestrelSettings = Program.g_Config.GetSection("Kestrel");
			var endpointSettings = kestrelSettings.GetSection("Endpoints");
			var httpsSettings = endpointSettings.GetSection("HTTPS");
			string? serverURI = httpsSettings.GetValue<string>("Url");

			if (serverURI == null)
			{
				Console.WriteLine("FATAL ERROR: serverURI is not set in the config");
				Console.ReadKey(true);
				return;
			}

			// Parse the port number out of serverURI
			int port = -1;
			try
			{
				if (!string.IsNullOrEmpty(serverURI))
				{
					var uri = new Uri(serverURI);
					port = uri.Port;
				}
			}
			catch
			{
				Console.WriteLine("ERROR: Failed to parse port from serverURI: " + serverURI);
			}



			// options
			builder.WebHost.ConfigureKestrel(options =>
			{
				options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
				options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);

				if (!bShouldUseOSCertSTore && X509Certificate2 != null)
				{
					options.ConfigureHttpsDefaults(httpsOptions =>
					{
						httpsOptions.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
						httpsOptions.ServerCertificate = X509Certificate2;
					});
				}

				if (!bShouldUseOSCertSTore && X509Certificate2 != null)
				{
					//options.ListenAnyIP(port, listenOptions => listenOptions.UseHttps(X509Certificate2!));
				}

			});

			// add DB
			await InitializeDatabase(builder);

			var app = builder.Build();
			ServiceLocator.Services = app.Services;

			if (bUseBuiltinRateLimiter)
			{
				app.UseRateLimiter();
			}

			// Enable response compression (must be early in pipeline)
			app.UseResponseCompression();

			// websocket

			var webSocketOptions = new WebSocketOptions
			{
				KeepAliveInterval = TimeSpan.FromSeconds(30)
			};

			app.UseWebSockets(webSocketOptions);

			// end websocket

			// Configure the HTTP request pipeline.
			/*
			if (app.Environment.IsDevelopment())
			{
				app.MapOpenApi();
			}
			*/

			app.Use((context, next) =>
			{
				context.Request.EnableBuffering();
				return next();
			});

			// HTTPS enforcement. Gated because TLS may be terminated upstream (or a plain-HTTP
			// listener may be deliberately in use), in which case redirecting would break clients.
			bool bEnforceHttps = builder.Configuration.GetSection("Core").GetValue<bool>("enforce_https");
			if (bEnforceHttps)
			{
				app.UseHsts();

				// Websocket upgrades are exempt: ws_address_insecure is an intentional fallback for
				// clients that can't do TLS, and redirecting the upgrade request would break it.
				app.UseWhen(context => !context.WebSockets.IsWebSocketRequest, branch =>
				{
					branch.UseHttpsRedirection();
				});
			}
			else
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("*** WARNING: Core:enforce_https is disabled. Bearer tokens will be sent in clear text over any plain-HTTP listener. ***");
				Console.ForegroundColor = ConsoleColor.Gray;
			}

			app.UseCors();
			app.UseAuthentication();
			app.UseAuthorization();

			app.MapControllers();


			// cleanup
			System.Timers.Timer timerCleanup = new System.Timers.Timer(5000); // 5s tick
			timerCleanup.AutoReset = false;
			timerCleanup.Elapsed += async (sender, e) =>
			{
				try
				{
					await WebSocketManager.CheckForTimeouts();

					var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();

					int numLobbies = lobbyManager.GetNumLobbies();
					await StatsTracker.Update(numLobbies, WebSocketManager.GetNumberOfUsersOnline());

					await lobbyManager.Cleanup();
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[timerCleanup] Exception: {ex}");
				}
				finally
				{
					timerCleanup.Start();
				}
			};
			timerCleanup.Start();

			// tick lobby
			{
				System.Timers.Timer timerTick = new System.Timers.Timer(5); // 5ms tick
				timerTick.AutoReset = false;
				timerTick.Elapsed += async (sender, e) =>
				{
					try
					{
						var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();
						await lobbyManager.Tick();
						await WebSocketManager.Tick();
					}
					catch (Exception ex)
					{
						Console.WriteLine($"[timerTick lobby] Exception: {ex}");
					}
					finally
					{
						timerTick.Start();
					}
				};
				timerTick.Start();
			}

			// tick matchmaking (done at lower frequency)
			{
				System.Timers.Timer timerTick = new System.Timers.Timer(1000); // 1s tick
				timerTick.AutoReset = false;
				timerTick.Elapsed += async (sender, e) =>
				{
					try
					{
						await MatchmakingManager.Tick();
					}
					catch (Exception ex)
					{
						Console.WriteLine($"[timerTick matchmaking] Exception: {ex}");
					}
					finally
					{
						timerTick.Start();
					}
				};
				timerTick.Start();
			}

			// tick network rooms (done at lower frequency)
			{
				System.Timers.Timer timerTick = new System.Timers.Timer(1000); // 1s tick
				timerTick.AutoReset = false;
				timerTick.Elapsed += async (sender, e) =>
				{
					try
					{
						await WebSocketManager.TickRoomMemberList();
					}
					catch (Exception ex)
					{
						Console.WriteLine($"[timerTick rooms] Exception: {ex}");
					}
					finally
					{
						timerTick.Start();
					}
				};
				timerTick.Start();
			}

			// timer to save daily stats
			{
				System.Timers.Timer timerTick = new System.Timers.Timer(60000); // 60s tick
				timerTick.AutoReset = false;
				timerTick.Elapsed += async (sender, e) =>
				{
					try
					{
						using (var scope = app.Services.CreateScope())
						{
							var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
							await using var db = await factory.CreateDbContextAsync();
							await DailyStatsManager.SaveToDB(db);
						}
					}
					catch (Exception ex)
					{
						Console.WriteLine($"[timerTick dailystats] Exception: {ex}");
					}
					finally
					{
						timerTick.Start();
					}
				};
				timerTick.Start();
			}

			// keep token revocation state in sync with bans applied directly in the database
			{
				System.Timers.Timer timerTick = new System.Timers.Timer(60000); // 60s tick
				timerTick.AutoReset = false;
				timerTick.Elapsed += async (sender, e) =>
				{
					try
					{
						using (var scope = app.Services.CreateScope())
						{
							var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
							await using var db = await factory.CreateDbContextAsync();
							await TokenRevocationManager.ReconcileBans(db);
						}
					}
					catch (Exception ex)
					{
						Console.WriteLine($"[timerTick tokenrevocation] Exception: {ex}");
					}
					finally
					{
						timerTick.Start();
					}
				};
				timerTick.Start();
			}

			AppDomain.CurrentDomain.ProcessExit += (_, _) =>
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("EXIT REQUESTED!");
			};

			// create a token
			g_tokenGenerator = new JwtTokenGenerator(builder.Configuration);

			// load daily stats
			using (var scope = app.Services.CreateScope())
			{
				var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
				await using var db = await factory.CreateDbContextAsync();

				// do a cleanup on startup
				await DoCleanup(db, true);
				await DailyStatsManager.LoadFromDB(db);

				// must happen before any token is issued or validated
				await TokenRevocationManager.Initialize(factory, db);
			}

			app.Run();

		}

		public static void ShowLogo()
		{
			ConsoleColor origCol = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Blue;
			Console.WriteLine("                        @@@                        ");
			Console.WriteLine("                        @@@                        ");
			Console.WriteLine("                        @@@                        ");
			Console.WriteLine("                        @@@                        ");
			Console.WriteLine("%@          @      @@@@@@@@@@@@       @          @%");
			Console.WriteLine("%=-*%     @+=@    @@@@@@@@@@@@@@@    %=#@    %%*-+#");
			Console.WriteLine(" #=---+*%@#+++#@       @@@@@       %#==*#@#*=---=% ");
			Console.WriteLine("  %+-----=+#*++=+#%@   @@@@@   @%+==++**+=-----+@  ");
			Console.WriteLine("   @%#=------=##++===--=+*==---==++##=------+#%    ");
			Console.WriteLine("    %+--*#+=--=+--+#**+**###***#+--#--==+#+--+%    ");
			Console.WriteLine("     %+-----+++#+---=##++---##=---+*++=-----+%     ");
			Console.WriteLine("      @%*=------+---+#**-::+##=--=+------=*%@      ");
			Console.WriteLine("       @+-=##==-=+===+#--*--#+===+=-==##=-*        ");
			Console.WriteLine("        %*=---=+#*======#+#======*#+----=#%        ");
			Console.WriteLine("         @%#**+=-+=+#++++-++++#==+-=+**#@          ");
			Console.WriteLine("          @#++***#*-=++-:::-*==-##***++%@          ");
			Console.WriteLine("           @#+++++*+--*=-=-++--#*++++*%@           ");
			Console.WriteLine("            @#*+++++###++=++#**+++++*#@            ");
			Console.WriteLine("             @#*++++#-*=+#=-#-**++++%@             ");
			Console.WriteLine("               @***#===--+--+-+*+*#@               ");
			Console.WriteLine("                @%*#-==--+--==-#*%@                ");
			Console.WriteLine("                  %=-==--+--==-+@                  ");
			Console.WriteLine("                   %++--=+-:=++%                   ");
			Console.WriteLine("                    @#+=-+-=+#@                    ");
			Console.WriteLine("                      @%###@                       ");
			Console.WriteLine("                       @@@@@                       ");
			Console.WriteLine("                       @@@@                        ");
			Console.WriteLine("                        @@@                        ");
			Console.WriteLine("");
			Console.WriteLine("               GeneralsOnline Service              ");
			Console.ForegroundColor = origCol;
		}

		public static DateTime g_LastStartTime = DateTime.Now;
		public static JwtTokenGenerator? g_tokenGenerator = null;

		public static void GlobalExceptionHandler(object sender, UnhandledExceptionEventArgs e)
		{
			try
			{
				Exception ex = (Exception)e.ExceptionObject;

				if (!Directory.Exists("Exceptions"))
				{
					Directory.CreateDirectory("Exceptions");
				}

				List<string> lstStrings = new List<string>();
				lstStrings.Add(ex.Message);
				lstStrings.Add(ex.ToString());

				if (ex.Source != null)
				{
					lstStrings.Add(ex.Source);
				}

				if (ex.StackTrace != null)
				{
					lstStrings.Add(ex.StackTrace);
				}

				if (ex.InnerException != null)
				{
					lstStrings.Add(ex.InnerException.Message);
					lstStrings.Add(ex.InnerException.ToString());
				}

				string exceptionFileName = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".txt";

				File.WriteAllLines(Path.Combine("Exceptions", exceptionFileName), lstStrings);
			}
			catch
			{

			}
		}
	}

	public static class ServiceLocator
	{
		public static IServiceProvider Services { get; set; } = default!;
	}

}
