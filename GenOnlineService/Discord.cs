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

#define USE_DISCORD_IN_DEBUG

using Amazon.S3.Model;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using GenOnlineService;
using GenOnlineService.NameFilter;
using Microsoft.EntityFrameworkCore;
using MySqlX.XDevAPI;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Collections.Concurrent;

public enum EDiscordChannelIDs
{
	Any = -1,
	NetworkRoomChat = 1,
	AdminCommands = 2,
	DirectMessage = 0
}

public enum EDiscordUserTypeRequirements
{
	Player,
	Staff
}

public enum DiscordCommandParsingFlags
{
	Default = 0,
	GreedyArgs = 1
}

public static class Helpers
{
	public static ConcurrentDictionary<Int64, string> g_dictInitialExeCRCs = new();
	public static void RegisterInitialPlayerExeCRC(Int64 user_id, string exe_crc)
	{
		g_dictInitialExeCRCs[user_id] = exe_crc;
	}

	public static string ComputeMD5Hash(string input)
	{
		using (MD5 md5 = MD5.Create())
		{
			byte[] inputBytes = Encoding.UTF8.GetBytes(input);
			byte[] hashBytes = md5.ComputeHash(inputBytes);

			// Convert byte array to hexadecimal string
			StringBuilder sb = new StringBuilder();
			foreach (byte b in hashBytes)
			{
				sb.Append(b.ToString("x2"));
			}
			return sb.ToString();
		}
	}
	public static Int64 GetUnixTimestamp(bool toUTC = false)
	{
		DateTime now = DateTime.Now;

		if (toUTC)
		{
			now = now.ToUniversalTime();
		}

		return (Int64)now.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
	}

	public static string FormatString(string strFormat, params object[] strParams)
	{
		return String.Format(new System.Globalization.CultureInfo("en-US"), strFormat, strParams);
	}
}

public class DiscordBot
{
	enum EBotAction
	{
		PushScriptedMessage
	}

	DiscordSocketClient? discord = null;

	private Dictionary<EDiscordChannelIDs, ulong> g_dictChannelIDs = new Dictionary<EDiscordChannelIDs, ulong>();
	private Dictionary<EDiscordChannelIDs, ISocketMessageChannel> g_dictChannels = new Dictionary<EDiscordChannelIDs, ISocketMessageChannel>();

	public DiscordBot()
	{
#if !DEBUG
		_ = InitAsync().ContinueWith(t =>
		{
			if (t.IsFaulted)
				Console.WriteLine("Discord initialization failed: " + t.Exception);
		}, TaskContinuationOptions.OnlyOnFaulted);
#endif
	}

	~DiscordBot()
	{
		if (discord != null)
		{
			discord.LogoutAsync();
		}
	}

	public async Task SendNetworkRoomChat(int roomID, Int64 userID, string strDisplayName, string strMessage)
	{
		try
		{
            if (Program.g_Config == null)
            {
                return;
            }

            IConfiguration? discordSettings = Program.g_Config.GetSection("Discord");

            if (discordSettings == null)
            {
                return;
            }

            bool discord_send_room_chat_to_discord = discordSettings.GetValue<bool>("send_room_chat_to_discord");

            if (discord_send_room_chat_to_discord == null)
            {
                return;
            }

			if (discord_send_room_chat_to_discord)
			{
                string strFormattedChatMsg = String.Format("[{0} - UID {1}] {2}", strDisplayName, userID, strMessage);

                ISocketMessageChannel? channel = GetChannel(EDiscordChannelIDs.NetworkRoomChat);
                if (channel != null)
                {
                    string strDiscordMsg = String.Format("[NETWORK ROOM CHAT ID #{0}] {1}", roomID, strFormattedChatMsg);
                    await channel.SendMessageAsync(strDiscordMsg).ConfigureAwait(true);
                }
            }
		}
		catch
		{

		}
	}

	public string GetDiscordUsernameFromID(UInt64 discordID)
	{
		if (discord != null)
		{
			var user = discord.GetUser(discordID);
			if (user != null)
			{
				return user.Username;
			}
		}

		return String.Empty;
	}

	public void UpdateBotStatus(string strStatus)
	{
		if (discord != null)
		{
			Game game = new Game(strStatus, ActivityType.Playing);
			discord.SetStatusAsync(UserStatus.Online);
			discord.SetActivityAsync(game);
		}
	}

