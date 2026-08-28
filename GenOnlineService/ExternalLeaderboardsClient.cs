using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GenOnlineService.Controllers;
using Microsoft.Extensions.Configuration;

namespace GenOnlineService
{
    public class EloRefreshResponse
    {
        public Dictionary<long, EloRefreshEntry> data { get; set; } = null!;
    }

    public class EloRefreshEntry
    {
        public EloRefreshRating overall { get; set; } = null!;
        public EloRefreshRating season { get; set; } = null!;
    }

    public class EloRefreshRating
    {
        public int rating { get; set; }
        public int matches { get; set; }
    }

    public static class ExternalLeaderboardsClient
    {
        private static IConfigurationSection GetExternalLeaderboardsConfigSection()
        {
            if (Program.g_Config == null)
                throw new Exception("Config not loaded");

            IConfigurationSection configSection = Program.g_Config.GetSection("ExternalLeaderboards");
            if (!configSection.Exists())
                throw new Exception("ExternalLeaderboards section missing in config");

            return configSection;
        }

        private static void GetExternalLeaderboardsPostConfig(out string postUrl, out string postToken)
        {
            IConfigurationSection configSection = GetExternalLeaderboardsConfigSection();

            string? sectionPostUrl = configSection.GetValue<string>("PostUrl");
            string? sectionPostToken = configSection.GetValue<string>("PostToken");

            if (string.IsNullOrEmpty(sectionPostUrl))
                throw new Exception("ExternalLeaderboards PostUrl missing in config");

            if (string.IsNullOrEmpty(sectionPostToken))
                throw new Exception("ExternalLeaderboards PostToken missing in config");

            postUrl = sectionPostUrl;
            postToken = sectionPostToken;
        }

        private static void GetExternalLeaderboardsConfig(out string getUrl, out string getToken)
        {
            IConfigurationSection configSection = GetExternalLeaderboardsConfigSection();

            string? sectionGetUrl = configSection.GetValue<string>("GetUrl");
            string? sectionGetToken = configSection.GetValue<string>("GetToken");

            if (string.IsNullOrEmpty(sectionGetUrl))
                throw new Exception("ExternalLeaderboards GetUrl missing in config");

            if (string.IsNullOrEmpty(sectionGetToken))
                throw new Exception("ExternalLeaderboards GetToken missing in config");

            getUrl = sectionGetUrl;
            getToken = sectionGetToken;
        }

        // NOTE: A single shared HttpClient/handler is used for every call. Creating one per request re-resolves DNS,
        // prevents TCP connection reuse and leaks sockets in TIME_WAIT, which leads to port exhaustion when a lot of
        // matches finish at once. PooledConnectionLifetime keeps DNS changes from being cached forever.
        private static readonly Lazy<HttpClient> g_LeaderboardsClient = new Lazy<HttpClient>(() =>
        {
            return new HttpClient(CreateLeaderboardsHandler(), disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
        }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        private static SocketsHttpHandler CreateLeaderboardsHandler()
        {
            return new SocketsHttpHandler()
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
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

        public static async Task<string> PostMatchResultAsync(MatchHistory_Entry matchEntry, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(matchEntry);

            long matchID = matchEntry.match_id;
            if (matchID <= 0)
                throw new ArgumentOutOfRangeException(nameof(matchEntry), "Match ID must be greater than zero.");

            GetExternalLeaderboardsPostConfig(out string postUrl, out string postToken);

            // The external ingest endpoint must deduplicate repeated submissions by match_id.
            string payloadJson = JsonSerializer.Serialize(matchEntry);

            string responseBody = string.Empty;
            HttpClient client = g_LeaderboardsClient.Value;

            using (var request = new HttpRequestMessage(HttpMethod.Post, postUrl))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", postToken);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                var sw = Stopwatch.StartNew();
                using (HttpResponseMessage response = await client.SendAsync(request, cancellationToken))
                {
                    sw.Stop();

                    Console.WriteLine($"[INFO] External Match Ingest POST Response for match {matchID} was received in {sw.ElapsedMilliseconds}ms (status: {response.StatusCode}).");

                    responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = responseBody.Length <= 256 ? responseBody : responseBody[..256];
                        throw new HttpRequestException(
                            $"External Match Ingest returned {(int)response.StatusCode} ({response.StatusCode}): {errorBody}",
                            null,
                            response.StatusCode);
                    }
                }
            }

            return responseBody;
        }

        public static async Task<EloData?> GetEloFromApi(long playerId)
        {
            try
            {
                GetExternalLeaderboardsConfig(out string getUrl, out string getToken);

                string requestUrl = getUrl.Replace("{playerId}", playerId.ToString());

                HttpClient client = g_LeaderboardsClient.Value;

                {
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

                            return new EloData(entry.overall.rating, entry.season.rating, entry.overall.matches);
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
