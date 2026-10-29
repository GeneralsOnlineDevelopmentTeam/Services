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
		private readonly ILogger<ExternalLeaderboardPublicationWorker> _logger;

		public ExternalLeaderboardPublicationWorker(IDbContextFactory<AppDbContext> dbFactory, ILogger<ExternalLeaderboardPublicationWorker> logger)
		{
			_dbFactory = dbFactory;
			_logger = logger;
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
					_logger.LogError(ex, "External leaderboard publication poll failed");
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
					_logger.LogError(ex, "External publication bookkeeping failed for match {MatchId}", item.MatchId);
					throw;
				}
			}

			await RecordBacklogAge(stoppingToken);
			return pending.Count;
		}

		// separate from publishing so a telemetry query failure never blocks it
		private async Task RecordBacklogAge(CancellationToken stoppingToken)
		{
			try
			{
				await using AppDbContext db = await _dbFactory.CreateDbContextAsync(stoppingToken);
				DateTime? oldest = await Database.MatchHistory.GetOldestUnpublishedMatchTime(db, stoppingToken);
				AppMetrics.RecordExternalPublicationBacklogAge(oldest is { } finishedAt ? DateTime.UtcNow - finishedAt : TimeSpan.Zero);
			}
			catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
			{
				_logger.LogWarning(ex, "External publication backlog age could not be read");
			}
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
					_logger.LogError(ex, "Match {MatchId} was ingested, but its optional ratings response could not be applied", item.MatchId);
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
				_logger.LogWarning(ex, "Failed to publish match {MatchId} to the external leaderboard on attempt {Attempt}", item.MatchId, item.Attempt);
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
					_logger.LogWarning("External Match Ingest response for QuickMatch {MatchId} contained no ratings body", matchId);
				return;
			}

			EloRefreshResponse? refreshResponse;
			try
			{
				refreshResponse = JsonSerializer.Deserialize<EloRefreshResponse>(responseBody);
			}
			catch (JsonException ex)
			{
				_logger.LogError(ex, "External Match Ingest response for match {MatchId} could not be deserialized", matchId);
				return;
			}

			if (refreshResponse?.data == null)
			{
				_logger.LogWarning("External Match Ingest response for match {MatchId} contained no ratings data", matchId);
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
					_logger.LogWarning("External Match Ingest response for match {MatchId} contained unexpected player ID {UserId}; skipping", matchId, userId);
					continue;
				}

				if (updatedPlayer?.overall == null || updatedPlayer.season == null)
				{
					_logger.LogWarning("External Match Ingest response for match {MatchId} contained incomplete ratings for player ID {UserId}; skipping", matchId, userId);
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
						_logger.LogWarning("External Match Ingest response for match {MatchId} referenced missing user ID {UserId}; skipping", matchId, userId);
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

			AppMetrics.RecordExternalPublicationFailure(retry ? "retrying" : "exhausted");
			if (!retry)
			{
				_logger.LogError(publicationException, "External publication for match {MatchId} stopped after {Attempt} attempts", item.MatchId, item.Attempt);
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
