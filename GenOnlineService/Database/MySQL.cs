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

#define USE_PER_QUERY_CONNECTION

using Amazon.S3.Model;
using Discord;
using GenOnlineService;
using GenOnlineService.Controllers;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Hosting;
using MySql.Data.MySqlClient;
using MySqlX.XDevAPI;
using MySqlX.XDevAPI.Common;
using Sentry.Protocol;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Policy;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using static Database.Functions;
using static Database.Functions.Auth;
using static Database.Functions.Lobby;

public class DailyStats
{
	public const int numSides = 12;
    public int[] matches { get; set; } = new int[12] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    public int[] wins { get; set; } = new int[12] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
}

/*
 * 2, // USA
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
*/

public static class DailyStatsManager
{
	public static DailyStats g_Stats = new();

	public static async Task LoadFromDB()
	{
        g_Stats = await Database.Functions.Auth.LoadDailyStats(GlobalDatabaseInstance.g_Database);
    }

	public static async Task SaveToDB()
	{
        await Database.Functions.Auth.StoreDailyStats(GlobalDatabaseInstance.g_Database, g_Stats);
    }

	public static void RegisterOutcome(int army, bool bWon)
	{
		try
		{
            int armyIndex = army - 2; // teams start at 2, so substract for array indices

            if (armyIndex >= 0 && armyIndex <= 11)
            {
                ++g_Stats.matches[armyIndex];

                if (bWon)
                {
                    ++g_Stats.wins[armyIndex];
                }

				// clamp to a sane value, just incase (wins can never be more than matches)
				if (g_Stats.wins[armyIndex] > g_Stats.matches[armyIndex])
				{
					g_Stats.wins[armyIndex] = g_Stats.matches[armyIndex];

                }
            }
        }
		catch (Exception ex)
		{
			Console.WriteLine($"[ERROR] RegisterOutcome failed: {ex.Message}");
		}
	}
}

namespace Database
{
	public static class Functions
	{
		public static class ServiceStats
		{
			public async static Task CommitStats(MySQLInstance m_Inst, int day_of_year, int hour_of_day, int player_peak, int lobbies_peak)
			{
				await m_Inst.Query("INSERT INTO service_stats SET day_of_year=@day_of_year, hour_of_day=@hour_of_day, player_peak=@player_peak, lobbies_peak=@lobbies_peak ON DUPLICATE KEY UPDATE player_peak=GREATEST(player_peak, @player_peak), lobbies_peak=GREATEST(lobbies_peak, @lobbies_peak);",
					new()
					{
						{ "@day_of_year", day_of_year },
						{ "@hour_of_day", hour_of_day },
						{ "@player_peak", player_peak },
						{ "@lobbies_peak", lobbies_peak }
					}
				);

				// TODO_URGENT: Handle year roll over
				await m_Inst.Query("DELETE FROM service_stats WHERE day_of_year<(@day_of_year - 30);",
					new()
					{
						{ "@day_of_year", day_of_year }
					}
				);
			}
		}

