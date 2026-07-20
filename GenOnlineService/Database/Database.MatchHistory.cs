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

using GenOnlineService;
using GenOnlineService.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Query;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

public class MatchHistoryEntry
{
	public long MatchId { get; set; }
	public long Owner { get; set; }
	public string Name { get; set; } = string.Empty;
	public bool Finished { get; set; }
	public DateTime Started { get; set; }
	public DateTime TimeFinished { get; set; }
	public string MapName { get; set; } = string.Empty;
	public bool MapOfficial { get; set; }
	public string MatchRosterType { get; set; } = string.Empty;
	public bool VanillaTeams { get; set; }
	public uint StartingCash { get; set; }
	public bool LimitSuperweapons { get; set; }
	public bool TrackStats { get; set; }
	public bool AllowObservers { get; set; }
	public ushort MaxCamHeight { get; set; }
	public string? MapPath { get; set; }

	// JSON slots
	public string? MemberSlot0 { get; set; }
	public string? MemberSlot1 { get; set; }
	public string? MemberSlot2 { get; set; }
	public string? MemberSlot3 { get; set; }
	public string? MemberSlot4 { get; set; }
	public string? MemberSlot5 { get; set; }
	public string? MemberSlot6 { get; set; }
	public string? MemberSlot7 { get; set; }
}

public class MatchHistoryConfiguration : IEntityTypeConfiguration<MatchHistoryEntry>
{
	public void Configure(EntityTypeBuilder<MatchHistoryEntry> entity)
	{
		entity.ToTable("match_history");

		entity.HasKey(e => e.MatchId);

		entity.Property(e => e.MatchId)
			.HasColumnName("match_id")
			.ValueGeneratedOnAdd();

		entity.Property(e => e.Owner)
			.HasColumnName("owner");

		entity.Property(e => e.Name)
			.HasColumnName("name")
			.HasMaxLength(64)
			.IsRequired();

		entity.Property(e => e.Finished)
			.HasColumnName("finished");

		entity.Property(e => e.Started)
			.HasColumnName("started")
			.HasColumnType("datetime")
			.HasDefaultValueSql("current_timestamp()");

		entity.Property(e => e.TimeFinished)
			.HasColumnName("time_finished")
			.HasColumnType("datetime")
			.HasDefaultValueSql("current_timestamp()");

		entity.Property(e => e.MapName)
			.HasColumnName("map_name")
			.HasMaxLength(128)
			.IsRequired();

		entity.Property(e => e.MapOfficial)
			.HasColumnName("map_official");

		entity.Property(e => e.MatchRosterType)
			.HasColumnName("match_roster_type")
			.HasMaxLength(32)
			.HasDefaultValue("");

		entity.Property(e => e.VanillaTeams)
			.HasColumnName("vanilla_teams");

		entity.Property(e => e.StartingCash)
			.HasColumnName("starting_cash")
			.HasColumnType("int unsigned");

		entity.Property(e => e.LimitSuperweapons)
			.HasColumnName("limit_superweapons");

		entity.Property(e => e.TrackStats)
			.HasColumnName("track_stats");

		entity.Property(e => e.AllowObservers)
			.HasColumnName("allow_observers");

		entity.Property(e => e.MaxCamHeight)
			.HasColumnName("max_cam_height")
			.HasColumnType("smallint unsigned");

		entity.Property(e => e.MapPath)
			.HasColumnName("map_path")
			.HasMaxLength(128);

		// JSON columns
		for (int i = 0; i < 8; i++)
		{
			entity.Property<string?>($"MemberSlot{i}")
				.HasColumnName($"member_slot_{i}")
				.HasColumnType("longtext")
				.HasCharSet("utf8mb4")
				.HasCollation("utf8mb4_bin");
		}
	}
}

// TODO_EFCORE: put everything in below namespace
namespace GenOnlineService
{

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

	public struct MemberMetadataModel
	{
		public string file_name { get; set; }
		public EMetadataFileType file_type { get; set; }
	}

	// Handles JSON booleans stored as integers (0/1) from legacy MySQL tinyint serialization.
	public sealed class BoolFromIntConverter : JsonConverter<bool>
	{
		public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType == JsonTokenType.Number)
				return reader.GetInt32() != 0;
			return reader.GetBoolean();
		}

		public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
			=> writer.WriteBooleanValue(value);
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

		[JsonConverter(typeof(BoolFromIntConverter))]
		public bool won { get; set; } = false;                // tinyint(4) DEFAULT NULL
		public List<MemberMetadataModel> metadata { get; set; } = new List<MemberMetadataModel>();

		public MatchdataMemberModel()
		{
		}
	}
}

