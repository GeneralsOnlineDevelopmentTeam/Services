using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Polly;

namespace GenOnlineService
{
	public class RelayLivestreamResponse
	{
		public string base_url { get; set; } = String.Empty;
	}

	public class RelayTokenResponse
	{
		public string url { get; set; } = String.Empty;
	}

	public enum RelayWatchTicketStatus
	{
		Ok,
		// Relay session for that lobby is gone (all sources left / reaped): the stream ended.
		StreamEnded,
		// Relay unreachable or returned an error.
		Failure
	}

	public class RelayWatchTicketResult
	{
		public RelayWatchTicketStatus Status { get; set; }
		public RelayTokenResponse? Token { get; set; }
	}

	public static class RelayClient
	{
		// Tight on purpose: every call sits inside a request a player is waiting on.
		private static readonly HttpClient s_httpClient = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(5)
		};

		// Off unless enabled:true and a base_url are configured, so an unconfigured deployment makes no relay call.
		public static bool IsEnabled()
		{
			if (Program.g_Config == null)
			{
				return false;
			}

			IConfigurationSection? configSection = Program.g_Config.GetSection("Relay");
			if (configSection == null)
			{
				return false;
			}

			// `enabled` is the master switch (mirrors Discord's enable_discord / Sentry's
			// enabled). A missing value means off - the feature ships inert.
			if (!configSection.GetValue<bool>("enabled"))
			{
				return false;
			}

			string? sectionBaseUrl = configSection.GetValue<string>("base_url");
			string? sectionApiKey = configSection.GetValue<string>("api_key");
			string? sectionIngressKey = configSection.GetValue<string>("ingress_api_key");

			return !string.IsNullOrEmpty(sectionBaseUrl) &&
				   !string.IsNullOrEmpty(sectionApiKey) &&
				   !string.IsNullOrEmpty(sectionIngressKey);
		}

		// Relay.ingress_api_key is the relay's credential for calls into GO, distinct from the api_key GO sends out.
		public static bool ValidateIngressKey(string? suppliedKey)
		{
			if (string.IsNullOrEmpty(suppliedKey) || Program.g_Config == null)
			{
				return false;
			}

			IConfigurationSection configSection = Program.g_Config.GetSection("Relay");
			string? expectedKey = configSection.GetValue<string>("ingress_api_key");

			if (string.IsNullOrEmpty(expectedKey))
			{
				return false;
			}

			// Fixed-time compare: this endpoint is reachable by anyone, and an ordinary string
			// comparison leaks how many leading bytes of the key were right. The relay does the
			// same on its side with hmac.compare_digest.
			return CryptographicOperations.FixedTimeEquals(
				Encoding.UTF8.GetBytes(suppliedKey),
				Encoding.UTF8.GetBytes(expectedKey));
		}

		private static void GetRelayConfig(out string baseUrl, out string apiKey)
		{
			baseUrl = String.Empty;
			apiKey = String.Empty;

			if (Program.g_Config == null)
			{
				throw new Exception("Config not loaded");
			}

			IConfigurationSection configSection = Program.g_Config.GetSection("Relay");

			string? sectionBaseUrl = configSection.GetValue<string>("base_url");
			string? sectionApiKey = configSection.GetValue<string>("api_key");

			if (string.IsNullOrEmpty(sectionBaseUrl))
			{
				throw new Exception("Relay base_url missing in config");
			}

			if (string.IsNullOrEmpty(sectionApiKey))
			{
				throw new Exception("Relay api_key missing in config");
			}

			baseUrl = sectionBaseUrl.TrimEnd('/');
			apiKey = sectionApiKey;
		}