		public static class MatchHistory
		{
			public async static Task<Int64> GetHighestMatchID(MySQLInstance m_Inst)
			{
				var res = await m_Inst.Query("SELECT MAX(match_id) as highest_id FROM `match_history`;", null);

				if (res.NumRows() > 0)
				{
					CMySQLRow row = res.GetRow(0);

					Int64 highestMatchID = Convert.ToInt64(row["highest_id"]);

					return highestMatchID;
				}

				return -1;
			}

			
			public async static Task<MatchHistoryCollection> GetMatchesInRange(MySQLInstance m_Inst, Int64 startID, Int64 endID)
			{
				var res = await m_Inst.Query("SELECT match_id, owner, name, finished, started, time_finished, map_name, map_path, match_roster_type, map_official, vanilla_teams, starting_cash, limit_superweapons, track_stats, allow_observers, max_cam_height, member_slot_0, member_slot_1, member_slot_2, member_slot_3, member_slot_4, member_slot_5, member_slot_6, member_slot_7 FROM match_history WHERE match_id>=@startID AND match_id<=@endID AND finished=true;",
					new()
					{
						{ "@startID", startID },
						{ "@endID", endID }
					}
				);

				MatchHistoryCollection collection = new();
				foreach (var row in res.GetRows())
				{

					Int64 match_id = Convert.ToInt64(row["match_id"]);
					Int64 owner = Convert.ToInt64(row["owner"]);
					string? name = Convert.ToString(row["name"]);
					bool finished = Convert.ToBoolean(row["finished"]);
					string? time_started = Convert.ToString(row["started"]);
					string? time_ended = Convert.ToString(row["time_finished"]);
					string? map_name = Convert.ToString(row["map_name"]);
					string? map_path = Convert.ToString(row["map_path"]);
					string? match_roster_type = Convert.ToString(row["match_roster_type"]);
					bool map_official = Convert.ToBoolean(row["map_official"]);
					bool vanilla_teams = Convert.ToBoolean(row["vanilla_teams"]);
					UInt32 starting_cash = Convert.ToUInt32(row["starting_cash"]);
					bool limit_superweapons = Convert.ToBoolean(row["limit_superweapons"]);
					bool track_stats = Convert.ToBoolean(row["track_stats"]);
					bool allow_observers = Convert.ToBoolean(row["allow_observers"]);
					UInt16 max_cam_height = Convert.ToUInt16(row["max_cam_height"]);

					string? strJson_Slot0 = Convert.ToString(row["member_slot_0"]);
					string? strJson_Slot1 = Convert.ToString(row["member_slot_1"]);
					string? strJson_Slot2 = Convert.ToString(row["member_slot_2"]);
					string? strJson_Slot3 = Convert.ToString(row["member_slot_3"]);
					string? strJson_Slot4 = Convert.ToString(row["member_slot_4"]);
					string? strJson_Slot5 = Convert.ToString(row["member_slot_5"]);
					string? strJson_Slot6 = Convert.ToString(row["member_slot_6"]);
					string? strJson_Slot7 = Convert.ToString(row["member_slot_7"]);

					if (name == null || time_started == null || time_ended == null || map_name == null || map_path == null)
					{
						continue;
					}

					string strMatchRosterType = String.Empty;
					
					MatchHistory_Entry collection_entry = new(
						match_id,
						owner,
						name,
						finished,
						time_started,
						time_ended,
						map_name,
						map_path,
						match_roster_type,
						map_official,
						vanilla_teams,
						starting_cash,
						limit_superweapons,
						track_stats,
						allow_observers,
						max_cam_height
						);

					// add members
					// TODO: Optimize, we deserialize to reserialize... just return the JSON directly
					MatchdataMemberModel? member0 = String.IsNullOrEmpty(strJson_Slot0) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot0);
					MatchdataMemberModel? member1 = String.IsNullOrEmpty(strJson_Slot1) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot1);
					MatchdataMemberModel? member2 = String.IsNullOrEmpty(strJson_Slot2) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot2);
					MatchdataMemberModel? member3 = String.IsNullOrEmpty(strJson_Slot3) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot3);
					MatchdataMemberModel? member4 = String.IsNullOrEmpty(strJson_Slot4) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot4);
					MatchdataMemberModel? member5 = String.IsNullOrEmpty(strJson_Slot5) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot5);
					MatchdataMemberModel? member6 = String.IsNullOrEmpty(strJson_Slot6) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot6);
					MatchdataMemberModel? member7 = String.IsNullOrEmpty(strJson_Slot7) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot7);

					// add members to collection
					if (member0 != null) { collection_entry.members.Add(member0); }
					if (member1 != null) { collection_entry.members.Add(member1); }
					if (member2 != null) { collection_entry.members.Add(member2); }
					if (member3 != null) { collection_entry.members.Add(member3); }
					if (member4 != null) { collection_entry.members.Add(member4); }
					if (member5 != null) { collection_entry.members.Add(member5); }
					if (member6 != null) { collection_entry.members.Add(member6); }
					if (member7 != null) { collection_entry.members.Add(member7); }

					// commit match
					collection.matches.Add(collection_entry);
				}

				return collection;
			}
		}

		public static class Leaderboards
		{
			public class LeaderboardPoints
			{
				public int daily = 0;
                public int daily_matches = 0;
                public int monthly = 0;
                public int monthly_matches = 0;
                public int yearly = 0;
                public int yearly_matches = 0;
            }

			public async static Task<LeaderboardPoints> GetLeaderboardDataForUser(MySQLInstance m_Inst, Int64 playerID, int dayOfYear, int monthOfYear, int year)
			{
				LeaderboardPoints retVal = new();

				// daily
				var resDaily = await m_Inst.Query("SELECT points, wins+losses as `matches` FROM leaderboard_daily WHERE user_id=@user_id AND day_of_year=@day_of_year AND year=@year LIMIT 1;",
						new()
						{
							{ "@user_id", playerID },
							{ "@day_of_year", dayOfYear },
							{ "@year", year }
						}
					);
				if (resDaily.NumRows() > 0)
				{
					CMySQLRow row = resDaily.GetRow(0);
					retVal.daily = Convert.ToInt32(row["points"]);
					retVal.daily_matches = Convert.ToInt32(row["matches"]);
                }

				// monthly
				var resMonthly = await m_Inst.Query("SELECT points, wins+losses as `matches` FROM leaderboard_monthly WHERE user_id=@user_id AND month_of_year=@month_of_year AND year=@year LIMIT 1;",
						new()
						{
							{ "@user_id", playerID },
							{ "@month_of_year", monthOfYear },
							{ "@year", year }
						}
					);
				if (resMonthly.NumRows() > 0)
				{
					CMySQLRow row = resMonthly.GetRow(0);
					retVal.monthly = Convert.ToInt32(row["points"]);
                    retVal.monthly_matches = Convert.ToInt32(row["matches"]);
                }

				// yearly
				var resYearly = await m_Inst.Query("SELECT points, wins+losses as `matches` FROM leaderboard_yearly WHERE user_id=@user_id AND year=@year LIMIT 1;",
						new()
						{
							{ "@user_id", playerID },
							{ "@year", year }
						}
					);
				if (resYearly.NumRows() > 0)
				{
					CMySQLRow row = resYearly.GetRow(0);
					retVal.yearly = Convert.ToInt32(row["points"]);
                    retVal.yearly_matches = Convert.ToInt32(row["matches"]);
                }

				return retVal;
			}

			public async static Task<Dictionary<Int64, LeaderboardPoints>> GetBulkLeaderboardData(MySQLInstance m_Inst, List<Int64> playerIDs, int dayOfYear, int monthOfYear, int year)
			{
				Dictionary<Int64, LeaderboardPoints> results = new();
				
				if (playerIDs == null || playerIDs.Count == 0)
				{
					return results;
				}

				// Initialize all users with default values
				foreach (Int64 playerId in playerIDs)
				{
					results[playerId] = new LeaderboardPoints();
				}

				// Build IN clause
				string inClause = string.Join(",", playerIDs);

				// Bulk daily
				var resDaily = await m_Inst.Query($"SELECT user_id, points, wins+losses as `matches` FROM leaderboard_daily WHERE user_id IN ({inClause}) AND day_of_year={dayOfYear} AND year={year};", null);
				foreach (var row in resDaily.GetRows())
				{
					Int64 userId = Convert.ToInt64(row["user_id"]);
					if (results.ContainsKey(userId))
					{
						results[userId].daily = Convert.ToInt32(row["points"]);
						results[userId].daily_matches = Convert.ToInt32(row["matches"]);
					}
				}

				// Bulk monthly
				var resMonthly = await m_Inst.Query($"SELECT user_id, points, wins+losses as `matches` FROM leaderboard_monthly WHERE user_id IN ({inClause}) AND month_of_year={monthOfYear} AND year={year};", null);
				foreach (var row in resMonthly.GetRows())
				{
					Int64 userId = Convert.ToInt64(row["user_id"]);
					if (results.ContainsKey(userId))
					{
						results[userId].monthly = Convert.ToInt32(row["points"]);
						results[userId].monthly_matches = Convert.ToInt32(row["matches"]);
					}
				}

				// Bulk yearly
				var resYearly = await m_Inst.Query($"SELECT user_id, points, wins+losses as `matches` FROM leaderboard_yearly WHERE user_id IN ({inClause}) AND year={year};", null);
				foreach (var row in resYearly.GetRows())
				{
					Int64 userId = Convert.ToInt64(row["user_id"]);
					if (results.ContainsKey(userId))
					{
						results[userId].yearly = Convert.ToInt32(row["points"]);
						results[userId].yearly_matches = Convert.ToInt32(row["matches"]);
					}
				}

				return results;
			}

			public async static Task DetermineLobbyWinnerIfNotPresent(MySQLInstance m_Inst, GenOnlineService.Lobby lobbyInst)
			{
				// NOTE: this works only when you call this function BEFORE updating ELO, as elo will read it all to award points

				// get each lobby member
				var res = await m_Inst.Query("SELECT member_slot_0, member_slot_1, member_slot_2, member_slot_3, member_slot_4, member_slot_5, member_slot_6, member_slot_7 FROM match_history WHERE match_id=@matchID LIMIT 1;",
						new()
						{
							{ "@matchID", lobbyInst.MatchID }
						}
					);

				Dictionary<int, MatchdataMemberModel> lstMembers = new Dictionary<int, MatchdataMemberModel>();
				foreach (var row in res.GetRows())
				{
					string? strJson_Slot0 = Convert.ToString(row["member_slot_0"]);
					string? strJson_Slot1 = Convert.ToString(row["member_slot_1"]);
					string? strJson_Slot2 = Convert.ToString(row["member_slot_2"]);
					string? strJson_Slot3 = Convert.ToString(row["member_slot_3"]);
					string? strJson_Slot4 = Convert.ToString(row["member_slot_4"]);
					string? strJson_Slot5 = Convert.ToString(row["member_slot_5"]);
					string? strJson_Slot6 = Convert.ToString(row["member_slot_6"]);
					string? strJson_Slot7 = Convert.ToString(row["member_slot_7"]);

					// TODO: Optimize, we deserialize to reserialize... just return the JSON directly
					MatchdataMemberModel? member0 = String.IsNullOrEmpty(strJson_Slot0) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot0);
					MatchdataMemberModel? member1 = String.IsNullOrEmpty(strJson_Slot1) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot1);
					MatchdataMemberModel? member2 = String.IsNullOrEmpty(strJson_Slot2) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot2);
					MatchdataMemberModel? member3 = String.IsNullOrEmpty(strJson_Slot3) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot3);
					MatchdataMemberModel? member4 = String.IsNullOrEmpty(strJson_Slot4) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot4);
					MatchdataMemberModel? member5 = String.IsNullOrEmpty(strJson_Slot5) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot5);
					MatchdataMemberModel? member6 = String.IsNullOrEmpty(strJson_Slot6) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot6);
					MatchdataMemberModel? member7 = String.IsNullOrEmpty(strJson_Slot7) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot7);

					// add members to collection with slot index as key
					if (member0 != null) { lstMembers[0] = member0.Value; }
					if (member1 != null) { lstMembers[1] = member1.Value; }
					if (member2 != null) { lstMembers[2] = member2.Value; }
					if (member3 != null) { lstMembers[3] = member3.Value; }
					if (member4 != null) { lstMembers[4] = member4.Value; }
					if (member5 != null) { lstMembers[5] = member5.Value; }
					if (member6 != null) { lstMembers[6] = member6.Value; }
					if (member7 != null) { lstMembers[7] = member7.Value; }
				}

				// do we have a winner already?
				bool bHasWinner = false;
				int winnerTeam = -1;
				foreach (var kvp in lstMembers)
				{
					if (kvp.Value.won)
					{
						bHasWinner = true;
						winnerTeam = kvp.Value.team;
						break;
					}
				}

				// if we have a winner, and they have a team, make everyone else on that team a winner
				if (bHasWinner)
				{
					if (winnerTeam != -1)
					{
						foreach (var kvp in lstMembers)
						{
							int slotIndex = kvp.Key;
							MatchdataMemberModel lobbyMember = kvp.Value;
							
							if (lobbyMember.team == winnerTeam) // same team, and not '-1'
							{
								// save it
								await Database.Functions.Lobby.UpdateMatchHistoryMakeWinner(GlobalDatabaseInstance.g_Database, lobbyInst.MatchID, slotIndex);
							}
						}
					}
				}

				// no winner? pick one
				if (!bHasWinner)
				{
					// pick the last person to leave
					DateTime mostRecentlyLeftTimestamp = DateTime.UnixEpoch;
					MatchdataMemberModel? lastPlayerToLeave = null;
					int lastPlayerSlotIndex = -1;
					foreach (var kvp in lstMembers)
					{
						MatchdataMemberModel lobbyMember = kvp.Value;
						if (lobbyInst.TimeMemberLeft.ContainsKey(lobbyMember.user_id))
						{
							if (lobbyInst.TimeMemberLeft[lobbyMember.user_id] >= mostRecentlyLeftTimestamp)
							{
								mostRecentlyLeftTimestamp = lobbyInst.TimeMemberLeft[lobbyMember.user_id];
								lastPlayerToLeave = lobbyMember;
								lastPlayerSlotIndex = kvp.Key;
							}
						}
					}

					if (lastPlayerToLeave != null)
					{
						int winningPlayerTeam = lastPlayerToLeave.Value.team;

						// this player + everyone on the same team is also a winner!
						foreach (var kvp in lstMembers)
						{
							int slotIndex = kvp.Key;
							MatchdataMemberModel lobbyMember = kvp.Value;
							
							// is it this guy?
							if (lobbyMember.user_id == lastPlayerToLeave.Value.user_id)
							{
								// save it
								await Database.Functions.Lobby.UpdateMatchHistoryMakeWinner(GlobalDatabaseInstance.g_Database, lobbyInst.MatchID, slotIndex);
							}
							else if (winningPlayerTeam != -1 && lobbyMember.team == winningPlayerTeam) // same team, and not '-1'
							{
								// save it
								await Database.Functions.Lobby.UpdateMatchHistoryMakeWinner(GlobalDatabaseInstance.g_Database, lobbyInst.MatchID, slotIndex);
							}
						}
					}



				}
			}

			public async static Task UpdateLeaderboardAndElo(MySQLInstance m_Inst, GenOnlineService.Lobby lobbyInst)
			{
				// must be in a QM
				if (lobbyInst.LobbyType != ELobbyType.QuickMatch)
				{
					return;
				}

				// TODO_QUICKMATCH: This is a bit slow probably, quite a few queries

				// we use the time at which the lobby was created, not when it ended, since the day of year etc might have changed
				int dayOfYear = lobbyInst.TimeCreated.DayOfYear;
				int monthOfYear = lobbyInst.TimeCreated.Month;
				int year = lobbyInst.TimeCreated.Year;

				// process each member
				var res = await m_Inst.Query("SELECT member_slot_0, member_slot_1, member_slot_2, member_slot_3, member_slot_4, member_slot_5, member_slot_6, member_slot_7 FROM match_history WHERE match_id=@matchID LIMIT 1;",
						new()
						{
							{ "@matchID", lobbyInst.MatchID }
						}
					);

				List<MatchdataMemberModel> lstMembers = new List<MatchdataMemberModel>();
				foreach (var row in res.GetRows())
				{
					string? strJson_Slot0 = Convert.ToString(row["member_slot_0"]);
					string? strJson_Slot1 = Convert.ToString(row["member_slot_1"]);
					string? strJson_Slot2 = Convert.ToString(row["member_slot_2"]);
					string? strJson_Slot3 = Convert.ToString(row["member_slot_3"]);
					string? strJson_Slot4 = Convert.ToString(row["member_slot_4"]);
					string? strJson_Slot5 = Convert.ToString(row["member_slot_5"]);
					string? strJson_Slot6 = Convert.ToString(row["member_slot_6"]);
					string? strJson_Slot7 = Convert.ToString(row["member_slot_7"]);

					// TODO: Optimize, we deserialize to reserialize... just return the JSON directly
					MatchdataMemberModel? member0 = String.IsNullOrEmpty(strJson_Slot0) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot0);
					MatchdataMemberModel? member1 = String.IsNullOrEmpty(strJson_Slot1) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot1);
					MatchdataMemberModel? member2 = String.IsNullOrEmpty(strJson_Slot2) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot2);
					MatchdataMemberModel? member3 = String.IsNullOrEmpty(strJson_Slot3) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot3);
					MatchdataMemberModel? member4 = String.IsNullOrEmpty(strJson_Slot4) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot4);
					MatchdataMemberModel? member5 = String.IsNullOrEmpty(strJson_Slot5) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot5);
					MatchdataMemberModel? member6 = String.IsNullOrEmpty(strJson_Slot6) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot6);
					MatchdataMemberModel? member7 = String.IsNullOrEmpty(strJson_Slot7) ? null : JsonSerializer.Deserialize<MatchdataMemberModel>(strJson_Slot7);

					// add members to collection
					if (member0 != null) { lstMembers.Add((MatchdataMemberModel)member0); }
					if (member1 != null) { lstMembers.Add((MatchdataMemberModel)member1); }
					if (member2 != null) { lstMembers.Add((MatchdataMemberModel)member2); }
					if (member3 != null) { lstMembers.Add((MatchdataMemberModel)member3); }
					if (member4 != null) { lstMembers.Add((MatchdataMemberModel)member4); }
					if (member5 != null) { lstMembers.Add((MatchdataMemberModel)member5); }
					if (member6 != null) { lstMembers.Add((MatchdataMemberModel)member6); }
					if (member7 != null) { lstMembers.Add((MatchdataMemberModel)member7); }
				}

				// ELO (current)
				{
                    Dictionary<Int64, EloData> dictEloData = new Dictionary<Int64, EloData>();

                    // initialize data with bulk query (1 query instead of N)
                    List<Int64> userIds = lstMembers.Select(m => m.user_id).ToList();
                    dictEloData = await Database.Functions.Auth.GetBulkELOData(GlobalDatabaseInstance.g_Database, userIds);

                    foreach (MatchdataMemberModel member in lstMembers)
                    {
                        // TODO_ELO: Opt, this is O(n^2)
                        // for this member, check results vs every other member we were against
                        foreach (MatchdataMemberModel compareToMember in lstMembers)
                        {
                            if (compareToMember.user_id != member.user_id)
                            {
                                ref EloData playerAData = ref CollectionsMarshal.GetValueRefOrAddDefault(dictEloData, member.user_id, out bool existsA);
                                ref EloData playerBData = ref CollectionsMarshal.GetValueRefOrAddDefault(dictEloData, compareToMember.user_id, out bool existsB);

                                if (existsA && existsB) // should always exist...
                                {
                                    // must be on different teams, otherwise we dont care, we can't win against our own team
                                    if (compareToMember.team != member.team || member.team == -1)
                                    {
                                        Elo.ApplyResult(ref playerAData, ref playerBData, member.won ? MatchResult.PlayerAWins : MatchResult.PlayerBWins);
                                    }
                                }
                            }
                        }
                    }

                    // now update num matches for everyone, we cant do this above because we iterate player A X times for example, so it increases incorrectly
                    foreach (MatchdataMemberModel member in lstMembers)
                    {
                        ref EloData playerData = ref CollectionsMarshal.GetValueRefOrAddDefault(dictEloData, member.user_id, out bool existsA);
                        ++playerData.NumMatches;
                    }

                    // save each ELO data to DB
                    foreach (var eloPair in dictEloData)
                    {
                        // store on player if online
                        UserSession? playerSess = GenOnlineService.WebSocketManager.GetDataFromUser(eloPair.Key);
                        if (playerSess != null)
                        {
                            playerSess.GameStats.EloRating = eloPair.Value.Rating;
                            playerSess.GameStats.EloMatches = eloPair.Value.NumMatches;
                        }
                        await Database.Functions.Auth.SaveELOData(GlobalDatabaseInstance.g_Database, eloPair.Key, eloPair.Value);
                    }
                }

				// ELO DAILY, MONTHLY AND ANNUAL
				{
                    Dictionary<Int64, EloData> dictEloData_Daily = new Dictionary<Int64, EloData>();
                    Dictionary<Int64, EloData> dictEloData_Monthly = new Dictionary<Int64, EloData>();
                    Dictionary<Int64, EloData> dictEloData_Yearly = new Dictionary<Int64, EloData>();

                    // initialize data with bulk query (3 queries instead of N*3)
                    List<Int64> userIds = lstMembers.Select(m => m.user_id).ToList();
                    Dictionary<Int64, LeaderboardPoints> bulkLbData = await GetBulkLeaderboardData(m_Inst, userIds, dayOfYear, monthOfYear, year);
                    
                    foreach (MatchdataMemberModel member in lstMembers)
                    {
                        LeaderboardPoints userLBPoints = bulkLbData[member.user_id];
                        dictEloData_Daily[member.user_id] = new EloData(userLBPoints.daily, userLBPoints.daily_matches);
                        dictEloData_Monthly[member.user_id] = new EloData(userLBPoints.monthly, userLBPoints.monthly_matches);
                        dictEloData_Yearly[member.user_id] = new EloData(userLBPoints.yearly, userLBPoints.yearly_matches);
                    }

                    foreach (MatchdataMemberModel member in lstMembers)
                    {
                        // TODO_ELO: Opt, this is O(n^2)
                        // for this member, check results vs every other member we were against
                        foreach (MatchdataMemberModel compareToMember in lstMembers)
                        {
                            if (compareToMember.user_id != member.user_id)
                            {

								// Daily
								{
                                    ref EloData playerAData = ref CollectionsMarshal.GetValueRefOrAddDefault(dictEloData_Daily, member.user_id, out bool existsA);
                                    ref EloData playerBData = ref CollectionsMarshal.GetValueRefOrAddDefault(dictEloData_Daily, compareToMember.user_id, out bool existsB);

                                    if (existsA && existsB) // should always exist...
                                    {
                                        // must be on different teams, otherwise we dont care, we can't win against our own team
                                        if (compareToMember.team != member.team || member.team == -1)
                                        {
                                            Elo.ApplyResult(ref playerAData, ref playerBData, member.won ? MatchResult.PlayerAWins : MatchResult.PlayerBWins);
                                        }
                                    }
                                }

								// Monthly
								{
                                    ref EloData playerAData = ref CollectionsMarshal.GetValueRefOrAddDefault(dictEloData_Monthly, member.user_id, out bool existsA);
                                    ref EloData playerBData = ref CollectionsMarshal.GetValueRefOrAddDefault(dictEloData_Monthly, compareToMember.user_id, out bool existsB);

                                    if (existsA && existsB) // should always exist...
                                    {
                                        // must be on different teams, otherwise we dont care, we can't win against our own team
                                        if (compareToMember.team != member.team || member.team == -1)
                                        {
                                            Elo.ApplyResult(ref playerAData, ref playerBData, member.won ? MatchResult.PlayerAWins : MatchResult.PlayerBWins);
                                        }
                                    }
                                }

								// Yearly
								{
                                    ref EloData playerAData = ref CollectionsMarshal.GetValueRefOrAddDefault(dictEloData_Yearly, member.user_id, out bool existsA);
                                    ref EloData playerBData = ref CollectionsMarshal.GetValueRefOrAddDefault(dictEloData_Yearly, compareToMember.user_id, out bool existsB);

                                    if (existsA && existsB) // should always exist...
                                    {
                                        // must be on different teams, otherwise we dont care, we can't win against our own team
                                        if (compareToMember.team != member.team || member.team == -1)
                                        {
                                            Elo.ApplyResult(ref playerAData, ref playerBData, member.won ? MatchResult.PlayerAWins : MatchResult.PlayerBWins);
                                        }
                                    }
                                }
                                
                            }
                        }
                    }

					// save each ELO data to DB using batched transaction
					// Build all UPDATE statements and execute in single transaction
					List<string> dailyUpdates = new();
					List<string> monthlyUpdates = new();
					List<string> yearlyUpdates = new();

					foreach (MatchdataMemberModel member in lstMembers)
					{
						EloData playerData_Daily = dictEloData_Daily[member.user_id];
						EloData playerData_Monthly = dictEloData_Monthly[member.user_id];
						EloData playerData_Yearly = dictEloData_Yearly[member.user_id];

                        int winsModifier = 0;
						int lossesModifier = 0;

						if (member.won)
						{
							++winsModifier;
						}
						else
						{
							++lossesModifier;
						}

						// Build UPDATE statements (sanitized parameters)
						dailyUpdates.Add($"UPDATE leaderboard_daily SET points={playerData_Daily.Rating}, losses=losses+{lossesModifier}, wins=wins+{winsModifier} WHERE user_id={member.user_id} AND day_of_year={dayOfYear} AND year={year} LIMIT 1;");
						monthlyUpdates.Add($"UPDATE leaderboard_monthly SET points={playerData_Monthly.Rating}, losses=losses+{lossesModifier}, wins=wins+{winsModifier} WHERE user_id={member.user_id} AND month_of_year={monthOfYear} AND year={year} LIMIT 1;");
						yearlyUpdates.Add($"UPDATE leaderboard_yearly SET points={playerData_Yearly.Rating}, losses=losses+{lossesModifier}, wins=wins+{winsModifier} WHERE user_id={member.user_id} AND year={year} LIMIT 1;");
					}

					// Execute all updates in single batch (3 queries instead of N*3)
					if (dailyUpdates.Count > 0)
					{
						string batchedDaily = string.Join("\n", dailyUpdates);
						await m_Inst.Query(batchedDaily, null);
					}

					if (monthlyUpdates.Count > 0)
					{
						string batchedMonthly = string.Join("\n", monthlyUpdates);
						await m_Inst.Query(batchedMonthly, null);
					}

					if (yearlyUpdates.Count > 0)
					{
						string batchedYearly = string.Join("\n", yearlyUpdates);
						await m_Inst.Query(batchedYearly, null);
					}

                }
			}

			public async static Task CreateUserEntriesIfNotExists(MySQLInstance m_Inst, Int64 playerID)
			{
				int dayOfYear = DateTime.UtcNow.DayOfYear;
				int monthOfYear = DateTime.UtcNow.Month;
				int year = DateTime.UtcNow.Year;

				// OK to try and insert here, will fail if key combination already exists
				await m_Inst.Query("INSERT IGNORE INTO leaderboard_daily SET user_id=@user_id, points=@points, day_of_year=@day_of_year, year=@year, wins=0, losses=0;",
				new()
				{
						{ "@user_id", playerID },
						{ "@points", EloConfig.BaseRating },
						{ "@day_of_year", dayOfYear },
						{ "@year", year }
					}
				);

				// Month
				await m_Inst.Query("INSERT IGNORE INTO leaderboard_monthly SET user_id=@user_id, points=@points, month_of_year=@month_of_year, year=@year, wins=0, losses=0;",
				new()
				{
						{ "@user_id", playerID },
						{ "@points", EloConfig.BaseRating },
						{ "@month_of_year", monthOfYear },
						{ "@year", year }
					}
				);

				// Year
				await m_Inst.Query("INSERT IGNORE INTO leaderboard_yearly SET user_id=@user_id, points=@points, year=@year, wins=0, losses=0;",
				new()
				{
						{ "@user_id", playerID },
						{ "@points", EloConfig.BaseRating },
						{ "@year", year }
					}
				);
			}
		}

			public static class Lobby
		{
			public async static Task UpdateDisplayName(MySQLInstance m_Inst, Int64 playerID, string strNewName)
			{
				await m_Inst.Query("UPDATE users SET displayname=@displayname WHERE user_id=@user_id LIMIT 1;",
				new()
				{
						{ "@displayname", strNewName },
						{ "@user_id", playerID }
					}
				);
			}

			// Called when a lobby is deleted, thats the true end of a match
			public async static Task CommitLobbyToMatchHistory(MySQLInstance m_Inst, GenOnlineService.Lobby lobby)
			{
				if (lobby.MatchID != 0) // 0 is invalid
				{
					await m_Inst.Query("UPDATE match_history SET finished=true, time_finished=current_timestamp() WHERE match_id=@match_id AND finished=false LIMIT 1;",
					new()
					{
						{ "@match_id", lobby.MatchID }
					});
				}
			}

			public enum EScreenshotType
			{
				NONE = -1,
				SCREENSHOT_TYPE_LOADSCREEN = 0,
				SCREENSHOT_TYPE_GAMEPLAY = 1,
				SCREENSHOT_TYPE_SCORESCREEN = 2
			}
			

			public enum EMetadataFileType
			{
				UNKNOWN = -1,
				FILE_TYPE_SCREENSHOT = 0,
				FILE_TYPE_REPLAY = 1
			};

			public async static Task AttachMatchHistoryMetadata(MySQLInstance m_Inst, UInt64 MatchID, int slotIndex, string strVal, EMetadataFileType fileType)
			{
				if (MatchID != 0) // 0 is invalid
				{
					if (slotIndex < 0)
					{
						return;
					}

					CMySQLResult resMember = await m_Inst.Query(String.Format("UPDATE match_history SET member_slot_{0} = JSON_ARRAY_APPEND(member_slot_{0}, '$.metadata', JSON_OBJECT('file_name', @file_name, 'file_type', @file_type)) WHERE match_id = @match_id;", slotIndex),
						new()
						{
						{ "@match_id", MatchID },
						{ "@file_name", strVal },
						{ "@file_type", (int)fileType },
						}
					);
				}
			}

			public async static Task UpdateMatchHistoryMakeWinner(MySQLInstance m_Inst, UInt64 MatchID, int slotIndex)
			{
				if (MatchID != 0) // 0 is invalid
				{
					if (slotIndex < 0)
					{
						return;
					}

					CMySQLResult resMember = await m_Inst.Query(String.Format("UPDATE match_history SET member_slot_{0} = JSON_SET(member_slot_{0}, '$.won', @won) WHERE match_id = @match_id;", slotIndex),
						new()
						{
						{ "@match_id", MatchID },
						{ "@won", true }
						}
					);
				}
			}

			public struct MatchdataMemberModel
			{
				public Int64 user_id { get; set; } = -1;            // bigint(20) NOT NULL
				public string display_name { get; set; } = String.Empty;    // varchar(32) NOT NULL
				public EPlayerType slot_state { get; set; } = EPlayerType.SLOT_CLOSED;        // smallint(6) unsigned NOT NULL
				public int side { get; set; } = -1;                 // int(2) NOT NULL
				public int color { get; set; } = -1;                // int(2) NOT NULL
				public int team { get; set; } = -1;                 // int(1) NOT NULL
				public int startpos { get; set; } = -1;             // int(1) NOT NULL
				public int buildings_built { get; set; } = 0;     // int(11) DEFAULT NULL
				public int buildings_killed { get; set; } = 0;     // int(11) DEFAULT NULL
				public int buildings_lost { get; set; } = 0;       // int(11) DEFAULT NULL
				public int units_built { get; set; } = 0;          // int(11) DEFAULT NULL
				public int units_killed { get; set; } = 0;         // int(11) DEFAULT NULL
				public int units_lost { get; set; } = 0;           // int(11) DEFAULT NULL
				public int total_money { get; set; } = 0;          // int(11) DEFAULT NULL

				[JsonConverter(typeof(IntToBoolConverter))]
				public bool won { get; set; } = false;                // tinyint(4) DEFAULT NULL
				public List<MemberMetadataModel> metadata { get; set; } = new List<MemberMetadataModel>();

				public MatchdataMemberModel()
				{
				}
			}

			public class IntToBoolConverter : JsonConverter<bool>
			{
				public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
				{
					return reader.GetInt32() != 0;
				}

				public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
				{
					writer.WriteNumberValue(value ? 1 : 0);
				}
			}

			public struct MemberMetadataModel
			{
				public string file_name { get; set; }
				public EMetadataFileType file_type { get; set; }
			}

			public async static Task<UInt64> CreatePlaceholderMatchHistory(MySQLInstance m_Inst, GenOnlineService.Lobby lobby)
			{
				if (lobby == null)
				{
					return 0;
				}

				// initial members

				// since this is a DB entry, we only insert occupied slots, different behavior from LobbyManager
				MatchdataMemberModel?[] arrMembers = new MatchdataMemberModel?[GenOnlineService.Lobby.maxLobbySize]
				{
					null,
					null,
					null,
					null,
					null,
					null,
					null,
					null
				};


				string strTeamRosterType = String.Empty;
				Dictionary<int, int> playersPerTeam = new();
				int playersSeen = 0;

				//List<MatchdataMemberModel> lstMembers = new List<MatchdataMemberModel>();
				foreach (var member in lobby.Members)
				{
					// dont care about empty/closed slots
					if (member.SlotState == EPlayerType.SLOT_OPEN || member.SlotState == EPlayerType.SLOT_CLOSED)
					{
						continue;
					}

					MatchdataMemberModel newMember = new();
					newMember.user_id = member.UserID;
					newMember.display_name = member.DisplayName;
					newMember.slot_state = member.SlotState;
					newMember.side = member.Side;
					newMember.color = member.Color;
					newMember.team = member.Team;
					newMember.startpos = member.StartingPosition;
					newMember.buildings_built = 0;
					newMember.buildings_killed = 0;
					newMember.buildings_lost = 0;
					newMember.units_built = 0;
					newMember.units_killed = 0;
					newMember.units_lost = 0;
					newMember.total_money = 0;
					newMember.won = false;
					arrMembers[member.SlotIndex] = newMember;

					++playersSeen;
					// used later to determine roster type
					if (playersPerTeam.ContainsKey(newMember.team))
					{
						++playersPerTeam[newMember.team];
					}
					else
					{
						playersPerTeam[newMember.team] = 1;
					}
				}

				// determine FFA, needs no more than 1 player per team, and must be more than 2 players total (cant be 1v1)
				bool bIsFFA = true;
				if (playersSeen <= 2)
				{
					bIsFFA = false;
				}
				else
				{
					foreach (var kvPair in playersPerTeam)
					{
						if (kvPair.Key != -1) // no team is ok for FFA
						{
							if (kvPair.Value > 1) // more than 1 person on a real team, so not FFA
							{
								bIsFFA = false;
								break;
							}
						}
					}
				}

				if (bIsFFA)
				{
					strTeamRosterType = String.Format("{0} Player FFA", playersSeen);
				}
				else
				{
					// now determine roster type
					foreach (var kvPair in playersPerTeam)
					{
						if (kvPair.Key == -1)
						{
							for (int i = 0; i < playersPerTeam[-1]; ++i)
							{
								if (String.IsNullOrEmpty(strTeamRosterType))
								{
									strTeamRosterType = "1";
								}
								else
								{
									strTeamRosterType += "v1";
								}
							}
						}
						else
						{
							if (String.IsNullOrEmpty(strTeamRosterType))
							{
								strTeamRosterType = kvPair.Value.ToString();
							}
							else
							{
								strTeamRosterType += String.Format("v{0}", kvPair.Value.ToString());
							}
						}
					}
				}

#pragma warning disable CS8604 // Possible null reference argument.
					CMySQLResult resMatch = await m_Inst.Query("INSERT INTO match_history(owner, name, map_name, map_path, map_official, match_roster_type, vanilla_teams, starting_cash, limit_superweapons, track_stats, allow_observers, max_cam_height, member_slot_0, member_slot_1, member_slot_2, member_slot_3, member_slot_4, member_slot_5, member_slot_6, member_slot_7) VALUES (@owner, @name, @map_name, @map_path, @map_official, @match_roster_type, @vanilla_teams, @starting_cash, @limit_superweapons, @track_stats, @allow_observers, @max_cam_height, @member_slot_0, @member_slot_1, @member_slot_2, @member_slot_3, @member_slot_4, @member_slot_5, @member_slot_6, @member_slot_7);",
						new()
						{
						{ "@owner", lobby.Owner },
						{ "@name", lobby.Name },
						{ "@map_name", lobby.MapName },
						{ "@map_path", lobby.MapPath },
						{ "@map_official", lobby.IsMapOfficial },
						{ "@match_roster_type", strTeamRosterType },
						{ "@vanilla_teams", lobby.IsVanillaTeamsOnly },
						{ "@starting_cash", lobby.StartingCash },
						{ "@limit_superweapons", lobby.IsLimitSuperweapons },
						{ "@track_stats", lobby.IsTrackingStats },
						{ "@allow_observers", lobby.AllowObservers },
						{ "@max_cam_height", lobby.MaximumCameraHeight },
						{ "@member_slot_0", arrMembers[0] == null ? null : JsonSerializer.Serialize(arrMembers[0])},
						{ "@member_slot_1", arrMembers[1] == null ? null : JsonSerializer.Serialize(arrMembers[1])},
						{ "@member_slot_2", arrMembers[2] == null ? null : JsonSerializer.Serialize(arrMembers[2])},
						{ "@member_slot_3", arrMembers[3] == null ? null : JsonSerializer.Serialize(arrMembers[3])},
						{ "@member_slot_4", arrMembers[4] == null ? null : JsonSerializer.Serialize(arrMembers[4])},
						{ "@member_slot_5", arrMembers[5] == null ? null : JsonSerializer.Serialize(arrMembers[5])},
						{ "@member_slot_6", arrMembers[6] == null ? null : JsonSerializer.Serialize(arrMembers[6])},
						{ "@member_slot_7", arrMembers[7] == null ? null : JsonSerializer.Serialize(arrMembers[7])},
						}
					);
#pragma warning restore CS8604 // Possible null reference argument.

				UInt64 matchID = resMatch.GetInsertID();
				lobby.SetMatchID(matchID);

				return matchID;
			}

			public async static Task CommitPlayerOutcome(MySQLInstance m_Inst, int slotIndex, UInt64 match_id,
				int buildingsBuilt, int buildingsKilled, int buildingsLost,
				int unitsBuilt, int unitsKilled, int unitsLost,
				int totalMoney, bool bWon)
			{
				if (slotIndex < 0)
				{
					return;
				}

				CMySQLResult resMember = await m_Inst.Query(String.Format("UPDATE match_history SET member_slot_{0} = JSON_SET(member_slot_{0}, '$.buildings_built', @buildings_built, '$.buildings_killed', @buildings_killed, '$.buildings_killed', @buildings_killed, '$.units_built', @units_built, '$.units_killed', @units_killed, '$.units_killed', @units_killed, '$.total_money', @total_money, '$.won', @won) WHERE match_id = @match_id;", slotIndex),
					new()
					{
						{ "@match_id", match_id },
						{ "@buildings_built", buildingsBuilt },
						{ "@buildings_killed", buildingsKilled },
						{ "@buildings_lost", buildingsLost },
						{ "@units_built", unitsBuilt },
						{ "@units_killed", unitsKilled },
						{ "@units_lost", unitsLost },
						{ "@total_money",totalMoney },
						{ "@won", bWon }
					}
				);
			}
		}

		// TODO: Cleanup things when a user disconnects, e.g. lobby they're in etc
		public static class Auth
		{
			public async static Task Cleanup(MySQLInstance m_Inst, bool bStartup)
			{
				string strTimeString = "00:05:00";

				if (bStartup)
				{
					strTimeString = "00:00:01";

				}

				// cleanup unused pending logins
				await m_Inst.Query("DELETE FROM `pending_logins` WHERE TIMEDIFF(NOW(), created) >= @time_string;",
					new()
					{
						{ "@time_string", strTimeString }
					}
				);
			}
			public class UserLobbyPreferences
			{
				public int favorite_color = -1;
				public int favorite_side = -1;
				public string favorite_map = String.Empty;
				public int favorite_starting_money = -1;
				public int favorite_limit_superweapons = -1;
			}

			public async static Task<UserLobbyPreferences?> GetUserLobbyPreferences(MySQLInstance m_Inst, Int64 user_id)
			{
				var res = await m_Inst.Query("SELECT favorite_color, favorite_side, favorite_map, favorite_starting_money, favorite_limit_superweapons FROM users WHERE user_id=@user_id;",
					new()
					{
						{ "@user_id", user_id }
					}
				);

				if (res.NumRows() > 0)
				{
					CMySQLRow row = res.GetRow(0);

					UserLobbyPreferences lobbyPrefs = new UserLobbyPreferences();
					lobbyPrefs.favorite_color = Convert.ToInt32(row["favorite_color"]);
					lobbyPrefs.favorite_side = Convert.ToInt32(row["favorite_side"]);
					lobbyPrefs.favorite_map = Convert.ToString(row["favorite_map"]) ?? String.Empty;
					lobbyPrefs.favorite_starting_money = Convert.ToInt32(row["favorite_starting_money"]);
					lobbyPrefs.favorite_limit_superweapons = Convert.ToInt32(row["favorite_limit_superweapons"]);

					return lobbyPrefs;
				}

				return null;
			}

			public async static Task SetFavorite_Color(MySQLInstance m_Inst, Int64 user_id, int favorite_color)
			{
				await m_Inst.Query("UPDATE users SET favorite_color=@favorite_color WHERE user_id=@user_id LIMIT 1;",
					new()
					{
						{ "@favorite_color", favorite_color },
						{ "@user_id", user_id }
					}
				);
			}

			public async static Task SetFavorite_Side(MySQLInstance m_Inst, Int64 user_id, int favorite_side)
			{
				await m_Inst.Query("UPDATE users SET favorite_side=@favorite_side WHERE user_id=@user_id LIMIT 1;",
					new()
					{
						{ "@favorite_side", favorite_side },
						{ "@user_id", user_id }
					}
				);
			}

			public async static Task SetFavorite_Map(MySQLInstance m_Inst, Int64 user_id, string favorite_map)
			{
				await m_Inst.Query("UPDATE users SET favorite_map=@favorite_map WHERE user_id=@user_id LIMIT 1;",
					new()
					{
						{ "@favorite_map", favorite_map },
						{ "@user_id", user_id }
					}
				);
			}

			public async static Task SetFavorite_StartingMoney(MySQLInstance m_Inst, Int64 user_id, int favorite_starting_money)
			{
				await m_Inst.Query("UPDATE users SET favorite_starting_money=@favorite_starting_money WHERE user_id=@user_id LIMIT 1;",
					new()
					{
						{ "@favorite_starting_money", favorite_starting_money },
						{ "@user_id", user_id }
					}
				);
			}

			public async static Task SetFavorite_LimitSuperweapons(MySQLInstance m_Inst, Int64 user_id, bool favorite_limit_superweapons)
			{
				await m_Inst.Query("UPDATE users SET favorite_limit_superweapons=@favorite_limit_superweapons WHERE user_id=@user_id LIMIT 1;",
					new()
					{
						{ "@favorite_limit_superweapons", favorite_limit_superweapons },
						{ "@user_id", user_id }
					}
				);
			}

			public async static Task UpdatePlayerStat(MySQLInstance m_Inst, Int64 user_id, int stat_id, int stat_val)
			{
				await m_Inst.Query("INSERT INTO user_stats_v2 (user_id, stats) VALUES (@user_id, JSON_OBJECT(@stat_key_raw, @stat_val)) ON DUPLICATE KEY UPDATE stats = JSON_SET(stats, @stat_key_formatted, @stat_val);",
					new()
					{
						{ "@user_id", user_id },
						{ "@stat_key_raw", stat_id },
						{ "@stat_key_formatted", String.Format("$.{0}", stat_id) },
						{ "@stat_val", stat_val }
					}
				);
			}

			public async static Task<DailyStats> LoadDailyStats(MySQLInstance m_Inst)
			{
				DailyStats ds = new();
				
                int day_of_year = DateTime.Now.DayOfYear;
                var res = await m_Inst.Query("SELECT stats_structure FROM daily_stats WHERE day_of_year=@day_of_year LIMIT 1;",
                new()
                {
                    { "@day_of_year", day_of_year }
                }
                );

                if (res.NumRows() == 0)
                {
                    return ds;
                }

				try
				{
                    string? jsonData = Convert.ToString(res.GetRow(0)["stats_structure"]);
					if (jsonData != null)
					{
                        DailyStats? statsDeserialized = JsonSerializer.Deserialize<DailyStats>(jsonData);

						if (statsDeserialized != null)
						{
							ds = statsDeserialized;
                        }

						return ds;
                    }
                }
				catch
				{
					return new DailyStats();
				}
                

				return new DailyStats();
            }

			public async static Task StoreDailyStats(MySQLInstance m_Inst, DailyStats stats)
			{
				string strJSON = JsonSerializer.Serialize(stats);

                int day_of_year = DateTime.Now.DayOfYear;
                await m_Inst.Query(String.Format("INSERT INTO daily_stats SET day_of_year=@day_of_year, stats_structure=@stats_structure ON DUPLICATE KEY UPDATE stats_structure=@stats_structure;"),
                    new()
                    {
                    { "@day_of_year", day_of_year },
                    { "@stats_structure", strJSON }
                    }
                );
            }

			public async static Task StoreConnectionOutcome(MySQLInstance m_Inst, EIPVersion protocol, EConnectionState outcome)
			{
				if (outcome != EConnectionState.CONNECTED_DIRECT && outcome != EConnectionState.CONNECTED_RELAY && outcome != EConnectionState.CONNECTION_FAILED) // states we dont track
				{
					return;
				}

				// increment count
				int day_of_year = DateTime.Now.DayOfYear;

				// these are used for creation, so we need to determine 0 1, if already exists, we increment instead
				int create_ipv4_count = protocol == EIPVersion.IPV4 ? 1 : 0;
				int create_ipv6_count = protocol == EIPVersion.IPV6 ? 1 : 0;
				int create_success_count = (outcome == EConnectionState.CONNECTED_DIRECT || outcome == EConnectionState.CONNECTED_RELAY) ? 1 : 0;
				int create_failed_count = (outcome == EConnectionState.CONNECTION_FAILED) ? 1 : 0;

				string onDupeAction = "";

				// what action do we want?
				if (protocol == EIPVersion.IPV4)
				{
					if (onDupeAction.Length > 0)
					{
						onDupeAction += ", ";
					}

					onDupeAction += "ipv4_count=ipv4_count+1";
				}
				else if (protocol == EIPVersion.IPV6)
				{
					if (onDupeAction.Length > 0)
					{
						onDupeAction += ", ";
					}

					onDupeAction += "ipv6_count=ipv6_count+1";
				}

				// 2nd part of action
				if (outcome == EConnectionState.CONNECTED_DIRECT || outcome == EConnectionState.CONNECTED_RELAY)
				{
					if (onDupeAction.Length > 0)
					{
						onDupeAction += ", ";
					}
					onDupeAction += "success_count=success_count+1";
				}
				else if (outcome == EConnectionState.CONNECTION_FAILED)
				{
					if (onDupeAction.Length > 0)
					{
						onDupeAction += ", ";
					}
					onDupeAction += "failed_count=failed_count+1";
				}

				await m_Inst.Query(String.Format("INSERT INTO connection_outcomes SET day_of_year=@day_of_year, ipv4_count=@ipv4_count, ipv6_count=@ipv6_count, success_count=@success_count, failed_count=@failed_count ON DUPLICATE KEY UPDATE {0};", onDupeAction),
					new()
					{
					{ "@day_of_year", day_of_year },
					{ "@ipv4_count", create_ipv4_count },
					{ "@ipv6_count", create_ipv6_count },
					{ "@success_count", create_success_count },
					{ "@failed_count", create_failed_count }
					}
				);

				// TODO_URGENT: Handle year roll over
				await m_Inst.Query("DELETE FROM connection_outcomes WHERE day_of_year<(@day_of_year - 30);",
					new()
					{
						{ "@day_of_year", day_of_year }
					}
				);
			}

			public async static Task SaveELOData(MySQLInstance m_Inst, Int64 user_id, EloData newEloData)
			{
                await m_Inst.Query("UPDATE users SET elo_rating=@elo_rating, elo_num_matches=@elo_num_matches WHERE user_id=@user_id LIMIT 1;",
                    new()
                    {
                        { "@user_id", user_id},
                        { "@elo_rating", newEloData.Rating},
                        { "@elo_num_matches", newEloData.NumMatches}
                    }
                );
            }

			public async static Task UpdateLastLoginData(MySQLInstance m_Inst, Int64 user_id, string ipAddr)
			{
                await m_Inst.Query("UPDATE users SET lastlogin=current_timestamp(), last_ip=@ip_addr WHERE user_id=@user_id LIMIT 1;",
                new()
                {
                    { "@ip_addr", ipAddr },
                    { "@user_id", user_id }
                }
                );
            }

            public async static Task<EloData> GetELOData(MySQLInstance m_Inst, Int64 user_id)
			{
                var res = await m_Inst.Query("SELECT elo_rating, elo_num_matches FROM users WHERE user_id=@user_id LIMIT 1;",
                new()
                {
                    { "@user_id", user_id }
                }
                );

                if (res.NumRows() > 0)
                {
					CMySQLRow row = res.GetRow(0);

                    EloData retData = new(Convert.ToInt32(row["elo_rating"]), Convert.ToInt32(row["elo_num_matches"]));
					return retData;
                }

				return new(EloConfig.BaseRating, 0);
            }

			public async static Task<Dictionary<Int64, EloData>> GetBulkELOData(MySQLInstance m_Inst, List<Int64> user_ids)
			{
				Dictionary<Int64, EloData> results = new();
				
				if (user_ids == null || user_ids.Count == 0)
				{
					return results;
				}

				// Build IN clause with parameters
				string inClause = string.Join(",", user_ids);
				var res = await m_Inst.Query($"SELECT user_id, elo_rating, elo_num_matches FROM users WHERE user_id IN ({inClause});", null);

				foreach (var row in res.GetRows())
				{
					Int64 userId = Convert.ToInt64(row["user_id"]);
					int rating = Convert.ToInt32(row["elo_rating"]);
					int numMatches = Convert.ToInt32(row["elo_num_matches"]);
					results[userId] = new EloData(rating, numMatches);
				}

				// Fill in default values for users not found
				foreach (Int64 userId in user_ids)
				{
					if (!results.ContainsKey(userId))
					{
						results[userId] = new EloData(EloConfig.BaseRating, 0);
					}
				}

				return results;
			}

			public async static Task<PlayerStats> GetPlayerStats(MySQLInstance m_Inst, Int64 user_id)
			{
				// TODO: Return null if user doesnt actually exist, instead of empty stats
				EloData eloData = await GetELOData(m_Inst, user_id);
                PlayerStats ps = new PlayerStats(user_id, eloData.Rating, eloData.NumMatches);

                var res = await m_Inst.Query("SELECT stats FROM user_stats_v2 WHERE user_id=@user_id LIMIT 1;",
				new()
				{
					{ "@user_id", user_id }
				}
				);

				if (res.NumRows() == 0)
				{
					return ps;
				}

				string? jsonData = Convert.ToString(res.GetRow(0)["stats"]);
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8604 // Converting null literal or possible null value to non-nullable type.
				Dictionary<int, int> dictStats = JsonSerializer.Deserialize<Dictionary<int, int>>(jsonData);
#pragma warning restore CS8604 // Converting null literal or possible null value to non-nullable type.
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.

				//foreach (var row in res.GetRows())
#pragma warning disable CS8602 // Dereference of a possibly null reference.
				foreach (var statPair in dictStats)
				{
					EStatIndex stat_id = (EStatIndex)Convert.ToUInt16(statPair.Key);
					int stat_value = statPair.Value;

					ps.ProcessFromDB(stat_id, stat_value);
				}
#pragma warning restore CS8602 // Dereference of a possibly null reference.

				return ps;
			}

			public static async Task FullyDestroyPlayerSession(MySQLInstance m_Inst, Int64 user_id, UserSession? userData, bool bMigrateLobbyIfPresent)
			{
				// NOTE: Dont assume userData is valid, use user_id for user id
				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine("FullyDestroyPlayerSession for user {0}", user_id);
				Console.ForegroundColor = ConsoleColor.Gray;

				// invalidate any TURN credentials
				TURNCredentialManager.DeleteCredentialsForUser(user_id);

				// TODO: Implement single point of presence? gets dicey if multiple logins
				// TODO: Dont destroy this, just mark inactive/offline, we use this as a saved credential system

				// session tied to this token (keep other ones attached to user_id, could be other machines)
				// TODO_JWT: Remove table fully + set logged out
				//await m_Inst.Query("DELETE FROM sessions WHERE user_id={0} AND session_type={1};", user_id, (int)ESessionType.Game);

				// leave any lobby
				Console.WriteLine("[Source 2] User {0} Leave Any Lobby", user_id);
				LobbyManager.LeaveAnyLobby(user_id);


				await LobbyManager.CleanupUserLobbiesNotStarted(user_id);

				// remove from any matchmaking
				if (userData != null)
				{
					MatchmakingManager.DeregisterPlayer(userData);
				}

				// TODO: Client needs to handle this... itll start returning 404
			}

			public async static Task<Int64> GetUserIDFromPendingLogin(MySQLInstance m_Inst, string gameCode)
			{
				gameCode = gameCode.ToUpper();

				CMySQLResult res = await m_Inst.Query("SELECT user_id FROM pending_logins WHERE code=@game_code LIMIT 1;",
					new()
					{
						{ "@game_code", gameCode}
					}
				);

				if (res.NumRows() > 0)
				{
					CMySQLRow row = res.GetRow(0);
					Int64 user_id = Convert.ToInt64(row["user_id"]);
					return user_id;
				}

				return -1;
			}

			// TODO: How do we stop dev clients connecting to PROD?
			// TODO: Check more here, like IP, client, etc
			
			public async static Task SetUsedLoggedIn(MySQLInstance m_Inst, Int64 userID, string clientIDStr)
			{
				UInt16 clientID = clientIDStr == "gen_online_60hz" ? (UInt16)1 : (UInt16)0;

				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine("StartSession deleing other sessions for user {0}", userID);
				Console.ForegroundColor = ConsoleColor.Gray;

				// kill any WS they had too, StartSession comes before WS connects
				// disconnect any other sessions with this ID
				UserSession? sess = GenOnlineService.WebSocketManager.GetDataFromUser(userID);
				if (sess != null)
				{
					Console.ForegroundColor = ConsoleColor.Cyan;
					Console.WriteLine("Found duplicate session for user {0}", userID);
					Console.ForegroundColor = ConsoleColor.Gray;

					UserWebSocketInstance? oldWS = GenOnlineService.WebSocketManager.GetWebSocketForSession(sess);
					await GenOnlineService.WebSocketManager.DeleteSession(userID, oldWS, false);
				}
			}


			private static string GenerateSessionToken()
			{
				const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
				StringBuilder sb = new StringBuilder(32);
				Random random = new Random();

				for (int i = 0; i < 32; i++)
				{
					sb.Append(chars[random.Next(chars.Length)]);
				}

				return sb.ToString();
			}

			public static async void CleanupPendingLogin(MySQLInstance m_Inst, string strGameCode)
			{
				try
				{
					strGameCode = strGameCode.ToUpper();

					await m_Inst.Query("DELETE FROM pending_logins WHERE code=@game_code LIMIT 1;",
						new()
						{
							{ "@game_code", strGameCode}
						}
					);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Error in CleanupPendingLogin: {ex.Message}");
				}
			}


			public async static Task<EAccountType> GetAccountType(MySQLInstance m_Inst, Int64 userID)
			{
				var res = await m_Inst.Query("SELECT account_type FROM users WHERE user_id=@user_id LIMIT 1;",
					new()
					{
						{ "@user_id", userID}
					}
				);

				if (res != null && res.NumRows() > 0)
				{
					var row = res.GetRow(0);

					EAccountType account_type = (EAccountType)Convert.ToInt32(row["account_type"]);
					return account_type;
				}

				return EAccountType.Unknown;
			}

			public async static Task<bool> IsUserAdmin(MySQLInstance m_Inst, Int64 userID)
			{
				var res = await m_Inst.Query("SELECT admin FROM users WHERE user_id=@user_id LIMIT 1;",
					new()
					{
						{ "@user_id", userID}
					}
				);

				if (res != null && res.NumRows() > 0)
				{
					var row = res.GetRow(0);

					return Convert.ToBoolean(row["admin"]);
				}

				return false;
			}

			public async static Task<bool> IsUserBanned(MySQLInstance m_Inst, Int64 userID)
			{
				var res = await m_Inst.Query("SELECT banned FROM users WHERE user_id=@user_id LIMIT 1;",
					new()
					{
						{ "@user_id", userID}
					}
				);

				if (res != null && res.NumRows() > 0)
				{
					var row = res.GetRow(0);

					return Convert.ToBoolean(row["banned"]);
				}

				return false;
			}

			public async static Task RegisterUserDevice(MySQLInstance m_Inst, Int64 userID, string hwid_0, string hwid_1, string hwid_2, string ipAddr)
			{
				// raw version
				string hwid_3 = hwid_0.ToUpper();
				string hwid_4 = hwid_1.ToUpper();
				string hwid_5 = hwid_2.ToUpper();

				// hash everything
				hwid_0 = Helpers.ComputeMD5Hash(hwid_0).ToUpper();
				hwid_1 = Helpers.ComputeMD5Hash(hwid_1).ToUpper();
				hwid_2 = Helpers.ComputeMD5Hash(hwid_2).ToUpper();

				var res = await m_Inst.Query("INSERT IGNORE INTO user_devices(user_id, hwid_0, hwid_1, hwid_2, hwid_3, hwid_4, hwid_5, ip_addr) VALUES (@user_id, @hwid_0, @hwid_1, @hwid_2, @hwid_3, @hwid_4, @hwid_5, @ip_addr);",
				new()
				{
					{ "@user_id", userID },
					{ "@hwid_0", hwid_0 },
					{ "@hwid_1", hwid_1 },
					{ "@hwid_2", hwid_2 },
					{ "@hwid_3", hwid_3 },
					{ "@hwid_4", hwid_4 },
					{ "@hwid_5", hwid_5 },
					{ "@ip_addr", ipAddr }
				}
				);
			}

			public async static Task<string> GetDisplayName(MySQLInstance m_Inst, Int64 userID)
			{
				var res = await m_Inst.Query("SELECT displayname FROM users WHERE user_id=@user_id LIMIT 1;",
					new()
					{
						{ "@user_id", userID}
					}
				);

				if (res != null && res.NumRows() > 0)
				{
					var row = res.GetRow(0);

					string? displayname = Convert.ToString(row["displayname"]);
					return displayname ?? String.Empty;
				}

				return String.Empty;
			}

			public async static Task<HashSet<Int64>> GetFriends(MySQLInstance m_Inst, Int64 user_id)
			{
				HashSet<Int64> setFriends = new();

				var res = await m_Inst.Query("SELECT user_id_1, user_id_2 FROM friends WHERE user_id_1=@user_id OR user_id_2=@user_id;",
				new()
				{
					{ "@user_id", user_id }
				}
				);

				foreach (var row in res.GetRows())
				{
					Int64 user_id_1 = Convert.ToInt64(row["user_id_1"]);
					Int64 user_id_2 = Convert.ToInt64(row["user_id_2"]);

					if (user_id_1 == user_id)
					{
						setFriends.Add(user_id_2);
					}
					else
					{
						setFriends.Add(user_id_1);
					}

				}

				return setFriends;
			}

			public async static Task<HashSet<Int64>> GetBlocked(MySQLInstance m_Inst, Int64 source_user_id)
			{
				HashSet<Int64> setBlocked = new();

				var res = await m_Inst.Query("SELECT target_user_id FROM friends_blocked WHERE source_user_id=@source_user_id;",
				new()
				{
					{ "@source_user_id", source_user_id }
				}
				);

				foreach (var row in res.GetRows())
				{
					Int64 blocked_user_id = Convert.ToInt64(row["target_user_id"]);
					setBlocked.Add(blocked_user_id);
				}

				return setBlocked;
			}

			public async static Task<HashSet<Int64>> GetPendingFriendsRequests(MySQLInstance m_Inst, Int64 target_user_id)
			{
				HashSet<Int64> setRequests = new();

				var res = await m_Inst.Query("SELECT source_user_id FROM friends_requests WHERE target_user_id=@target_user_id;",
				new()
				{
					{ "@target_user_id", target_user_id }
				}
				);

				foreach (var row in res.GetRows())
				{
					Int64 source_user_id = Convert.ToInt64(row["source_user_id"]);
					setRequests.Add(source_user_id);
				}

				return setRequests;
			}

			public async static Task RemovePendingFriendRequest(MySQLInstance m_Inst, Int64 source_user_id, Int64 target_user_id)
			{
				// delete in either direction
				var res = await m_Inst.Query("DELETE FROM friends_requests WHERE (source_user_id=@source_user_id AND target_user_id=@target_user_id) OR (source_user_id=@target_user_id AND target_user_id=@source_user_id) LIMIT 1;",
				new()
				{
					{ "@source_user_id", source_user_id },
					{ "@target_user_id", target_user_id }
				}
				);
			}

			public async static Task CreateFriendship(MySQLInstance m_Inst, Int64 source_user_id, Int64 target_user_id)
			{
				var res = await m_Inst.Query("INSERT INTO friends(user_id_1, user_id_2) VALUES (@source_user_id, @target_user_id);",
				new()
				{
					{ "@source_user_id", source_user_id },
					{ "@target_user_id", target_user_id }
				}
				);
			}

			public async static Task RemoveFriendship(MySQLInstance m_Inst, Int64 source_user_id, Int64 target_user_id)
			{
				var res = await m_Inst.Query("DELETE FROM friends WHERE (user_id_1=@source_user_id AND user_id_2=@target_user_id) OR  (user_id_1=@target_user_id AND user_id_2=@source_user_id ) LIMIT 1;",
				new()
				{
					{ "@source_user_id", source_user_id },
					{ "@target_user_id", target_user_id }
				}
				);
			}

			public async static Task AddBlock(MySQLInstance m_Inst, Int64 source_user_id, Int64 target_user_id)
			{
				var res = await m_Inst.Query("INSERT INTO friends_blocked(source_user_id, target_user_id) VALUES (@source_user_id, @target_user_id);",
				new()
				{
					{ "@source_user_id", source_user_id },
					{ "@target_user_id", target_user_id }
				}
				);
			}

			public async static Task RemoveBlock(MySQLInstance m_Inst, Int64 source_user_id, Int64 target_user_id)
			{
				var res = await m_Inst.Query("DELETE FROM friends_blocked WHERE source_user_id=@source_user_id AND target_user_id=@target_user_id LIMIT 1;",
				new()
				{
					{ "@source_user_id", source_user_id },
					{ "@target_user_id", target_user_id }
				}
				);
			}


			public async static Task AddPendingFriendRequest(MySQLInstance m_Inst, Int64 source_user_id, Int64 target_user_id)
			{
				var res = await m_Inst.Query("INSERT INTO friends_requests(source_user_id, target_user_id) VALUES (@source_user_id, @target_user_id);",
				new()
				{
					{ "@source_user_id", source_user_id },
					{ "@target_user_id", target_user_id }
				}
				);
			}

			public async static Task<Dictionary<Int64, string>> GetDisplayNameBulk(MySQLInstance m_Inst, List<Int64> lstUserIDs)
			{
				Dictionary<Int64, string> dictResult = new();

				// Build parameter placeholders
				var parameters = new List<string>();
				Dictionary<string, object> dictParams = new();

				for (int i = 0; i < lstUserIDs.Count; i++)
				{
					// for query string
					parameters.Add($"@id{i}");

					// actual param
					dictParams.Add($"@id{i}", lstUserIDs[i]);
				}

				var res = await m_Inst.Query($"SELECT user_id, displayname FROM users WHERE user_id IN ({string.Join(",", parameters)})",
					dictParams
				);

				foreach (var row in res.GetRows())
				{
					Int64 user_id = Convert.ToInt64(row["user_id"]);
					string? displayname = Convert.ToString(row["displayname"]);

					if (displayname != null)
					{
						try
						{
							dictResult.Add(user_id, displayname);
						}
						catch // probably duplicate
						{

						}
					}
				}

				return dictResult;
			}

			public enum EAccountType
			{
				Unknown = -1,
				Steam = 0,
				Discord = 1,
				Ghost = 2,
				DevAccount = 3
			}

			public enum ESessionType
			{
				Unknown = -1,
				Website = 0,
				Game = 1
			}

			internal static async Task CreateUserIfNotExists_DevAccount(MySQLInstance m_Inst, Int64 user_id, string display_name)
			{
				var res = await m_Inst.Query("SELECT user_id FROM users WHERE user_id=@user_id LIMIT 1;",
					new()
					{
						{ "@user_id", user_id}
					}
				);

				if (res == null || res.NumRows() == 0) // doesnt exist, create it
				{
					await m_Inst.Query("INSERT INTO users(user_id, account_type, displayname) VALUES (@user_id, @account_type, @displayname);",
						new()
						{
							{ "@user_id", user_id},
							{ "@account_type", EAccountType.DevAccount},
							{ "@displayname", display_name},
						}
					);
				}
			}

            internal static async Task SetUserPortMappingTech(MySQLInstance m_Inst, Int64 user_id, EMappingTech mappingTech, bool bIPV4, bool bIPV6)
			{
				await m_Inst.Query("UPDATE users SET portmapping_tech=@mappingTech, ipv4=@ipv4, ipv6=@ipv6 WHERE user_id=@user_id LIMIT 1;",
					new()
					{
						{ "@user_id", user_id},
						{ "@mapping_tech", mappingTech},
						{ "@ipv4", bIPV4},
						{ "@ipv6", bIPV6}
					}
				);
			}

			// Cache for display names (24-hour TTL - names rarely change)
			public static class DisplayNameCache
			{
				private static readonly System.Collections.Concurrent.ConcurrentDictionary<Int64, (string DisplayName, DateTime CachedAt)> s_cache = new();
				private static readonly TimeSpan s_cacheDuration = TimeSpan.FromHours(24);

				public static async Task<string> GetCachedDisplayName(MySQLInstance m_Inst, Int64 userID)
				{
					if (s_cache.TryGetValue(userID, out var cached))
					{
						if (DateTime.UtcNow - cached.CachedAt < s_cacheDuration)
						{
							return cached.DisplayName;
						}
						s_cache.TryRemove(userID, out _);
					}

					string displayName = await GetDisplayName(m_Inst, userID);
					s_cache.TryAdd(userID, (displayName, DateTime.UtcNow));
					return displayName;
				}

				public static async Task<Dictionary<Int64, string>> GetCachedDisplayNameBulk(MySQLInstance m_Inst, List<Int64> lstUserIDs)
				{
					Dictionary<Int64, string> result = new();
					List<Int64> uncachedIDs = new();

					foreach (Int64 userID in lstUserIDs)
					{
						if (s_cache.TryGetValue(userID, out var cached) && DateTime.UtcNow - cached.CachedAt < s_cacheDuration)
						{
							result[userID] = cached.DisplayName;
						}
						else
						{
							s_cache.TryRemove(userID, out _);
							uncachedIDs.Add(userID);
						}
					}

					if (uncachedIDs.Count > 0)
					{
						Dictionary<Int64, string> dbResults = await GetDisplayNameBulk(m_Inst, uncachedIDs);
						foreach (var kvp in dbResults)
						{
							s_cache.TryAdd(kvp.Key, (kvp.Value, DateTime.UtcNow));
							result[kvp.Key] = kvp.Value;
						}
					}

					return result;
				}

				public static void InvalidateCache(Int64 userID)
				{
					s_cache.TryRemove(userID, out _);
				}
			}

			// Cache for user lobby preferences (1-hour TTL)
			public static class UserPreferencesCache
			{
				private static readonly System.Collections.Concurrent.ConcurrentDictionary<Int64, (UserLobbyPreferences Prefs, DateTime CachedAt)> s_cache = new();
				private static readonly TimeSpan s_cacheDuration = TimeSpan.FromHours(1);

				public static async Task<UserLobbyPreferences> GetCachedPreferences(MySQLInstance m_Inst, Int64 userID)
				{
					if (s_cache.TryGetValue(userID, out var cached))
					{
						if (DateTime.UtcNow - cached.CachedAt < s_cacheDuration)
						{
							return cached.Prefs;
						}
						s_cache.TryRemove(userID, out _);
					}

					UserLobbyPreferences prefs = await GetUserLobbyPreferences(m_Inst, userID);
					s_cache.TryAdd(userID, (prefs, DateTime.UtcNow));
					return prefs;
				}

				public static void InvalidateCache(Int64 userID)
				{
					s_cache.TryRemove(userID, out _);
				}
			}
		}
	}

	// Updated MySQLInstance class to fix memory leaks by ensuring proper disposal of resources.
	public class MySQLInstance : IDisposable
	{
		//private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

		// Cached database configuration to avoid parsing config on every query
		private static class CachedDbConfig
		{
			public static string? Host { get; set; }
			public static string? Name { get; set; }
			public static string? Username { get; set; }
			public static string? Password { get; set; }
			public static ushort Port { get; set; }
			public static int MinPoolSize { get; set; } = 50;
			public static int MaxPoolSize { get; set; } = 500;
			public static bool UsePooling { get; set; } = true;
			public static bool ConnReset { get; set; } = true;
			public static int ConnectTimeout { get; set; } = 10;
			public static int CommandTimeout { get; set; } = 10;
			public static bool IsInitialized { get; set; } = false;

			public static void Initialize(IConfiguration dbSettings)
			{
				if (!IsInitialized)
				{
					Host = dbSettings.GetValue<string>("db_host");
					Name = dbSettings.GetValue<string>("db_name");
					Username = dbSettings.GetValue<string>("db_username");
					Password = dbSettings.GetValue<string>("db_password");
					Port = dbSettings.GetValue<ushort>("db_port");
					MinPoolSize = dbSettings.GetValue<int?>("db_min_poolsize") ?? 50;
					MaxPoolSize = dbSettings.GetValue<int?>("db_max_poolsize") ?? 500;
					UsePooling = dbSettings.GetValue<bool?>("db_use_pooling") ?? true;
					ConnReset = dbSettings.GetValue<bool?>("db_conn_reset") ?? true;
					ConnectTimeout = dbSettings.GetValue<int?>("db_connect_timeout") ?? 10;
					CommandTimeout = dbSettings.GetValue<int?>("db_command_timeout") ?? 10;
					IsInitialized = true;
				}
			}
		}

#if !USE_PER_QUERY_CONNECTION
        private MySqlConnection m_Connection = null;
#endif

		public MySQLInstance()
		{

		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
			{
#if !USE_PER_QUERY_CONNECTION
                if (m_Connection != null)
                {
                    m_Connection.Dispose();
                    m_Connection = null;
                }
#endif
				//_semaphore.Dispose();
			}
		}

		private DateTime m_LastQueryTime = DateTime.Now;

		public async void KeepAlive()
		{
			try
			{
				double timeSinceLastQueryAuth = (DateTime.Now - m_LastQueryTime).TotalMilliseconds;
				if (timeSinceLastQueryAuth > 300000)
				{
					await Query("SELECT user_id FROM users LIMIT 1;", null).ConfigureAwait(false);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error in KeepAlive: {ex.Message}");
			}
		}

		public async static Task TestQuery(MySQLInstance m_Inst)
		{
			await m_Inst.Query("SELECT * FROM users LIMIT 1", null);
		}

		public async Task<bool> Initialize(bool bIsStartup = true)
		{
			if (Program.g_Config == null)
			{
				throw new Exception("Config is null. Check config file exists.");
			}

			IConfiguration? dbSettings = Program.g_Config.GetSection("Database");

			if (dbSettings == null)
			{
				throw new Exception("Database section in config is null / not set in config");
			}

			string? hostname = dbSettings.GetValue<string>("db_host");
			string? dbname = dbSettings.GetValue<string>("db_name");
			string? username = dbSettings.GetValue<string>("db_username");
			string? password = dbSettings.GetValue<string>("db_password");
			UInt16? port = dbSettings.GetValue<UInt16>("db_port");

			if (hostname == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}

			if (dbname == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}

			if (username == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}

			if (password == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}

			if (port == null)
			{
				throw new Exception("DB Hostname is null / not set in config");
			}


			if (!Directory.Exists("Exceptions"))
			{
				Directory.CreateDirectory("Exceptions");
			}

			try
			{
				Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

#if !USE_PER_QUERY_CONNECTION
				//m_Connection = new MySqlConnection(String.Format("Server={0}; database={1}; user={2}; password={3}; port={4};Pooling=true;Connect Timeout=10;MinimumPoolSize=1;maximumpoolsize=100;AllowUserVariables=true;ConnectionReset=false;SslMode=Required;", dbSettings));
				m_Connection = new MySqlConnection(String.Format("Server={0}; database={1}; user={2}; password={3}; port={4};Pooling=true;Connect Timeout=10;MinimumPoolSize=1;maximumpoolsize=100;AllowUserVariables=true;ConnectionReset=false;", hostname, dbname, username, password, port));

				//Console.WriteLine(String.Format("Server={0}; database={1}; user={2}; password={3}; port={4};Pooling=true;Connect Timeout=100;MinimumPoolSize=1;maximumpoolsize=100;AllowUserVariables=true;ConnectionReset=false;SslMode=Required;", dbSettings));

				Console.WriteLine("Connecting to DB...");
				m_Connection.Open();

				Console.WriteLine("Connected to: " + m_Connection.ServerVersion);


				Console.WriteLine("MySQL Initialized");

				var t = Database.Functions.Lobby.GetAllLobbyInfo(this, 0, true, true, true, true, true);

				List<LobbyData> lstLobbies = await t;
#endif

				return true;
			}
			catch (MySqlException ex)
			{
				Console.WriteLine(ex.ToString());
				HandleMySqlException(ex, bIsStartup);
				return false;
			}
			catch (InvalidOperationException ex)
			{
				Console.WriteLine(ex.ToString());
				Console.WriteLine("MySQL Connection Failed. Potentially Malformed Connection String.");
				if (bIsStartup)
				{
					Console.WriteLine("\tPress any key to exit");
					Console.Read();
					Environment.Exit(1);
				}
				return false;
			}
			catch (Exception e)
			{
				Console.WriteLine(e.ToString());
				Console.WriteLine("\tPress any key to exit");
				return false;
			}
		}

		private void HandleMySqlException(MySqlException ex, bool bIsStartup)
		{
			File.WriteAllText(Path.Combine("Exceptions", "MYSQL_1_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".txt"), ex.ToString());

			switch (ex.Number)
			{
				case 0:
					Console.WriteLine("MySQL Connection Failed. Cannot Connect to Server.");
					break;
				case 1:
					Console.WriteLine("MySQL Connection Failed. Invalid username/password.");
					break;
				case 1042:
					Console.WriteLine("MySQL Connection Failed. Connection Timed Out.");
					break;
			}

			if (bIsStartup)
			{
				Console.WriteLine("\tFATAL ERROR, Press any key to exit");
				Console.Read();
				Environment.Exit(1);
			}
		}

		private string EscapeAllAndFormatQuery(string strQuery, params object[] formatParams)
		{
			for (int i = 0; i < formatParams.Length; ++i)
			{
				if (formatParams[i].GetType() == typeof(string))
				{
					formatParams[i] = MySqlHelper.EscapeString((string)formatParams[i]);
				}
				else if (formatParams[i].GetType().IsEnum)
				{
					formatParams[i] = (int)formatParams[i];
				}
			}

			return String.Format(strQuery, formatParams);
		}

		public async Task<CMySQLResult> Query(string commandStr, Dictionary<string, object>? dictCommandValues, int attempt = 0)
		{
			bool semaphoreAcquired = false;
			CMySQLResult result = new CMySQLResult(0); // default with 0 rows
			MySqlConnection? connection = null;

			// after 3 attempts, give up
			if (attempt >= 3)
			{
				return result;
			}

			try
			{
				//await _semaphore.WaitAsync();
				semaphoreAcquired = true;
				m_LastQueryTime = DateTime.Now;

#if !USE_PER_QUERY_CONNECTION
                connection = m_Connection;
#else
				if (Program.g_Config == null)
				{
					throw new Exception("Config is null. Check config file exists.");
				}

				// Initialize cached config if needed
				if (!CachedDbConfig.IsInitialized)
				{
					IConfiguration? dbSettings = Program.g_Config.GetSection("Database");
					if (dbSettings == null)
					{
						throw new Exception("Database section in config is null / not set in config");
					}
					CachedDbConfig.Initialize(dbSettings);
				}

				// Use cached config values
				string? db_host = CachedDbConfig.Host;
				string? db_name = CachedDbConfig.Name;
				string? db_username = CachedDbConfig.Username;
				string? db_password = CachedDbConfig.Password;
				ushort db_port = CachedDbConfig.Port;
				int db_min_poolsize = CachedDbConfig.MinPoolSize;
				int db_max_poolsize = CachedDbConfig.MaxPoolSize;
				bool db_use_pooling = CachedDbConfig.UsePooling;
				bool db_conn_reset = CachedDbConfig.ConnReset;
				int db_connect_timeout = CachedDbConfig.ConnectTimeout;
				int db_command_timeout = CachedDbConfig.CommandTimeout;

				if (db_host == null)
				{
					throw new Exception("DB Hostname is null / not set in config");
				}

				if (db_name == null)
				{
					throw new Exception("DB Name is null / not set in config");
				}

				if (db_username == null)
				{
					throw new Exception("DB Username is null / not set in config");
				}

				if (db_password == null)
				{
					throw new Exception("DB Password is null / not set in config");
				}

				
				
#endif
				using (connection = new MySqlConnection(String.Format("Server={0}; database={1}; user={2}; password={3}; port={4};Pooling={5};DefaultCommandTimeout={9};Connect Timeout={10};MinimumPoolSize={6};maximumpoolsize={7};AllowUserVariables=true;ConnectionReset={8};",
					db_host, db_name, db_username, db_password, db_port, db_use_pooling, db_min_poolsize, db_max_poolsize, db_conn_reset, db_command_timeout, db_connect_timeout)))
				{
					connection.Open();

					try
					{
						using (var command = new MySqlCommand(commandStr, connection))
						{
							if (dictCommandValues != null)
							{
								foreach (var kvPair in dictCommandValues)
								{
									command.Parameters.AddWithValue(kvPair.Key, kvPair.Value);
								}
							}

							if (commandStr.ToUpper().StartsWith("DELETE") || commandStr.ToUpper().StartsWith("UPDATE"))
							{
								int numRowsModified = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
								result = new CMySQLResult(numRowsModified);
							}
							else
							{
								using (System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
								{
									result = new CMySQLResult(reader, (ulong)command.LastInsertedId);
								}
							}
						}
					}
					catch (InvalidOperationException e)
					{
						Console.WriteLine("MySQL Query Error: {0}", e.InnerException == null ? e.Message : e.InnerException.ToString());
						Console.WriteLine("MySQL is attempting to reconnect");

						string strExceptionMsg = String.Empty;
						if (e.InnerException != null)
						{
							strExceptionMsg = e.InnerException.ToString();
						}
						else
						{
							strExceptionMsg = e.Message;
						}

						File.WriteAllText(Path.Combine("Exceptions", "MYSQL_2_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".txt"), "MySQL Query Error:" + strExceptionMsg);

						Initialize(false);
						// Ensure semaphore is released before recursive call
						if (semaphoreAcquired)
						{
							//_semaphore.Release();
							semaphoreAcquired = false;
						}
						return await Query(commandStr, dictCommandValues, attempt + 1).ConfigureAwait(false);
					}
					catch (MySqlException ex)
					{
						Console.WriteLine(ex.ToString());
						HandleMySqlException(ex, false);
					}
					catch (Exception e)
					{
						string strErrorMsg = String.Format("MySQL Query Error: {0}", e.Message);
						Console.WriteLine(strErrorMsg);
						File.WriteAllText(Path.Combine("Exceptions", "MYSQL_3_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".txt"), strErrorMsg);

						// Ensure semaphore is released before throwing
						if (semaphoreAcquired)
						{
							//_semaphore.Release();
							semaphoreAcquired = false;
						}

						if (System.Diagnostics.Debugger.IsAttached)
						{
							throw;
						}
					}
				}
			}
			catch (Exception e)
			{
				string strErrorMsg = String.Format("MySQL Query Error: {0}", e.Message);
					Console.WriteLine(strErrorMsg);
					File.WriteAllText(Path.Combine("Exceptions", "MYSQL_4_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".txt"), strErrorMsg);
			}
			finally
			{
				if (semaphoreAcquired)
				{
					//_semaphore.Release();
				}
#if USE_PER_QUERY_CONNECTION
				if (connection != null)
				{
					connection.Close();
					connection.Dispose();
				}
#endif
			}

			return result;
		}
	}

	public class CMySQLResult
	{
		public CMySQLResult(int rowsAffected)
		{
			m_RowsAffected = rowsAffected;
		}

		public CMySQLResult(System.Data.Common.DbDataReader dbReader, ulong InsertID)
		{
			try
			{
				while (dbReader.Read())
				{
					CMySQLRow thisRow = new CMySQLRow();
					for (int i = 0; i < dbReader.FieldCount; i++)
					{
						object? value = !dbReader.IsDBNull(i) ? dbReader.GetValue(i) : null;
						string fieldName = dbReader.GetName(i);
						thisRow[fieldName] = value;
					}
					m_Rows.Add(thisRow);
				}
			}
			finally
			{
				dbReader.Close();
				dbReader.Dispose(); // Ensure proper disposal
			}

			m_InsertID = InsertID;
			m_RowsAffected = 0;
		}

		public List<CMySQLRow> GetRows()
		{
			return m_Rows;
		}

		public CMySQLRow GetRow(int a_Index)
		{
			return m_Rows[a_Index];
		}

		public int NumRows()
		{
			return m_Rows.Count;
		}

		public ulong GetInsertID()
		{
			return m_InsertID;
		}

		public int GetNumRowsAffected()
		{
			return m_RowsAffected;
		}

		private List<CMySQLRow> m_Rows = new List<CMySQLRow>();
		private readonly ulong m_InsertID = 0;
		private readonly int m_RowsAffected = 0;
	}
}