namespace Database
{
	// TODO_EFCORE: Consider moving to zero-serialization model
	public static class MatchHistory
	{
		private static readonly Expression<Func<MatchHistoryEntry, string?>>[] _slotSelectors =
	{
			m => m.MemberSlot0,
			m => m.MemberSlot1,
			m => m.MemberSlot2,
			m => m.MemberSlot3,
			m => m.MemberSlot4,
			m => m.MemberSlot5,
			m => m.MemberSlot6,
			m => m.MemberSlot7
		};

		private static readonly Func<AppDbContext, long, Task<string?[]>> _getAllMemberSlots =
	EF.CompileAsyncQuery(
		(AppDbContext db, long matchId) =>
			db.MatchHistory
			  .Where(m => m.MatchId == matchId)
			  .Select(m => new string?[]
			  {
				  m.MemberSlot0,
				  m.MemberSlot1,
				  m.MemberSlot2,
				  m.MemberSlot3,
				  m.MemberSlot4,
				  m.MemberSlot5,
				  m.MemberSlot6,
				  m.MemberSlot7
			  })
			  .FirstOrDefault()
	);


		private static Expression<Func<SetPropertyCalls<MatchHistoryEntry>, SetPropertyCalls<MatchHistoryEntry>>>
	BuildSetter(int slotIndex, string? json)
		{
			return slotIndex switch
			{
				0 => s => s.SetProperty(m => m.MemberSlot0, json),
				1 => s => s.SetProperty(m => m.MemberSlot1, json),
				2 => s => s.SetProperty(m => m.MemberSlot2, json),
				3 => s => s.SetProperty(m => m.MemberSlot3, json),
				4 => s => s.SetProperty(m => m.MemberSlot4, json),
				5 => s => s.SetProperty(m => m.MemberSlot5, json),
				6 => s => s.SetProperty(m => m.MemberSlot6, json),
				7 => s => s.SetProperty(m => m.MemberSlot7, json),
				_ => throw new ArgumentOutOfRangeException(nameof(slotIndex))
			};
		}


		private static readonly Action<SetPropertyCalls<MatchHistoryEntry>, string?>[] _slotSetters =
{
	(s, v) => s.SetProperty(m => m.MemberSlot0, v),
	(s, v) => s.SetProperty(m => m.MemberSlot1, v),
	(s, v) => s.SetProperty(m => m.MemberSlot2, v),
	(s, v) => s.SetProperty(m => m.MemberSlot3, v),
	(s, v) => s.SetProperty(m => m.MemberSlot4, v),
	(s, v) => s.SetProperty(m => m.MemberSlot5, v),
	(s, v) => s.SetProperty(m => m.MemberSlot6, v),
	(s, v) => s.SetProperty(m => m.MemberSlot7, v)
};




		private static async Task<string?> _getMemberSlot(AppDbContext db, long matchId, int slotIndex)
		{
			if (slotIndex < 0 || slotIndex > 7)
				return null;

			return await db.MatchHistory
				.Where(m => m.MatchId == matchId)
				.Select(_slotSelectors[slotIndex])
				.FirstOrDefaultAsync();
		}



		private static readonly Func<AppDbContext, Task<long?>> _getHighestMatchId =
			EF.CompileAsyncQuery(
				(AppDbContext db) =>
					db.MatchHistory
					  .Max(m => (long?)m.MatchId)
			);

		private static readonly Func<AppDbContext, DateTime, int, IAsyncEnumerable<MatchHistoryEntry>> _getMatchesSince =
			EF.CompileAsyncQuery(
				(AppDbContext db, DateTime since, int maxLobbiesPerRequest) =>
					db.MatchHistory
					  .Where(m => m.Finished && m.TimeFinished >= since)
					  .OrderBy(m => m.TimeFinished)
					  .ThenBy(m => m.MatchId)
					  .Take(maxLobbiesPerRequest)
			);



