using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
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
    public class EloRefreshResponse
    {
        public Dictionary<long, EloRefreshEntry> data { get; set; }
    }

    public class EloRefreshEntry
    {
        public int rating { get; set; }
        public int matches { get; set; }
        public int? rank { get; set; }
    }

    public static class ExternalLeaderboardsClient
    {
        private static void GetExternalLeaderboardsConfig(out string postUrl, out string getUrl, out string postToken, out string getToken)
        {
            postUrl = string.Empty;
            getUrl = string.Empty;
            postToken = string.Empty;
            getToken = string.Empty;

            if (Program.g_Config == null)
            {
                throw new Exception("Config not loaded");
            }

            IConfigurationSection? configSection = Program.g_Config.GetSection("ExternalLeaderboards");
            if (configSection == null)
            {
                throw new Exception("ExternalLeaderboards section missing in config");
            }

            string? sectionPostUrl = configSection.GetValue<string>("PostUrl");
            string? sectionGetUrl = configSection.GetValue<string>("GetUrl");
            string? sectionPostToken = configSection.GetValue<string>("PostToken");
            string? sectionGetToken = configSection.GetValue<string>("GetToken");

            if (string.IsNullOrEmpty(sectionPostUrl))
            {
                throw new Exception("ExternalLeaderboards PostUrl missing in config");
            }

            if (string.IsNullOrEmpty(sectionGetUrl))
            {
                throw new Exception("ExternalLeaderboards GetUrl missing in config");
            }

            if (string.IsNullOrEmpty(sectionPostToken))
            {
                throw new Exception("ExternalLeaderboards PostToken missing in config");
            }

            if (string.IsNullOrEmpty(sectionGetToken))
            {
                throw new Exception("ExternalLeaderboards GetToken missing in config");
            }

            postUrl = sectionPostUrl;
            getUrl = sectionGetUrl;
            postToken = sectionPostToken;
            getToken = sectionGetToken;
        }

        private static SocketsHttpHandler CreateLeaderboardsHandler()
        {
            return new SocketsHttpHandler()
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };

                    try
                    {
                        await socket.ConnectAsync(entry.AddressList, context.DnsEndPoint.Port, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
        }

        public static async Task PostMatchResultAsync(AppDbContext db, Lobby lobby)
        {
            if (lobby.MatchID == 0)
                return;

            try
            {
                GetExternalLeaderboardsConfig(out string postUrl, out _, out string postToken, out _);

                // Load the match payload
                var matchEntry = await Database.MatchHistory.LoadMatchHistoryEntryAsync(db, (long)lobby.MatchID);
                if (matchEntry == null)
                {
                    Console.WriteLine($"[WARNING] MatchHistory entry not found for match ID {lobby.MatchID}");
                    return;
                }

                // Serialize payload to JSON
                string payloadJson = JsonSerializer.Serialize(matchEntry);

                // Configure Polly wait-and-retry policy with exponential backoff on HTTP/Socket errors
                var retryPolicy = Policy
                    .Handle<HttpRequestException>()
                    .Or<SocketException>()
                    .Or<TaskCanceledException>()
                    .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), (exception, timeSpan, retryCount, context) =>
                    {
                        Console.WriteLine($"[WARNING] External Match ingest POST failed (attempt {retryCount}). Retrying in {timeSpan.TotalSeconds}s. Error: {exception.Message}");
                    });

                HttpResponseMessage? response = null;

                await retryPolicy.ExecuteAsync(async () =>
                {
                    using (var handler = CreateLeaderboardsHandler())
                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(10);

                        using (var request = new HttpRequestMessage(HttpMethod.Post, postUrl))
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", postToken);
                            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                            var sw = Stopwatch.StartNew();
                            response = await client.SendAsync(request);
                            sw.Stop();

                            Console.WriteLine($"[INFO] External Match Ingest POST Response for match {lobby.MatchID} was received in {sw.ElapsedMilliseconds}ms (status: {response.StatusCode}).");

                            // Explicitly verify response success inside execution block to ensure retry triggers on HTTP error statuses
                            response.EnsureSuccessStatusCode();
                        }
                    }
                });

                if (response == null || !response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[ERROR] External Match Ingest POST failed for match {lobby.MatchID}.");
                    return;
                }

                // Only QuickMatch responses are expected to carry a ratings body.
                if (lobby.LobbyType == ELobbyType.QuickMatch)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    var refreshResponse = JsonSerializer.Deserialize<EloRefreshResponse>(responseBody);
                    if (refreshResponse?.data == null)
                    {
                        Console.WriteLine($"[WARNING] External Match Ingest response body contains no data or could not be deserialized: {responseBody}");
                        return;
                    }

                    // Only player IDs that were actually part of this match are valid recipients of an ELO update.
                    var expectedPlayerIds = new HashSet<long>(matchEntry.members.Where(m => m.HasValue).Select(m => m.Value.user_id));

                    foreach (var (userId, updatedPlayer) in refreshResponse.data)
                    {
                        if (!expectedPlayerIds.Contains(userId))
                        {
                            Console.WriteLine($"[WARNING] External Match Ingest response for match {lobby.MatchID} contained unexpected player_id {userId}; skipping (ELO left unchanged).");
                            continue;
                        }

                        int newRating = updatedPlayer.rating;
                        int newMatches = updatedPlayer.matches;

                        // Update in-memory session cache if the player is online
                        var sharedData = WebSocketManager.GetSharedDataForUser(userId);
                        if (sharedData?.GameStats != null)
                        {
                            sharedData.GameStats.EloRating = newRating;
                            sharedData.GameStats.EloMatches = newMatches;
                        }

                        // Call SaveELOData to persist as fallback
                        await Database.Users.SaveELOData(db, userId, new EloData(newRating, newMatches));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Exception during External Match Ingest POST: {ex.Message}");
            }
        }

        public static async Task<EloData?> GetEloFromApi(long playerId)
        {
            try
            {
                GetExternalLeaderboardsConfig(out _, out string getUrl, out _, out string getToken);

                string requestUrl = getUrl.Replace("{playerId}", playerId.ToString());

                using (var handler = CreateLeaderboardsHandler())
                using (var client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromSeconds(10);

                    using (var request = new HttpRequestMessage(HttpMethod.Get, requestUrl))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", getToken);
                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                        var sw = Stopwatch.StartNew();
                        using (var response = await client.SendAsync(request))
                        {
                            sw.Stop();
                            Console.WriteLine($"[INFO] External ELO API call for player {playerId} took {sw.ElapsedMilliseconds}ms (status: {response.StatusCode}).");

                            if (!response.IsSuccessStatusCode)
                            {
                                Console.WriteLine($"[ERROR] External ELO API call failed for player {playerId} with status: {response.StatusCode}");
                                return null;
                            }

                            string responseBody = await response.Content.ReadAsStringAsync();
                            var result = JsonSerializer.Deserialize<EloRefreshResponse>(responseBody);
                            if (result?.data == null || !result.data.TryGetValue(playerId, out var entry))
                            {
                                Console.WriteLine($"[ERROR] External ELO API response for player {playerId} did not contain that player_id or could not be deserialized: {responseBody}");
                                return null;
                            }

                            return new EloData(entry.rating, entry.matches);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Exception during external ELO API call for player {playerId}: {ex.Message}");
                return null;
            }
        }
    }
}
