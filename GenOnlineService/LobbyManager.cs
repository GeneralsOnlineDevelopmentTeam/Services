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

using Amazon.S3.Model;
using Discord;
using GenOnlineService.Controllers;
using Microsoft.EntityFrameworkCore;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace GenOnlineService
{
	internal static class FullMeshCheckProtocol
	{
		// TODO: Remove the zero-valued response compatibility path when legacy
		// clients are no longer supported.
		internal static bool IsLegacyResponse(WebSocketMessage_FullMeshConnectivityCheckResponseFromUser response)
		{
			return response.mesh_check_id == 0 && response.attempt == 0;
		}

		internal static bool MatchesCurrentAttempt(
			WebSocketMessage_FullMeshConnectivityCheckResponseFromUser response,
			Int64 currentCheckID,
			int currentAttempt)
		{
			return IsLegacyResponse(response)
				? currentAttempt == 1
				: response.mesh_check_id == currentCheckID && response.attempt == currentAttempt;
		}

		internal static bool ShouldRetry(bool meshComplete, bool hasLegacyResponse, int currentAttempt, int maxAttempts)
		{
			// TODO: Remove legacy retry suppression together with the legacy response path.
			return !meshComplete && !hasLegacyResponse && currentAttempt < maxAttempts;
		}
	}

	// Core:full_mesh_check_* in appsettings.json, read on use
	internal static class FullMeshCheckSettings
	{
		// how long an attempt waits for every connection before judging it
		internal static int AttemptWindowMs => Get("full_mesh_check_attempt_window_ms", 8000);

		// how often an incomplete attempt asks members for a fresh snapshot
		internal static int SnapshotIntervalMs => Get("full_mesh_check_snapshot_interval_ms", 1000);

		// time given to re-signalled connections before the next attempt
		internal static int RetryDelayMs => Get("full_mesh_check_retry_delay_ms", 3000);

		internal static int MaxAttempts => Get("full_mesh_check_max_attempts", 2);

		// upper bound for a whole check, used as the clients' setup timeout
		internal static int MaxDurationMs => (AttemptWindowMs * MaxAttempts) + (RetryDelayMs * (MaxAttempts - 1));

		private static int Get(string key, int defaultValue)
		{
			int value = Program.g_Config?.GetSection("Core").GetValue(key, defaultValue) ?? defaultValue;
			return value > 0 ? value : defaultValue;
		}
	}

	public class Lobby
	{
		private static readonly ILogger s_log = AppLog.For(typeof(Lobby));

		public Int64 LobbyID { get; private set; } = -1;
		public Int64 Owner { get; private set; } = -1;
		public string Name { get; private set; } = "";
		public ELobbyState State { get; private set; } = ELobbyState.UNKNOWN;
		public string MapName { get; private set; } = "";
		public string MapPath { get; private set; } = "";
		public bool IsMapOfficial { get; private set; } = false;
		public UInt64 MatchID { get; private set; } = 0;

		public DateTime TimeCreated { get; private set; } = DateTime.UtcNow;

		[JsonIgnore]
		public bool PendingFullMeshConnectivityChecks { get; private set; } = false;

		[JsonIgnore]
		public Int64 TimeStartFullMeshChecks { get; private set; } = -1;

		// Single per-lobby exclusive gate for every mutation of this lobby's Members/Owner, slot
		// state/fields, ready state, and mesh-check state. Everything that used to be split across
		// g_SlotLock (slots) and m_FullMeshCheckLock (mesh-check fields) now goes through this one
		// SemaphoreSlim, so there is exactly one lock to reason about and no lock-ordering hazard
		// between the two. It is NOT reentrant: a callback passed to RunExclusiveAsync must never
		// call back into RunExclusiveAsync on this same lobby, or it will deadlock - callers that are
		// already inside a callback (e.g. DoHostMigration, called from RemoveMemberAsync's callback) must
		// mutate state directly instead. Sending a websocket message from inside a callback is safe:
		// QueueWebsocketSend only enqueues bytes onto an in-memory bounded channel (a non-blocking
		// TryWrite) - it never performs real network I/O itself, so it cannot block while the gate is
		// held.
		private readonly SemaphoreSlim m_LobbyGate = new SemaphoreSlim(1, 1);

#if DEBUG
		// Debug-only reentrancy guard. AsyncLocal flows through the awaited async chain (and into
		// fire-and-forget children too, since ExecutionContext is captured when they're created), so
		// a callback that calls back into RunExclusiveAsync on this same lobby - the exact bug class
		// HOST_ACTION_KICK_USER hit - throws immediately here instead of deadlocking (if awaited) or
		// silently racing (if not). Cheap enough to leave on for tests; compiled out of Release.
		private readonly AsyncLocal<bool> m_bGateHeldInThisFlow = new();
#endif

		public async Task RunExclusiveAsync(Func<Task> action)
		{
#if DEBUG
			if (m_bGateHeldInThisFlow.Value)
			{
				throw new InvalidOperationException(
					$"Lobby {LobbyID}: RunExclusiveAsync was re-entered on the same lobby within the same async flow. " +
					"This gate is not reentrant and a nested call would deadlock (or race, if not awaited) in Release.");
			}
#endif
			await m_LobbyGate.WaitAsync();
#if DEBUG
			m_bGateHeldInThisFlow.Value = true;
#endif
			try
			{
				await action();
			}
			finally
			{
#if DEBUG
				m_bGateHeldInThisFlow.Value = false;
#endif
				m_LobbyGate.Release();
			}
		}

		public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action)
		{
#if DEBUG
			if (m_bGateHeldInThisFlow.Value)
			{
				throw new InvalidOperationException(
					$"Lobby {LobbyID}: RunExclusiveAsync was re-entered on the same lobby within the same async flow. " +
					"This gate is not reentrant and a nested call would deadlock (or race, if not awaited) in Release.");
			}
#endif
			await m_LobbyGate.WaitAsync();
#if DEBUG
			m_bGateHeldInThisFlow.Value = true;
#endif
			try
			{
				return await action();
			}
			finally
			{
#if DEBUG
				m_bGateHeldInThisFlow.Value = false;
#endif
				m_LobbyGate.Release();
			}
		}

		[JsonIgnore]
		public bool? LastFullMeshConnectivityCheckOutcome { get; private set; } = null;

		[JsonIgnore]
		public int FullMeshCheckAttempt { get; private set; } = 0;

		[JsonIgnore]
		public Int64 FullMeshCheckID { get; private set; } = 0;

		[JsonIgnore]
		private Int64 m_TimeToRetryFullMeshChecks = -1;

		[JsonIgnore]
		private Int64 m_TimeNextFullMeshSnapshotRequest = -1;

		// per member, the peers they reported as still connecting in the current attempt
		[JsonIgnore]
		private Dictionary<Int64, HashSet<Int64>> m_FullMeshConnecting = new();

		[JsonIgnore]
		private bool m_bCurrentAttemptHasLegacyResponse = false;

		private static Int64 s_NextFullMeshCheckID = 0;

		[JsonIgnore]
		public ConcurrentDictionary<Int64, ConcurrentList<Int64>> FullMeshConnectivityChecks { get; set; } = new();


		// AC Probes
		[JsonIgnore]
		ConcurrentDictionary<Int64, int> m_dictProbe1_Sent = new();

		[JsonIgnore]
		ConcurrentDictionary<Int64, int> m_dictProbe2_Sent = new();

		[JsonIgnore]
		ConcurrentDictionary<Int64, int> m_dictProbe1_Received = new();

		[JsonIgnore]
		ConcurrentDictionary<Int64, int> m_dictProbe2_Received = new();
		public void RegisterProbeSent_Type1(Int64 userID)
		{
			// AddOrUpdate is atomic; the previous check-then-write on the dictionary indexer
			// could lose an increment when two probes for the same user race.
			m_dictProbe1_Sent.AddOrUpdate(userID, 1, (_, count) => count + 1);
		}

		public void RegisterProbeSent_Type2(Int64 userID)
		{
			m_dictProbe2_Sent.AddOrUpdate(userID, 1, (_, count) => count + 1);
		}

		public void RegisterProbeResponse_Type1(Int64 userID)
		{
			m_dictProbe1_Received.AddOrUpdate(userID, 1, (_, count) => count + 1);
		}

		public async Task RegisterProbeResponse_Malformed_Type1(Int64 userID)
		{
			// register immediately
			using var scope = ServiceLocator.Services.CreateScope();
			var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
			await using var db = await factory.CreateDbContextAsync();
			await Database.AntiCheat.FlagAccountForReview_SuspectProbes(db, userID, MatchID, "Malformed probe response (Type 1)");
		}

		public void RegisterProbeResponse_Type2(Int64 userID)
		{
			m_dictProbe2_Received.AddOrUpdate(userID, 1, (_, count) => count + 1);
		}

		public async Task RegisterProbeResponse_Malformed_Type2(Int64 userID)
		{
			// register immediately
			using var scope = ServiceLocator.Services.CreateScope();
			var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
			await using var db = await factory.CreateDbContextAsync();
			await Database.AntiCheat.FlagAccountForReview_SuspectProbes(db, userID, MatchID, "Malformed probe response (Type 2)");
		}

		// End AC Probes
		private sealed record MeshParticipant(LobbyMember Member, UserSession Session, UserWebSocketInstance? Socket);
		private Dictionary<long, MeshParticipant> m_MeshParticipants = new();
		private MeshParticipant? m_MeshHost;
		private long m_MembershipVersion;
		private long m_MembershipVersionAtMeshCheckStart;
		[JsonIgnore]
		public string? LastFullMeshCheckFailureReason { get; private set; }

		public Task<long> StartMeshCheckAsync(UserSession? requester = null, UserWebSocketInstance? socket = null)
		{
			return RunExclusiveAsync(() =>
			{
				var host = GetMemberFromUserID(Owner);
				if (State != ELobbyState.GAME_SETUP || host == null || !host.GetSession().TryGetTarget(out var hostSession)
					|| (requester != null && (!ReferenceEquals(hostSession, requester)
						|| !ReferenceEquals(WebSocketManager.GetWebSocketForSession(requester), socket))))
					return Task.FromResult(0L);

				if (requester != null)
				{
					foreach (var member in Members)
						if (member.SlotState == EPlayerType.SLOT_OPEN) member.SetPlayerSlotState(EPlayerType.SLOT_CLOSED);
					DirtyRetransmit();
				}
				FullMeshCheckID = Interlocked.Increment(ref s_NextFullMeshCheckID);
				FullMeshCheckAttempt = 1;
				m_MembershipVersionAtMeshCheckStart = m_MembershipVersion;
				m_MeshParticipants = new();
				foreach (var member in Members.Where(member => member.IsHuman()))
					if (member.GetSession().TryGetTarget(out var session))
						m_MeshParticipants[member.UserID] = new(member, session, WebSocketManager.GetWebSocketForSession(session));
				m_MeshHost = m_MeshParticipants.GetValueOrDefault(Owner);
				m_TimeToRetryFullMeshChecks = -1;
				LastFullMeshConnectivityCheckOutcome = null;
				LastFullMeshCheckFailureReason = null;
				BeginFullMeshConnectivityCheckAttempt();
				if (EnsureMeshCheckIsCurrentLocked()) SendFullMeshConnectivityCheckRequestToMembers();
				return Task.FromResult(FullMeshCheckID);
			});
		}

		private bool IsCurrentMeshParticipant(MeshParticipant participant) =>
			ReferenceEquals(GetMemberFromUserID(participant.Session.m_UserID), participant.Member)
			&& participant.Member.GetSession().TryGetTarget(out var session) && ReferenceEquals(session, participant.Session)
			&& !session.IsRetired && participant.Socket?.IsOpen == true
			&& ReferenceEquals(WebSocketManager.GetWebSocketForSession(session), participant.Socket);

		private bool EnsureMeshCheckIsCurrentLocked()
		{
			if (FullMeshCheckID == 0) return false;
			string? reason = State != ELobbyState.GAME_SETUP ? "lobby_state_changed"
				: m_MembershipVersion != m_MembershipVersionAtMeshCheckStart
				|| m_MeshParticipants.Count != GetNumberOfHumans() ? "membership_changed"
				: m_MeshParticipants.Values.Any(participant => !IsCurrentMeshParticipant(participant)) ? "session_changed"
				: m_MeshHost == null || Owner != m_MeshHost.Session.m_UserID ? "host_changed" : null;
			if (reason == null) return true;
			InvalidateMeshCheck(reason);
			return false;
		}

		private void InvalidateMeshCheck(string reason)
		{
			if (PendingFullMeshConnectivityChecks) CompleteMeshCheck(false, reason, new());
			else if (FullMeshCheckID != 0)
			{
				LastFullMeshConnectivityCheckOutcome = false;
				LastFullMeshCheckFailureReason = reason;
			}
		}

		internal Task InvalidateMeshForSessionAsync(UserSession session) => RunExclusiveAsync(() =>
		{
			if (m_MeshParticipants.Values.Any(participant => ReferenceEquals(participant.Session, session)))
				InvalidateMeshCheck("session_changed");
			return Task.CompletedTask;
		});

		public Task<bool?> GetValidatedMeshCheckResultAsync(long checkID) => RunExclusiveAsync(() =>
		{
			EnsureMeshCheckIsCurrentLocked();
			return Task.FromResult(checkID == FullMeshCheckID && checkID != 0
				? LastFullMeshConnectivityCheckOutcome : (bool?)false);
		});

		private void CompleteMeshCheck(bool success, string? reason, List<MissingConnectionEntry> missing)
		{
			if (!PendingFullMeshConnectivityChecks) return;
			PendingFullMeshConnectivityChecks = false;
			TimeStartFullMeshChecks = -1;
			m_TimeToRetryFullMeshChecks = -1;
			LastFullMeshConnectivityCheckOutcome = success;
			LastFullMeshCheckFailureReason = reason;
			if (m_MeshHost == null || Owner != m_MeshHost.Session.m_UserID || !IsCurrentMeshParticipant(m_MeshHost)) return;
			var outcome = new WebSocketMessage_FullMeshConnectivityCheckOutcome
			{
				msg_id = (int)EWebSocketMessageID.FULL_MESH_CONNECTIVITY_CHECK_RESPONSE_COMPLETE_TO_HOST,
				mesh_check_id = FullMeshCheckID, attempt = FullMeshCheckAttempt,
				mesh_complete = success, reason = reason ?? string.Empty, missing_connections = missing
			};
			m_MeshHost.Session.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outcome)));
		}

		private void BeginFullMeshConnectivityCheckAttempt()
		{
			PendingFullMeshConnectivityChecks = true;
			FullMeshConnectivityChecks = new();
			m_FullMeshConnecting = new();
			m_bCurrentAttemptHasLegacyResponse = false;
			TimeStartFullMeshChecks = Environment.TickCount64;
			m_TimeNextFullMeshSnapshotRequest = TimeStartFullMeshChecks + FullMeshCheckSettings.SnapshotIntervalMs;
		}

		private bool IsFullMeshPairStillConnecting(Int64 userA, Int64 userB)
		{
			return (m_FullMeshConnecting.TryGetValue(userA, out HashSet<Int64>? fromA) && fromA.Contains(userB))
				|| (m_FullMeshConnecting.TryGetValue(userB, out HashSet<Int64>? fromB) && fromB.Contains(userA));
		}

		// one line per judged attempt, so the window can be tuned from real connect times
		private void LogFullMeshCheckAttempt(bool bMeshComplete, List<MissingConnectionEntry> lstMissingConnections)
		{
			Int64 elapsedMS = Environment.TickCount64 - TimeStartFullMeshChecks;
			string strMissing = string.Join(", ", lstMissingConnections
				.Select(c => (Math.Min(c.source_user_id, c.target_user_id), Math.Max(c.source_user_id, c.target_user_id)))
				.Distinct()
				.Select(p => $"{p.Item1}<->{p.Item2}"));
			string strConnecting = string.Join(", ", lstMissingConnections
				.Where(c => IsFullMeshPairStillConnecting(c.source_user_id, c.target_user_id))
				.Select(c => (Math.Min(c.source_user_id, c.target_user_id), Math.Max(c.source_user_id, c.target_user_id)))
				.Distinct()
				.Select(p => $"{p.Item1}<->{p.Item2}"));

			s_log.LogInformation("Lobby {LobbyId}: mesh check {MeshCheckId} attempt {Attempt}/{MaxAttempts} {MeshResult} after {ElapsedMs} ms with {HumanCount} humans{MeshDetail}",
				LobbyID, FullMeshCheckID, FullMeshCheckAttempt, FullMeshCheckSettings.MaxAttempts,
				bMeshComplete ? "complete" : "incomplete", elapsedMS, GetNumberOfHumans(),
				bMeshComplete ? "" : $", missing {strMissing}{(strConnecting.Length > 0 ? $" (still connecting {strConnecting})" : "")}");
		}

		private void SendFullMeshConnectivityCheckRequestToMembers()
		{
			WebSocketMessage_FullMeshConnectivityCheckRequest startCommand = new WebSocketMessage_FullMeshConnectivityCheckRequest();
			startCommand.msg_id = (int)EWebSocketMessageID.FULL_MESH_CONNECTIVITY_CHECK_RESPONSE;
			startCommand.mesh_check_id = FullMeshCheckID;
			startCommand.attempt = FullMeshCheckAttempt;
			byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(startCommand));

			foreach (LobbyMember member in Members)
			{
				if (member.GetSession().TryGetTarget(out UserSession? session) && session != null)
				{
					session.QueueWebsocketSend(bytesJSON);
				}
			}
		}

		// Re-issues signalling between the pairs that failed to connect. This is the same handshake a player
		// gets when they join, which is why manually rejoining the lobby often repairs a broken mesh.
		private void RestartSignallingForMissingConnections(List<MissingConnectionEntry> lstMissingConnections)
		{
			HashSet<(Int64, Int64)> alreadyResignalled = new();

			foreach (MissingConnectionEntry missingConnection in lstMissingConnections)
			{
				Int64 lowUserID = Math.Min(missingConnection.source_user_id, missingConnection.target_user_id);
				Int64 highUserID = Math.Max(missingConnection.source_user_id, missingConnection.target_user_id);

				if (!alreadyResignalled.Add((lowUserID, highUserID)))
				{
					continue;
				}

				LobbyMember? sourceMember = GetMemberFromUserID(missingConnection.source_user_id);
				LobbyMember? targetMember = GetMemberFromUserID(missingConnection.target_user_id);

				if (sourceMember == null || targetMember == null)
				{
					continue;
				}

				s_log.LogDebug("Lobby {LobbyId}: re-signalling {SourceUserId} <-> {TargetUserId} before mesh check retry", LobbyID, sourceMember.UserID, targetMember.UserID);

				SendStartSignallingToMember(sourceMember, targetMember);
				SendStartSignallingToMember(targetMember, sourceMember);
			}
		}

		private void SendStartSignallingToMember(LobbyMember recipient, LobbyMember peer)
		{
			if (recipient.GetSession().TryGetTarget(out UserSession? recipientSession) && recipientSession != null)
			{
				WebSocketMessage_NetworkStartSignalling signallingMsg = new WebSocketMessage_NetworkStartSignalling();
				signallingMsg.msg_id = (int)EWebSocketMessageID.NETWORK_CONNECTION_START_SIGNALLING;
				signallingMsg.lobby_id = LobbyID;
				signallingMsg.user_id = peer.UserID;
				signallingMsg.preferred_port = peer.Port;
				signallingMsg.middleware_id = peer.MiddlewareUserID;
				recipientSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(signallingMsg)));
			}
		}

		public Task StoreMeshResponseAsync(UserSession source, UserWebSocketInstance socket,
			WebSocketMessage_FullMeshConnectivityCheckResponseFromUser response) => RunExclusiveAsync(() =>
		{
			// Legacy zero IDs cannot distinguish successive checks on the same socket.
			if (!EnsureMeshCheckIsCurrentLocked() || !PendingFullMeshConnectivityChecks || m_TimeToRetryFullMeshChecks != -1
				|| !m_MeshParticipants.TryGetValue(source.m_UserID, out var participant)
				|| !ReferenceEquals(participant.Session, source) || !ReferenceEquals(participant.Socket, socket)
				|| !FullMeshCheckProtocol.MatchesCurrentAttempt(response, FullMeshCheckID, FullMeshCheckAttempt))
				return Task.CompletedTask;
			m_bCurrentAttemptHasLegacyResponse |= FullMeshCheckProtocol.IsLegacyResponse(response);
			FullMeshConnectivityChecks[source.m_UserID] = new(response.connectivity_map ?? new());
			m_FullMeshConnecting[source.m_UserID] = new(response.connecting_map ?? new());
			ProcessPendingFullMeshConnectivityChecksInternal();
			return Task.CompletedTask;
		});

		public async Task ProcessPendingFullMeshConnectivityChecks()
		{
			await RunExclusiveAsync(() =>
			{
				ProcessPendingFullMeshConnectivityChecksInternal();
				return Task.CompletedTask;
			});
		}

		// Must only be called while holding m_LobbyGate (via RunExclusiveAsync).
		private void ProcessPendingFullMeshConnectivityChecksInternal()
		{
			if (!EnsureMeshCheckIsCurrentLocked() || !PendingFullMeshConnectivityChecks) return;
			if (m_TimeToRetryFullMeshChecks != -1)
			{
				if (Environment.TickCount64 < m_TimeToRetryFullMeshChecks) return;
				m_TimeToRetryFullMeshChecks = -1;
				BeginFullMeshConnectivityCheckAttempt();
				SendFullMeshConnectivityCheckRequestToMembers();
				return;
			}

			var missing = new List<MissingConnectionEntry>();
			foreach (var source in m_MeshParticipants.Keys)
				foreach (var target in m_MeshParticipants.Keys)
					if (source != target && (!FullMeshConnectivityChecks.TryGetValue(source, out var connected) || !connected.Contains(target)))
						missing.Add(new() { source_user_id = source, target_user_id = target });
			bool disabled = Program.g_Config?.GetSection("Core").GetValue<bool>("disable_full_mesh_check") == true;
			bool allReported = m_MeshParticipants.Keys.All(FullMeshConnectivityChecks.ContainsKey);
			bool complete = disabled || (allReported && missing.Count == 0);
			bool attemptTimedOut = Environment.TickCount64 - TimeStartFullMeshChecks >= FullMeshCheckSettings.AttemptWindowMs;
			if (!complete && !attemptTimedOut)
			{
				if (Environment.TickCount64 >= m_TimeNextFullMeshSnapshotRequest)
				{
					m_TimeNextFullMeshSnapshotRequest = Environment.TickCount64 + FullMeshCheckSettings.SnapshotIntervalMs;
					SendFullMeshConnectivityCheckRequestToMembers();
				}
				return;
			}
			LogFullMeshCheckAttempt(complete, missing);
			if (FullMeshCheckProtocol.ShouldRetry(complete, m_bCurrentAttemptHasLegacyResponse, FullMeshCheckAttempt, FullMeshCheckSettings.MaxAttempts))
			{
				++FullMeshCheckAttempt;
				RestartSignallingForMissingConnections(missing.Where(connection => !IsFullMeshPairStillConnecting(connection.source_user_id, connection.target_user_id)).ToList());
				m_TimeToRetryFullMeshChecks = Environment.TickCount64 + FullMeshCheckSettings.RetryDelayMs;
				return;
			}
			CompleteMeshCheck(complete, complete ? null : allReported ? "missing_connections" : "timeout", disabled ? new() : missing);
		}

		public void AddPassword(string password)
		{
			if (IsPassworded && Password == password)
			{
				return;
			}

			Password = password;
			IsPassworded = true;
			DirtyRetransmitLobbyList();
		}

		public void RemovePassword()
		{
			if (!IsPassworded && Password.Length == 0)
			{
				return;
			}

			Password = String.Empty;
			IsPassworded = false;
			DirtyRetransmitLobbyList();
		}

		public double GetLatitude() { return m_dHostLatitude; }
		public double GetLongitude() { return m_dHostLongitude; }

		public async Task SetMatchID(UInt64 a_matchID)
		{
#if DEBUG
			MatchID = 123456;
#else
			MatchID = a_matchID;
#endif

			// Reported outcomes are per-match, not per-lobby. Nothing guards a lobby from entering INGAME
			// more than once, so drop anything carried over from a previous match on this lobby.
			ReportedOutcomes.Clear();

			// store on each player
			foreach (LobbyMember member in Members)
			{
				if (member.GetSession().TryGetTarget(out UserSession? session))
				{
					session.RegisterHistoricMatchID(MatchID, member.SlotIndex, member.Side, TimeCreated);
				}
			}

			// for each member, if this is QM, and they havent played 10 games, flag it for review, its a new user
			if (LobbyType == ELobbyType.QuickMatch)
			{
				foreach (LobbyMember member in Members)
				{
					if (member != null)
					{
						var shared = GenOnlineService.WebSocketManager.GetSharedDataForUser(member.UserID);
						if (shared != null)
						{
							if (shared.GameStats.EloMatches < 10)
							{
								using var scope = ServiceLocator.Services.CreateScope();
								var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
								await using var db = await factory.CreateDbContextAsync();
								await Database.AntiCheat.FlagAccountForReview_NewAccount_FirstMatches(db, member.UserID, MatchID);
							}
						}
					}
				}
			}

			DirtyRetransmit();
		}

		// NOTE: Do not use Members.Length on a lobby. It will include empty slots. Use NumCurrentPlayers instead
		public int NumCurrentPlayers
		{
			get
			{
				// TODO: More optimal to store this instead of looping for it every time
				int currentPlayers = 0;
				foreach (LobbyMember member in Members)
				{
					if (member.SlotState != EPlayerType.SLOT_CLOSED && member.SlotState != EPlayerType.SLOT_OPEN)
					{
						++currentPlayers;
					}
				}

				return currentPlayers;
			}
		}
		//public int MaxPlayers { get; private set; } = 0;
		public int MaxPlayers
		{
			get
			{
				// TODO: More optimal to store this instead of looping for it every time
				int maxPlayers = 0;
				foreach (LobbyMember member in Members)
				{
					if (member.SlotState != EPlayerType.SLOT_CLOSED)
					{
						++maxPlayers;
					}
				}

				return maxPlayers;
			}
		}

		public bool IsVanillaTeamsOnly { get; private set; } = false;
		public UInt32 StartingCash { get; private set; } = 0;
		public bool IsLimitSuperweapons { get; private set; } = false;
		public bool IsTrackingStats { get; private set; } = false;
		public bool IsPassworded { get; private set; } = false;

		[JsonIgnore] // never serialize the password, we only need it on the service
		public string Password { get; private set; } = String.Empty;

		public bool AllowObservers { get; private set; } = false;
		public UInt32 ExeCRC { get; private set; } = 0;
		public UInt32 IniCRC { get; private set; } = 0;

		public Int16 NetworkRoomID { get; private set; } = -1;

		public int RNGSeed { get; private set; } = -1;

		public ELobbyType LobbyType { get; private set; } = ELobbyType.CustomGame;

		// quick match lobbies only admit the players the matchmaker paired
		public HashSet<Int64> MatchedUserIDs { get; } = new();

		public string Region { get; private set; } = "";
		public int EstimatedLatency { get; private set; } = 999999;

		public UInt16 MaximumCameraHeight { get; private set; } = GenOnlineService.Constants.g_DefaultCameraMaxHeight;

        [JsonIgnore] // This is not serialized as the client doesn't need to know, the service checks it
        public ELobbyJoinability LobbyJoinability { get; private set; } = ELobbyJoinability.Public; // public by default


        public const int MaxLobbySize = 8;
		public LobbyMember[] Members { get; private set; } = new LobbyMember[MaxLobbySize];

		public EKnownAnticheatID AnticheatID { get; private set; }  = EKnownAnticheatID.NONE;

		[JsonIgnore]
		public ConcurrentDictionary<Int64, DateTime> TimeMemberLeft { get; private set; } = new();

		// Records the first time each player's in-game WebSocket connection dropped (i.e., when they first "quit"
		// while the match was in progress). Only the first disconnect is stored � reconnects do not reset it.
		// Used by DetermineLobbyWinnerIfNotPresent to find who abandoned first (= loser) vs last (= winner).
		// NOTE: These are mutated from HTTP/websocket threads and the lobby tick loop at the same time, so they must
		// be concurrent collections.
		[JsonIgnore]
		public ConcurrentDictionary<Int64, DateTime> TimePlayerAbandonedIngame { get; private set; } = new();

		// Records which players submitted their own /Outcome for this match, and what they claimed.
		// Kept here rather than on UserSession because a disconnected player's session is destroyed before
		// the lobby closes - and that player is exactly the one the winner determination needs to reason
		// about. Used by DetermineLobbyWinnerIfNotPresent to tell "reported a loss" apart from "never
		// reported", which the stored won flag alone cannot express (it defaults to false).
		[JsonIgnore]
		public ConcurrentDictionary<Int64, bool> ReportedOutcomes { get; private set; } = new();

		/// <summary>
		/// Records the moment a player's WebSocket dropped while the lobby was in INGAME state.
		/// Only the FIRST disconnect is stored; subsequent reconnect/disconnect cycles are ignored
		/// so that a player who briefly loses connection is not penalised more than the player who
		/// intentionally killed the game first.
		/// </summary>
		public void RecordPlayerIngameAbandon(Int64 userId)
		{
			DateTime abandonTime = DateTime.UtcNow;
			if (TimePlayerAbandonedIngame.TryAdd(userId, abandonTime))
			{
				s_log.LogInformation("Lobby {LobbyId}: recorded in-game abandon for user {UserId} at {AbandonTime}", LobbyID, userId, abandonTime);
			}
		}

		/// <summary>
		/// Removes the in-game abandon timestamp for a player who successfully reconnected.
		/// This ensures a future disconnect records the correct (later) quit time.
		/// </summary>
		public void ClearPlayerIngameAbandon(Int64 userId)
		{
			if (TimePlayerAbandonedIngame.TryRemove(userId, out _))
			{
				s_log.LogInformation("Lobby {LobbyId}: cleared in-game abandon record for reconnected user {UserId}", LobbyID, userId);
			}
		}

		/// <summary>
		/// Records that a player reported their own match outcome. Only the FIRST report is kept: the
		/// outcome endpoint can be called repeatedly for the same match, and a later call must not be
		/// able to flip a result that was already reported.
		/// </summary>
		public void RecordPlayerOutcome(Int64 userId, bool bWon)
		{
			if (ReportedOutcomes.TryAdd(userId, bWon))
			{
				s_log.LogInformation("Lobby {LobbyId}: recorded reported outcome for user {UserId}, won={Won}", LobbyID, userId, bWon);
			}
		}

		// Initial sync retries only resend lobby state.
		private int m_lobbyUpdatePending = 0;
		private int m_lobbyListUpdatePending = 0;

		[JsonIgnore]
		private Int64 m_LastInitialSync = Environment.TickCount64;

		[JsonIgnore]
		private int m_InitialSyncs = 0;

		[JsonIgnore]
		private Int64 m_NextProbe = 0;

		// used for ping calculation but never sent to clients
		[JsonIgnore]
		private double m_dHostLatitude = 0;

		[JsonIgnore]
		private double m_dHostLongitude = 0;

		public Lobby(Int64 lobby_id, UserSession owner, string name, ELobbyState state, string map_name, string map_path, bool vanilla_teams, UInt32 starting_cash, bool limit_superweapons,
			bool track_stats, bool passworded, string password, bool map_official, int rng_seed, Int16 network_room, bool allow_observers, UInt16 max_cam_height, UInt32 exe_crc, UInt32 ini_crc,
			int max_players, ELobbyType lobbyType, EKnownAnticheatID inAnticheatID)
		{
			LobbyID = lobby_id;
			Owner = owner.m_UserID;

			string strAnticheatName = "OTHER AC";
			if (inAnticheatID == EKnownAnticheatID.NONE)
			{
				strAnticheatName = "\u26C9";
			}
			else if (inAnticheatID == EKnownAnticheatID.GO_INTEGRATED_AC)
			{
				strAnticheatName = "\u26CA";
			}
			else if (inAnticheatID == EKnownAnticheatID.EASY_ANTICHEAT)
			{
				strAnticheatName = "\u26CA";
			}


			Name = String.Format("[{0}][{1}] {2}", owner.m_strContinent, strAnticheatName, name);
			Region = String.Format("{0}", owner.GetFullContinentName());
			m_dHostLatitude = owner.m_dLatitude;
			m_dHostLongitude = owner.m_dLongitude;
			State = state;
			MapName = map_name;
			MapPath = FixMapPathForGame(map_path);
			IsMapOfficial = map_official;
			IsVanillaTeamsOnly = vanilla_teams;
			StartingCash = starting_cash;
			IsLimitSuperweapons = limit_superweapons;
			IsTrackingStats = track_stats;
			IsPassworded = passworded;
			Password = password;
			AllowObservers = allow_observers;
			ExeCRC = exe_crc;
			IniCRC = ini_crc;
			NetworkRoomID = network_room;
			RNGSeed = rng_seed;
			MaximumCameraHeight = max_cam_height;
			LobbyType = lobbyType;
			Members = new LobbyMember[MaxLobbySize];
			AnticheatID = inAnticheatID;

			// create default slots
			for (UInt16 i = 0; i < MaxLobbySize; ++i)
			{
				LobbyMember placeholderMember = new LobbyMember(this, null, -1, String.Empty, String.Empty, 0, -1, -1, -1, i < max_players ? EPlayerType.SLOT_OPEN : EPlayerType.SLOT_CLOSED, i, true);
				Members[i] = placeholderMember;
			}

        }

        public event Action<Lobby>? OnLobbyNeedsDestroyed;

		// Must only be called while holding m_LobbyGate (from within RemoveMemberAsync's callback): it reads
		// and mutates Members/Owner and must observe RemoveMemberAsync's slot clear atomically with any
		// concurrent join/leave/migration.
		private void OnAfterPlayerLeftLocked(Int64 leavingUserID)
		{
			// NOTE: By the time this is called, the member is no longer in the members list
			bool bNeedsHostMigrate = Owner == leavingUserID;

			// we need human members, not real members
			int numHumanMembers = GetNumberOfHumans();

			if (numHumanMembers == 0)
			{
				s_log.LogInformation("Lobby {LobbyId}: last human member left, requesting destruction", LobbyID);

				OnLobbyNeedsDestroyed?.Invoke(this);
			}
			else
			{
				// Host migration is only meaningful in the lobby setup phase.
				// During an active game the P2P host is determined by the game engine,
				// and migrating the server-side owner causes confusing mid-game lobby
				// state broadcasts to surviving clients.
				if (bNeedsHostMigrate && State != ELobbyState.INGAME)
				{
					DoHostMigration();
				}
			}
		}

