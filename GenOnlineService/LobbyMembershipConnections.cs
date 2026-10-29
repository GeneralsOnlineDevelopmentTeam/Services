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

namespace GenOnlineService;

// Adapter for the legacy session and relay registries used by membership commands.
public sealed class LobbyMembershipConnections
{
	private static readonly ILogger s_log = AppLog.For(typeof(LobbyMembershipConnections));

	public UserSession? GetSession(long userID, EUserSessionType sessionType) =>
		WebSocketManager.GetSessionFromUser(userID, sessionType);

	public Task DeleteRelayCredentialsAsync(long userID) =>
		TURNCredentialManager.DeleteCredentialsForUser(userID);

	public async Task<(string Username, string Token)> CreateRelayCredentialsAsync(long userID)
	{
		try
		{
			var credentials = await TURNCredentialManager.CreateCredentialsForUser(userID);
			return (credentials?.m_strUsername ?? "", credentials?.m_strToken ?? "");
		}
		catch (Exception ex)
		{
			// Relay failure does not undo a successful lobby join.
			s_log.LogError(ex, "Creating relay credentials for user {UserId} failed", userID);
			return ("", "");
		}
	}
}
