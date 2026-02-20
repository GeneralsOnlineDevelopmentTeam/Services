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
	public static Dictionary<Int64, string> g_dictInitialExeCRCs = new();
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

	private static readonly TimeSpan DiscordOperationTimeout = TimeSpan.FromSeconds(10);

	private static async void FireAndForgetAsync(Func<Task> taskFactory)
	{
		try
		{
			await taskFactory();
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Error in fire-and-forget task: {ex.Message}");
		}
	}

	public DiscordBot()
	{
#if !DEBUG
		InitAsync();
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
			string strFormattedChatMsg = String.Format("[{0} - UID {1}] {2}", strDisplayName, userID, strMessage);

			ISocketMessageChannel? channel = GetChannel(EDiscordChannelIDs.NetworkRoomChat);
			if (channel != null)
			{
				string strDiscordMsg = String.Format("[NETWORK ROOM CHAT ID #{0}] {1}", roomID, strFormattedChatMsg);
				await channel.SendMessageAsync(strDiscordMsg).ConfigureAwait(true);
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
							int numPlayers = GenOnlineService.WebSocketManager.GetUserDataCache().Count;
							string strMessage = String.Format("There are currently {0} players online.", numPlayers);

							if (enumChannelID == EDiscordChannelIDs.DirectMessage)
							{
								FireAndForgetAsync(() => PushDM(message.Author, strMessage));
							}
							else
							{
								FireAndForgetAsync(() => PushChannelMessage(enumChannelID, strMessage));
							}
						}
						else if (message.Content.ToLower() == "!lobbies")
						{
							int numLobbies = LobbyManager.GetNumLobbies();
							string strMessage = String.Format("There are currently {0} lobbies.", numLobbies);

							if (enumChannelID == EDiscordChannelIDs.DirectMessage)
							{
								FireAndForgetAsync(() => PushDM(message.Author, strMessage));
							}
							else
							{
								FireAndForgetAsync(() => PushChannelMessage(enumChannelID, strMessage));
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
										FireAndForgetAsync(() => PushDM(message.Author, strMessage));
									}
									else
									{
										FireAndForgetAsync(() => PushChannelMessage(enumChannelID, strMessage));
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
										FireAndForgetAsync(() => PushDM(message.Author, strMessage));
									}
									else
									{
										FireAndForgetAsync(() => PushChannelMessage(enumChannelID, strMessage));
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
											UserSession? targetData = GenOnlineService.WebSocketManager.GetDataFromUser(TargetUserID);
											
											if (targetData != null)
											{
												FireAndForgetAsync(() => PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"User {TargetUserID} ({targetData.m_strDisplayName}) has been kicked from the server."));

												UserWebSocketInstance? oldWS = GenOnlineService.WebSocketManager.GetWebSocketForSession(targetData);
												await GenOnlineService.WebSocketManager.DeleteSession(TargetUserID, oldWS, true);
											}
											else
											{
												FireAndForgetAsync(() => PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"User {TargetUserID} is not active on the server."));
											}
										}
										else
										{
											FireAndForgetAsync(() => PushChannelMessage(EDiscordChannelIDs.AdminCommands, "Invalid Command Syntax. !kick <user_id> (e.g. !kick 123)"));
										}
									}
									else
									{
										PushChannelMessage(EDiscordChannelIDs.AdminCommands, "Invalid Command Syntax. !kick <user_id> (e.g. !kick 123)");
									}
								}
								else
								{
									FireAndForgetAsync(() => PushDM(message.Author, "You don't have access to staff commands."));
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

										bool bFound = false;
										var sessions = GenOnlineService.WebSocketManager.GetUserDataCache();
										foreach (var session in sessions)
										{
											if (session.Value.m_strDisplayName.ToLower() == strname.ToLower())
											{
												FireAndForgetAsync(() => PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"User {session.Value.m_strDisplayName} is user ID {session.Key}."));
												bFound = true;
												break;
											}
										}

										if (!bFound)
										{
											FireAndForgetAsync(() => PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"User {strname} is not active on the server."));
										}
									}
									else
									{
										PushChannelMessage(EDiscordChannelIDs.AdminCommands, "Invalid Command Syntax. !kick <user_id> (e.g. !kick 123)");
									}
								}
								else
								{
									FireAndForgetAsync(() => PushDM(message.Author, "You don't have access to staff commands."));
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
										foreach (KeyValuePair<Int64, UserSession> sessionData in GenOnlineService.WebSocketManager.GetUserDataCache())
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

										FireAndForgetAsync(() => PushChannelMessage(EDiscordChannelIDs.AdminCommands, $"Announcement '{strMessage}' was delivered to {numDelivered} users"));
									}
									else
									{
										FireAndForgetAsync(() => PushChannelMessage(EDiscordChannelIDs.AdminCommands, "Invalid Command Syntax. !announce <message> (e.g. !announce Hello)"));
									}
								}
								else
								{
									FireAndForgetAsync(() => PushDM(message.Author, "You don't have access to staff commands."));
								}
							}
						}


						//JSONRequest_PushCommand requestToSend = new JSONRequest_PushCommand(new DiscordUser(message.Author.Id, message.Author.Username), message.Content, enumChannelID);
						//Program.GetRestClient().QueueRequest(requestToSend, CRestClient.ERestCallbackThreadingMode.ContinueOnWorkerThread, null);
					}
					else
					{
						FireAndForgetAsync(() => PushDM(message.Author, "Too many commands. Please wait."));
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

	private static Task LogAsync(LogMessage log)
	{
		Console.WriteLine(log.ToString());
		System.Diagnostics.Debug.WriteLine(log.ToString());
		return Task.CompletedTask;
	}

	private async void InitAsync()
	{
		try
		{
#if !DEBUG || USE_DISCORD_IN_DEBUG
		DiscordSocketConfig conf = new();
		conf.GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.DirectMessages | GatewayIntents.MessageContent;
		conf.DefaultRetryMode = RetryMode.AlwaysRetry;
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
		catch (Exception ex)
		{
			Console.WriteLine($"Error in Discord InitAsync: {ex.Message}");
		}
	}

	public async Task PushDM(SocketUser user, string strMessage)
	{
		try
		{
			if (user != null)
			{
				using var cts = new CancellationTokenSource(DiscordOperationTimeout);
				await user.SendMessageAsync(strMessage).WaitAsync(cts.Token).ConfigureAwait(false);
			}
		}
		catch
		{
			// User probably has some privacy settings that do not allow us to send DMs, or timeout occurred
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

	public async Task PushChannelMessage(EDiscordChannelIDs channelID, string strMessage)
	{
		try
		{
			ISocketMessageChannel? channel = GetChannel(channelID);
			if (channel != null)
			{
				using var cts = new CancellationTokenSource(DiscordOperationTimeout);
				await channel.SendMessageAsync(strMessage).WaitAsync(cts.Token).ConfigureAwait(false);
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
					await PushDM(user, strMessage).ConfigureAwait(false);
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