	private Task OnReady()
	{
		if (Program.g_Config == null)
		{
			return Task.CompletedTask;
		}

		IConfiguration? discordSettings = Program.g_Config.GetSection("Discord");

		if (discordSettings == null)
		{
			return Task.CompletedTask;
		}

		UInt64? discord_network_room_chat_channel = discordSettings.GetValue<UInt64>("discord_network_room_chat_channel");
		UInt64? discord_admin_commands_channel = discordSettings.GetValue<UInt64>("discord_admin_commands_channel");

		if (discord_network_room_chat_channel == null || discord_admin_commands_channel == null)
		{
			return Task.CompletedTask;
		}

		// cache our channels
		g_dictChannelIDs[EDiscordChannelIDs.NetworkRoomChat] = (ulong)discord_network_room_chat_channel;
		g_dictChannelIDs[EDiscordChannelIDs.AdminCommands] = (ulong)discord_admin_commands_channel;

		ISocketMessageChannel? channel = GetChannel(EDiscordChannelIDs.NetworkRoomChat);
		if (channel != null)
		{
			//await channel.SendMessageAsync("Bot Started").ConfigureAwait(true);
		}

		return Task.CompletedTask;
	}

	private bool IsChannelAnAdminChannel(ulong channelID)
	{
		EDiscordChannelIDs discordChannelID = EDiscordChannelIDs.NetworkRoomChat;
		foreach (var channel in g_dictChannelIDs)
		{
			if (channel.Value == channelID)
			{
				discordChannelID = channel.Key;
				break;
			}
		}

		if (discordChannelID == EDiscordChannelIDs.NetworkRoomChat || discordChannelID == EDiscordChannelIDs.AdminCommands)
		{
			return true;
		}

		return false;
	}

	public bool IsReady()
	{
		return discord != null && discord.ConnectionState == ConnectionState.Connected;
	}

	public bool GetChannelID(out ulong channelID, EDiscordChannelIDs discordChannelID)
	{
		if (g_dictChannelIDs.ContainsKey(discordChannelID))
		{
			channelID = g_dictChannelIDs[discordChannelID];
		}

		channelID = 999999;
		return false;
	}

	public bool IsChannelIDDefined(ulong channelID, EDiscordChannelIDs discordChannelID)
	{
		if (g_dictChannelIDs.ContainsKey(discordChannelID))
		{
			return g_dictChannelIDs[discordChannelID] == channelID;
		}

		return false;
	}

	private uint g_cooldownLengthSeconds = 20;
	private Dictionary<ulong, double> m_dictCooldowns = new Dictionary<ulong, double>();

	private bool DoesDiscordClientHaveCooldown(ulong channelID, SocketUser user)
	{
		// Never have a cooldown for admin channels
		if (IsChannelAnAdminChannel(channelID))
		{
			return false;
		}

		ExpireCooldowns();
		return m_dictCooldowns.ContainsKey(user.Id);
	}

	private void ExpireCooldowns()
	{
		Int64 unixTimestamp = Helpers.GetUnixTimestamp();

		List<ulong> m_lstToRemove = new List<ulong>();
		foreach (var kvPair in m_dictCooldowns)
		{
			if ((kvPair.Value + g_cooldownLengthSeconds) <= unixTimestamp)
			{
				m_lstToRemove.Add(kvPair.Key);
			}
		}

		foreach (ulong key in m_lstToRemove)
		{
			m_dictCooldowns.Remove(key);
		}
	}

	private void CreateCooldown(ulong channelID, SocketUser user)
	{
		if (!IsChannelAnAdminChannel(channelID))
		{
			Int64 unixTimestamp = Helpers.GetUnixTimestamp();
			m_dictCooldowns[user.Id] = unixTimestamp;
		}
	}

	bool HasRole(IGuildUser user, string roleName)
	{
		return user.RoleIds
				   .Select(roleId => user.Guild.GetRole(roleId))
				   .Any(role => role.Name.Equals(roleName, StringComparison.OrdinalIgnoreCase));
	}