		private static Polly.Retry.AsyncRetryPolicy BuildRetryPolicy(string description)
		{
			// Two retries at 400ms/800ms - covers a dropped connection or relay restart
			// without turning a sick relay into a minute-long hang.
			return Policy
				.Handle<HttpRequestException>()
				.Or<SocketException>()
				.Or<TaskCanceledException>()
				.WaitAndRetryAsync(2, retryAttempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, retryAttempt)), (exception, timeSpan, retryCount, context) =>
				{
					Console.WriteLine($"[WARNING] Relay {description} call failed (attempt {retryCount}). Retrying in {timeSpan.TotalMilliseconds}ms. Error: {exception.Message}");
				});
		}

		// throwOnError false returns non-success as-is, because a relay 404 (stream ended) is an answer, not a failure.
		private static async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string path, string? payloadJson, string description, bool throwOnError)
		{
			GetRelayConfig(out string baseUrl, out string apiKey);

			string requestUrl = baseUrl + path;

			var retryPolicy = BuildRetryPolicy(description);

			HttpResponseMessage? response = null;

			try
			{
				await retryPolicy.ExecuteAsync(async () =>
				{
					using (var request = new HttpRequestMessage(method, requestUrl))
					{
						request.Headers.TryAddWithoutValidation("X-Relay-Key", apiKey);
						request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

						if (payloadJson != null)
						{
							request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
						}

						// A retried attempt overwrites this local; dispose the previous
						// attempt's response first or it leaks (HttpResponseMessage is
						// IDisposable and holds the response stream).
						response?.Dispose();
						response = await s_httpClient.SendAsync(request);

						// Explicitly verify response success inside execution block so the retry
						// policy also triggers on HTTP error statuses.
						if (throwOnError)
						{
							response.EnsureSuccessStatusCode();
						}
					}
				});
			}
			catch (Exception ex)
			{
				// The last attempt's response (if any) is a failure Polly gave up retrying -
				// still needs disposing.
				response?.Dispose();
				response = null;
				Console.WriteLine($"[ERROR] Relay {description} call failed: {ex.Message}");
				return null;
			}

			return response;
		}

		public static async Task<RelayLivestreamResponse?> CreateLivestreamAsync(long lobbyId, long ownerUserId, int? delaySeconds = null)
		{
			try
			{
				string payloadJson = JsonSerializer.Serialize(new { lobby_id = lobbyId, owner_user_id = ownerUserId, delay_seconds = delaySeconds });

				using (var response = await SendAsync(HttpMethod.Post, "/internal/livestreams", payloadJson, "CreateLivestream", true))
				{
					if (response == null)
					{
						return null;
					}

					string responseBody = await response.Content.ReadAsStringAsync();
					return JsonSerializer.Deserialize<RelayLivestreamResponse>(responseBody);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] Relay CreateLivestream failed for lobby {lobbyId}: {ex.Message}");
				return null;
			}
		}

		public static async Task<RelayTokenResponse?> CreateStreamTokenAsync(long lobbyId, long userId)
		{
			try
			{
				string payloadJson = JsonSerializer.Serialize(new { lobby_id = lobbyId, user_id = userId });

				using (var response = await SendAsync(HttpMethod.Post, "/internal/stream_tokens", payloadJson, "CreateStreamToken", true))
				{
					if (response == null)
					{
						return null;
					}

					string responseBody = await response.Content.ReadAsStringAsync();
					return JsonSerializer.Deserialize<RelayTokenResponse>(responseBody);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] Relay CreateStreamToken failed for lobby {lobbyId} user {userId}: {ex.Message}");
				return null;
			}
		}

		// The relay answers "no such live session" with {"detail": {"code": "stream_ended", ...}}.
		// Anything else 404ing on that path is a routing problem, not an ended stream.
		private static bool IsStreamEndedBody(string responseBody)
		{
			if (String.IsNullOrEmpty(responseBody))
			{
				return false;
			}

			try
			{
				using (JsonDocument document = JsonDocument.Parse(responseBody))
				{
					if (document.RootElement.ValueKind == JsonValueKind.Object &&
						document.RootElement.TryGetProperty("detail", out JsonElement detail) &&
						detail.ValueKind == JsonValueKind.Object &&
						detail.TryGetProperty("code", out JsonElement code) &&
						code.ValueKind == JsonValueKind.String)
					{
						return code.GetString() == "stream_ended";
					}
				}
			}
			catch (JsonException)
			{
			}

			return false;
		}

		// A priority ticket lets the relay bypass its byte-level broadcast-delay hold for that watcher.
		public static async Task<RelayWatchTicketResult> CreateWatchTicketAsync(long lobbyId, long userId, bool priority = false)
		{
			try
			{
				string payloadJson = JsonSerializer.Serialize(new { lobby_id = lobbyId, user_id = userId, priority = priority });

				using (var response = await SendAsync(HttpMethod.Post, "/internal/watch_tickets", payloadJson, "CreateWatchTicket", false))
				{
					if (response == null)
					{
						return new RelayWatchTicketResult { Status = RelayWatchTicketStatus.Failure };
					}

					// Only a 404 carrying the relay's own marker means the stream ended - a bare
					// 404 is a routing problem (wrong base_url, a mishandled proxy prefix) and
					// must not be reported to the player as the stream having ended.
					if ((int)response.StatusCode == 404)
					{
						string notFoundBody = await response.Content.ReadAsStringAsync();
						if (IsStreamEndedBody(notFoundBody))
						{
							return new RelayWatchTicketResult { Status = RelayWatchTicketStatus.StreamEnded };
						}

						Console.WriteLine($"[ERROR] Relay CreateWatchTicket for lobby {lobbyId} got a 404 that did not come from the relay's livestream handler. " +
							$"Check Relay.base_url and any reverse-proxy path prefix. Body: {notFoundBody}");
						return new RelayWatchTicketResult { Status = RelayWatchTicketStatus.Failure };
					}

					if (!response.IsSuccessStatusCode)
					{
						Console.WriteLine($"[ERROR] Relay CreateWatchTicket for lobby {lobbyId} user {userId} returned status {response.StatusCode}.");
						return new RelayWatchTicketResult { Status = RelayWatchTicketStatus.Failure };
					}

					string responseBody = await response.Content.ReadAsStringAsync();
					return new RelayWatchTicketResult
					{
						Status = RelayWatchTicketStatus.Ok,
						Token = JsonSerializer.Deserialize<RelayTokenResponse>(responseBody)
					};
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] Relay CreateWatchTicket failed for lobby {lobbyId} user {userId}: {ex.Message}");
				return new RelayWatchTicketResult { Status = RelayWatchTicketStatus.Failure };
			}
		}
	}
}