		public static async Task CommitPlayerOutcome(
	AppDbContext db,
	int slotIndex,
	ulong matchId,
	int side,
	int buildingsBuilt,
	int buildingsKilled,
	int buildingsLost,
	int unitsBuilt,
	int unitsKilled,
	int unitsLost,
	int totalMoney,
	bool won)
		{
			if (slotIndex < 0 || slotIndex > 7)
				return;

			try
			{
				// 1. Load JSON for this slot
				string? json = await _getMemberSlot(db, (long)matchId, slotIndex);
				if (string.IsNullOrEmpty(json))
					return;

				// 2. Deserialize
				MatchdataMemberModel? modelNullable = JsonSerializer.Deserialize<MatchdataMemberModel?>(json);
				if (modelNullable == null)
					return;

				// 3. Update fields
				MatchdataMemberModel model = modelNullable.Value;
				model.side = side;
				model.buildings_built = buildingsBuilt;
				model.buildings_killed = buildingsKilled;
				model.buildings_lost = buildingsLost;
				model.units_built = unitsBuilt;
				model.units_killed = unitsKilled;
				model.units_lost = unitsLost;
				model.total_money = totalMoney;
				model.won = won;

				// 4. Serialize back
				string updatedJson = JsonSerializer.Serialize(model);

				// 5. Update DB (single SQL UPDATE)
				await _updateMemberSlot(db, (long)matchId, slotIndex, updatedJson);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] CommitPlayerOutcome failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		public static async Task _updateMemberSlot(
	AppDbContext db, long matchId, int slotIndex, string? json)
		{
			var setter = BuildSetter(slotIndex, json);

			await db.MatchHistory
				.Where(m => m.MatchId == matchId)
				.ExecuteUpdateAsync(setter);
		}


		private static string ComputeRosterType(Dictionary<int, int> playersPerTeam)
		{
			int noTeamCount = playersPerTeam.GetValueOrDefault(-1, 0);

			var groups = playersPerTeam
				.Where(kv => kv.Key != -1)
				.Select(kv => kv.Value)
				.Concat(Enumerable.Repeat(1, noTeamCount))
				.OrderBy(c => c)
				.ToList();

			int activePlayers = groups.Sum();

			if (activePlayers == 0 || groups.Count == 1)
			{
				return "Unknown";
			}

			if (activePlayers > 2 && groups.All(c => c == 1))
			{
				return $"{activePlayers} Player FFA";
			}

			return string.Join("v", groups);
		}


		public static async Task<ulong> CreatePlaceholderMatchHistory(
	AppDbContext db,
	GenOnlineService.Lobby lobby)
		{
			if (lobby == null)
				return 0;

			try
			{
				// Build member JSON array
				string?[] jsonSlots = new string?[8];

				Dictionary<int, int> playersPerTeam = new();

				foreach (var member in lobby.Members)
				{
					if (member.SlotState == EPlayerType.SLOT_OPEN ||
						member.SlotState == EPlayerType.SLOT_CLOSED)
						continue;

					var model = new MatchdataMemberModel
					{
						user_id = member.UserID,
						display_name = member.DisplayName,
						slot_state = member.SlotState,
						side = member.Side,
						color = member.Color,
						team = member.Team,
						startpos = member.StartingPosition,
						buildings_built = 0,
						buildings_killed = 0,
						buildings_lost = 0,
						units_built = 0,
						units_killed = 0,
						units_lost = 0,
						total_money = 0,
						won = false
					};

					jsonSlots[member.SlotIndex] = JsonSerializer.Serialize(model);

					// Observers are not active players
					if (model.side != Constants.OBSERVER_SIDE_VALUE)
					{
						if (playersPerTeam.ContainsKey(model.team))
						{
							playersPerTeam[model.team]++;
						}
						else
						{
							playersPerTeam[model.team] = 1;
						}
					}
				}

				// Determine roster type
				string rosterType = ComputeRosterType(playersPerTeam);

				// Build EF entity
				var entity = new MatchHistoryEntry
				{
					Owner = lobby.Owner,
					Name = lobby.Name,
					MapName = lobby.MapName,
					MapPath = lobby.MapPath,
					MapOfficial = lobby.IsMapOfficial,
					MatchRosterType = rosterType,
					VanillaTeams = lobby.IsVanillaTeamsOnly,
					StartingCash = lobby.StartingCash,
					LimitSuperweapons = lobby.IsLimitSuperweapons,
					TrackStats = lobby.IsTrackingStats,
					AllowObservers = lobby.AllowObservers,
					MaxCamHeight = lobby.MaximumCameraHeight,

					MemberSlot0 = jsonSlots[0],
					MemberSlot1 = jsonSlots[1],
					MemberSlot2 = jsonSlots[2],
					MemberSlot3 = jsonSlots[3],
					MemberSlot4 = jsonSlots[4],
					MemberSlot5 = jsonSlots[5],
					MemberSlot6 = jsonSlots[6],
					MemberSlot7 = jsonSlots[7]
				};

				// Add entity to DbSet
				db.MatchHistory.Add(entity);

				// Save
				await db.SaveChangesAsync();

				ulong id = (ulong)entity.MatchId;
				lobby.SetMatchID(id);

				return id;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] CreatePlaceholderMatchHistory failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return 0;
			}
		}