	Regex g_HtmlRegex = new Regex(@"<\s*([^ >]+)[^>]*>.*?<\s*/\s*\1\s*>");
	private async Task OnMessageReceived(SocketMessage message)
	{
		try
		{
			if (message.Content.Length > 0)
			{
				if (message.Content[0] == '!')
				{
					if (!DoesDiscordClientHaveCooldown(message.Channel.Id, message.Author))
					{
						EDiscordChannelIDs enumChannelID = EDiscordChannelIDs.DirectMessage;
						// Do we have this channel / is it a real channel and not DM?
						foreach (var kvPair in g_dictChannelIDs)
						{
							if (kvPair.Value == message.Channel.Id)
							{
								enumChannelID = kvPair.Key;
								break;
							}
						}

						CreateCooldown(message.Channel.Id, message.Author);

						if (message.Content.ToLower() == "!playercount" || message.Content.ToLower() == "!players")
						{
							int numPlayers = GenOnlineService.WebSocketManager.GetNumberOfUsersOnline();
							string strMessage = String.Format("There are currently {0} players online.", numPlayers);

							if (enumChannelID == EDiscordChannelIDs.DirectMessage)
							{
								PushDM(message.Author, strMessage);
							}
							else
							{
								PushChannelMessage(enumChannelID, strMessage);
							}
						}
						else if (message.Content.ToLower() == "!lobbies")
						{
							var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();
							int numLobbies = lobbyManager.GetNumLobbies();
							string strMessage = String.Format("There are currently {0} lobbies.", numLobbies);

							if (enumChannelID == EDiscordChannelIDs.DirectMessage)
							{
								PushDM(message.Author, strMessage);
							}
							else
							{
								PushChannelMessage(enumChannelID, strMessage);
							}
						}
						else if (message.Content.ToLower() == "!uptime")
						{
							if (message.Channel.Id == g_dictChannelIDs[EDiscordChannelIDs.AdminCommands])
							{
								if (Program.g_Config == null)
								{
									return;
								}

								// TODO_DISCORD: Cache this
								IConfiguration? discordSettings = Program.g_Config.GetSection("Discord");

								if (discordSettings == null)
								{
									return;
								}

								List<UInt64>? discord_admins = discordSettings.GetSection("discord_admins").Get<List<UInt64>>();
								if (discord_admins == null)
								{
									return;
								}

								// is it an admin?
								if (discord_admins.Contains(message.Author.Id))
								{
									string start_time = Program.g_LastStartTime.ToString("yyyy-MM-dd HH:mm:ss");

									TimeSpan difference = DateTime.Now.Subtract(Program.g_LastStartTime);
									string uptime = $"Days: {difference.Days}, Hours: {difference.Hours}, Minutes: {difference.Minutes}";

									string strMessage = String.Format("The server was last started at {0} and the current uptime is {1}", start_time, uptime);

									if (enumChannelID == EDiscordChannelIDs.DirectMessage)
									{
										PushDM(message.Author, strMessage);
									}
									else
									{
										PushChannelMessage(enumChannelID, strMessage);
									}
								}
							}
						}
						else if (message.Content.ToLower() == "!peak")
						{
							if (message.Channel.Id == g_dictChannelIDs[EDiscordChannelIDs.AdminCommands])
							{
								if (Program.g_Config == null)
								{
									return;
								}

								// is it an admin?
								IConfiguration? discordSettings = Program.g_Config.GetSection("Discord");

								if (discordSettings == null)
								{
									return;
								}

								List<UInt64>? discord_admins = discordSettings.GetSection("discord_admins").Get<List<UInt64>>();
								if (discord_admins == null)
								{
									return;
								}

								if (discord_admins.Contains(message.Author.Id))
								{
									int peak = GenOnlineService.WebSocketManager.g_PeakConnectionCount;
									string strMessage = String.Format("The highest player peak seen (since last server restart) is {0}", peak);

									if (enumChannelID == EDiscordChannelIDs.DirectMessage)
									{
										PushDM(message.Author, strMessage);
									}
									else
									{
										PushChannelMessage(enumChannelID, strMessage);
									}
								}
							}
						}
						else if (message.Content.ToLower().StartsWith("!kick"))
						{
							// TODO: In future we should validate users not just channels
							// is it in the admin channel?
							if (message.Channel.Id == g_dictChannelIDs[EDiscordChannelIDs.AdminCommands])
							{
								if (Program.g_Config == null)
								{
									return;
								}

								// is it an admin?
								IConfiguration? discordSettings = Program.g_Config.GetSection("Discord");

								if (discordSettings == null)
								{
									return;
								}

								List<UInt64>? discord_admins = discordSettings.GetSection("discord_admins").Get<List<UInt64>>();
								if (discord_admins == null)
								{
									return;
								}

								if (discord_admins.Contains(message.Author.Id))
								{
									string[] strComponents = message.Content.Split(' ');
									//var clients = message.Author.ActiveClients;

									//var user = message.Author as IGuildUser; // Get the user from the command context
									if (strComponents.Length == 2)
									{
										string strUser = string.Join(' ', strComponents.Skip(1));
										if (Int64.TryParse(strUser, out Int64 TargetUserID))
										{
											SharedUserData? targetData = GenOnlineService.WebSocketManager.GetSharedDataForUser(TargetUserID);
											
											if (targetData != null)
											{
												PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"User {TargetUserID} ({targetData.m_strDisplayName}) has been kicked from the server.");

												// we need to kill all websockets they have
												List<UserSession> lstUserSessions = GenOnlineService.WebSocketManager.GetAllDataFromUser(TargetUserID);
												foreach (UserSession userSession in lstUserSessions)
												{
													UserWebSocketInstance? oldWS = GenOnlineService.WebSocketManager.GetWebSocketForSession(userSession);
													await GenOnlineService.WebSocketManager.DeleteSession(TargetUserID, userSession.GetSessionType(), oldWS, true);
												}

												
											}
											else
											{
												PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"User {TargetUserID} is not active on the server.");
											}
										}
										else
										{
											PushChannelMessage(EDiscordChannelIDs.AdminCommands, "Invalid Command Syntax. !kick <user_id> (e.g. !kick 123)");
										}
									}
									else
									{
										PushChannelMessage(EDiscordChannelIDs.AdminCommands, "Invalid Command Syntax. !kick <user_id> (e.g. !kick 123)");
									}
								}
								else
								{
									PushDM(message.Author, "You don't have access to staff commands.");
								}
							}
						}
						else if (message.Content.ToLower().StartsWith("!whois"))
						{
							// TODO: In future we should validate users not just channels
							// is it in the admin channel?
							if (message.Channel.Id == g_dictChannelIDs[EDiscordChannelIDs.AdminCommands])
							{
								if (Program.g_Config == null)
								{
									return;
								}

								// is it an admin?
								IConfiguration? discordSettings = Program.g_Config.GetSection("Discord");

								if (discordSettings == null)
								{
									return;
								}

								List<UInt64>? discord_admins = discordSettings.GetSection("discord_admins").Get<List<UInt64>>();
								if (discord_admins == null)
								{
									return;
								}

								if (discord_admins.Contains(message.Author.Id))
								{
									string[] strComponents = message.Content.Split(' ');

									if (strComponents.Length >= 2)
									{
										string strname = string.Join(' ', strComponents.Skip(1));


										SharedUserData? userDataFound = GenOnlineService.WebSocketManager.GetSharedDataForUser(strname);
										if (userDataFound != null)
										{
											PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"User {userDataFound.m_strDisplayName} is user ID {userDataFound.m_UserID}.");
										}
										else
										{
											PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"User {strname} is not active on the server.");
										}
									}
									else
									{
										PushChannelMessage(EDiscordChannelIDs.AdminCommands, "Invalid Command Syntax. !whois <display name> (e.g. !whois x64)");
									}
								}
								else
								{
									PushDM(message.Author, "You don't have access to staff commands.");
								}
							}
						}
						else if (message.Content.ToLower().StartsWith("!announce"))
						{
							// TODO: In future we should validate users not just channels
							// is it in the admin channel?
							if (message.Channel.Id == g_dictChannelIDs[EDiscordChannelIDs.AdminCommands])
							{
								if (Program.g_Config == null)
								{
									return;
								}

								// is it an admin?
								IConfiguration? discordSettings = Program.g_Config.GetSection("Discord");

								if (discordSettings == null)
								{
									return;
								}

								List<UInt64>? discord_admins = discordSettings.GetSection("discord_admins").Get<List<UInt64>>();
								if (discord_admins == null)
								{
									return;
								}

								if (discord_admins.Contains(message.Author.Id))
								{
									string[] strComponents = message.Content.Split(' ');



									if (strComponents.Length >= 2)
									{
										string strMessage = string.Join(' ', strComponents.Skip(1));

										// TODO: Later we should deliver this to ingame chat too

										// prepare WS messages
										// net room
										WebSocketMessage_NetworkRoomChatMessageOutbound outboundMsgRoom = new WebSocketMessage_NetworkRoomChatMessageOutbound();
										outboundMsgRoom.msg_id = (int)EWebSocketMessageID.NETWORK_ROOM_CHAT_FROM_SERVER;
										outboundMsgRoom.message = String.Format("--- ADMIN ANNOUNCEMENT ---    {0}", strMessage);
										outboundMsgRoom.action = true;
										byte[] outboundMsgRoomJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsgRoom));

										// lobby
										WebSocketMessage_LobbyChatMessageOutbound outboundMsgLobby = new WebSocketMessage_LobbyChatMessageOutbound();
										outboundMsgLobby.user_id = -2;
										outboundMsgLobby.msg_id = (int)EWebSocketMessageID.LOBBY_CHAT_FROM_SERVER;
										outboundMsgLobby.message = String.Format("--- ADMIN ANNOUNCEMENT ---    {0}", strMessage);
										outboundMsgLobby.action = true;
										outboundMsgLobby.announcement = true;
										outboundMsgLobby.show_announcement_to_host = true;
										byte[] outboundMsgLobbyJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(outboundMsgLobby));

										// send to everyone
										int numDelivered = 0;
										foreach (var sessionDataByClient in GenOnlineService.WebSocketManager.GetUserDataCache())
										{
											foreach (var sessionData in sessionDataByClient.Value)
											{
												UserSession sess = sessionData.Value;

												if (sess != null)
												{
													if (sess.currentLobbyID == -1)
													{
														sess.QueueWebsocketSend(outboundMsgRoomJSON);
													}
													else
													{
														sess.QueueWebsocketSend(outboundMsgLobbyJSON);
													}

													++numDelivered;
												}
											}
										}

										PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"Announcement '{strMessage}' was delivered to {numDelivered} users");
									}
									else
									{
										PushChannelMessage(EDiscordChannelIDs.AdminCommands, "Invalid Command Syntax. !announce <message> (e.g. !announce Hello)");
									}
								}
								else
								{
									PushDM(message.Author, "You don't have access to staff commands.");
								}
							}
						}
						else if (message.Content.ToLower().StartsWith("!namefilter"))
						{
							if (message.Channel.Id == g_dictChannelIDs[EDiscordChannelIDs.AdminCommands])
							{
								if (IsDiscordAdmin(message.Author.Id))
								{
									await HandleNameFilterCommand(message);
								}
								else
								{
									PushDM(message.Author, "You don't have access to staff commands.");
								}
							}
						}


						//JSONRequest_PushCommand requestToSend = new JSONRequest_PushCommand(new DiscordUser(message.Author.Id, message.Author.Username), message.Content, enumChannelID);
						//Program.GetRestClient().QueueRequest(requestToSend, CRestClient.ERestCallbackThreadingMode.ContinueOnWorkerThread, null);
					}
					else
					{
						PushDM(message.Author, "Too many commands. Please wait.");
					}
				}
				else
				{
					// admin chat bi-directional chat
					if (g_dictChannelIDs.ContainsKey(EDiscordChannelIDs.NetworkRoomChat) && message.Channel.Id == g_dictChannelIDs[EDiscordChannelIDs.NetworkRoomChat])
					{
						if (!message.Author.IsBot)
						{
							string strMessage = message.Content;
							if (g_HtmlRegex.IsMatch(strMessage))
							{
								//strMessage = Helpers.FormatString("{0} is naughty and tried to send HTML!", message.Author.Username);
							}

							//message.Channel.SendMessageAsync(Helpers.FormatString("`{0}`: {1}", message.Author.Username, strMessage));

							//JSONRequest_BiDirectionalAdminChat requestToSend = new JSONRequest_BiDirectionalAdminChat(new DiscordUser(message.Author.Id, message.Author.Username), strMessage);
							//Program.GetRestClient().QueueRequest(requestToSend, CRestClient.ERestCallbackThreadingMode.ContinueOnWorkerThread, null);
						}
					}
				}
			}
		}
		catch
		{

		}
	}

	// ---- Staff commands: display name filter -----------------------------------------------

	private bool IsDiscordAdmin(UInt64 discordUserID)
	{
		if (Program.g_Config == null)
		{
			return false;
		}

		List<UInt64>? discord_admins = Program.g_Config.GetSection("Discord").GetSection("discord_admins").Get<List<UInt64>>();

		return discord_admins != null && discord_admins.Contains(discordUserID);
	}

	private async Task HandleNameFilterCommand(SocketMessage message)
	{
		string[] strComponents = message.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		string strSubCommand = strComponents.Length > 1 ? strComponents[1].ToLower() : "help";

		NameFilterService nameFilter = ServiceLocator.Services.GetRequiredService<NameFilterService>();

		if (strSubCommand == "test")
		{
			if (strComponents.Length < 3)
			{
				PushChannelMessage(EDiscordChannelIDs.AdminCommands, "Invalid Command Syntax. !namefilter test <name>");
				return;
			}

			string strName = String.Join(' ', strComponents.Skip(2));
			NameCheck check = nameFilter.Check(strName);

			string strVerdict = check.MatchedRule != null
				? $"rule {check.MatchedRule.ID} `{check.MatchedRule.Pattern}` ({check.MatchedRule.MatchType}, {check.MatchedRule.Action}, {check.MatchedRule.Category})"
				: "no rule matched";

			PushChannelMessage(EDiscordChannelIDs.AdminCommands,
				$"`{strName}`\nnormalized: `{check.Normalized}`\nskeleton: `{check.Skeleton}`\nresult: {check.Result}\n{strVerdict}");
			return;
		}

		if (strSubCommand == "list")
		{
			string strCategory = strComponents.Length > 2 ? strComponents[2] : String.Empty;

			List<NameFilterRule> lstRules = nameFilter.GetRules();
			if (strCategory.Length > 0)
			{
				lstRules = lstRules.Where(r => r.Category == strCategory).ToList();
			}

			if (lstRules.Count == 0)
			{
				PushChannelMessage(EDiscordChannelIDs.AdminCommands, "No rules found.");
				return;
			}

			// a rule id is its line in data/namefilter_rules.txt, so the listing points at the edit
			int numTotal = lstRules.Count;
			lstRules = lstRules.Take(30).ToList();

			string strResults = String.Join("\n", lstRules.Select(r => $"`{r.ID}` {r.Pattern} ({r.MatchType}, {r.Action}, {r.Category})"));
			PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"Name filter rules ({lstRules.Count} of {numTotal} shown, ids are line numbers in data/namefilter_rules.txt):\n{strResults}");
			return;
		}

		if (strSubCommand == "reload")
		{
			int numRules = nameFilter.ReloadRules();
			if (numRules < 0)
			{
				PushChannelMessage(EDiscordChannelIDs.AdminCommands, "Could not read data/namefilter_rules.txt. The rules already loaded stay in force - check the service log.");
				return;
			}

			PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"Reloaded data/namefilter_rules.txt, {numRules} rules active.");
			return;
		}

		if (strSubCommand == "rescan")
		{
			// renaming is destructive and touches accounts that did nothing today, so it needs the
			// word rename plus an explicit confirm - a bare rescan only reports
			bool bRename = strComponents.Length > 2 && strComponents[2].ToLower() == "rename";
			bool bConfirmed = strComponents.Length > 3 && strComponents[3].ToLower() == "confirm";

			// renaming can be limited to one rule, so a rule set can be worked through a decision
			// at a time instead of all at once
			int? renameRuleID = null;
			if (strComponents.Length > 5 && strComponents[4].ToLower() == "rule" && Int32.TryParse(strComponents[5], out int scopedRuleID))
			{
				renameRuleID = scopedRuleID;
			}

			if (bRename && !bConfirmed)
			{
				PushChannelMessage(EDiscordChannelIDs.AdminCommands,
					"!namefilter rescan rename replaces every display name a block rule matches with `Player<user id>`. Run `!namefilter rescan` and `!namefilter scanreport` first, then `!namefilter rescan rename confirm [rule <id>]` to apply it.");
				return;
			}

			PushChannelMessage(EDiscordChannelIDs.AdminCommands, bRename ? "Rescanning and renaming, this takes a while..." : "Rescanning, this takes a while...");

			NameScanResult scan = await nameFilter.ScanExistingNames(bRename, renameRuleID, message.Author.Username);

			if (scan.NumHits == 0)
			{
				PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"Rescanned {scan.NumScanned} display names, no hits.");
				return;
			}

			string strResults = String.Join("\n", scan.Samples);
			PushChannelMessage(EDiscordChannelIDs.AdminCommands,
				$"Rescanned {scan.NumScanned} display names, {scan.NumHits} hits, {scan.NumRenamed} renamed ({scan.Samples.Count} shown):\n{strResults}\nRun `!namefilter scanreport` for the breakdown and the CSV.");
			return;
		}

		if (strSubCommand == "categories")
		{
			List<string> lstCategories = nameFilter.GetCategories();

			PushChannelMessage(EDiscordChannelIDs.AdminCommands,
				lstCategories.Count == 0 ? "No categories." : $"Categories: {String.Join(", ", lstCategories)}");
			return;
		}

		if (strSubCommand == "decisions")
		{
			if (strComponents.Length < 3)
			{
				PushChannelMessage(EDiscordChannelIDs.AdminCommands,
					"Invalid Command Syntax. !namefilter decisions <file> [confirm]. The file goes in data/namefilter_decisions/ and is the scanreport CSV with a verdict column of keep, remove or unsure. Without confirm this only reports what it would do.");
				return;
			}

			string strFileName = Path.GetFileName(strComponents[2]);
			bool bApply = strComponents.Length > 3 && strComponents[3].ToLower() == "confirm";

			NameDecisionResult decisions = await nameFilter.ApplyDecisions(strFileName, bApply, message.Author.Username);

			if (!String.IsNullOrEmpty(decisions.Error))
			{
				PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"Could not read the decisions: {decisions.Error}");
				return;
			}

			string strVerb = bApply ? "Applied" : "Would apply";

			// the service does not write the rules file, so allow lines come back as text to paste
			string strAllow = String.Empty;
			if (decisions.AllowLines.Count > 0)
			{
				strAllow = $"\nAdd these {decisions.AllowLines.Count} lines to data/namefilter_rules.txt and run `!namefilter reload`:\n```\n{String.Join("\n", decisions.AllowLines)}\n```";
			}

			PushChannelMessage(EDiscordChannelIDs.AdminCommands,
				$"{strVerb} {strFileName}: {decisions.NumRows} rows, {decisions.NumRenamed} accounts renamed, {decisions.NumUnsure} left for a human, {decisions.NumSkipped} skipped, {decisions.NumFailed} failed."
				+ (bApply ? String.Empty : "\nRun it again with `confirm` to apply.")
				+ strAllow);
			return;
		}

		if (strSubCommand == "scanreport")
		{
			int? reportRuleID = null;
			if (strComponents.Length > 2 && Int32.TryParse(strComponents[2], out int parsedRuleID))
			{
				reportRuleID = parsedRuleID;
			}

			using var scope = ServiceLocator.Services.CreateScope();
			var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
			await using var db = await factory.CreateDbContextAsync();

			List<NameScanRuleGroup> lstGroups = await Database.NameFilter.GetScanSummary(db, nameFilter.GetRuleDictionary());
			if (lstGroups.Count == 0)
			{
				PushChannelMessage(EDiscordChannelIDs.AdminCommands, "No scan results. Run `!namefilter rescan` first.");
				return;
			}

			int numTotal = lstGroups.Sum(g => g.NumHits);

			// The breakdown is the decision list: one rule, or one skeleton inside it, usually
			// accounts for hundreds of names, so this is tens of decisions rather than thousands.
			string strSummary = String.Join("\n", lstGroups.Select(g =>
				$"rule `{g.RuleID}` {g.Pattern} ({g.MatchType}, {g.Action}) - {g.NumHits} accounts, {g.NumSkeletons} distinct names"));

			string strCsv = await nameFilter.BuildScanCsv(reportRuleID, 5000);
			string strFileName = reportRuleID.HasValue ? $"namescan_rule{reportRuleID.Value}.csv" : "namescan.csv";

			PushChannelFile(EDiscordChannelIDs.AdminCommands, strFileName, strCsv,
				$"{numTotal} accounts hit, grouped by rule:\n{strSummary}\nThe CSV is one row per distinct name with a severity, not one per account.");
			return;
		}

		PushChannelMessage(EDiscordChannelIDs.AdminCommands,
			"The rules live in data/namefilter_rules.txt. Edit that file and run `!namefilter reload` to change them.\n" +
			"!namefilter test <name> - show the normalized form, the skeleton and the rule that fires\n" +
			"!namefilter list [category] - the loaded rules, by their line number in the file\n" +
			"!namefilter categories\n" +
			"!namefilter reload - re-read data/namefilter_rules.txt\n" +
			"!namefilter rescan - run every existing display name through the filter and record the hits\n" +
			"!namefilter scanreport [rule id] - breakdown per rule plus a CSV of the distinct names\n" +
			"!namefilter decisions <file> [confirm] - read that CSV back with a verdict column filled in\n" +
			"!namefilter rescan rename confirm [rule <id>] - replace the names a block rule matches with Player<user id>\n" +
			"Match types: skeleton = substring of the canonical form, word = word boundaries in the normalized text, exact = whole canonical form.");
	}

	private static Task LogAsync(LogMessage log)
	{
		Console.WriteLine(log.ToString());
		System.Diagnostics.Debug.WriteLine(log.ToString());
		return Task.CompletedTask;
	}

	private async Task InitAsync()
	{
#if !DEBUG || USE_DISCORD_IN_DEBUG
		DiscordSocketConfig conf = new();
		conf.GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.DirectMessages | GatewayIntents.MessageContent;
		discord = new DiscordSocketClient(conf);

		// event handlers
		discord.Connected += OnReady;
		discord.Log += LogAsync;
		discord.MessageReceived += OnMessageReceived;

		//1354979004507226294

		IConfigurationSection? discordSettings = Program.g_Config.GetSection("Discord");

		if (discordSettings == null)
		{
			throw new Exception("Discord section missing in config");
		}

		string? discordToken = discordSettings.GetValue<string>("token");

		if (discordToken == null)
		{
			throw new Exception("Discord Token missing in config");
		}

		await discord.LoginAsync(TokenType.Bot, discordToken).ConfigureAwait(true);
		await discord.StartAsync().ConfigureAwait(true);
#else
		await Task.Delay(1).ConfigureAwait(true);
#endif
	}

	public void PushDM(SocketUser user, string strMessage)
	{
		try
		{
			if (user != null)
			{
				user.SendMessageAsync(strMessage).ContinueWith(t => { }, TaskContinuationOptions.OnlyOnFaulted);
			}
		}
		catch
		{
			// User probably has some privacy settings that do not allow us to send DMs
		}
	}

	public SocketUser? GetDiscordUserFromDiscordID(ulong DiscordUserID)
	{
		if (discord != null)
		{
			return discord.GetUser(DiscordUserID);
		}

		return null;
	}

	private ISocketMessageChannel? GetChannel(EDiscordChannelIDs channelID)
	{
		ISocketMessageChannel? channel = null;
		if (discord != null)
		{
			if (g_dictChannels.ContainsKey(channelID) && g_dictChannels[channelID] != null)
			{
				channel = g_dictChannels[channelID];
			}
			else
			{
				if (g_dictChannelIDs.ContainsKey(channelID))
				{
					channel = (ISocketMessageChannel)discord.GetChannel(g_dictChannelIDs[channelID]);
					g_dictChannels[channelID] = channel;
				}
			}
		}

		return channel;
	}

	public void PushChannelMessage(EDiscordChannelIDs channelID, string strMessage)
	{
		try
		{
			ISocketMessageChannel? channel = GetChannel(channelID);
			if (channel != null)
			{
				channel.SendMessageAsync(strMessage).ContinueWith(t => { }, TaskContinuationOptions.OnlyOnFaulted);
			}
		}
		catch
		{

		}
	}

	// For results too long to be a message - a name filter scan can produce tens of thousands of
	// rows, which belong in a file the staff can sort, not in the channel.
	public void PushChannelFile(EDiscordChannelIDs channelID, string strFileName, string strContents, string strMessage)
	{
		try
		{
			ISocketMessageChannel? channel = GetChannel(channelID);
			if (channel != null)
			{
				MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(strContents));

				channel.SendFileAsync(stream, strFileName, strMessage)
					.ContinueWith(t => stream.Dispose());
			}
		}
		catch
		{

		}
	}

	public async Task PushMessage(SocketUser user, ulong channelToUse, string strMessage)
	{
		try
		{
			if (discord != null)
			{
				if (channelToUse == (ulong)EDiscordChannelIDs.DirectMessage)
				{
					PushDM(user, strMessage);
				}
				else
				{
					ISocketMessageChannel channel = (ISocketMessageChannel)discord.GetChannel(channelToUse);
					if (channel != null)
					{
						RestUserMessage msg = await channel.SendMessageAsync(strMessage).ConfigureAwait(true);
					}
				}
			}
		}
		catch
		{

		}
	}
}