public async Task FinalizeACChecks()
		{
			// do AC checks
			foreach (var kvPair in m_dictProbe1_Sent)
			{
				Int64 userID = kvPair.Key;

				if (!m_dictProbe1_Received.ContainsKey(userID))
				{
					m_dictProbe1_Received[userID] = 0;
				}

				if (m_dictProbe1_Received[userID] != kvPair.Value)
				{
					using var scope = ServiceLocator.Services.CreateScope();
					var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
					await using var db = await factory.CreateDbContextAsync();
					await Database.AntiCheat.FlagAccountForReview_SuspectProbes(db, userID, MatchID, String.Format("Missing probe response (Type 1): Sent {0}, Got {1} responses", kvPair.Value, m_dictProbe1_Received[userID]));
				}
			}

			foreach (var kvPair in m_dictProbe2_Sent)
			{
				Int64 userID = kvPair.Key;

				if (!m_dictProbe2_Received.ContainsKey(userID))
				{
					m_dictProbe2_Received[userID] = 0;
				}

				if (m_dictProbe2_Received[userID] != kvPair.Value)
				{
					using var scope = ServiceLocator.Services.CreateScope();
					var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
					await using var db = await factory.CreateDbContextAsync();
					await Database.AntiCheat.FlagAccountForReview_SuspectProbes(db, userID, MatchID, String.Format("Missing probe response (Type 1): Sent {0}, Got {1} responses", kvPair.Value, m_dictProbe2_Received[userID]));
				}
			}
		}

		// Runs under the per-lobby gate: this mutates slot state and previously ran unguarded,
		// racing against AddMember/RemoveMemberAsync/other slot updates. Sequential with (never nested
		// inside) StartMeshCheckAsync's own gate use - callers await this first and then
		// await StartMeshCheckAsync as a separate gated operation, so the gate is never
		// re-entered.
		public async Task CloseOpenSlots()
		{
			await RunExclusiveAsync(() =>
			{
				foreach (LobbyMember member in Members)
				{
					if (member.SlotState == EPlayerType.SLOT_OPEN)
					{
						member.SetPlayerSlotState(EPlayerType.SLOT_CLOSED);
					}
				}

				DirtyRetransmit();
				return Task.CompletedTask;
			});
		}

		// Must only be called while holding m_LobbyGate (currently only true from
		// OnAfterPlayerLeftLocked, itself only called from within RemoveMemberAsync's callback).
		private void DoHostMigration()
		{
			Int64 oldOwner = Owner;

			foreach (LobbyMember member in Members)
			{
				if (member.SlotState == EPlayerType.SLOT_PLAYER)
				{
					if (member.UserID != oldOwner)
					{
						UInt16 oldSlot = member.SlotIndex;

						Owner = member.UserID;

						member.UpdateSlotIndex(0);
						var migrated = (LobbyMember[])Members.Clone();
						migrated[0] = member;
						migrated[oldSlot] = new LobbyMember(this, null, -1, String.Empty, String.Empty, 0, -1, -1, -1, EPlayerType.SLOT_OPEN, oldSlot, true);
						Members = migrated;

						member.SetReadyState(true);
						DirtyRetransmitLobbyList();
						break;
					}
				}
			}
		}

		private void CalculateNextProbeTime(bool bIsFirstProbe)
		{
#if DEBUG
			m_NextProbe = Environment.TickCount64 + (bIsFirstProbe ? 5000 : 30000);
#else
			if (bIsFirstProbe) // 30s
			{
				m_NextProbe = Environment.TickCount64 + 30000;
			}
			else // 5 to 10 min
			{
				int nextProbeInterval = Random.Shared.Next(5, 10);
				m_NextProbe = Environment.TickCount64 + nextProbeInterval * 60000;
			}
#endif
		}

		public async Task Tick()
		{
			await ProcessPendingFullMeshConnectivityChecks();

			if (m_NextProbe != 0 && Environment.TickCount64 >= m_NextProbe)
			{
				// send probe
				{
					foreach (LobbyMember memberEntry in Members)
					{
						// per user endpoint
						string? strUploadURI = await S3CredentialManager.GetPresignedURL(EMetadataFileType.FILE_TYPE_SCREENSHOT, EScreenshotType.SCREENSHOT_TYPE_GAMEPLAY, MatchID, memberEntry.UserID, memberEntry.SlotIndex, TimeCreated);

						if (strUploadURI != null) // should never be null really
						{
							WebSocketMessage_Probe probe = new WebSocketMessage_Probe();
							probe.msg_id = (int)EWebSocketMessageID.PROBE;
							probe.url = strUploadURI;
							byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(probe));

							if (memberEntry.GetSession().TryGetTarget(out UserSession? session))
							{
								session.QueueWebsocketSend(bytesJSON);
							}
						}
					}
				}

				// send probe 2
				{
					WebSocketMessage_Simple probe = new WebSocketMessage_Simple();
					probe.msg_id = (int)EWebSocketMessageID.WS_KEEPALIVE;
					byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(probe));

					foreach (LobbyMember memberEntry in Members)
					{
						if (memberEntry.GetSession().TryGetTarget(out UserSession? session))
						{
							// TODO_AC: We should increment the number of expected screenshots here (individually for each user, since people might've quit), so we can compare later
							session.QueueWebsocketSend(bytesJSON);

							RegisterProbeSent_Type2(memberEntry.UserID);
						}
					}
				}

				// calculate next probe time
				CalculateNextProbeTime(false);
			}

			if (m_InitialSyncs < 5 && Environment.TickCount64 - m_LastInitialSync > 200)
			{
				m_LastInitialSync = Environment.TickCount64;
				Interlocked.Exchange(ref m_lobbyUpdatePending, 1);
				++m_InitialSyncs;
			}

			if (Interlocked.Exchange(ref m_lobbyUpdatePending, 0) != 0)
			{
				WebSocketMessage_CurrentLobbyUpdate lobbyUpdate = new WebSocketMessage_CurrentLobbyUpdate();
				lobbyUpdate.msg_id = (int)EWebSocketMessageID.LOBBY_CURRENT_LOBBY_UPDATE;

				byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lobbyUpdate));

				foreach (LobbyMember memberEntry in Members)
				{
					if (memberEntry.GetSession().TryGetTarget(out UserSession? session))
					{
						UserSession? sess = WebSocketManager.GetSessionFromUser(session.m_UserID, session.GetSessionType());
						if (sess != null)
						{
							s_log.LogDebug("Lobby {LobbyId}: sending WebSocket lobby update", LobbyID);
							sess.QueueWebsocketSend(bytesJSON);
						}
					}
				}
			}

			if (Interlocked.Exchange(ref m_lobbyListUpdatePending, 0) != 0)
			{
				WebSocketManager.QueueLobbyListUpdateForViewers(NetworkRoomID);
			}
		}

		internal static async Task<LobbyMembershipStatus> TransferMemberAsync(Lobby destination, IReadOnlyCollection<Lobby> sources,
			JoinLobbyCommand command, UserSession session, string displayName, UserLobbyPreferences preferences)
		{
			var locked = sources.Append(destination).Distinct().OrderBy(lobby => lobby.LobbyID).ToArray();
			int acquired = 0;
			try
			{
				// The caller holds the user's membership gate. All multi-lobby operations use
				// ascending IDs; only synchronous state changes and queue writes follow.
				foreach (var lobby in locked)
				{
					await lobby.m_LobbyGate.WaitAsync();
					++acquired;
				}
				var admission = destination.ValidateJoin(command);
				if (admission != LobbyMembershipStatus.Success) return admission;
				var added = destination.AddMemberLocked(session, displayName, command.PreferredPort, command.HasMap, preferences);
				if (added.Member == null) return added.Status;
				// Destination admission and insertion must succeed before releasing old membership.
				foreach (var source in sources)
				{
					if (ReferenceEquals(source, destination)) continue;
					var member = source.GetMemberFromUserID(command.UserID);
					if (member != null && source.RemoveMemberLocked(member)) source.PublishMemberRemoval(member);
				}
				destination.PublishMemberAddition(session, added.Member);
				return LobbyMembershipStatus.Success;
			}
			finally
			{
				for (int index = acquired - 1; index >= 0; --index) locked[index].m_LobbyGate.Release();
			}
		}

		internal Task<bool> TryMarkCompleteIfEmptyAsync(long? expectedOwner = null, bool setupOnly = false) =>
			RunExclusiveAsync(() =>
			{
				if ((expectedOwner.HasValue && Owner != expectedOwner.Value) || (setupOnly && State != ELobbyState.GAME_SETUP)
					|| GetNumberOfHumans() != 0) return Task.FromResult(false);
				State = ELobbyState.COMPLETE;
				DirtyRetransmitLobbyList();
				return Task.FromResult(true);
			});

		internal Task<LobbyMembershipStatus> AddCreatorAsync(UserSession session, string displayName, ushort preferredPort, UserLobbyPreferences preferences) =>
			RunExclusiveAsync(() =>
			{
				var added = AddMemberLocked(session, displayName, preferredPort, true, preferences);
				if (added.Member != null) PublishMemberAddition(session, added.Member);
				return Task.FromResult(added.Status);
			});

		private (LobbyMembershipStatus Status, LobbyMember? Member) AddMemberLocked(UserSession playerSession, string strDisplayName, UInt16 userPreferredPort, bool bHasMap, UserLobbyPreferences lobbyPrefs)
		{
			if (State != ELobbyState.GAME_SETUP) return (LobbyMembershipStatus.NotInSetup, null);
			if (GetMemberFromUserID(playerSession.m_UserID) != null) return (LobbyMembershipStatus.AlreadyMember, null);

			int openSlot = Array.FindIndex(Members, member => member.SlotState == EPlayerType.SLOT_OPEN);
			if (openSlot < 0) return (LobbyMembershipStatus.NoOpenSlot, null);
			ushort slotIndex = (ushort)openSlot;

			// QuickMatch applies social eligibility during matchmaking.
			if (LobbyType == ELobbyType.CustomGame)
			{
				var ownerData = WebSocketManager.GetSharedDataForUser(Owner);
				if (ownerData != null)
				{
					var social = ownerData.GetSocialContainer();
					if (social.Blocked.Contains(playerSession.m_UserID)) return (LobbyMembershipStatus.BlockedByOwner, null);
					if (LobbyJoinability == ELobbyJoinability.FriendsOnly && !social.Friends.Contains(playerSession.m_UserID))
						return (LobbyMembershipStatus.FriendshipRequired, null);
				}
			}

			string strOriginalDisplayName = strDisplayName;
			int dupesSeen = 0;
			string strNameLower = strDisplayName.ToLower();
			foreach (var memberEntry in Members)
			{
				if (memberEntry.DisplayNameNotDeduped.ToLower() == strNameLower)
				{
					++dupesSeen;
				}
			}

			if (dupesSeen > 0)
			{
				strDisplayName = String.Format("{0} ({1})", strDisplayName, dupesSeen);
			}

			// only apply lobby prefs if not QM
			LobbyMember? newMember = null;
			if (LobbyType == ELobbyType.CustomGame)
			{

				// if vanilla teams, dont apply favorite
				int sideToUse = lobbyPrefs.favorite_side;
				if (IsVanillaTeamsOnly)
				{
					sideToUse = -1;
				}

				// if our preferred color is already in use, revert to random
				int colorToUse = lobbyPrefs.favorite_color;
				foreach (var memberEntry in Members)
				{
					// NOTE: only occupied slots matter - empty/closed placeholder slots have Color 0, which would
					// otherwise always force players who prefer color 0 onto a random color
					if (memberEntry.IsHuman() && memberEntry.Color == lobbyPrefs.favorite_color)
					{
						colorToUse = -1;
						break;
					}
				}

				newMember = new LobbyMember(this, playerSession, playerSession.m_UserID, strDisplayName, strOriginalDisplayName, userPreferredPort, sideToUse, colorToUse, -1, EPlayerType.SLOT_PLAYER, slotIndex, bHasMap);
			}
			else
			{
				// NOTE: In quick match, we need to pick their team, client doesn't do it for us.
				int[] allowedTeams =
				[
					2, // USA
					3, // CHINA
					4, // GLA
					5, // USA Super Weapon
					6, // USA Laser
					7, // USA Airforce
					8, // China Tank
					9, // China Infantry
					10, // China Nuke
					11, // GLA Toxin
					12, // GLA Demo
					13 // GLA Stealth
				];

				int sideToUse = allowedTeams[Random.Shared.Next(0, allowedTeams.Length)];

				// team is random for now, matchmaker will assign teams on start
				newMember = new LobbyMember(this, playerSession, playerSession.m_UserID, strDisplayName, strOriginalDisplayName, userPreferredPort, sideToUse, -1, -1, EPlayerType.SLOT_PLAYER, slotIndex, bHasMap);
			}

			Members[slotIndex] = newMember;
			++m_MembershipVersion;
			InvalidateMeshCheck("membership_changed");
			TimeMemberLeft[playerSession.m_UserID] = DateTime.UnixEpoch;

			// Lobby members leave public-room presence.
			playerSession.TryUpdateSessionNetworkRoom(-1);

			playerSession.UpdateSessionLobbyID(LobbyID);

			return (LobbyMembershipStatus.Success, newMember);
		}

		private void PublishMemberAddition(UserSession playerSession, LobbyMember newMember)
		{
			// START NETWORK SIGNALLING
			// send all existing player to new player and vice-versa
			foreach (LobbyMember memberEntry in Members)
			{
				if (memberEntry != newMember) // NOT us
				{
					if (memberEntry.GetSession().TryGetTarget(out UserSession? remoteSession))
					{
						if (playerSession != null)
						{
							// send signal start to joining player
							WebSocketMessage_NetworkStartSignalling joiningPlayerMsg = new WebSocketMessage_NetworkStartSignalling();
							joiningPlayerMsg.msg_id = (int)EWebSocketMessageID.NETWORK_CONNECTION_START_SIGNALLING;
							joiningPlayerMsg.lobby_id = LobbyID;
							joiningPlayerMsg.user_id = memberEntry.UserID;
							joiningPlayerMsg.preferred_port = memberEntry.Port;
							joiningPlayerMsg.middleware_id = memberEntry.MiddlewareUserID;
							playerSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(joiningPlayerMsg)));

							// send register player
							WebSocketMessage_ACRegisterPlayer joiningPlayerACMsg = new WebSocketMessage_ACRegisterPlayer();
							joiningPlayerACMsg.msg_id = (int)EWebSocketMessageID.AC_REGISTER_PLAYER;
							joiningPlayerACMsg.user_id = memberEntry.UserID;
							joiningPlayerACMsg.mwid = memberEntry.MiddlewareUserID;
							playerSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(joiningPlayerACMsg)));
						}

						if (remoteSession != null)
						{
							// send the reverse to the existing player
							WebSocketMessage_NetworkStartSignalling existingPlayerMsg = new WebSocketMessage_NetworkStartSignalling();
							existingPlayerMsg.msg_id = (int)EWebSocketMessageID.NETWORK_CONNECTION_START_SIGNALLING;
							existingPlayerMsg.lobby_id = LobbyID;
							existingPlayerMsg.user_id = playerSession.m_UserID;
							existingPlayerMsg.preferred_port = newMember.Port;
							existingPlayerMsg.middleware_id = playerSession.GetMiddlewareID();
							remoteSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(existingPlayerMsg)));

							// send register player
							WebSocketMessage_ACRegisterPlayer existingPlayerACMsg = new WebSocketMessage_ACRegisterPlayer();
							existingPlayerACMsg.msg_id = (int)EWebSocketMessageID.AC_REGISTER_PLAYER;
							existingPlayerACMsg.user_id = playerSession.m_UserID;
							existingPlayerACMsg.mwid = playerSession.GetMiddlewareID();
							remoteSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(existingPlayerACMsg)));
						}
					}
				}
			}
			// END NETWORK SIGNALLING

			// Notify lobby members and room browsers.
			DirtyRetransmitLobbyList();

			s_log.LogInformation("User {UserId} joined lobby {LobbyId} in slot {SlotIndex}", playerSession.m_UserID, LobbyID, newMember.SlotIndex);

		}

		public void SendPeerTeardownToDepartingMember(LobbyMember departingMember)
		{
			if (!departingMember.GetSession().TryGetTarget(out UserSession? departingSession) || departingSession == null)
			{
				return;
			}

			foreach (LobbyMember remoteMember in Members)
			{
				if (remoteMember.SlotState != EPlayerType.SLOT_PLAYER || remoteMember.UserID == departingMember.UserID)
				{
					continue;
				}

				WebSocketMessage_ACDeregisterPlayer remotePlayerAcMsg = new WebSocketMessage_ACDeregisterPlayer();
				remotePlayerAcMsg.msg_id = (int)EWebSocketMessageID.AC_DEREGISTER_PLAYER;
				remotePlayerAcMsg.user_id = remoteMember.UserID;
				remotePlayerAcMsg.mwid = remoteMember.MiddlewareUserID;
				departingSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(remotePlayerAcMsg)));

				if (State != ELobbyState.INGAME)
				{
					WebSocketMessage_NetworkDisconnectPlayer remotePlayerMsg = new WebSocketMessage_NetworkDisconnectPlayer();
					remotePlayerMsg.msg_id = (int)EWebSocketMessageID.NETWORK_CONNECTION_DISCONNECT_PLAYER;
					remotePlayerMsg.lobby_id = LobbyID;
					remotePlayerMsg.user_id = remoteMember.UserID;
					departingSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(remotePlayerMsg)));
				}
			}
		}

		internal async Task<LobbyMember?> RemoveMemberAsync(long userID, LobbyMember? expectedMember = null)
		{
			LobbyMember? removed = await RunExclusiveAsync(() =>
			{
				var member = GetMemberFromUserID(userID);
				return Task.FromResult(member != null && (expectedMember == null || ReferenceEquals(member, expectedMember))
					&& RemoveMemberLocked(member) ? member : null);
			});
			if (removed != null) PublishMemberRemoval(removed);
			return removed;
		}

		internal async Task<LobbyMembershipStatus> KickMemberAsync(KickLobbyMemberCommand command)
		{
			LobbyMember? removed = null;
			var status = await RunExclusiveAsync(() =>
			{
				if (GetMemberFromUserID(command.ActorUserID) == null)
					return Task.FromResult(LobbyMembershipStatus.ActorNotMember);
				if (Owner != command.ActorUserID)
					return Task.FromResult(LobbyMembershipStatus.ActorNotHost);
				if (command.TargetUserID == Owner)
					return Task.FromResult(LobbyMembershipStatus.NoChange);
				var member = command.TargetUserID.HasValue ? GetMemberFromUserID(command.TargetUserID.Value) : null;
				if (member != null && RemoveMemberLocked(member)) removed = member;
				return Task.FromResult(removed != null ? LobbyMembershipStatus.Success : LobbyMembershipStatus.NoChange);
			});
			if (removed != null) PublishMemberRemoval(removed);
			return status;
		}

		// Caller holds the lobby gate; validation and host migration form one transition.
		private bool RemoveMemberLocked(LobbyMember member)
		{
			if (member.SlotIndex < 0
				|| member.SlotIndex >= Members.Length
				|| !ReferenceEquals(Members[member.SlotIndex], member))
			{
				return false;
			}

			LobbyMember placeholderMember = new LobbyMember(this, null, -1, String.Empty, String.Empty, 0, -1, -1, -1, EPlayerType.SLOT_OPEN, member.SlotIndex, true);
			Members[member.SlotIndex] = placeholderMember;
			TimeMemberLeft[member.UserID] = DateTime.UtcNow;

			OnAfterPlayerLeftLocked(member.UserID);
			++m_MembershipVersion;
			InvalidateMeshCheck("membership_changed");

			return true;
		}

		private void PublishMemberRemoval(LobbyMember member)
		{
			// TODO_LOBBY: Optimize this
			Int64 UserID = member.UserID;
			s_log.LogInformation("User {UserId} left lobby {LobbyId}", UserID, LobbyID);

			// AC dergister
			WebSocketMessage_ACDeregisterPlayer remotePlayerAcMsg = new WebSocketMessage_ACDeregisterPlayer();
			remotePlayerAcMsg.msg_id = (int)EWebSocketMessageID.AC_DEREGISTER_PLAYER;
			remotePlayerAcMsg.user_id = member.UserID;
			remotePlayerAcMsg.mwid = member.MiddlewareUserID;
			foreach (LobbyMember remoteMember in Members)
			{
				if (remoteMember.GetSession().TryGetTarget(out UserSession? remoteSession))
				{
					if (remoteSession != null)
					{
						s_log.LogDebug("Lobby {LobbyId}: sent AC deregister for user {UserId} to user {RemoteUserId}", LobbyID, member.UserID, remoteMember.UserID);
						remoteSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(remotePlayerAcMsg)));
					}
				}
			}

			// send signal to disconnect (only if not ingame, ingame we let the client handle it so a service disconnect doesnt end the game)
			if (State != ELobbyState.INGAME)
			{
				WebSocketMessage_NetworkDisconnectPlayer remotePlayerMsg = new WebSocketMessage_NetworkDisconnectPlayer();
				remotePlayerMsg.msg_id = (int)EWebSocketMessageID.NETWORK_CONNECTION_DISCONNECT_PLAYER;
				remotePlayerMsg.lobby_id = LobbyID;
				remotePlayerMsg.user_id = member.UserID;

				// START NETWORK DISCONNECT
				foreach (LobbyMember remoteMember in Members)
				{
					if (remoteMember.GetSession().TryGetTarget(out UserSession? remoteSession))
					{
						if (remoteSession != null)
						{
							s_log.LogDebug("Lobby {LobbyId}: sent network disconnect for user {UserId} to user {RemoteUserId}", LobbyID, member.UserID, remoteMember.UserID);
							remoteSession.QueueWebsocketSend(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(remotePlayerMsg)));
						}
					}
				}
				// END NETWORK DISCONNECT
			}

			DirtyRetransmitLobbyList();
		}

		private LobbyMembershipStatus ValidateJoin(JoinLobbyCommand command)
		{
			if (LobbyType == ELobbyType.QuickMatch && !MatchedUserIDs.Contains(command.UserID))
				return LobbyMembershipStatus.NotMatched;
			if (IsPassworded && command.Password != Password)
				return LobbyMembershipStatus.PasswordMismatch;
			return command.AnticheatID == AnticheatID ? LobbyMembershipStatus.Success : LobbyMembershipStatus.AnticheatMismatch;
		}

		public int GetNumberOfHumans()
		{
			int numHumanMembers = 0;
			foreach (LobbyMember memberEntry in Members)
			{
				if (memberEntry.SlotState == EPlayerType.SLOT_PLAYER) // we only care about human members, AI can't play alone
				{
					++numHumanMembers;
				}
			}

			return numHumanMembers;
		}

		public void GetParticipantBreakdown(out int numHumans, out int numAI, out int numOpen, out int numClosed)
		{
			numHumans = 0;
			numAI = 0;
			numOpen = 0;
			numClosed = 0;

			foreach (LobbyMember memberEntry in Members)
			{
				if (memberEntry.SlotState == EPlayerType.SLOT_OPEN)
				{
					++numOpen;
				}
				else if (memberEntry.SlotState == EPlayerType.SLOT_CLOSED)
				{
					++numClosed;
				}
				else if (memberEntry.SlotState == EPlayerType.SLOT_EASY_AI || memberEntry.SlotState == EPlayerType.SLOT_MED_AI || memberEntry.SlotState == EPlayerType.SLOT_BRUTAL_AI)
				{
					++numAI;
				}
				else if (memberEntry.SlotState == EPlayerType.SLOT_PLAYER)
				{
					++numHumans;
				}
			}
		}

		public void DirtyRetransmit()
		{
			Interlocked.Exchange(ref m_lobbyUpdatePending, 1);
		}

		public void DirtyRetransmitLobbyList()
		{
			DirtyRetransmit();
			Interlocked.Exchange(ref m_lobbyListUpdatePending, 1);
		}

		public async Task DirtyRetransmitToSingleMember(Int64 targetUserID)
		{
			var session = WebSocketManager.GetSessionFromUser(targetUserID, EUserSessionType.GameClient); // lobby member must be a game client
			if (session != null)
			{
				s_log.LogDebug("Lobby {LobbyId}: sending WebSocket lobby update", LobbyID);

				WebSocketMessage_CurrentLobbyUpdate lobbyUpdate = new WebSocketMessage_CurrentLobbyUpdate();
				lobbyUpdate.msg_id = (int)EWebSocketMessageID.LOBBY_CURRENT_LOBBY_UPDATE;
				byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lobbyUpdate));

				session.QueueWebsocketSend(bytesJSON);
			}
		}

		public LobbyMember? GetMemberFromUserID(Int64 user_id)
		{
			foreach (LobbyMember memberEntry in Members)
			{
				if (memberEntry.UserID == user_id && memberEntry.SlotState == EPlayerType.SLOT_PLAYER)
				{
					return memberEntry;
				}
			}
			return null;
		}

		public LobbyMember? GetMemberFromSlot(int slotIndex)
		{
			if (slotIndex >= 0 && slotIndex < Members.Length)
			{
				return Members[slotIndex];
			}

			return null;
		}

		private static String FixMapPathForGame(string strMapPath)
		{
			strMapPath = String.Format(@"{0}\{1}", Path.GetFileNameWithoutExtension(strMapPath), strMapPath);
			return strMapPath;
		}

		public async Task UpdateMap(AppDbContext _db, string strMap, string strMapPath, bool bOfficialMap, int newMaxPlayers)
		{
			int oldMaxPlayers = MaxPlayers;
			MapName = strMap;
			MapPath = FixMapPathForGame(strMapPath);
			IsMapOfficial = bOfficialMap;

			// close any slots that were open and now below the max, open anything not already occupied, up to new max
			foreach (var slot in Members)
			{
				if (slot.SlotIndex < newMaxPlayers)
				{
					if (slot.SlotState == EPlayerType.SLOT_CLOSED)
					{
						slot.SetPlayerSlotState(EPlayerType.SLOT_OPEN);
					}
				}

				if (slot.SlotIndex >= newMaxPlayers)
				{
					if (slot.SlotState == EPlayerType.SLOT_OPEN)
					{
						slot.SetPlayerSlotState(EPlayerType.SLOT_CLOSED);
					}
				}
			}

			// only if official, since we cant guarantee if they log in on another machine that the map is installed
			if (bOfficialMap)
			{
				await Database.Users.SetFavorite_Map(_db, Owner, strMapPath);
			}

			DirtyRetransmitLobbyList();
		}

		public async Task UpdateStartingCash(AppDbContext _db, UInt32 newStartingCash)
		{
			StartingCash = newStartingCash;

			await Database.Users.SetFavorite_StartingMoney(_db, Owner, (int)newStartingCash);

			DirtyRetransmitLobbyList();
		}

		public async Task UpdateLimitSuperweapons(AppDbContext _db, bool bLimitSuperweapons)
		{
			IsLimitSuperweapons = bLimitSuperweapons;

			await Database.Users.SetFavorite_LimitSuperweapons(_db, Owner, bLimitSuperweapons);

			DirtyRetransmitLobbyList();
		}

		public void ForceReady()
		{
			foreach (var member in Members)
			{
				member.SetReadyState(true);
			}

			DirtyRetransmit();
		}

		private int m_cachedAtStart_numHumans = -1;
		private int m_cachedAtStart_numOpen = -1;
		private int m_cachedAtStart_numClosed = -1;
		private int m_cachedAtStart_numAI = -1;

        // TODO: Really, client also shouldnt upload data we arent going to process in this situation, its wasteful
        public bool WasPVPAtStart()
		{
			// debug
#if DEBUG
			return true;
#endif

			// use cached data, we can call this after people left etc

			// We are a PVP lobby if we have > 1 human, and 0 AI
			bool bWasPVP = m_cachedAtStart_numHumans > 1 && m_cachedAtStart_numAI == 0;
			return bWasPVP;
		}

		public bool HadAIAtStart()
		{
			// debug
#if DEBUG
			return false;
#endif

			// use cached data, we can call this after people left etc
			return m_cachedAtStart_numAI > 0;
		}

		public async Task UpdateState(ELobbyState state)
		{
			await RunExclusiveAsync(() =>
			{
				State = state;
				return Task.CompletedTask;
			});

			// if start, init our AC probe
			if (state == ELobbyState.INGAME)
			{
				// cache starting data, we use this later in replay/ss upload
				GetParticipantBreakdown(out int numHumans, out int numAI, out int numOpen, out int numClosed);
				m_cachedAtStart_numHumans = numHumans;
				m_cachedAtStart_numOpen = numOpen;
				m_cachedAtStart_numClosed = numClosed;
				m_cachedAtStart_numAI = numAI;

				// lobby cant have AI and must have at least 2 human players at some point
				if (WasPVPAtStart() && !HadAIAtStart())
				{
					try
					{
						// create placeholder
						using var scope = ServiceLocator.Services.CreateScope();
						var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
						await using var db = await factory.CreateDbContextAsync();
						await Database.MatchHistory.CreatePlaceholderMatchHistory(db, this);
					}
					catch (Exception ex)
					{
						s_log.LogError(ex, "Lobby {LobbyId}: UpdateState placeholder creation failed", LobbyID);
					}

					// calculate first probe time
					CalculateNextProbeTime(true);
				}
				else
				{
					// disable probes
					m_NextProbe = 0;
				}


			}

			DirtyRetransmitLobbyList();
		}

		public void UpdateJoinability(ELobbyJoinability newJoinability)
		{
			if (LobbyType == ELobbyType.CustomGame && LobbyJoinability != newJoinability)
			{
				LobbyJoinability = newJoinability;
				DirtyRetransmitLobbyList();
			}
		}

		public void UpdateMaxCameraHeight(UInt16 maxCamHeight)
		{
			if (maxCamHeight >= 210 && maxCamHeight <= 1000)
			{
				MaximumCameraHeight = maxCamHeight;
				DirtyRetransmit();
			}
		}

		public void ResetReadyStates()
		{
			foreach (LobbyMember member in Members)
			{
				member.SetReadyState(false);
			}

			DirtyRetransmit();
		}

	}
	public class LobbyMember
	{
		public Int64 UserID { get; private set; } = -999999;
		public string DisplayName { get; private set; } = "";

		[JsonIgnore]
		public string DisplayNameNotDeduped { get; set; } = ""; // internal only, used for determining dedupe counts

		private bool m_Ready;
		public bool IsReady
		{
			get
			{
				// host is always ready
				if (SlotIndex == 0)
				{
					return true;
				}

				// AI is always ready
				if (IsAI())
				{
					return true;
				}

				return m_Ready;
			}
			set { m_Ready = value; }
		}

		public UInt16 Port { get; private set; } = 0;

		public void UpdateSlotIndex(UInt16 index)
		{
			SlotIndex = index;
		}
		public int Side { get; private set; } = 0;
		public int Team { get; private set; } = -1;
		public int Color { get; private set; } = 0;
		public int StartingPosition { get; private set; } = 0;
		public bool HasMap { get; private set; } = false;

		public EPlayerType SlotState { get; private set; } = 0;
		public UInt16 SlotIndex { get; private set; } = 0;
		public string Region { get; private set; } = "Unknown";
		public string MiddlewareUserID { get; private set; } = String.Empty;

		[JsonIgnore] // cant serialize refs
		private WeakReference<Lobby?> CurrentLobby = new(null);

		[JsonIgnore]
		private WeakReference<UserSession?> PlayerSession = new(null);

		public WeakReference<UserSession?> GetSession()
		{
			return PlayerSession;
		}

		public LobbyMember(Lobby owningLobby, UserSession? owningSession, Int64 UserID_in, string DisplayName_in, string strUndedupedDisplayName, UInt16 Port_in, int Side_in, int Color_in, int StartingPosition_in, EPlayerType SlotState_in, UInt16 SlotIndex_in, bool bHasMap_in)
		{
			CurrentLobby = new WeakReference<Lobby?>(owningLobby);
			PlayerSession = new WeakReference<UserSession?>(owningSession);

			UserID = UserID_in;
			DisplayName = DisplayName_in;
			DisplayNameNotDeduped = strUndedupedDisplayName;
			Port = Port_in;
			Side = Side_in;
			Color = Color_in;
			StartingPosition = StartingPosition_in;
			HasMap = bHasMap_in;
			SlotState = SlotState_in;
			SlotIndex = SlotIndex_in;

			// default slots are created with null
			if (owningSession != null)
			{
				MiddlewareUserID = owningSession.GetMiddlewareID();
			}
			else
			{
				MiddlewareUserID = String.Empty;
			}

			IsReady = false;
			Region = owningSession == null ? "Unknown" : owningSession.GetFullContinentName();
		}

		public bool IsHuman() {  return SlotState == EPlayerType.SLOT_PLAYER; }
		public bool IsAI() { return SlotState == EPlayerType.SLOT_EASY_AI || SlotState == EPlayerType.SLOT_MED_AI || SlotState == EPlayerType.SLOT_BRUTAL_AI; }

		private void DirtyRetransmit()
		{
			CurrentLobby.TryGetTarget(out Lobby? lobby);
			lobby?.DirtyRetransmit();
		}

		private void DirtyRetransmitLobbyList()
		{
			CurrentLobby.TryGetTarget(out Lobby? lobby);
			lobby?.DirtyRetransmitLobbyList();
		}

		public void SetReadyState(bool bReady)
		{
			IsReady = bReady;

			DirtyRetransmit();
		}

		public void SetPlayerSlotState(EPlayerType newState)
		{
			SlotState = newState;

			// AI is always ready
			if (IsAI())
			{
				IsReady = true;
			}

			DirtyRetransmitLobbyList();
		}

		public async Task UpdateSide(AppDbContext _db, int newSide, int start_pos)
		{
			Side = newSide;

			await Database.Users.SetFavorite_Side(_db, UserID, newSide);

			DirtyRetransmit();
		}

		public async Task UpdateColor(AppDbContext _db, int newColor)
		{
			Color = newColor;
			await Database.Users.SetFavorite_Color(_db, UserID, newColor);

			DirtyRetransmit();
		}

		public void UpdateStartPos(int startpos)
		{
			StartingPosition = startpos;

			DirtyRetransmit();
		}

		public void UpdateTeam(int team)
		{
			Team = team;

			DirtyRetransmit();
		}

		public void UpdateHasMap(bool bHasMap)
		{
			HasMap = bHasMap;

			DirtyRetransmit();
		}
	}

	public enum ELobbyType
	{
		CustomGame = 0,
		QuickMatch = 1
	}

	public enum EKnownAnticheatID
	{
		NONE = -1,
		GO_INTEGRATED_AC = 0,
		EASY_ANTICHEAT = 9481
	}


	public partial class LobbyManager
	{
		private ConcurrentDictionary<Int64, Lobby> m_dictLobbies = new();

		private Int64 m_NextLobbyID = 0;

		// Concurrent CreateLobby calls (e.g. a custom lobby and a QuickMatch allocation racing) must never be
		// handed the same ID. Interlocked.Increment returns the post-increment value, so subtracting 1 keeps
		// the original "starts at 0" sequence while making the read-and-bump atomic.
		// Internal (not private) so it is unit-testable via InternalsVisibleTo without needing the rest of
		// CreateLobby's database dependencies.
		internal Int64 GenerateNextLobbyID()
		{
			return Interlocked.Increment(ref m_NextLobbyID) - 1;
		}

		private readonly IServiceProvider _services;
		private readonly ILogger<LobbyManager> _logger;

		public LobbyManager(IServiceProvider services, IDbContextFactory<AppDbContext> membershipDbFactory, LobbyMembershipConnections membershipConnections, ILogger<LobbyManager> logger)
		{
			_services = services;
			_logger = logger;
			this.membershipDbFactory = membershipDbFactory;
			this.membershipConnections = membershipConnections;
        }

        public async Task Cleanup()
		{
			// Remove any lobby that has 0 members and has been around for a bit (enough time for host to join)
			List<Lobby> lstLobbiesToRemove = new List<Lobby>();
			foreach (var kvPair in m_dictLobbies)
			{
				Lobby iterLobby = kvPair.Value;

				TimeSpan timeSinceCreated = DateTime.UtcNow.Subtract(iterLobby.TimeCreated);
				if (iterLobby.GetNumberOfHumans() == 0 && timeSinceCreated.TotalMinutes >= 1.0)
				{
					_logger.LogInformation("Garbage collecting lobby {LobbyId}", iterLobby.LobbyID);

					// mark for removal
					lstLobbiesToRemove.Add(iterLobby);
				}
			}

			foreach (Lobby lobbyToRemove in lstLobbiesToRemove)
			{
                // remove it, also commit it + update leaderboard
                if (await lobbyToRemove.TryMarkCompleteIfEmptyAsync()) await DeleteLobby(lobbyToRemove);
            }
		}

		// Lobbies that asked to be destroyed from a synchronous event callback. They are deleted from the tick loop so
		// the delete is properly awaited and its exceptions are observed (an `async void` handler would let a failure
		// escape unobserved and leave the lobby leaked in m_dictLobbies forever).
		private readonly ConcurrentQueue<Lobby> m_queueLobbiesNeedingDestroyed = new();

		private void HandleLobbyNeedsDestroyed(Lobby lobby)
		{
			m_queueLobbiesNeedingDestroyed.Enqueue(lobby);
		}

		public async Task ProcessLobbiesNeedingDestroyed()
		{
			while (m_queueLobbiesNeedingDestroyed.TryDequeue(out Lobby? lobbyToDestroy))
			{
				if (lobbyToDestroy == null)
				{
					continue;
				}

				try
				{
					if (await lobbyToDestroy.TryMarkCompleteIfEmptyAsync()) await DeleteLobby(lobbyToDestroy);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Failed to destroy lobby {LobbyId}", lobbyToDestroy.LobbyID);
				}
			}
		}

		public async Task<Int64> CreateLobby(AppDbContext _db, UserSession owningSession, string strOwnerDisplayName, string strName, string strMapName, string strMapPath, bool bMapOfficial, int maxPlayers, string HostIPAddr,
			UInt16 hostPreferredPort, bool bVanillaTeams, bool bTrackStats, UInt32 default_starting_cash, bool bPassworded, String strPassword, Int16 parentNetworkRoom, bool bAllowObservers,
			UInt16 maxCamHeight, UInt32 exe_crc, UInt32 ini_crc, ELobbyType lobbyType, EKnownAnticheatID anticheatID)
		{
			using var membership = await AcquireMembershipGateAsync(owningSession.m_UserID);
			if (!IsCurrentSession(owningSession) || owningSession.IsRetired) return -1;
			_logger.LogInformation("Creating lobby for user {UserId}", owningSession.m_UserID);
			// cant own two lobbies at once, unless in gameplay
			await CleanupUserLobbiesNotStartedAsync(owningSession.m_UserID);

			_logger.LogDebug("User {UserId} leaving any lobby before creating a lobby", owningSession.m_UserID);
			await LeaveMembershipsAsync(owningSession.m_UserID);

			int rng_seed = new Random().Next();

			Int64 newLobbyID = GenerateNextLobbyID();

			// load and apply user preferences (custom game only)
			bool bLimitSuperweapons = false;
			UInt32 starting_cash = default_starting_cash;
			if (lobbyType == ELobbyType.CustomGame)
			{
				UserLobbyPreferences? lobbyPrefs = await Database.Users.GetUserLobbyPreferences(_db, owningSession.m_UserID);
				bLimitSuperweapons = lobbyPrefs != null ? lobbyPrefs.favorite_limit_superweapons : false; // limit superweapons (NOTE: not present in clientside create lobby UI)

				// sane defaults
				if (lobbyPrefs != null && lobbyPrefs.favorite_starting_money > 0)
				{
					starting_cash = (uint)lobbyPrefs.favorite_starting_money;
				}
			}

			Lobby newLobby = new Lobby(newLobbyID, owningSession, strName, ELobbyState.GAME_SETUP, strMapName, strMapPath, bVanillaTeams, starting_cash, bLimitSuperweapons, bTrackStats, bPassworded, strPassword, bMapOfficial, rng_seed, parentNetworkRoom, bAllowObservers, maxCamHeight, exe_crc, ini_crc, maxPlayers, lobbyType, anticheatID);
			m_dictLobbies[newLobbyID] = newLobby;

			newLobby.OnLobbyNeedsDestroyed += HandleLobbyNeedsDestroyed;

			if (lobbyType != ELobbyType.QuickMatch) // QuickMatch clients join after allocation.
			{
				var preferences = await Database.Users.GetUserLobbyPreferences(_db, owningSession.m_UserID);
				if (preferences != null)
					await newLobby.AddCreatorAsync(owningSession, strOwnerDisplayName, hostPreferredPort, preferences);
			}

			// Send initial lobby state and invalidate room listings.
			newLobby.DirtyRetransmitLobbyList();

			return newLobbyID;
		}

		public async Task Tick()
		{
			foreach (var kvPair in m_dictLobbies)
			{
				await kvPair.Value.Tick();
			}
		}

		public int GetNumLobbies()
		{
			return m_dictLobbies.Count;
		}

		private async Task CleanupUserLobbiesNotStartedAsync(Int64 userID)
		{
			foreach (var lobby in GetPlayerOwnedLobbies(userID))
				if (await lobby.TryMarkCompleteIfEmptyAsync(userID, setupOnly: true)) await DeleteLobby(lobby);
		}

		public List<Lobby> GetAllLobbies(Int16 selectedRoomID, bool bIncludePassword, bool bAllowInSetup, bool bAllowInGame, bool bAllowCompleted, bool bIncludeAllNetworkRooms)
		{
			List<Lobby> listLobbies = new List<Lobby>();
			foreach (var kvp in m_dictLobbies)
			{
				Lobby lobby = kvp.Value;
				if (!bIncludeAllNetworkRooms && !RoomCatalog.CanViewLobby(selectedRoomID, lobby.NetworkRoomID))
				{
					continue;
				}

				bool bMeetsCriteria = true;
				if (lobby.IsPassworded && !bIncludePassword)
				{
					bMeetsCriteria = false;
				}

				// don't allow quickmatch to show
				if (lobby.LobbyType == ELobbyType.QuickMatch)
				{
					bMeetsCriteria = false;
				}

				if (lobby.State == ELobbyState.GAME_SETUP && !bAllowInSetup)
				{
					bMeetsCriteria = false;
				}

				if (lobby.State == ELobbyState.INGAME && !bAllowInGame)
				{
					bMeetsCriteria = false;
				}

				if (lobby.State == ELobbyState.COMPLETE && !bAllowCompleted)
				{
					bMeetsCriteria = false;
				}

				if (bMeetsCriteria)
				{
					listLobbies.Add(lobby);
				}
			}

			return listLobbies;
		}

		public Lobby? GetLobby(Int64 lobbyID)
		{
			if (m_dictLobbies.TryGetValue(lobbyID, out Lobby? lobby))
			{
				return lobby;
			}

			return null;
		}

		public Lobby? GetLobbyFiltered(Int64 lobbyID, bool bIncludePassword, bool bAllowInSetup, bool bAllowInGame, bool bAllowCompleted)
		{
			if (m_dictLobbies.TryGetValue(lobbyID, out Lobby? lobby))
			{
				bool bMeetsCriteria = true;

				if (lobby.IsPassworded && !bIncludePassword)
				{
					bMeetsCriteria = false;
				}

				if (lobby.State == ELobbyState.GAME_SETUP && !bAllowInSetup)
				{
					bMeetsCriteria = false;
				}

				if (lobby.State == ELobbyState.INGAME && !bAllowInGame)
				{
					bMeetsCriteria = false;
				}

				if (lobby.State == ELobbyState.COMPLETE && !bAllowCompleted)
				{
					bMeetsCriteria = false;
				}

				if (bMeetsCriteria)
				{
					return lobby;
				}
				else
				{
					return null;
				}
			}
			return null;
		}

		public Lobby? GetPlayerParticipantLobby(Int64 userID)
		{
			// TODO_LOBBY: Optimize this, maintain a dictionary of userid
			foreach (Lobby lobbyInst in m_dictLobbies.Values)
			{
				if (lobbyInst.GetMemberFromUserID(userID) != null)
				{
					return lobbyInst;
				}
			}

			return null;
		}

		public List<Lobby> GetPlayerOwnedLobbies(Int64 userID)
		{
			// NOTE: This function doesnt account for games in progress, the callee must process those (the owner can have left and orphaned the session if in-game)
			// TODO_LOBBY: Optimize this, maintain a dictionary of userid
			List<Lobby> lstLobbies = new List<Lobby>();
			foreach (Lobby lobbyInst in m_dictLobbies.Values)
			{
				if (lobbyInst.Owner == userID)
				{
					lstLobbies.Add(lobbyInst);
				}
			}

			return lstLobbies;
		}

		public async Task<bool> DeleteLobby(Lobby lobby)
		{
			try
			{
				using var scope = _services.CreateScope();
				var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
				await using var db = await factory.CreateDbContextAsync();

				if (lobby.State != ELobbyState.COMPLETE)
				{
					await lobby.UpdateState(ELobbyState.COMPLETE);
				}
				// Persist publication before removing the lobby.
				await Database.MatchHistory.FinalizeAndScheduleExternalPublication(db, lobby);

				// delete
				bool bRemoved = m_dictLobbies.Remove(lobby.LobbyID, out _);

				if (bRemoved)
				{
					WebSocketManager.QueueLobbyListUpdateForViewers(lobby.NetworkRoomID);

					lobby.OnLobbyNeedsDestroyed -= HandleLobbyNeedsDestroyed;
				}

				return bRemoved;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "DeleteLobby failed");
				return false;
			}
		}

		public bool IsUserInLobby(Lobby lobby, Int64 user_id)
		{
			LobbyMember? member = lobby.GetMemberFromUserID(user_id);
			return member != null;
		}
	}
}
