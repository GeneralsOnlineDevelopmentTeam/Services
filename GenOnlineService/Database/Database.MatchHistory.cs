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
			var param = Expression.Parameter(typeof(SetPropertyCalls<MatchHistoryEntry>), "s");

			var call = Expression.Call(
				param,
				nameof(SetPropertyCalls<MatchHistoryEntry>.SetProperty),
				typeArguments: null,
				arguments: new Expression[]
				{
			_slotSelectors[slotIndex],
			Expression.Constant(json, typeof(string))
				}
			);

			return Expression.Lambda<Func<SetPropertyCalls<MatchHistoryEntry>, SetPropertyCalls<MatchHistoryEntry>>>(
				call,
				param
			);
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


		private static Expression<Func<SetPropertyCalls<MatchHistoryEntry>, SetPropertyCalls<MatchHistoryEntry>>>
	BuildWinnerSetter(int slotIndex, string updatedJson)
		{
			var param = Expression.Parameter(typeof(SetPropertyCalls<MatchHistoryEntry>), "s");

			var call = Expression.Call(
				param,
				nameof(SetPropertyCalls<MatchHistoryEntry>.SetProperty),
				typeArguments: null,
				arguments: new Expression[]
				{
			_slotSelectors[slotIndex],
			Expression.Constant(updatedJson, typeof(string))
				}
			);

			return Expression.Lambda<Func<SetPropertyCalls<MatchHistoryEntry>, SetPropertyCalls<MatchHistoryEntry>>>(
				call,
				param
			);
		}


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

		private static readonly Func<AppDbContext, long, long, IAsyncEnumerable<MatchHistory_Entry>> _getMatchesInRange =
	EF.CompileAsyncQuery(
		(AppDbContext db, long startId, long endId) =>
			db.MatchHistory
			  .Where(m => m.MatchId >= startId &&
						  m.MatchId <= endId &&
						  m.Finished)
			  .Select(m => new MatchHistory_Entry(
				  m.MatchId,
				  m.Owner,
				  m.Name,
				  m.Finished,
				  m.Started.ToString("O"),
				  m.TimeFinished.ToString("O"),
				  m.MapName,
				  m.MapPath!,
				  m.MatchRosterType,
				  m.MapOfficial,
				  m.VanillaTeams,
				  m.StartingCash,
				  m.LimitSuperweapons,
				  m.TrackStats,
				  m.AllowObservers,
				  m.MaxCamHeight
			  ))
	);

		public static async Task CommitPlayerOutcome(
	AppDbContext db,
	int slotIndex,
	ulong matchId,
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
			int noTeamCount = playersPerTeam.TryGetValue(-1, out int n) ? n : 0;

			var teamedGroups = playersPerTeam
				.Where(kv => kv.Key != -1)
				.Select(kv => kv.Value)
				.OrderBy(c => c)
				.ToList();

			int activePlayers = noTeamCount + teamedGroups.Sum();

			if (activePlayers == 0)
			{
				return "Unknown";
			}

			bool isFFA = activePlayers > 2 &&
						 (noTeamCount == activePlayers || teamedGroups.All(c => c == 1));

			if (isFFA)
			{
				return $"{activePlayers} Player FFA";
			}

			return string.Join("v", teamedGroups);
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

					// Observers (side == -2) are not active players
					if (model.side != -2)
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

				// 3. Check if a winner already exists
				bool hasWinner = false;
				int winnerTeam = -1;

				foreach (var kv in members)
				{
					if (kv.Value.won)
					{
						hasWinner = true;
						winnerTeam = kv.Value.team;
						break;
					}
				}

				// 4. If winner exists, propagate to teammates
				if (hasWinner && winnerTeam != -1)
				{
					foreach (var kv in members)
					{
						if (kv.Value.team == winnerTeam)
						{
							await UpdateMatchHistoryMakeWinner(db, lobby.MatchID, kv.Key);
						}
					}

					return;
				}

				// 5. No winner — pick last player to leave
				DateTime latestLeave = DateTime.UnixEpoch;
				MatchdataMemberModel? lastPlayerNullable = null;
				int lastSlot = -1;

				foreach (var kv in members)
				{
					var model = kv.Value;

					if (lobby.TimeMemberLeft.TryGetValue(model.user_id, out DateTime leftAt))
					{
						if (leftAt >= latestLeave)
						{
							latestLeave = leftAt;
							lastPlayerNullable = model;
							lastSlot = kv.Key;
						}
					}
				}

				if (lastPlayerNullable == null)
					return;

				MatchdataMemberModel lastPlayer = lastPlayerNullable.Value;
				int winningTeam = lastPlayer.team;

				// 6. Mark last player + teammates as winners
				foreach (var kv in members)
				{
					var model = kv.Value;

					if (model.user_id == lastPlayer.user_id ||
						(winningTeam != -1 && model.team == winningTeam))
					{
						await UpdateMatchHistoryMakeWinner(db, lobby.MatchID, kv.Key);
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] DetermineLobbyWinnerIfNotPresent failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		public static async Task UpdateMatchHistoryMakeWinner(
	AppDbContext db,
	ulong matchId,
	int slotIndex)
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
				model.won = true;

				// 4. Serialize back
				string updatedJson = JsonSerializer.Serialize(model);

				// 5. Build setter expression
				var setter = BuildWinnerSetter(slotIndex, updatedJson);

				// 6. Execute update (single SQL UPDATE)
				await db.MatchHistory
					.Where(m => m.MatchId == (long)matchId)
					.ExecuteUpdateAsync(setter);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] UpdateMatchHistoryMakeWinner failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}



		public static async Task<MatchHistoryCollection> GetMatchesInRange(
	AppDbContext db, long startID, long endID)
		{
			MatchHistoryCollection collection = new();

			try
			{
				await foreach (var entry in _getMatchesInRange(db, startID, endID))
				{
					// Load JSON members (optional optimization below)
					var entity = await db.MatchHistory
						.Where(m => m.MatchId == entry.match_id)
						.Select(m => new
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
						.FirstOrDefaultAsync();

						if (entity == null)
							continue;

					// Deserialize only if not null
					AddMemberIfNotNull(entry, entity.MemberSlot0);
					AddMemberIfNotNull(entry, entity.MemberSlot1);
					AddMemberIfNotNull(entry, entity.MemberSlot2);
					AddMemberIfNotNull(entry, entity.MemberSlot3);
					AddMemberIfNotNull(entry, entity.MemberSlot4);
					AddMemberIfNotNull(entry, entity.MemberSlot5);
					AddMemberIfNotNull(entry, entity.MemberSlot6);
					AddMemberIfNotNull(entry, entity.MemberSlot7);

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

		// METADATA
		private static Expression<Func<SetPropertyCalls<MatchHistoryEntry>, SetPropertyCalls<MatchHistoryEntry>>>
	BuildSlotSetter(int slotIndex, string updatedJson)
		{
			var param = Expression.Parameter(typeof(SetPropertyCalls<MatchHistoryEntry>), "s");

			var call = Expression.Call(
				param,
				nameof(SetPropertyCalls<MatchHistoryEntry>.SetProperty),
				typeArguments: null,
				arguments: new Expression[]
				{
			_slotSelectors[slotIndex],
			Expression.Constant(updatedJson, typeof(string))
				}
			);

			return Expression.Lambda<Func<SetPropertyCalls<MatchHistoryEntry>, SetPropertyCalls<MatchHistoryEntry>>>(
				call,
				param
			);
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

			try
			{
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
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] AttachMatchHistoryMetadata failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
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
						list.Add(model.Value);
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