		public static async Task DetermineLobbyWinnerIfNotPresent(
	AppDbContext db,
	GenOnlineService.Lobby lobby)
		{
			if (lobby == null || lobby.MatchID == 0)
				return;

			try
			{
				// 1. Load all JSON slots
				string?[]? slots = await _getAllMemberSlots(db, (long)lobby.MatchID);
				if (slots == null)
					return;

				// 2. Deserialize only non-null slots
				Dictionary<int, MatchdataMemberModel> members = new();

				for (int i = 0; i < 8; i++)
				{
					if (!string.IsNullOrEmpty(slots[i]))
					{
						MatchdataMemberModel? model = JsonSerializer.Deserialize<MatchdataMemberModel>(slots[i]!);
						if (model != null)
							members[i] = model.Value;
					}
				}

				// 3. Build winner groups from active, non-observer members.
                //    Teamless players are treated individually using synthetic keys.
				Dictionary<int, List<int>> winGroups = new();
				foreach (var kv in members)
				{
					var model = kv.Value;
					if (model.side == Constants.OBSERVER_SIDE_VALUE || !model.won)
						continue;

					int groupKey = model.team != -1 ? model.team : -1000 - kv.Key;
					if (!winGroups.TryGetValue(groupKey, out var slotList))
					{
						slotList = new List<int>();
						winGroups[groupKey] = slotList;
					}
					slotList.Add(kv.Key);
				}

				// 4. Compare winner groups by report count.
				//    A unique maximum wins; ties or no winner groups fall back to
                //    the abandoned timestamp-based resolution.
				int? conclusiveWinningTeam = null;
				int conclusiveWinningSlot = -1;
				if (winGroups.Count > 0)
				{
					int maxCount = winGroups.Values.Max(g => g.Count);
					var topGroups = winGroups.Where(g => g.Value.Count == maxCount).ToList();
					if (topGroups.Count == 1)
					{
						var winningGroup = topGroups[0];
						int teamKey = winningGroup.Key;
						if (teamKey > -1000)
						{
							conclusiveWinningTeam = teamKey;
						}
						else
						{
							conclusiveWinningSlot = winningGroup.Value[0];
						}
					}
				}

				// 5. If conclusively determined, propagate the win to the team 
				//    and explicitly award a loss to everyone else.
				if (conclusiveWinningTeam != null || conclusiveWinningSlot != -1)
				{
					foreach (var kv in members)
					{
						bool isWinner = conclusiveWinningTeam != null
							? kv.Value.team == conclusiveWinningTeam.Value
							: kv.Key == conclusiveWinningSlot;

						await UpdateMatchHistorySetWinFlag(db, lobby.MatchID, kv.Key, isWinner);
					}

					return;
				}

				// 6. No winner — determine who quit last (= winner) using the most accurate timing available.
				//    Prefer TimePlayerAbandonedIngame (recorded the instant each player's WS dropped while
				//    in-game) over TimeMemberLeft (recorded when the player was structurally removed from the
				//    lobby, which can happen much later, or earlier due to a fresh-session reconnect, skewing
				//    the order relative to the actual in-game quit order).
				Console.WriteLine($"[WinnerDet] Match={lobby.MatchID} LobbyID={lobby.LobbyID}: no winner in DB — timestamp fallback. IngameAbandon={lobby.TimePlayerAbandonedIngame.Count} MemberLeft={lobby.TimeMemberLeft.Count}");
				foreach (var _kv in lobby.TimePlayerAbandonedIngame)
					Console.WriteLine($"[WinnerDet]   IngameAbandon: user={_kv.Key} at={_kv.Value:O}");
				foreach (var _kv in lobby.TimeMemberLeft)
					Console.WriteLine($"[WinnerDet]   MemberLeft:    user={_kv.Key} at={_kv.Value:O}");
				DateTime latestLeave = DateTime.MinValue;
				MatchdataMemberModel? lastPlayerNullable = null;
				int lastSlot = -1;

				foreach (var kv in members)
				{
					var model = kv.Value;

					// Skip observer slots and AI/placeholder slots (user_id <= 0);
					// they never quit the game and must not be selected as "last to leave = winner".
					if (model.side == Constants.OBSERVER_SIDE_VALUE || model.user_id <= 0)
						continue;

					DateTime abandonTime = DateTime.MinValue;
					string abandonSource = "none";

					// Use the in-game abandon timestamp if available (most accurate).
					if (lobby.TimePlayerAbandonedIngame.TryGetValue(model.user_id, out DateTime ingameAbandon))
					{
						abandonTime = ingameAbandon;
						abandonSource = "IngameAbandon";
					}
					else if (lobby.TimeMemberLeft.TryGetValue(model.user_id, out DateTime leftAt) && leftAt > DateTime.UnixEpoch)
					{
						// Fall back to lobby-removal time only if we have no in-game abandon record.
						abandonTime = leftAt;
						abandonSource = "MemberLeft";
					}

					Console.WriteLine($"[WinnerDet]   Slot={kv.Key} user={model.user_id} side={model.side} team={model.team} time={abandonTime:O} src={abandonSource}");
					if (abandonTime > latestLeave)
					{
						latestLeave = abandonTime;
						lastPlayerNullable = model;
						lastSlot = kv.Key;
					}
				}

				if (lastPlayerNullable == null)
				{
					Console.WriteLine($"[WinnerDet] Match={lobby.MatchID}: no valid abandon timestamps found — fully inconclusive, clearing won flags.");
					foreach (var kv in members)
					{
						if (kv.Value.side == Constants.OBSERVER_SIDE_VALUE)
							continue;

						await UpdateMatchHistorySetWinFlag(db, lobby.MatchID, kv.Key, false);
					}
					return;
				}

				MatchdataMemberModel lastPlayer = lastPlayerNullable.Value;
				int winningTeam = lastPlayer.team;
				Console.WriteLine($"[WinnerDet] Match={lobby.MatchID}: winner selected → user={lastPlayer.user_id} slot={lastSlot} team={winningTeam} at={latestLeave:O}");

				// 7. Mark last player + teammates as winners, explicitly clear everyone else.
				foreach (var kv in members)
				{
					var model = kv.Value;
					if (model.side == Constants.OBSERVER_SIDE_VALUE)
						continue;

					bool isWinner = model.user_id == lastPlayer.user_id ||
						(winningTeam != -1 && model.team == winningTeam);

					Console.WriteLine($"[WinnerDet] Match={lobby.MatchID}: marking slot={kv.Key} user={model.user_id} as {(isWinner ? "WINNER" : "loser")}.");
					await UpdateMatchHistorySetWinFlag(db, lobby.MatchID, kv.Key, isWinner);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] DetermineLobbyWinnerIfNotPresent failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		public static async Task UpdateMatchHistorySetWinFlag(
	AppDbContext db,
	ulong matchId,
	int slotIndex,
	bool won)
		{
			if (matchId == 0 || slotIndex < 0 || slotIndex > 7)
				return;

			try
			{
				// 1. Load the JSON for this slot
				string? json = await _getMemberSlot(db, (long)matchId, slotIndex);
				if (string.IsNullOrEmpty(json))
					return;

				// 2. Deserialize
				MatchdataMemberModel? modelNullable = JsonSerializer.Deserialize<MatchdataMemberModel>(json);
				if (modelNullable == null)
					return;

				// 3. Update winner flag
				MatchdataMemberModel model = modelNullable.Value;
				model.won = won;

				// 4. Serialize back
				string updatedJson = JsonSerializer.Serialize(model);

				// 5. Build setter expression
				var setter = BuildSetter(slotIndex, updatedJson);

				// 6. Execute update (single SQL UPDATE)
				await db.MatchHistory
					.Where(m => m.MatchId == (long)matchId)
					.ExecuteUpdateAsync(setter);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] UpdateMatchHistorySetWinFlag failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}



		public static async Task<MatchHistoryCollection> GetMatchesInRange(
	AppDbContext db, long startID, long endID)
		{
			MatchHistoryCollection collection = new();

			try
			{
				// Single query fetches metadata + all slot columns — no concurrent reader issue.
				var rows = await db.MatchHistory
					.Where(m => m.MatchId >= startID && m.MatchId <= endID && m.Finished)
					.Select(m => new
					{
						m.MatchId,
						m.Owner,
						m.Name,
						m.Finished,
						m.Started,
						m.TimeFinished,
						m.MapName,
						m.MapPath,
						m.MatchRosterType,
						m.MapOfficial,
						m.VanillaTeams,
						m.StartingCash,
						m.LimitSuperweapons,
						m.TrackStats,
						m.AllowObservers,
						m.MaxCamHeight,
						m.MemberSlot0,
						m.MemberSlot1,
						m.MemberSlot2,
						m.MemberSlot3,
						m.MemberSlot4,
						m.MemberSlot5,
						m.MemberSlot6,
						m.MemberSlot7
					})
					.ToListAsync();

				foreach (var row in rows)
				{
					var entry = new MatchHistory_Entry(
						row.MatchId,
						row.Owner,
						row.Name,
						row.Finished,
						row.Started.ToString("O"),
						row.TimeFinished.ToString("O"),
						row.MapName,
						row.MapPath ?? string.Empty,
						row.MatchRosterType,
						row.MapOfficial,
						row.VanillaTeams,
						row.StartingCash,
						row.LimitSuperweapons,
						row.TrackStats,
						row.AllowObservers,
						row.MaxCamHeight
					);

					AddMemberIfNotNull(entry, row.MemberSlot0);
					AddMemberIfNotNull(entry, row.MemberSlot1);
					AddMemberIfNotNull(entry, row.MemberSlot2);
					AddMemberIfNotNull(entry, row.MemberSlot3);
					AddMemberIfNotNull(entry, row.MemberSlot4);
					AddMemberIfNotNull(entry, row.MemberSlot5);
					AddMemberIfNotNull(entry, row.MemberSlot6);
					AddMemberIfNotNull(entry, row.MemberSlot7);

					collection.matches.Add(entry);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] GetMatchesInRange failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}

			return collection;
		}

		public static async Task<MatchHistoryCollection> GetMatchesSince(
	AppDbContext db, DateTime since, int maxLobbiesPerRequest)
		{
			MatchHistoryCollection collection = new();

			since = DateTime.SpecifyKind(since, DateTimeKind.Utc);

			try
			{
				// Single query fetches metadata + all slot columns — no concurrent reader issue.
				await foreach (var row in _getMatchesSince(db, since, maxLobbiesPerRequest))
				{
					var entry = new MatchHistory_Entry(
						row.MatchId,
						row.Owner,
						row.Name,
						row.Finished,
						row.Started.ToString("O"),
						row.TimeFinished.ToString("O"),
						row.MapName,
						row.MapPath ?? string.Empty,
						row.MatchRosterType,
						row.MapOfficial,
						row.VanillaTeams,
						row.StartingCash,
						row.LimitSuperweapons,
						row.TrackStats,
						row.AllowObservers,
						row.MaxCamHeight
					);

					AddMemberIfNotNull(entry, row.MemberSlot0);
					AddMemberIfNotNull(entry, row.MemberSlot1);
					AddMemberIfNotNull(entry, row.MemberSlot2);
					AddMemberIfNotNull(entry, row.MemberSlot3);
					AddMemberIfNotNull(entry, row.MemberSlot4);
					AddMemberIfNotNull(entry, row.MemberSlot5);
					AddMemberIfNotNull(entry, row.MemberSlot6);
					AddMemberIfNotNull(entry, row.MemberSlot7);

					collection.matches.Add(entry);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] GetMatchesSince failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}

			return collection;
		}

		private static void AddMemberIfNotNull(MatchHistory_Entry entry, string? json)
		{
			if (!string.IsNullOrEmpty(json))
			{
				var model = JsonSerializer.Deserialize<GenOnlineService.MatchdataMemberModel?>(json);
				if (model != null)
					entry.members.Add(model);
			}
		}



		public static async Task<long> GetHighestMatchID(AppDbContext db)
		{
			try
			{
				long? result = await _getHighestMatchId(db);
				return result ?? -1;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] GetHighestMatchID failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
				return -1;
			}
		}

		// Called when a lobby is deleted, thats the true end of a match
		public static async Task CommitLobbyToMatchHistory(AppDbContext db, GenOnlineService.Lobby lobby)
		{
			if (lobby.MatchID == 0)
				return;

			try
			{
				await db.MatchHistory
					.Where(m => m.MatchId == (long)lobby.MatchID && !m.Finished)
					.ExecuteUpdateAsync(s => s
						.SetProperty(m => m.Finished, true)
						.SetProperty(m => m.TimeFinished, DateTime.UtcNow));
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] CommitLobbyToMatchHistory failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		internal static async Task<MatchHistory_Entry?> LoadMatchHistoryEntryAsync(
			AppDbContext db, long matchId)
		{
			var row = await db.MatchHistory.FirstOrDefaultAsync(m => m.MatchId == matchId);
			if (row == null)
				return null;

			var entry = new MatchHistory_Entry(
				row.MatchId,
				row.Owner,
				row.Name,
				row.Finished,
				row.Started.ToString("O"),
				row.TimeFinished.ToString("O"),
				row.MapName,
				row.MapPath ?? string.Empty,
				row.MatchRosterType,
				row.MapOfficial,
				row.VanillaTeams,
				row.StartingCash,
				row.LimitSuperweapons,
				row.TrackStats,
				row.AllowObservers,
				row.MaxCamHeight
			);

			AddMemberIfNotNull(entry, row.MemberSlot0);
			AddMemberIfNotNull(entry, row.MemberSlot1);
			AddMemberIfNotNull(entry, row.MemberSlot2);
			AddMemberIfNotNull(entry, row.MemberSlot3);
			AddMemberIfNotNull(entry, row.MemberSlot4);
			AddMemberIfNotNull(entry, row.MemberSlot5);
			AddMemberIfNotNull(entry, row.MemberSlot6);
			AddMemberIfNotNull(entry, row.MemberSlot7);

			return entry;
		}

		// METADATA
		private static Expression<Func<SetPropertyCalls<MatchHistoryEntry>, SetPropertyCalls<MatchHistoryEntry>>>
	BuildSlotSetter(int slotIndex, string updatedJson)
		{
			return slotIndex switch
			{
				0 => s => s.SetProperty(m => m.MemberSlot0, updatedJson),
				1 => s => s.SetProperty(m => m.MemberSlot1, updatedJson),
				2 => s => s.SetProperty(m => m.MemberSlot2, updatedJson),
				3 => s => s.SetProperty(m => m.MemberSlot3, updatedJson),
				4 => s => s.SetProperty(m => m.MemberSlot4, updatedJson),
				5 => s => s.SetProperty(m => m.MemberSlot5, updatedJson),
				6 => s => s.SetProperty(m => m.MemberSlot6, updatedJson),
				7 => s => s.SetProperty(m => m.MemberSlot7, updatedJson),
				_ => throw new ArgumentOutOfRangeException(nameof(slotIndex))
			};
		}


		public static async Task AttachMatchHistoryMetadata(
	AppDbContext db,
	ulong matchId,
	int slotIndex,
	string fileName,
	EMetadataFileType fileType)
		{
			if (matchId == 0 || slotIndex < 0 || slotIndex > 7)
				return;

			// 1. Load JSON for this slot
			string? json = await _getMemberSlot(db, (long)matchId, slotIndex);
			if (string.IsNullOrEmpty(json))
				return;

			// 2. Deserialize
			MatchdataMemberModel? modelN = JsonSerializer.Deserialize<MatchdataMemberModel?>(json);
			if (modelN == null)
				return;

			MatchdataMemberModel model = modelN.Value;

			// 3. Ensure metadata list exists
			model.metadata ??= new List<MemberMetadataModel>();

			// 4. Append metadata entry
			model.metadata.Add(new MemberMetadataModel
			{
				file_name = fileName,
				file_type = (EMetadataFileType)fileType
			});

			// 5. Serialize back
			string updatedJson = JsonSerializer.Serialize(model);

			// 6. Build setter expression
			var setter = BuildSlotSetter(slotIndex, updatedJson);

			// 7. Execute update (single SQL UPDATE)
			await db.MatchHistory
				.Where(m => m.MatchId == (long)matchId)
				.ExecuteUpdateAsync(setter);
		}

		// ELO
		public static async Task UpdateLeaderboardAndElo(
	AppDbContext db,
	GenOnlineService.Lobby lobby)
		{
			if (lobby.LobbyType != ELobbyType.QuickMatch)
				return;

			try
			{
				int dayOfYear = lobby.TimeCreated.DayOfYear;
				int monthOfYear = lobby.TimeCreated.Month;
				int year = lobby.TimeCreated.Year;

				var members = await LoadMatchMembersAsync(db, (long)lobby.MatchID);
				if (members.Count == 0)
					return;

				await UpdateCurrentEloAsync(db, members);
				await UpdatePeriodEloAndLeaderboardsAsync(
					db, members, dayOfYear, monthOfYear, year);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] UpdateLeaderboardAndElo failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}
		private static async Task<List<MatchdataMemberModel>> LoadMatchMembersAsync(
			AppDbContext db, long matchId)
		{
			var slots = await _getAllMemberSlots(db, matchId);
			var list = new List<MatchdataMemberModel>();

			if (slots == null)
				return list;

			for (int i = 0; i < slots.Length; i++)
			{
				if (!string.IsNullOrEmpty(slots[i]))
				{
					MatchdataMemberModel? model = JsonSerializer.Deserialize<MatchdataMemberModel?>(slots[i]!);
					if (model != null)
					{
						// Exclude observer slots and AI/placeholder slots (user_id <= 0)
						// from ELO calculations — only real human players are ranked.
						if (model.Value.side == Constants.OBSERVER_SIDE_VALUE || model.Value.user_id <= 0)
							continue;

						list.Add(model.Value);
					}
				}
			}

			return list;
		}

		private static async Task UpdateCurrentEloAsync(
	AppDbContext db,
	List<MatchdataMemberModel> members)
		{
			var userIds = members.Select(m => (long)m.user_id).ToList();
			var dictElo = await Database.Users.GetBulkELOData(db, userIds);

			// --- ELO pairwise loop (ref-safe) ---
			foreach (var a in members)
			{
				foreach (var b in members)
				{
					if (a.user_id == b.user_id)
						continue;

					if (b.team == a.team && a.team != -1)
						continue;

					if (a.user_id >= b.user_id)
						continue;

					Console.WriteLine($"[ELO] Pairing a={a.user_id}(won={a.won}) vs b={b.user_id}(won={b.won}) → result={(a.won ? "PlayerAWins" : "PlayerBWins")}");
					ref EloData A = ref CollectionsMarshal.GetValueRefOrAddDefault(
						dictElo, a.user_id, out _);

					ref EloData B = ref CollectionsMarshal.GetValueRefOrAddDefault(
						dictElo, b.user_id, out _);

					Elo.ApplyResult(
						ref A,
						ref B,
						a.won ? MatchResult.PlayerAWins : MatchResult.PlayerBWins);
				}
			}

			// --- Increment matches (still ref-safe) ---
			foreach (var m in members)
			{
				ref EloData data = ref CollectionsMarshal.GetValueRefOrAddDefault(
					dictElo, m.user_id, out _);
				data.NumMatches++;
			}

			// --- Persist (copy out of ref before EF) ---
			foreach (var pair in dictElo)
			{
				long userId = pair.Key;
				EloData data = pair.Value;   // <-- COPY OUT OF REF HERE

				// Update live user if online
				var shared = GenOnlineService.WebSocketManager.GetSharedDataForUser(userId);
				if (shared != null)
				{
					shared.GameStats.EloRating = data.Rating;
					shared.GameStats.EloMatches = data.NumMatches;
				}

				// EF Core persistence (no ref locals allowed)
				await Database.Users.SaveELOData(db, userId, data);
			}
		}


		private static async Task UpdatePeriodEloAndLeaderboardsAsync(
	AppDbContext db,
	List<MatchdataMemberModel> members,
	int dayOfYear,
	int monthOfYear,
	int year)
		{
			var userIds = members.Select(m => (long)m.user_id).ToList();
			var bulk = await Database.Leaderboards.GetBulkLeaderboardData(
				db, userIds, dayOfYear, monthOfYear, year);

			var daily = new Dictionary<long, EloData>();
			var monthly = new Dictionary<long, EloData>();
			var yearly = new Dictionary<long, EloData>();

			// Initialize from DB
			foreach (var m in members)
			{
				var lb = bulk[m.user_id];
				daily[m.user_id] = new EloData(lb.daily, lb.daily_matches);
				monthly[m.user_id] = new EloData(lb.monthly, lb.monthly_matches);
				yearly[m.user_id] = new EloData(lb.yearly, lb.yearly_matches);
			}

			// --- Pairwise ELO (ref-safe) ---
			foreach (var a in members)
			{
				foreach (var b in members)
				{
					if (a.user_id == b.user_id)
						continue;

					if (b.team == a.team && a.team != -1)
						continue;

					if (a.user_id >= b.user_id)
						continue;

					// Daily
					{
						ref EloData A = ref CollectionsMarshal.GetValueRefOrAddDefault(daily, a.user_id, out _);
						ref EloData B = ref CollectionsMarshal.GetValueRefOrAddDefault(daily, b.user_id, out _);
						Elo.ApplyResult(ref A, ref B, a.won ? MatchResult.PlayerAWins : MatchResult.PlayerBWins);
					}

					// Monthly
					{
						ref EloData A = ref CollectionsMarshal.GetValueRefOrAddDefault(monthly, a.user_id, out _);
						ref EloData B = ref CollectionsMarshal.GetValueRefOrAddDefault(monthly, b.user_id, out _);
						Elo.ApplyResult(ref A, ref B, a.won ? MatchResult.PlayerAWins : MatchResult.PlayerBWins);
					}

					// Yearly
					{
						ref EloData A = ref CollectionsMarshal.GetValueRefOrAddDefault(yearly, a.user_id, out _);
						ref EloData B = ref CollectionsMarshal.GetValueRefOrAddDefault(yearly, b.user_id, out _);
						Elo.ApplyResult(ref A, ref B, a.won ? MatchResult.PlayerAWins : MatchResult.PlayerBWins);
					}
				}
			}

			// --- Persist (copy out of ref before EF) ---
			foreach (var m in members)
			{
				long userId = m.user_id;

				EloData d = daily[userId];   // <-- COPY OUT OF REF
				EloData mo = monthly[userId];
				EloData y = yearly[userId];

				int wins = m.won ? 1 : 0;
				int losses = m.won ? 0 : 1;

				// Daily
				await db.LeaderboardDaily
					.Where(x => x.UserId == userId &&
								x.DayOfYear == dayOfYear &&
								x.Year == year)
					.ExecuteUpdateAsync(s => s
						.SetProperty(x => x.Points, d.Rating)
						.SetProperty(x => x.Wins, x => x.Wins + wins)
						.SetProperty(x => x.Losses, x => x.Losses + losses));

				// Monthly
				await db.LeaderboardMonthly
					.Where(x => x.UserId == userId &&
								x.MonthOfYear == monthOfYear &&
								x.Year == year)
					.ExecuteUpdateAsync(s => s
						.SetProperty(x => x.Points, mo.Rating)
						.SetProperty(x => x.Wins, x => x.Wins + wins)
						.SetProperty(x => x.Losses, x => x.Losses + losses));

				// Yearly
				await db.LeaderboardYearly
					.Where(x => x.UserId == userId &&
								x.Year == year)
					.ExecuteUpdateAsync(s => s
						.SetProperty(x => x.Points, y.Rating)
						.SetProperty(x => x.Wins, x => x.Wins + wins)
						.SetProperty(x => x.Losses, x => x.Losses + losses));
			}
		}





	}
}
