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
using System.Net.WebSockets;

namespace GenOnlineService;

public partial class LobbyManager
{
	internal async Task<UserWebSocketInstance> OpenSessionAsync(AppDbContext db, EUserSessionType sessionType, bool reconnect,
		long userID, KnownClients.EKnownClients clientID, string ipAddress, string continent, string country,
		double latitude, double longitude, bool admin)
	{
		using var membership = await AcquireMembershipGateAsync(userID);
		var previous = membershipConnections.GetSession(userID, sessionType);
		if (!reconnect && previous != null) await DestroySessionAsync(previous);
		if (reconnect && (previous == null || previous.IsRetired)) return null;
		return await WebSocketManager.InstallSession(db, sessionType, reconnect, userID, clientID,
			ipAddress, continent, country, latitude, longitude, admin);
	}

	internal async Task DisconnectSessionAsync(UserSession? session, UserWebSocketInstance? socket, bool destroySession)
	{
		if (session == null) return;
		using (await AcquireMembershipGateAsync(session.m_UserID))
		{
			if (IsCurrentSession(session) && ReferenceEquals(WebSocketManager.GetWebSocketForSession(session), socket))
			{
				if (destroySession) await DestroySessionAsync(session);
				else
				{
					WebSocketManager.DetachSocket(session);
					session.MarkAbandoned();
					var lobby = GetLobby(session.currentLobbyID);
					if (lobby?.State == ELobbyState.INGAME) lobby.RecordPlayerIngameAbandon(session.m_UserID);
				}
			}
			if (socket != null) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session being deleted");
		}
	}

	internal Task<bool> ClearSessionAsync(UserSession? session) => RemoveSessionAsync(session, expiredOnly: false);
	internal Task<bool> ExpireSessionAsync(UserSession session) => RemoveSessionAsync(session, expiredOnly: true);

	private async Task<bool> RemoveSessionAsync(UserSession? session, bool expiredOnly)
	{
		if (session == null) return false;
		using var membership = await AcquireMembershipGateAsync(session.m_UserID);
		if (!IsCurrentSession(session)) return false;
		if (expiredOnly && !WebSocketManager.ShouldStillClearAbandonedSession(session, session)) return false;
		await DestroySessionAsync(session);
		return true;
	}

	private bool IsCurrentSession(UserSession session) =>
		ReferenceEquals(membershipConnections.GetSession(session.m_UserID, session.GetSessionType()), session);

	private async Task DestroySessionAsync(UserSession session)
	{
		session.Retire();
		bool gameplay = SessionHelpers.SessionTypeHasAccessTo(session.GetSessionType(), ESessionAccessType.Gameplay);
		try
		{
			if (gameplay)
			{
				await MatchmakingManager.DetachSessionAsync(session);
				await LeaveMembershipsAsync(session.m_UserID);
				await CleanupUserLobbiesNotStartedAsync(session.m_UserID);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Destroying session for user {UserId} failed", session.m_UserID);
			throw;
		}
		finally
		{
			try
			{
				if (gameplay) await membershipConnections.DeleteRelayCredentialsAsync(session.m_UserID);
			}
			finally
			{
				var socket = WebSocketManager.DetachSocket(session);
				try
				{
					WebSocketManager.RemoveSessionData(session);
				}
				finally
				{
					// Send loops never invoke lifecycle operations. Stop the old queue reader
					// before allowing a reconnect to attach another reader to the same session.
					if (socket != null) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session replaced or expired");
				}
			}
		}
	}

	internal async Task CancelMatchmakingAsync(UserSession session)
	{
		using var membership = await AcquireMembershipGateAsync(session.m_UserID);
		if (!IsCurrentSession(session) || session.IsRetired) return;
		var detached = await MatchmakingManager.DetachSessionAsync(session);
		foreach (var entry in detached.Members)
			await RemoveMembershipAsync(entry.Lobby, session.m_UserID, entry.Member);
		if (!detached.CancellationRejected) await LeaveMembershipsAsync(session.m_UserID);
	}
}
