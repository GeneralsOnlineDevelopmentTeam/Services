using GenOnlineService.Controllers;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.Json;

namespace GenOnlineService
{
	public sealed class ExternalLeaderboardPublicationWorker : BackgroundService
	{
		private static readonly TimeSpan c_PollInterval = TimeSpan.FromSeconds(1);
		private static readonly TimeSpan[] c_RetryDelays = new[]
		{
			TimeSpan.FromSeconds(2),
			TimeSpan.FromSeconds(4),
			TimeSpan.FromSeconds(15),
			TimeSpan.FromSeconds(30),
			TimeSpan.FromMinutes(1),
			TimeSpan.FromMinutes(2),
			TimeSpan.FromMinutes(2)
		};
		private const int c_MaxBatchSize = 8;

		private readonly IDbContextFactory<AppDbContext> _dbFactory;

		public ExternalLeaderboardPublicationWorker(IDbContextFactory<AppDbContext> dbFactory)
		{
			_dbFactory = dbFactory;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					int processed = await PublishPendingMatches(stoppingToken);
					if (processed > 0)
						continue;
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[ERROR] External leaderboard publication poll failed: {ex}");
					SentrySdk.CaptureException(ex);
				}

				try
				{
					await Task.Delay(c_PollInterval, stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
			}
		}

		private async Task<int> PublishPendingMatches(CancellationToken stoppingToken)
		{
			// This worker is intentionally a single consumer. Use a distributed queue before
			// running more than one service instance.
			List<Database.ExternalPublicationWorkItem> pending;
			await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(stoppingToken))
			{
				pending = await Database.MatchHistory.GetPendingExternalPublications(
					db,
					DateTime.UtcNow,
					c_MaxBatchSize,
					stoppingToken);
			}

			foreach (Database.ExternalPublicationWorkItem item in pending)
			{
				stoppingToken.ThrowIfCancellationRequested();

				try
				{
					await PublishItem(item, stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[ERROR] External publication bookkeeping failed for match {item.MatchId}: {ex}");
					SentrySdk.CaptureException(ex);
					throw;
				}
			}

			return pending.Count;
		}

