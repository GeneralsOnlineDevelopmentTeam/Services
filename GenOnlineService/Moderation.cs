/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
*/

using System.Text;
using System.Text.Json;

namespace GenOnlineService
{
	public enum EModerationResult
	{
		Success,
		TargetNotOnline,
		ReasonTooLong
	}

	public enum EModerationAction
	{
		Ban,
		Kick
	}

	public sealed class ModerationResult
	{
		public EModerationResult Result { get; init; }
		public string TargetDisplayName { get; init; } = String.Empty;
	}

	public static class ModerationManager
	{
		public const int MaximumReasonLength = 256;

		public static async Task<ModerationResult> KickUser(Int64 targetUserID, string reason)
		{
			if (reason.Length > MaximumReasonLength)
			{
				return new ModerationResult { Result = EModerationResult.ReasonTooLong };
			}

			SharedUserData? target = WebSocketManager.GetSharedDataForUser(targetUserID);
			if (target == null)
			{
				return new ModerationResult { Result = EModerationResult.TargetNotOnline };
			}

			await DisconnectUser(targetUserID, EModerationAction.Kick, reason);
			return new ModerationResult
			{
				Result = EModerationResult.Success,
				TargetDisplayName = target.m_strDisplayName
			};
		}

		public static async Task DisconnectUser(Int64 userID, EModerationAction action, string? reason)
		{
			WebSocketMessage_ModerationNotice notice = new WebSocketMessage_ModerationNotice
			{
				msg_id = (int)EWebSocketMessageID.MODERATION_NOTICE,
				action_type = action switch
				{
					EModerationAction.Ban => "ban",
					EModerationAction.Kick => "kick",
					_ => throw new ArgumentOutOfRangeException(nameof(action))
				},
				reason = reason ?? String.Empty
			};
			byte[] noticeJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(notice));

			await WebSocketManager.DisconnectUser(userID, noticeJson);
		}
	}
}
