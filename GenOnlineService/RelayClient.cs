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
		// Every relay call sits inside a request a player is waiting on (they have just pressed
		// "stream" or "watch"), so the budget is tight on purpose: a relay that is not answering
		// in a couple of seconds is not going to answer usefully, and the player is better served
		// by a prompt failure than by a client that appears to hang.
		private static readonly HttpClient s_httpClient = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(5)
		};

		/// <summary>Whether the relay feature is enabled. Off by default — the relay config is
		/// intentionally optional on day one, so without an explicit `enabled: true` (and a
		/// base_url) the livestream endpoints must not attempt any relay call.</summary>
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
			// enabled). A missing value means off — the feature ships inert.
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

		/// <summary>Whether a key supplied on an inbound relay call (X-Relay-Key) matches the
		/// configured ingress key. The relay authenticates to GO with this credential
		/// (Relay.ingress_api_key), distinct from the api_key GO sends to the relay.</summary>
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
			// Wait-and-retry with a deliberately short budget. These calls are made while a
			// player waits on the response, so the whole policy has to fit inside a request
			// they will sit through: two retries at 400ms and 800ms, which covers a dropped
			// connection or a relay restart without turning a sick relay into a minute-long
			// hang. Anything slower than this is a failure worth surfacing.
			return Policy
				.Handle<HttpRequestException>()
				.Or<SocketException>()
				.Or<TaskCanceledException>()
				.WaitAndRetryAsync(2, retryAttempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, retryAttempt)), (exception, timeSpan, retryCount, context) =>
				{
					Console.WriteLine($"[WARNING] Relay {description} call failed (attempt {retryCount}). Retrying in {timeSpan.TotalMilliseconds}ms. Error: {exception.Message}");
				});
		}

		// Sends a relay request with the shared retry policy and auth header. When throwOnError is
		// true, non-success statuses raise inside the retry block (so they are retried) and any
		// failure surfaces as null. When false, non-success statuses are returned as-is — the
		// caller must interpret them (e.g. a relay 404 "stream ended" is valid, not a failure).
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

		// priority: privileged watchers (admin or user_priority = Viewer) get a priority
		// watch ticket; the relay lets those connections bypass its byte-level
		// broadcast-delay hold (plans/relay/relay-server-side-delay-hold.md).
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

					// A 404 means the relay session is gone — the stream ended — but only when it
					// carries the relay's own marker. A bare 404 came from something else on the
					// path (wrong base_url, a reverse proxy that mishandled the prefix) and must
					// not be reported to the player as "the stream ended", because the stream is
					// very likely fine and the deployment is not.
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
