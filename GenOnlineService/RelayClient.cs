using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
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
		private static readonly HttpClient s_httpClient = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(10)
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

			return !string.IsNullOrEmpty(expectedKey) && suppliedKey == expectedKey;
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
			// Configure Polly wait-and-retry policy with exponential backoff on HTTP/Socket errors
			return Policy
				.Handle<HttpRequestException>()
				.Or<SocketException>()
				.Or<TaskCanceledException>()
				.WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), (exception, timeSpan, retryCount, context) =>
				{
					Console.WriteLine($"[WARNING] Relay {description} call failed (attempt {retryCount}). Retrying in {timeSpan.TotalSeconds}s. Error: {exception.Message}");
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

		public static async Task<RelayWatchTicketResult> CreateWatchTicketAsync(long lobbyId, long userId)
		{
			try
			{
				string payloadJson = JsonSerializer.Serialize(new { lobby_id = lobbyId, user_id = userId });

				using (var response = await SendAsync(HttpMethod.Post, "/internal/watch_tickets", payloadJson, "CreateWatchTicket", false))
				{
					if (response == null)
					{
						return new RelayWatchTicketResult { Status = RelayWatchTicketStatus.Failure };
					}

					// 404 means the relay session is gone — the stream ended. Everything else
					// non-success is a relay failure.
					if ((int)response.StatusCode == 404)
					{
						return new RelayWatchTicketResult { Status = RelayWatchTicketStatus.StreamEnded };
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
