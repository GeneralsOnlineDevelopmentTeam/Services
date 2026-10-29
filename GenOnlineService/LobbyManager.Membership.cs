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

using Database;
using Microsoft.EntityFrameworkCore;

namespace GenOnlineService;

public enum LobbyMembershipStatus
{
	Success,
	NoChange,
	ActorNotMember,
	ActorNotHost,
	NotMatched,
	AnticheatMismatch,
	SessionUnavailable,
	PasswordMismatch,
	NotInSetup,
	AlreadyMember,
	NoOpenSlot,
	BlockedByOwner,
	FriendshipRequired,
	PreferencesUnavailable
}

public sealed record JoinLobbyCommand(long LobbyID, long UserID, EUserSessionType SessionType,
	ushort PreferredPort, bool HasMap, EKnownAnticheatID AnticheatID, string? Password);
public sealed record LeaveLobbyCommand(long LobbyID, long UserID, EUserSessionType SessionType);
public sealed record KickLobbyMemberCommand(long LobbyID, long ActorUserID, long? TargetUserID);
public sealed record JoinLobbyResult(LobbyMembershipStatus Status, string TurnUsername = "", string TurnToken = "");

public partial class LobbyManager
{
	// Bounded stripes serialize a user's membership transitions across lobbies. Always
	// acquire this before a lobby gate; never acquire another stripe while holding one.
	private readonly SemaphoreSlim[] membershipGates = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

	private async Task<IDisposable> AcquireMembershipGateAsync(long userID)
	{
		var gate = membershipGates[(int)((ulong)userID % (uint)membershipGates.Length)];
		await gate.WaitAsync();
		return new MembershipLease(gate);
	}

	private sealed class MembershipLease(SemaphoreSlim gate) : IDisposable
	{
		public void Dispose() => gate.Release();
	}

	private readonly IDbContextFactory<AppDbContext> membershipDbFactory;
	private readonly LobbyMembershipConnections membershipConnections;

	public async Task<JoinLobbyResult> JoinAsync(JoinLobbyCommand command)
	{
		using var membership = await AcquireMembershipGateAsync(command.UserID);
		var lobby = GetLobby(command.LobbyID);
		if (lobby == null) return new(LobbyMembershipStatus.NoChange);
		if (command.UserID == -1 || !SessionHelpers.SessionTypeHasAccessTo(command.SessionType, ESessionAccessType.Gameplay))
			return new(LobbyMembershipStatus.NoChange);

		var session = membershipConnections.GetSession(command.UserID, command.SessionType);
		if (session == null || session.IsRetired) return new(LobbyMembershipStatus.SessionUnavailable);

		await using var db = await membershipDbFactory.CreateDbContextAsync();
		var displayName = await Users.GetDisplayName(db, command.UserID);
		var preferences = await Users.GetUserLobbyPreferences(db, command.UserID);
		if (preferences == null) return new(LobbyMembershipStatus.PreferencesUnavailable);
		var sources = m_dictLobbies.Values.Where(source => source.GetMemberFromUserID(command.UserID) != null).ToArray();
		var status = await Lobby.TransferMemberAsync(lobby, sources, command, session, displayName, preferences);
		if (status != LobbyMembershipStatus.Success) return new(status);

		var credentials = await membershipConnections.CreateRelayCredentialsAsync(command.UserID);
		return new(status, credentials.Username, credentials.Token);
	}

	public async Task<bool> LeaveAsync(LeaveLobbyCommand command)
	{
		if (command.UserID == -1 || !SessionHelpers.SessionTypeHasAccessTo(command.SessionType, ESessionAccessType.Gameplay))
			return false;
		using var membership = await AcquireMembershipGateAsync(command.UserID);
		var lobby = GetLobby(command.LobbyID);
		if (lobby == null) return false;
		return await RemoveMembershipAsync(lobby, command.UserID) != null;
	}

	public async Task<LobbyMembershipStatus> KickAsync(KickLobbyMemberCommand command)
	{
		using var membership = await AcquireMembershipGateAsync(command.TargetUserID ?? command.ActorUserID);
		var lobby = GetLobby(command.LobbyID);
		if (lobby == null) return LobbyMembershipStatus.NoChange;
		var status = await lobby.KickMemberAsync(command);
		if (status == LobbyMembershipStatus.Success && command.TargetUserID is long targetID)
		{
			await ClearSessionMembershipStateAsync(targetID, EUserSessionType.GameClient);
			// Removed members no longer receive ordinary dirty lobby updates.
			await lobby.DirtyRetransmitToSingleMember(targetID);
		}
		return status;
	}

	private async Task ClearSessionMembershipStateAsync(long userID, EUserSessionType sessionType)
	{
		try
		{
			await membershipConnections.DeleteRelayCredentialsAsync(userID);
		}
		finally
		{
			membershipConnections.GetSession(userID, sessionType)?.UpdateSessionLobbyID(-1);
		}
		// Match-history membership is owned by the match-history service.
	}

	internal async Task RemoveMatchmakingMemberAsync(Lobby lobby, LobbyMember member, bool sendPeerTeardown = false)
	{
		using var membership = await AcquireMembershipGateAsync(member.UserID);
		if (!member.GetSession().TryGetTarget(out var session) || !IsCurrentSession(session) || session.IsRetired) return;
		if (sendPeerTeardown) lobby.SendPeerTeardownToDepartingMember(member);
		await RemoveMembershipAsync(lobby, member.UserID, member);
	}

	// Caller holds the user's membership gate through removal and session cleanup.
	private async Task<LobbyMember?> RemoveMembershipAsync(Lobby lobby, long userID, LobbyMember? expectedMember = null)
	{
		var removed = await lobby.RemoveMemberAsync(userID, expectedMember);
		if (removed != null && removed.GetSession().TryGetTarget(out var session)
			&& IsCurrentSession(session) && !session.IsRetired && session.currentLobbyID == lobby.LobbyID)
			await ClearSessionMembershipStateAsync(userID, session.GetSessionType());
		return removed;
	}

	internal async Task<bool> TryRequeueMatchmakingAsync(UserSession session, long expectedLobbyID, byte[] requeueActionJSON)
	{
		using var membership = await AcquireMembershipGateAsync(session.m_UserID);
		if (!IsCurrentSession(session) || session.IsRetired
			|| (session.currentLobbyID != -1 && session.currentLobbyID != expectedLobbyID)) return false;
		if (session.currentLobbyID == expectedLobbyID && expectedLobbyID != -1)
			await ClearSessionMembershipStateAsync(session.m_UserID, session.GetSessionType());
		return await MatchmakingManager.TryRequeueRegisteredPlayer(session, requeueActionJSON);
	}

	public async Task LeaveAllLobbiesAsync(long userID)
	{
		using var membership = await AcquireMembershipGateAsync(userID);
		await LeaveMembershipsAsync(userID);
	}

	private async Task LeaveMembershipsAsync(long userID)
	{
		foreach (Lobby lobby in m_dictLobbies.Values)
			await RemoveMembershipAsync(lobby, userID);
	}
	internal async Task<(string Username, string Token)> CreateLobbyRelayCredentialsAsync(UserSession session, long lobbyID)
	{
		using var membership = await AcquireMembershipGateAsync(session.m_UserID);
		if (!IsCurrentSession(session) || session.IsRetired || session.currentLobbyID != lobbyID)
			return ("", "");
		return await membershipConnections.CreateRelayCredentialsAsync(session.m_UserID);
	}

}