		private async Task PublishItem(
			Database.ExternalPublicationWorkItem item,
			CancellationToken stoppingToken)
		{
			try
			{
				MatchHistory_Entry? matchEntry;
				await using (AppDbContext loadDb = await _dbFactory.CreateDbContextAsync(stoppingToken))
				{
					matchEntry = await Database.MatchHistory.LoadMatchHistoryEntryAsync(
						loadDb,
						item.MatchId,
						stoppingToken);
				}

				if (matchEntry == null)
					throw new InvalidOperationException($"MatchHistory entry not found for match ID {item.MatchId}.");

				string responseBody = await ExternalLeaderboardsClient.PostMatchResultAsync(matchEntry, stoppingToken);
				try
				{
					await ApplyRatingsResponse(matchEntry, responseBody, stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[WARNING] Match {item.MatchId} was ingested, but its optional ratings response could not be applied: {ex}");
					SentrySdk.CaptureException(ex);
				}

				await using AppDbContext completionDb = await _dbFactory.CreateDbContextAsync(stoppingToken);
				await Database.MatchHistory.MarkExternalPublicationSucceeded(
					completionDb,
					(ulong)item.MatchId,
					item.Attempt,
					stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[WARNING] Failed to publish match {item.MatchId} to the external leaderboard (attempt {item.Attempt}): {ex.Message}");
				await RecordFailure(item, ex, stoppingToken);
			}
		}

		private async Task ApplyRatingsResponse(
			MatchHistory_Entry matchEntry,
			string responseBody,
			CancellationToken stoppingToken)
		{
			long matchId = matchEntry.match_id;
			if (string.IsNullOrWhiteSpace(responseBody))
			{
				if (matchEntry.lobby_type == ELobbyType.QuickMatch)
					Console.WriteLine($"[WARNING] External Match Ingest response for QuickMatch {matchId} contained no ratings body.");
				return;
			}

			EloRefreshResponse? refreshResponse;
			try
			{
				refreshResponse = JsonSerializer.Deserialize<EloRefreshResponse>(responseBody);
			}
			catch (JsonException ex)
			{
				Console.WriteLine($"[WARNING] External Match Ingest response for match {matchId} could not be deserialized: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return;
			}

			if (refreshResponse?.data == null)
			{
				Console.WriteLine($"[WARNING] External Match Ingest response for match {matchId} contained no ratings data.");
				return;
			}

			HashSet<long> expectedPlayerIds = matchEntry.members
				.Where(member => member.HasValue)
				.Select(member => member.GetValueOrDefault().user_id)
				.ToHashSet();
			List<(long UserId, EloData Data)> pendingUpdates = new();

			foreach ((long userId, EloRefreshEntry updatedPlayer) in refreshResponse.data)
			{
				if (!expectedPlayerIds.Contains(userId))
				{
					Console.WriteLine($"[WARNING] External Match Ingest response for match {matchId} contained unexpected player_id {userId}; skipping (ELO left unchanged).");
					continue;
				}

				if (updatedPlayer?.overall == null || updatedPlayer.season == null)
				{
					Console.WriteLine($"[WARNING] External Match Ingest response for match {matchId} contained incomplete ratings for player_id {userId}; skipping.");
					continue;
				}

				pendingUpdates.Add((
					userId,
					new EloData(
						updatedPlayer.overall.rating,
						updatedPlayer.season.rating,
						updatedPlayer.overall.matches)));
			}

			if (pendingUpdates.Count == 0)
				return;

			List<(long UserId, EloData Data)> savedUpdates = new(pendingUpdates.Count);
			await using (AppDbContext db = await _dbFactory.CreateDbContextAsync(stoppingToken))
			await using (var transaction = await db.Database.BeginTransactionAsync(stoppingToken))
			{
				foreach ((long userId, EloData data) in pendingUpdates)
				{
					bool saved = await Database.Users.SaveExternalELOData(
						db,
						userId,
						data,
						stoppingToken);

					if (saved)
						savedUpdates.Add((userId, data));
					else
						Console.WriteLine($"[WARNING] External Match Ingest response for match {matchId} referenced missing user_id {userId}; skipping.");
				}

				await transaction.CommitAsync(stoppingToken);
			}

			foreach ((long userId, EloData data) in savedUpdates)
			{
				var sharedData = WebSocketManager.GetSharedDataForUser(userId);
				if (sharedData?.GameStats == null)
					continue;

				sharedData.GameStats.EloRating = data.Rating;
				sharedData.GameStats.EloMatches = data.NumMatches;
				sharedData.GameStats.MonthlyEloRating = data.MonthlyRating;
			}
		}

		private async Task RecordFailure(
			Database.ExternalPublicationWorkItem item,
			Exception publicationException,
			CancellationToken stoppingToken)
		{
			bool retry = IsRetryable(publicationException) && item.Attempt <= c_RetryDelays.Length;
			DateTime? nextAttemptAt = retry
				? DateTime.UtcNow.Add(c_RetryDelays[item.Attempt - 1])
				: null;
			await using AppDbContext db = await _dbFactory.CreateDbContextAsync(stoppingToken);

			await Database.MatchHistory.MarkExternalPublicationFailed(
				db,
				(ulong)item.MatchId,
				item.Attempt,
				nextAttemptAt,
				publicationException.Message,
				stoppingToken);

			if (!retry)
			{
				Console.WriteLine($"[ERROR] External publication for match {item.MatchId} stopped after {item.Attempt} attempts.");
				SentrySdk.CaptureException(publicationException);
			}
		}

		private static bool IsRetryable(Exception exception)
		{
			if (exception is HttpRequestException httpException)
			{
				if (httpException.StatusCode is not HttpStatusCode status)
					return true;

				if ((int)status >= 400 && (int)status < 500)
					return status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

				return true;
			}

			// Database, configuration and other infrastructure failures are retried. Only
			// explicit permanent HTTP client errors stop immediately.
			return true;
		}
	}
}
