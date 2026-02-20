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

using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Crypto;
using System.Security.Cryptography.X509Certificates;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using System.Text;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.WebSockets;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Google.Protobuf.WellKnownTypes;
using System.Xml;
using System.Drawing;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using Sentry;
using MaxMind.GeoIP2;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

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

	public static class APIKeyHelpers
	{
		private static HashSet<string>? s_cachedApiKeys = null;
		private static readonly object s_cacheLock = new object();

		public static bool ValidateKey(string strKey)
		{
			if (Program.g_Config == null)
			{
				return false;
			}

			// Use cached HashSet for O(1) lookup
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

						// Convert to HashSet and uppercase all keys for O(1) lookup
						s_cachedApiKeys = new HashSet<string>(api_keys.Select(k => k.ToUpper()), StringComparer.OrdinalIgnoreCase);
					}
				}
			}

			return s_cachedApiKeys.Contains(strKey);
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

							if (strUsername == monitorUsername && strPassword == monitorPassword)
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
			await Database.Functions.ServiceStats.CommitStats(GlobalDatabaseInstance.g_Database, DateTime.Now.DayOfYear, hourOfDay, numPlayers, numLobbies);
		}
	}

	public static class TokenHelper
	{
		public static Int64 GetUserID(ControllerBase controller)
		{
			if (controller.User.IsInRole("Monitor"))
			{
				return -1;
			}

			return Convert.ToInt64(controller.User.Claims.First().Value);
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
		static async void DoCleanup(bool bStartup)
		{
			try
			{
				await Database.Functions.Auth.Cleanup(GlobalDatabaseInstance.g_Database, bStartup);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in DoCleanup: {ex.Message}");
			}
		}

		private static Task AdditionalValidation(TokenValidatedContext context)
		{
			//controller.User.Claims.First().Value
			try
			{
				if (context.Principal == null || context.Principal.Claims == null)
				{
					context.Fail("Failed Validation #1");
				}

#pragma warning disable CS8602 // Dereference of a possibly null reference. (Appears to be erroronous flagging)
#pragma warning disable CS8604 // null reference. (Appears to be erroronous flagging)
				Claim? userIdClaim = context.Principal.FindFirst(ClaimTypes.NameIdentifier);
				if (userIdClaim == null || !Int64.TryParse(userIdClaim.Value, out Int64 userID))
				{
					context.Fail("Failed Validation #2");
				}


				if (context.Principal.FindFirst(JwtRegisteredClaimNames.Name) == null || String.IsNullOrEmpty(context.Principal.FindFirst(JwtRegisteredClaimNames.Name).Value))
				{
					context.Fail("Failed Validation #3");
				}

				// must have type
				if (context.Principal.FindFirst(JwtRegisteredClaimNames.Typ) == null)
				{
					context.Fail("Failed Validation #4");
				}

				// refresh tokens are only valid for LoginWithToken
				Claim? firstType = context.Principal.FindFirst(JwtRegisteredClaimNames.Typ);

				if (firstType == null || string.IsNullOrEmpty(firstType.Value))
				{
					context.Fail("Failed Validation #8");
				}

				string strTypeClaim = firstType.Value;
				JwtTokenGenerator.ETokenType tokenType = (JwtTokenGenerator.ETokenType)Convert.ToInt32(strTypeClaim);

				// Use claim-based validation instead of path-based to prevent bypass
				if (tokenType == JwtTokenGenerator.ETokenType.Refresh)
				{
					bool bIsLoginWithToken = context.Request.Path.ToString().ToLower().Contains("loginwithtoken");
					if (!bIsLoginWithToken)
					{
						context.Fail("Failed Validation #5 - Refresh token used on non-refresh endpoint");
					}
				}
				else if (tokenType == JwtTokenGenerator.ETokenType.Session)
				{
					bool bIsLoginWithToken = context.Request.Path.ToString().ToLower().Contains("loginwithtoken");
					if (bIsLoginWithToken)
					{
						context.Fail("Failed Validation #6 - Session token used on refresh endpoint");
					}
				}
				else
				{
					context.Fail("Failed Validation #10 - Unknown token type");
				}

				if (context.Principal.FindFirst(JwtRegisteredClaimNames.Address) == null)
				{
					context.Fail("Failed Validation #7");
				}

				string strExpectedIP = context.Principal.FindFirst(JwtRegisteredClaimNames.Address).Value;
				string currentIP = IPHelpers.NormalizeIP(context.HttpContext.Connection.RemoteIpAddress?.ToString());
				if (strExpectedIP != currentIP)
				{
					context.Fail("Failed Validation #8 - IP mismatch");
				}
#pragma warning restore CS8602 // Dereference of a possibly null reference.
#pragma warning restore CS8604 // Dereference of a possibly null reference.
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

			public string GenerateToken(string displayname, Int64 userID, string ipAddr, ETokenType tokenType, string client_id, bool bIsAdmin)
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

				List<Claim> claims = new List<Claim>
				{
					new Claim(JwtRegisteredClaimNames.Sub, userID.ToString()),
					new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
					new Claim(JwtRegisteredClaimNames.Name, displayname),
					new Claim(JwtRegisteredClaimNames.Address, ipAddr),
					new Claim(JwtRegisteredClaimNames.Typ, ((int)tokenType).ToString()),
					new Claim("client_id", client_id),
					new Claim(ClaimTypes.Role, "Player")
				};

				if (bIsAdmin)
				{
					claims.Add(new Claim(ClaimTypes.Role, "Admin"));
				}


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

			if (sentry_enabled == null)
			{
				throw new Exception("sentry_enabled missing in config");
			}

			if (sentry_dsn == null)
			{
				throw new Exception("sentry_dsn missing in config");
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
				});
			}


			// create discord?
			var discordSettings = Program.g_Config.GetSection("Discord");
			bool bEnableDiscord = discordSettings.GetValue<bool>("enable_discord");
			if (bEnableDiscord)
			{
				g_Discord = new DiscordBot();
			}

			await GlobalDatabaseInstance.g_Database.Initialize();

			// do a cleanup on startup
			DoCleanup(true);



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

				options.TokenValidationParameters = new TokenValidationParameters
				{
					ValidateIssuer = true,
					ValidateAudience = true,
					ValidateLifetime = true,
					ValidateIssuerSigningKey = true,
					ValidIssuer = jwtSettings["Issuer"],
					ValidAudience = jwtSettings["Audience"],
					IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(strKey))
				};

				options.Events = new JwtBearerEvents
				{
					OnTokenValidated = AdditionalValidation
				};
			}).AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>("Basic", null);

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

				options.AddPolicy("PlayerOrMonitorOrApiKey", policy =>
					policy.RequireAssertion(context =>
					{
						// Check roles
						if (context.User.IsInRole("Player"))
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

			var app = builder.Build();

			app.UseRateLimiter();

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

			//app.UseHttpsRedirection();

			app.UseCors();
			app.UseAuthentication();
			app.UseAuthorization();

			await Database.MySQLInstance.TestQuery(GlobalDatabaseInstance.g_Database);

			app.MapControllers();

			// cleanup
			System.Timers.Timer timerCleanup = new System.Timers.Timer(5000); // 5s tick
			timerCleanup.AutoReset = false;
			timerCleanup.Elapsed += async (sender, e) =>
			{
				try
				{
					await WebSocketManager.CheckForTimeouts();

					int numLobbies = LobbyManager.GetNumLobbies();
					await StatsTracker.Update(numLobbies, WebSocketManager.GetUserDataCache().Count);

					timerCleanup.Start();

					LobbyManager.Cleanup();

					// disconnect test
					/*
					bool bDisc = false;
					if (bDisc)
					{
						ChatSession? targetSession = GenOnlineService.WebSocketManager.GetSessionFromUser(2);
						if (targetSession != null)
						{
							await GenOnlineService.WebSocketManager.DeleteSession(targetSession);
						}
					}
					*/
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Error in cleanup timer: {ex.Message}");
					timerCleanup.Start();
				}
			};
			timerCleanup.Start();

			// Init background uploaded
			BackgroundS3Uploader.Initialize();

			// tick lobby
			{
				System.Timers.Timer timerTick = new System.Timers.Timer(5); // 5ms tick
				timerTick.AutoReset = false;
				timerTick.Elapsed += async (sender, e) =>
				{
					try
					{
						await LobbyManager.Tick();

						await WebSocketManager.Tick();

						timerTick.Start();
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Error in lobby tick timer: {ex.Message}");
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

						timerTick.Start();
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Error in matchmaking timer: {ex.Message}");
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

						timerTick.Start();
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Error in network rooms timer: {ex.Message}");
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
						await DailyStatsManager.SaveToDB();
						timerTick.Start();
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Error in daily stats timer: {ex.Message}");
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
			// TODO_SOCIAL: await
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
			DailyStatsManager.LoadFromDB();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed


			app.Run();

			// shutdown
			BackgroundS3Uploader.Shutdown();

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
}
