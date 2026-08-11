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
using Discord.Rest;
using GenOnlineService;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Tls;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

public class PlaylistMap
{
	public string Name { get; private set; }
	public string Path { get; private set; }
	public bool Custom { get; private set; }
	public int MaxPlayers { get; private set; }

	public PlaylistMap(string strName, string strPath, bool bCustom, int maxPlayers)
	{
		Name = strName;
		Path = strPath;
		Custom = bCustom;
		MaxPlayers = maxPlayers;
	}
}

public class ConcurrentList<T>
{
	private List<T> m_internalList;
	private readonly object m_lockObj = new object();

	public ConcurrentList()
	{
		m_internalList = new List<T>();
	}

	public ConcurrentList(IEnumerable<T> collection)
	{
		if (collection != null)
		{
			m_internalList = new List<T>(collection);
		}
		else
		{
			m_internalList = new List<T>();
		}
	}

	public void AddRange(IEnumerable<T> collection)
	{
		lock (m_lockObj)
		{
			m_internalList.AddRange(collection);
		}
	}

	public void Add(T item)
	{
		lock (m_lockObj)
		{
			m_internalList.Add(item);
		}
	}

	public void Clear()
	{
		lock (m_lockObj)
		{
			m_internalList.Clear();
		}
	}

	public T this[int index]
	{
		get
		{
			lock (m_lockObj)
			{
				if (index < 0 || index >= m_internalList.Count)
					throw new IndexOutOfRangeException("Index out of range.");
				return m_internalList[index];
			}
		}
	}

	public int Count
	{
		get
		{
			lock (m_lockObj)
			{
				return m_internalList.Count;
			}
		}
	}

	public bool Contains(T item)
	{
		lock (m_lockObj)
		{
			return m_internalList.Contains(item);
		}
	}
	public bool Remove(T item)
	{
		lock (m_lockObj)
		{
			return m_internalList.Remove(item);
		}
	}
	public List<T> ToList()
	{
		lock (m_lockObj)
		{
			return m_internalList.ToList();
		}
	}
	public IEnumerator<T> GetEnumerator()
	{
		lock (m_lockObj)
		{
			return m_internalList.ToList().GetEnumerator();
		}
	}
}

public class Playlist
{
	public UInt16 PlaylistID { get; private set; }
	public string Name { get; private set; }
	public int MinPlayers { get; private set; }
	public int DesiredPlayers { get; private set; }
	public int MinSelectedMaps { get; private set; }

	public bool AllowTeams { get; private set; }
	public int TeamSize { get; private set; }
	public bool AllowArmySelection { get; private set; }
	public UInt16 GracePeriodAtMinPlayersMSec { get; private set; }
	public List<PlaylistMap> Maps { get; private set; }

	public Playlist(UInt16 a_PlaylistID, string a_strName,
		int a_MinPlayers, int a_DesiredPlayers, int a_MinSelectedMaps, bool a_bAllowTeams, int a_TeamSize, bool a_bAllowArmySelection, UInt16 a_gracePeriodAtMinPlayersMSec, List<PlaylistMap> allowedMaps)
	{
		PlaylistID = a_PlaylistID;
		Name = a_strName;
		MinPlayers = a_MinPlayers;
		MinSelectedMaps = a_MinSelectedMaps;
		DesiredPlayers = a_DesiredPlayers;
		AllowTeams = a_bAllowTeams;
		TeamSize = a_TeamSize;
		AllowArmySelection = a_bAllowArmySelection;
		GracePeriodAtMinPlayersMSec = a_gracePeriodAtMinPlayersMSec;
		Maps = allowedMaps;
	}
}

static class MatchmakingManager
{
	public static void PlayerWidenSearch(UserSession playerSession)
	{
		// NOTE: we dont check the state of the bucket here, but it doesn't really matter since expanding the maps after it started won't do anything anyway

		// remove the map limitation
		foreach (var kvPair in m_dictMatchmakingBuckets)
		{
			foreach (MatchmakingBucket mmBucket in kvPair.Value)
			{
				if (mmBucket.HasPlayer(playerSession))
				{
					// get the maximum map set for this playlist
					if (MatchmakingManager.g_Playlists.TryGetValue(mmBucket.PlaylistID, out Playlist? playlist))
					{
						List<int> lstAllMaps = new();
						for (int mapIndex = 0; mapIndex < playlist.Maps.Count; ++mapIndex)
						{
							lstAllMaps.Add(mapIndex);
						}

						// are we the owner of the bucket? update the original list too

						MatchmakingBucketMember? bucketOwner = mmBucket.GetOwner();
						if (bucketOwner != null)
						{
							if (mmBucket.lstMapIndices.Count > 0 && bucketOwner.GetAssociatedSession() == playerSession)
							{
								mmBucket.lstMapIndices = new ConcurrentList<int>(lstAllMaps);
							}
						}

						// update our player too
						playerSession.MatchmakingMapIndicies = new ConcurrentList<int>(lstAllMaps);

						// can't be in multiple buckets
						return;
					}
				}
			}
		}

		// not in a bucket yet (still queued waiting to be sorted) - widen their selection anyway, otherwise the
		// widen request is silently dropped and they get bucketed with their original map list
		if (MatchmakingManager.g_Playlists.TryGetValue(playerSession.MatchmakingPlaylistID, out Playlist? queuedPlaylist))
		{
			List<int> lstAllMaps = new();
			for (int mapIndex = 0; mapIndex < queuedPlaylist.Maps.Count; ++mapIndex)
			{
				lstAllMaps.Add(mapIndex);
			}

			playerSession.MatchmakingMapIndicies = new ConcurrentList<int>(lstAllMaps);
		}
	}

	private static async Task SendMatchmakingMessage(UserSession cache, string message)
	{
		UserSession? sess = GenOnlineService.WebSocketManager.GetSessionFromUser(cache.m_UserID, cache.GetSessionType());
		if (sess != null)
		{
			WebSocketMessage_MatchmakingMessage msg = new WebSocketMessage_MatchmakingMessage();
			msg.msg_id = (int)EWebSocketMessageID.MATCHMAKING_MESSAGE;
			msg.message = message;
			byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));

			sess.QueueWebsocketSend(bytesJSON);
		}
	}

	public class MatchmakingBucketMember
	{
		WeakReference<UserSession>? m_SessionRef = null;

		public MatchmakingBucketMember(UserSession owningSession)
		{
			m_SessionRef = new WeakReference<UserSession>(owningSession);
		}

		public bool Is(UserSession playerSession)
		{
			if (m_SessionRef != null)
			{
				if (m_SessionRef.TryGetTarget(out UserSession? thisSession))
				{
					if (thisSession != null && thisSession == playerSession)
					{
						return true;
					}
				}
			}

			return false;
		}

		public UserSession? GetAssociatedSession()
		{
			if (m_SessionRef != null)
			{
				if (m_SessionRef.TryGetTarget(out UserSession? thisSession))
				{
					return thisSession;
				}
			}

			return null;
		}
	}

	public class MatchmakingBucket
	{
		private ConcurrentList<MatchmakingBucketMember> m_lstMembers = new();
		public ConcurrentList<int> lstMapIndices { get; set; } = new();

		private Int64 m_timeReachedMinPlayers = -1;
		private bool m_bReachedMinPlayers = false;
		private bool m_bWaitingOnLobbyJoins = false;
		private bool m_bHasStartedCountdown = false;
		private bool m_bMergedAway = false;

		// a bucket that has been merged into another bucket (or that has already been handed a lobby) must never
		// accept or donate members again, otherwise a player ends up in two buckets and gets sent to two lobbies
		public bool IsMergedAway() { return m_bMergedAway; }
		public bool IsLockedForMatchStart() { return m_bHasStartedCountdown || m_bWaitingOnLobbyJoins; }

		public UInt16 PlaylistID { get; private set; }
		public int MinPlayers { get; private set; }
		public int DesiredPlayers { get; private set; }

		public UInt32 ExeCRC { get; private set; }
		public UInt32 IniCRC { get; private set; }

		public EKnownAnticheatID AnticheatID { get; private set; }

		public int eloExpansionIteration { get; private set; } = 1; // 1 * EloExpansionValue, matches the initial value

        public void ExpandElo()
		{
			++eloExpansionIteration;
			m_LastELOExpansionTime = DateTime.Now;
        }

		DateTime m_CreationTime = DateTime.Now;
        DateTime m_LastELOExpansionTime = DateTime.Now;

		private TimeSpan TimeSinceLastEloExpansion()
		{
            TimeSpan timeDifference = DateTime.Now - m_LastELOExpansionTime;
			return timeDifference;
        }

        public MatchmakingBucketMember? GetOwner()
		{
			if (m_lstMembers.Count > 0)
			{
				return m_lstMembers[0];
			}

			return null;
		}

		public void DetermineMap(out string strMapName, out string strMapPath)
		{
			// If they are in this bucket, they had SOME map overlap with the bucket creator, now we need to find the common ground between everyone

			// TODO_QUICKMATCH: what if we cant find a suitable map?

			// first condense the map list doesn to a list that has mutually agreed upon maps/preferences from all participants
			//var mapSetFromBucketCreator = new HashSet<int>(lstMapIndices);

			var perPlayerMapSet = new List<HashSet<int>>();
			var finalMapSet = new HashSet<int>(lstMapIndices.ToList()); // we need to check intersection against this, so pre-populate it with the original bucket creation list, because that's the "biggest set" in theory

			// TODO_QUICKMATCH: Optimize this, it's inefficient

			foreach (MatchmakingBucketMember member in m_lstMembers)
			{
				UserSession? memberSession = member.GetAssociatedSession();
				if (memberSession != null)
				{
					perPlayerMapSet.Add(new HashSet<int>(memberSession.MatchmakingMapIndicies.ToList()));
				}
			}

			// Find shared values across all of perPlayerMapSet
			if (perPlayerMapSet.Count > 0)
			{
				for (int i = 0; i < perPlayerMapSet.Count; i++)
				{
					finalMapSet.IntersectWith(perPlayerMapSet[i]);
				}
			}

			// remove any maps that aren't big enough (mainly applies to FFA's where maps may be 6 players but bucket could be 8 players)
			// NOTE: iterate a real copy, we are mutating finalMapSet below
			var copyMapSetForIter = new HashSet<int>(finalMapSet);
			foreach (int mapIndex in copyMapSetForIter)
			{
				if (MatchmakingManager.g_Playlists.TryGetValue(PlaylistID, out Playlist? playlist))
				{
					if (mapIndex >= 0 && mapIndex < playlist.Maps.Count)
					{
						if (playlist.Maps[mapIndex].MaxPlayers < CurrentMemberCount())
						{
							finalMapSet.Remove(mapIndex);
						}
					}
				}
			}

			// Randomly select a map from the map list
			if (finalMapSet.Count > 0)
			{
				// Get the playlist for this bucket
				if (MatchmakingManager.g_Playlists.TryGetValue(PlaylistID, out Playlist? playlist))
				{
					var finalMapIndices = finalMapSet.ToList();
					int selectedIndex = Random.Shared.Next(finalMapIndices.Count);
					int mapIndex = finalMapIndices[selectedIndex];

					// Defensive: ensure index is valid for playlist.Maps
					if (mapIndex >= 0 && mapIndex < playlist.Maps.Count)
					{
						strMapName = playlist.Maps[mapIndex].Name;
						strMapPath = playlist.Maps[mapIndex].Path;
						return;
					}
				}
			}
			else
			{
				// pick a sensible default (biggest map in playlist), probably not what the players asked for, but we cant play on no map
				Console.WriteLine("WARNING: No mutually agreed upon map found for matchmaking bucket, falling back to largest map in playlist");

				if (MatchmakingManager.g_Playlists.TryGetValue(PlaylistID, out Playlist? playlist))
				{
					int biggestCountSeen = 0;
					PlaylistMap? mapToUse = null;
					foreach (var map in playlist.Maps)
					{
						if (map.MaxPlayers > biggestCountSeen)
						{
							biggestCountSeen = map.MaxPlayers;
							mapToUse = map;
						}
					}

					// TODO_QUICKMATCH: What if it's still null? don't think we can get into that state since we must have some kind of map in the playlist
					if (mapToUse != null)
					{
						strMapName = mapToUse.Name;
						strMapPath = mapToUse.Path;
						return;
					}
				}
			}

			// TODO_QUICKMATCH: What happens if you widen when already in a bucket? tell the user htey cant? you would need everyone to expand, or just expand for everyone?

			// Fallback: use first map from bucket creator's list if available
			if (lstMapIndices.Count > 0 && MatchmakingManager.g_Playlists.TryGetValue(PlaylistID, out Playlist? fallbackPlaylist))
			{
				int fallbackIndex = lstMapIndices[0];
				if (fallbackIndex >= 0 && fallbackIndex < fallbackPlaylist.Maps.Count)
				{
					strMapName = fallbackPlaylist.Maps[fallbackIndex].Name;
					strMapPath = fallbackPlaylist.Maps[fallbackIndex].Path;
					return;
				}
			}

			// If no map found, set to empty
			strMapName = string.Empty;
			strMapPath = string.Empty;

		}

		public bool DoMapSelectionsIntersect(ConcurrentList<int> lstRhs)
		{
			var mapSet = new HashSet<int>(lstMapIndices.ToList());
			var mapSetRhs = new HashSet<int>(lstRhs.ToList());
			foreach (int mapIndex in mapSetRhs)
			{
				if (mapSet.Contains(mapIndex))
				{
					return true;
				}
			}
			return false;
		}

		public bool CanMergeWithOtherBucket(MatchmakingBucket bucketToMerge)
		{
			// playlist must match
			if (bucketToMerge.PlaylistID != this.PlaylistID)
			{
				return false;
			}

			// either bucket already merged away this tick? it is stale, never touch it again
			if (m_bMergedAway || bucketToMerge.m_bMergedAway)
			{
				return false;
			}

			// if we're already counting down, dont let people join, just pretend we are full
			if (IsLockedForMatchStart())
			{
				return false;
			}

			// the other bucket may have already been given a lobby this tick - its members are on their way into
			// that lobby, so pulling them in here would matchmake them into a second lobby
			if (bucketToMerge.IsLockedForMatchStart())
			{
				return false;
			}

			// must have space for all users in the rhs bucket, and CRCs must match
			if (!HasSpaceForUsers(bucketToMerge.CurrentMemberCount(), bucketToMerge.ExeCRC, bucketToMerge.IniCRC, bucketToMerge.AnticheatID))
			{
				return false;
			}

			// must be within the eloThreshold
			int eloExpansionToUse = (bucketToMerge.GetAvgElo() >= EloConfig.HighEloThreshold || GetAvgElo() >= EloConfig.HighEloThreshold) ? EloConfig.EloExpansionValue_HighELO : EloConfig.EloExpansionValue_Standard;
			if (!IsAvgEloWithinThreshold(bucketToMerge.GetAvgElo(), eloExpansionIteration * eloExpansionToUse))
			{
				return false;
			}

			// cant be blocked by any participant (or have any participant blocked)
			// TODO_OPTIMIZE: This is O(n^2)
			foreach (MatchmakingBucketMember rhsMember in bucketToMerge.m_lstMembers)
			{
				UserSession? rhsSession = rhsMember.GetAssociatedSession();

				if (rhsSession != null)
				{
                    if (IsJoiningUserBlockedByOrHasBlockedAnyBucketMember(rhsSession, rhsSession.m_UserID))
					{
						return false;
                    }
                }
            }
                

			// must have overlap in our map selections
			if (!bucketToMerge.DoMapSelectionsIntersect(this.lstMapIndices))
			{
				return false;
			}

			return true;
		}

		public async Task MergeWithOtherBucket(MatchmakingBucket bucketToMerge)
		{
			// copy over players (skipping anyone already present, and dead sessions)
			foreach (MatchmakingBucketMember rhsMember in bucketToMerge.m_lstMembers)
			{
				UserSession? rhsSession = rhsMember.GetAssociatedSession();
				if (rhsSession == null || HasPlayer(rhsSession))
				{
					continue;
				}

				this.m_lstMembers.Add(rhsMember);
			}

			// the source bucket is now empty and flagged so it can never merge/accept players again
			bucketToMerge.m_bMergedAway = true;
			bucketToMerge.m_lstMembers.Clear();

			// nothing else to copy... everything else should match since we were a merge candidate

			// tell all players
			foreach (MatchmakingBucketMember member in m_lstMembers)
			{
				UserSession? session = member.GetAssociatedSession();
				if (session != null)
				{
					await SendMatchmakingMessage(session, String.Format("Your matchmaking bucket was merged with another bucket. Status: {0}/{1} players. ({2} required to start)", CurrentMemberCount(), DesiredPlayers, MinPlayers));
				}
			}
		}

		public bool HasPlayer(UserSession playerSession)
		{
			foreach (MatchmakingBucketMember member in m_lstMembers)
			{
				if (member.Is(playerSession))
				{
					return true;
				}
			}

			return false;
		}

		public bool RemovePlayer(UserSession playerSession)
		{
			foreach (MatchmakingBucketMember member in m_lstMembers)
			{
				if (member.Is(playerSession))
				{
					m_lstMembers.Remove(member);
					return true;
				}
			}

			return false;
		}

		public int CurrentMemberCount()
		{
			return m_lstMembers.Count;
		}

		// members whose UserSession has been collected/disconnected are dead weight - they inflate the member count,
		// which both blocks the "everyone joined the lobby" check and skews the average elo
		public void PruneDeadMembers()
		{
			foreach (MatchmakingBucketMember member in m_lstMembers)
			{
				if (member.GetAssociatedSession() == null)
				{
					m_lstMembers.Remove(member);
				}
			}
		}

		// TODO_EFCORE: Shared User data, and session<->websocket could be weakrefs
		public bool IsJoiningUserBlockedByOrHasBlockedAnyBucketMember(UserSession? joiningUserSession, Int64 joining_user)
		{
			// NOTE: We check blocking in both directions, joiner blocked them, or joiner is blocked by a player
			SharedUserData? joiningUserData = joiningUserSession != null
				? GenOnlineService.WebSocketManager.GetSharedDataForUser(joiningUserSession.m_UserID)
				: null;

			foreach (MatchmakingBucketMember member in m_lstMembers)
			{
				UserSession? memberSession = member.GetAssociatedSession();
				if (memberSession != null)
				{
					SharedUserData? memberUserData = GenOnlineService.WebSocketManager.GetSharedDataForUser(memberSession.m_UserID);

					if (memberUserData != null)
					{
						// Bucket member has blocked the joining user
						if (memberUserData.GetSocialContainer().Blocked.Contains(joining_user))
							return true;

						// Joining user has blocked the bucket member
						if (joiningUserData?.GetSocialContainer().Blocked.Contains(memberSession.m_UserID) == true)
							return true;
					}
				}
			}

			return false;
		}

		public bool HasSpaceForUsers(int numUsers, UInt32 exe_crc, UInt32 ini_crc, EKnownAnticheatID anticheatID)
		{
			// stale bucket that was merged into another one
			if (m_bMergedAway)
			{
				return false;
			}

			// if we're already counting down, dont let people join, just pretend we are full
			if (IsLockedForMatchStart())
			{
				return false;
			}

			// crcs must match too
			if (exe_crc != ExeCRC || ini_crc != IniCRC)
			{
				return false;
			}

			// must be running same AC
			if (anticheatID != AnticheatID)
			{
				return false;
			}

			return numUsers <= (DesiredPlayers - m_lstMembers.Count);
		}

		public bool IsAvgEloWithinThreshold(int playerElo, int eloThreshold)
		{
            // calculate an average elo if it's a team game, othewrise average will just be the other player
            int avgElo = GetAvgElo();

            int lowerEloBound = playerElo - eloThreshold;
            int upperEloBound = playerElo + eloThreshold;

            return (avgElo >= lowerEloBound && avgElo <= upperEloBound);
        }

		public int GetAvgElo()
		{
			int numMembers = m_lstMembers.Count;

			if (numMembers == 0)
			{
				return EloConfig.BaseRating;
			}

            int avgElo = 0;
			int numContributingMembers = 0;
            foreach (MatchmakingBucketMember member in m_lstMembers)
            {
				UserSession? memberSession = member.GetAssociatedSession();
                if (memberSession != null)
                {
					SharedUserData? memberUserData = GenOnlineService.WebSocketManager.GetSharedDataForUser(memberSession.m_UserID);

					if (memberUserData != null)
					{
						avgElo += memberUserData.GameStats.EloRating;
						++numContributingMembers;
					}
                }
            }

			// only divide by the members we actually got an elo for, otherwise dead/unknown sessions drag the average down
			if (numContributingMembers == 0)
			{
				return EloConfig.BaseRating;
			}

            avgElo /= numContributingMembers;

			return avgElo;
        }


        public async Task<bool> Join(UserSession playerSession)
		{
			// already in here? nothing to do (and never add a duplicate member)
			if (HasPlayer(playerSession))
			{
				return true;
			}

			// cant be blocked by others in this bucket
			if (!IsJoiningUserBlockedByOrHasBlockedAnyBucketMember(playerSession, playerSession.m_UserID))
            {
                if (HasSpaceForUsers(1, playerSession.ExeCRC, playerSession.IniCRC, playerSession.AnticheatID))
                {
                    m_lstMembers.Add(new MatchmakingBucketMember(playerSession));

                    // tell everyone
                    foreach (MatchmakingBucketMember member in m_lstMembers)
                    {
						UserSession? memberSession = member.GetAssociatedSession();
                        if (memberSession != null)
                        {
                            await SendMatchmakingMessage(memberSession, String.Format("Status: {0}/{1} players. ({2} required to start)", CurrentMemberCount(), DesiredPlayers, MinPlayers));
                        }
                    }

                    return true;
                }
            }

            return false;
		}

		public MatchmakingBucket(UInt16 playlistID, UserSession owningSession, int minPlayers, int desiredPlayers, ConcurrentList<int> mapIndices, UInt32 exe_crc, UInt32 ini_crc, EKnownAnticheatID anticheatID)
		{
			PlaylistID = playlistID;
			MinPlayers = minPlayers;
			DesiredPlayers = desiredPlayers;
			lstMapIndices = mapIndices;
			ExeCRC = exe_crc;
			IniCRC = ini_crc;
			AnticheatID = anticheatID;

			m_lstMembers.Add(new MatchmakingBucketMember(owningSession));
		}

		public Int64 GetLobbyID()
		{
			return m_LobbyID;
		}

		Int64 m_LobbyID = -1;
		Int64 m_StartTime = -1;
		Int64 m_timeStartedWaitingOnLobbyJoins = -1;

		// how long we give everyone to actually connect to the QuickMatch lobby before we give up on the stragglers
		private const Int64 c_LobbyJoinTimeoutMSec = 45000;
		public async Task Tick()
		{
			// merged into another bucket - our members live there now, ticking would create a second lobby for them
			if (m_bMergedAway)
			{
				return;
			}

			var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();

			// drop any members whose session has gone away, otherwise they are counted forever and the bucket can
			// never reach the "everyone is in the lobby" condition
			PruneDeadMembers();

			// TODO_QUICKMATCH: What if the playlist is null? is this even possible since we validated before creating the bucket
			if (g_Playlists.TryGetValue(PlaylistID, out Playlist? playlist))
			{
				// nobody left? clean ourselves up rather than lingering as a ghost bucket
				if (CurrentMemberCount() == 0 && !m_bWaitingOnLobbyJoins && !m_bHasStartedCountdown)
				{
					MatchmakingManager.DestroyBucket(this);
					return;
				}

				// do we need to start?
				// TODO_MATCHMAKING: Add a timeout at which > min players starts
				if (!m_bWaitingOnLobbyJoins && !m_bHasStartedCountdown)
				{
					// must have a min player count
					if (MinPlayers != DesiredPlayers)
					{
						// have we hit the min player count? start a timer
						// NOTE: >= not ==, a merge (or several joins in one tick) can jump straight past MinPlayers
						if (!m_bReachedMinPlayers && CurrentMemberCount() >= MinPlayers)
						{
							m_bReachedMinPlayers = true;
							m_timeReachedMinPlayers = Environment.TickCount64;

							// tell players we reached min and will start in
							foreach (MatchmakingBucketMember member in m_lstMembers)
							{
								UserSession? memberSession = member.GetAssociatedSession();
								if (memberSession != null)
								{
									await SendMatchmakingMessage(memberSession,
										String.Format("Matchmaker has reached the minimum number of players required. Starting in {0} seconds (more players can still join in the mean time)", playlist.GracePeriodAtMinPlayersMSec / 1000));
								}
							}
						}
						else if (m_bReachedMinPlayers && CurrentMemberCount() < MinPlayers) // did we go back under min players? stop countdown
						{
							m_bReachedMinPlayers = false;
							m_timeReachedMinPlayers = -1;

							// tell players we stopped
							foreach (MatchmakingBucketMember member in m_lstMembers)
							{
								UserSession? memberSession = member.GetAssociatedSession();
								if (memberSession != null)
								{
									await SendMatchmakingMessage(memberSession, "One or more players have left which resulted in the matchmaker dropping below the minimum players required to start - the countdown to start has been cancelled");
								}
							}
						}
					}

                    // do we need an expansion of elo criteria?
                    if (TimeSinceLastEloExpansion().TotalSeconds >= EloConfig.SecondsBetweenEloExpansionsInMatchmaking)
                    {
                        // expand
                        ExpandElo();

						foreach (MatchmakingBucketMember member in m_lstMembers)
						{
							UserSession? memberSession = member.GetAssociatedSession();
							if (memberSession != null)
							{
								await SendMatchmakingMessage(memberSession, "Expanding search criteria to find more players...");
							}
						}
                    }

                    // did we hit the timer OR have enough players to start?
                    bool bMinPlayersCountdownExpired = m_bReachedMinPlayers && (Environment.TickCount64 - m_timeReachedMinPlayers) > playlist.GracePeriodAtMinPlayersMSec;
					if (bMinPlayersCountdownExpired || CurrentMemberCount() >= DesiredPlayers)
					{
						// reset min player countdown
						m_bReachedMinPlayers = false;
						m_timeReachedMinPlayers = -1;

						m_bWaitingOnLobbyJoins = true;
						m_timeStartedWaitingOnLobbyJoins = Environment.TickCount64;

						// tell everyone
						UserSession? dummyHostUser = null;
						foreach (MatchmakingBucketMember member in m_lstMembers)
						{
							UserSession? memberSession = member.GetAssociatedSession();
							if (memberSession != null)
							{
								// create lb data if necessary
								using var scope = ServiceLocator.Services.CreateScope();
								var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
								await using var db = await factory.CreateDbContextAsync();

								await Database.Leaderboards.CreateUserEntriesIfNotExists(db, memberSession.m_UserID);

								if (dummyHostUser == null)
								{
									dummyHostUser = memberSession;
								}
								await SendMatchmakingMessage(memberSession, "Creating a QuickMatch lobby for everyone...");
							}
						}

						// should have a user by now
						if (dummyHostUser != null)
						{
							SharedUserData? dummyHostUserData = GenOnlineService.WebSocketManager.GetSharedDataForUser(dummyHostUser.m_UserID);

							if (dummyHostUserData != null)
							{
								// make a lobby
								DetermineMap(out string strMapName, out string strMapPath);

								using var scope = ServiceLocator.Services.CreateScope();
								var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
								await using var db = await factory.CreateDbContextAsync();

								m_LobbyID = await lobbyManager.CreateLobby(db, dummyHostUser, dummyHostUserData.m_strDisplayName, "Quickmatch Lobby", strMapName, strMapPath + ".map",
										true, playlist.DesiredPlayers, "", 12345, false, true, 10000, false, String.Empty, -5, false, Constants.g_DefaultCameraMaxHeight, 123, 456, ELobbyType.QuickMatch,
										dummyHostUser.AnticheatID);

								// tell both to join our lobby
								WebSocketMessage_MatchmakerJoinLobby joinAction = new WebSocketMessage_MatchmakerJoinLobby();
								joinAction.msg_id = (int)EWebSocketMessageID.MATCHMAKING_ACTION_JOIN_PREARRANGED_LOBBY;
								joinAction.lobby_id = m_LobbyID;
								byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(joinAction));

								foreach (MatchmakingBucketMember member in m_lstMembers)
								{
									UserSession? memberSession = member.GetAssociatedSession();
									if (memberSession != null)
									{
										memberSession.QueueWebsocketSend(bytesJSON);
									}
								}
							}
						}
					}
				}
				else
				{
					// do we need to cancel? people might have left
					// TODO_QUICKMATCH: Re-enable
					/*
					if (m_StartTime != -1 || m_bWaitingOnLobbyJoins)
					{
						if (CurrentMemberCount() < MinPlayers)
						{
							m_bWaitingOnLobbyJoins = false;
							m_bHasStartedCountdown = false;
							m_StartTime = -1;

							// destroy lobby
							Lobby? lobby = LobbyManager.GetLobby(m_LobbyID);
							if (lobby != null)
							{
								await LobbyManager.DeleteLobby(lobby);
							}

							foreach (MatchmakingBucketMember member in m_lstMembers)
							{
								ActiveUserDataCache? memberSession = member.GetAssociatedSession();
								if (memberSession != null)
								{
									await SendMatchmakingMessage(memberSession, String.Format("A player has left and the starting countdown has been cancelled. Status: {0}/{1} players. ({2} required to start)", CurrentMemberCount(), DesiredPlayers, MinPlayers));
								}
							}

							return;
						}
					}
					*/

					// waiting on lobby joins?
					if (m_bWaitingOnLobbyJoins)
					{
						// done? start time etc
						Lobby? lobby = lobbyManager.GetLobby(m_LobbyID);
						if (lobby == null)
						{
							// the lobby went away underneath us (deleted/failed) - don't leave everyone stuck waiting forever
							Console.WriteLine("Matchmaking bucket lost its QuickMatch lobby {0} while waiting on joins, abandoning bucket", m_LobbyID);

							foreach (MatchmakingBucketMember member in m_lstMembers)
							{
								UserSession? memberSession = member.GetAssociatedSession();
								if (memberSession != null)
								{
									await SendMatchmakingMessage(memberSession, "The QuickMatch lobby could not be created. Please try matchmaking again.");
								}
							}

							m_bWaitingOnLobbyJoins = false;
							m_timeStartedWaitingOnLobbyJoins = -1;
							MatchmakingManager.DestroyBucket(this);
							return;
						}

						// has someone failed to connect? drop them so the rest of the players aren't stuck here forever
						bool bJoinTimeoutExpired = m_timeStartedWaitingOnLobbyJoins != -1
							&& (Environment.TickCount64 - m_timeStartedWaitingOnLobbyJoins) > c_LobbyJoinTimeoutMSec;

						if (bJoinTimeoutExpired && lobby.NumCurrentPlayers != CurrentMemberCount())
						{
							foreach (MatchmakingBucketMember member in m_lstMembers)
							{
								UserSession? memberSession = member.GetAssociatedSession();
								if (memberSession == null || lobby.GetMemberFromUserID(memberSession.m_UserID) == null)
								{
									if (memberSession != null)
									{
										Console.WriteLine("User {0} failed to join QuickMatch lobby {1} in time, dropping from bucket", memberSession.m_UserID, m_LobbyID);
										await SendMatchmakingMessage(memberSession, "You failed to join the QuickMatch lobby in time and have been removed from matchmaking.");
									}

									m_lstMembers.Remove(member);
								}
							}

							// not enough players survived? tear the whole thing down and let people re-queue
							if (CurrentMemberCount() < MinPlayers)
							{
								foreach (MatchmakingBucketMember member in m_lstMembers)
								{
									UserSession? memberSession = member.GetAssociatedSession();
									if (memberSession != null)
									{
										await SendMatchmakingMessage(memberSession, "Not enough players joined the QuickMatch lobby. Please try matchmaking again.");
									}
								}

								m_bWaitingOnLobbyJoins = false;
								m_timeStartedWaitingOnLobbyJoins = -1;

								await lobbyManager.DeleteLobby(lobby);
								MatchmakingManager.DestroyBucket(this);
								return;
							}
						}

						{
							if (lobby.NumCurrentPlayers == CurrentMemberCount()) // everyone is in, lets start for real
							{
								m_timeStartedWaitingOnLobbyJoins = -1;

								// wait 5 sec
								m_StartTime = Environment.TickCount64 + 5000;
								foreach (MatchmakingBucketMember member in m_lstMembers)
								{
									UserSession? memberSession = member.GetAssociatedSession();
									if (memberSession != null)
									{
										await SendMatchmakingMessage(memberSession, $"Starting Game in 5 seconds");
									}
								}

								m_bWaitingOnLobbyJoins = false;
								m_bHasStartedCountdown = true;

								// finalize the teams
								const int playlistMaxPlayerPerTeam = 2;
								bool bIsFFA = true;
								int numTeams = lobby.NumCurrentPlayers / playlistMaxPlayerPerTeam;

								int teamID = 0;
								foreach (LobbyMember member in lobby.Members)
								{
									if (bIsFFA)
									{
										member.UpdateTeam(-1);
									}
									else
									{
										member.UpdateTeam(teamID);

										teamID++;

										if (teamID >= numTeams)
										{
											teamID = 0;
										}
									}
								}
							}
						}

					}

					// TODO_QUICKMATCH: Do full mesh connectivity check + handle not being connected
					// do we have a countdown?
					if (m_StartTime != -1)
					{
						if (Environment.TickCount64 >= m_StartTime)
						{
							m_StartTime = -1;

							Console.WriteLine("START GAME");

							// send start
							WebSocketMessage_MatchmakerStartGame startGameAction = new WebSocketMessage_MatchmakerStartGame();
							startGameAction.msg_id = (int)EWebSocketMessageID.MATCHMAKING_ACTION_START_GAME;
							byte[] bytesJSON = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(startGameAction));

							foreach (MatchmakingBucketMember member in m_lstMembers)
							{
								UserSession? memberSession = member.GetAssociatedSession();
								if (memberSession != null)
								{
									memberSession.QueueWebsocketSend(bytesJSON);
								}
							}

							// start match + create placeholder match
							Lobby? lobby = lobbyManager.GetLobby(m_LobbyID);
							if (lobby != null)
							{
								await lobby.UpdateState(ELobbyState.INGAME);
							}

							// destroy the bucket
							MatchmakingManager.DestroyBucket(this);
						}
					}
				}
			}
		}

		// TODO_MATCHMAKING: Delete buckets if participants becomes 0
	}
	// Using ConcurrentBag instead of ConcurrentList for lock-free bucket management
	private static ConcurrentDictionary<UInt16, ConcurrentBag<MatchmakingBucket>> m_dictMatchmakingBuckets = new();

	// TODO_QUICKMATCH: Read from db or file
	private static Dictionary<UInt16, Playlist> g_Playlists = new()
	{
		{ 0, new Playlist(0, "1v1 (Random Armies)", 2, 2, 8, false, -1, false, 0, new List<PlaylistMap>()
			{
				new PlaylistMap("[RANK] Snowy Drought ZH v5 (2)", "[RANK] Snowy Drought ZH v5", true, 2),
				new PlaylistMap("[RANK] Natural Threats ZH v4 (2)", "[RANK] Natural Threats ZH v4", true, 2),
				new PlaylistMap("[RANK] Arctic Lagoon ZH v2 (2)", "[RANK] Arctic Lagoon ZH v2", true, 2),
				new PlaylistMap("[RANK] ZH Carrier is Over v2 (2)", "[RANK] ZH Carrier is Over v2", true, 2),
				new PlaylistMap("[RANK] Vendetta ZH v1 (2)", "[RANK] Vendetta ZH v1", true, 2),
				new PlaylistMap("[RANK] TD NoBugsCars ZH v1 (2)", "[RANK] TD NoBugsCars ZH v1", true, 2),
				new PlaylistMap("[RANK] Sand Scorpion (2)", "[RANK] Sand Scorpion", true, 2),
				new PlaylistMap("[RANK] Mountain Mayhem v2 (2)", "[RANK] Mountain Mayhem v2", true, 2),
				new PlaylistMap("[RANK] Liquid Gold ZH v2 (2)", "[RANK] Liquid Gold ZH v2", true, 2),
				new PlaylistMap("[RANK] Imminent Victory ZH v2 (2)", "[RANK] Imminent Victory ZH v2", true, 2),
				new PlaylistMap("[RANK] Egyptian Oasis ZH v1 (2)", "[RANK] Egyptian Oasis ZH v1", true, 2),
				new PlaylistMap("[RANK] Desolated District ZH v1 (2)", "[RANK] Desolated District ZH v1", true, 2),
				new PlaylistMap("[RANK] Canyon of the Dead ZH v2 (2)", "[RANK] Canyon of the Dead ZH v2", true, 2),
				new PlaylistMap("[RANK] Blossoming Valley ZH v1 (2)", "[RANK] Blossoming Valley ZH v1", true, 2),
				//new PlaylistMap("[RANK] Battle Plan ZH v1 (2)", "[RANK] Battle Plan ZH v1", true, 2), // NOTE: Disabled. OldA say it is bugged according to map creator.
				new PlaylistMap("[RANK] Barren Badlands Balanced ZH v2 (2)", "[RANK] Barren Badlands Balanced ZH v2", true, 2),

				// new maps added in 12_05_25 update
				new PlaylistMap("Battle Plan ZH v3", "Battle Plan ZH v3", true, 2),
                new PlaylistMap("Canyon Frost ZH v1", "Canyon Frost ZH v1", true, 2),
                new PlaylistMap("Koujou Okawa v3", "Koujou Okawa v3", true, 2),
                new PlaylistMap("Oxygen 1", "Oxygen 1", true, 2),
                new PlaylistMap("Shivas Paradise v4", "Shivas Paradise v4", true, 2),
                new PlaylistMap("Thermopylae v5", "Thermopylae v5", true, 2),
                new PlaylistMap("Tiny Tactics ZH v2", "Tiny Tactics ZH v2", true, 2),
                new PlaylistMap("yota nation arena v3", "yota nation arena v3", true, 2),
                new PlaylistMap("[RANK] AKAs Magic ZH v1", "[RANK] AKAs Magic ZH v1", true, 2),
                new PlaylistMap("[RANK] Arctic Arena ZH v1", "[RANK] Arctic Arena ZH v1", true, 2),
                new PlaylistMap("[RANK] Black Hell ZH v1", "[RANK] Black Hell ZH v1", true, 2),
                new PlaylistMap("[RANK] Blue Hole ZH v1", "[RANK] Blue Hole ZH v1", true, 2),
                new PlaylistMap("[RANK] Dammed Scorpion ZH v1", "[RANK] Dammed Scorpion ZH v1", true, 2),
                new PlaylistMap("[RANK] Drallim Desert ZH v2", "[RANK] Drallim Desert ZH v2", true, 2),
                new PlaylistMap("[RANK] Farmlands of the Fallen ZH v1", "[RANK] Farmlands of the Fallen ZH v1", true, 2),
                new PlaylistMap("[RANK] Sakura Forest II ZH v1", "[RANK] Sakura Forest II ZH v1", true, 2),
                new PlaylistMap("[RANK] Sovereignty ZH v1", "[RANK] Sovereignty ZH v1", true, 2),

				// new maps added in 4_28_26 EAC update (for WS / from GR)
				new PlaylistMap("Arabia v2 (2)", "Arabia v2", true, 2),
				new PlaylistMap("SandForest Domination v5 (2)", "SandForest Domination v5", true, 2),
				new PlaylistMap("Toxic Lake v4 (2)", "Toxic Lake v4", true, 2),
				new PlaylistMap("Tremble v2 (2)", "Tremble v2", true, 2),
				new PlaylistMap("[RANK] Hanamura Temple ZH v1 (2)", "[RANK] Hanamura Temple ZH v1", true, 2),
			}
		) },

		/* PRE-WS list:
		 * { 0, new Playlist(0, "1v1 (Random Armies)", 2, 2, 1, false, -1, false, 0, new List<PlaylistMap>()
			{
				new PlaylistMap("[RANK] Snowy Drought ZH v5 (2)", "[RANK] Snowy Drought ZH v5", true, 2),
				new PlaylistMap("[RANK] Natural Threats ZH v4 (2)", "[RANK] Natural Threats ZH v4", true, 2),
				new PlaylistMap("[RANK] Arctic Lagoon ZH v2 (2)", "[RANK] Arctic Lagoon ZH v2", true, 2),
				new PlaylistMap("[RANK] ZH Carrier is Over v2 (2)", "[RANK] ZH Carrier is Over v2", true, 2),
				new PlaylistMap("[RANK] Vendetta ZH v1 (2)", "[RANK] Vendetta ZH v1", true, 2),
				new PlaylistMap("[RANK] TD NoBugsCars ZH v1 (2)", "[RANK] TD NoBugsCars ZH v1", true, 2),
				new PlaylistMap("[RANK] Sand Scorpion (2)", "[RANK] Sand Scorpion", true, 2),
				new PlaylistMap("[RANK] Mountain Mayhem v2 (2)", "[RANK] Mountain Mayhem v2", true, 2),
				new PlaylistMap("[RANK] Liquid Gold ZH v2 (2)", "[RANK] Liquid Gold ZH v2", true, 2),
				new PlaylistMap("[RANK] Imminent Victory ZH v2 (2)", "[RANK] Imminent Victory ZH v2", true, 2),
				new PlaylistMap("[RANK] Egyptian Oasis ZH v1 (2)", "[RANK] Egyptian Oasis ZH v1", true, 2),
				new PlaylistMap("[RANK] Desolated District ZH v1 (2)", "[RANK] Desolated District ZH v1", true, 2),
				new PlaylistMap("[RANK] Canyon of the Dead ZH v2 (2)", "[RANK] Canyon of the Dead ZH v2", true, 2),
				new PlaylistMap("[RANK] Blossoming Valley ZH v1 (2)", "[RANK] Blossoming Valley ZH v1", true, 2),
				//new PlaylistMap("[RANK] Battle Plan ZH v1 (2)", "[RANK] Battle Plan ZH v1", true, 2), // NOTE: Disabled. OldA say it is bugged according to map creator.
				new PlaylistMap("[RANK] Barren Badlands Balanced ZH v2 (2)", "[RANK] Barren Badlands Balanced ZH v2", true, 2),

				// new maps added in 12_05_25 update
				new PlaylistMap("Battle Plan ZH v3", "Battle Plan ZH v3", true, 2),
                new PlaylistMap("Canyon Frost ZH v1", "Canyon Frost ZH v1", true, 2),
                new PlaylistMap("Koujou Okawa v3", "Koujou Okawa v3", true, 2),
                new PlaylistMap("Oxygen 1", "Oxygen 1", true, 2),
                new PlaylistMap("Shivas Paradise v4", "Shivas Paradise v4", true, 2),
                new PlaylistMap("Thermopylae v5", "Thermopylae v5", true, 2),
                new PlaylistMap("Tiny Tactics ZH v2", "Tiny Tactics ZH v2", true, 2),
                new PlaylistMap("yota nation arena v3", "yota nation arena v3", true, 2),
                new PlaylistMap("[RANK] AKAs Magic ZH v1", "[RANK] AKAs Magic ZH v1", true, 2),
                new PlaylistMap("[RANK] Arctic Arena ZH v1", "[RANK] Arctic Arena ZH v1", true, 2),
                new PlaylistMap("[RANK] Black Hell ZH v1", "[RANK] Black Hell ZH v1", true, 2),
                new PlaylistMap("[RANK] Blue Hole ZH v1", "[RANK] Blue Hole ZH v1", true, 2),
                new PlaylistMap("[RANK] Dammed Scorpion ZH v1", "[RANK] Dammed Scorpion ZH v1", true, 2),
                new PlaylistMap("[RANK] Drallim Desert ZH v2", "[RANK] Drallim Desert ZH v2", true, 2),
                new PlaylistMap("[RANK] Farmlands of the Fallen ZH v1", "[RANK] Farmlands of the Fallen ZH v1", true, 2),
                new PlaylistMap("[RANK] Sakura Forest II ZH v1", "[RANK] Sakura Forest II ZH v1", true, 2),
                new PlaylistMap("[RANK] Sovereignty ZH v1", "[RANK] Sovereignty ZH v1", true, 2),

				// new maps added in 4_28_26 EAC update (for WS / from GR)
				new PlaylistMap("Arabia v2 (2)", "Arabia v2", true, 2),
				new PlaylistMap("SandForest Domination v5 (2)", "SandForest Domination v5", true, 2),
				new PlaylistMap("Toxic Lake v4 (2)", "Toxic Lake v4", true, 2),
				new PlaylistMap("Tremble v2 (2)", "Tremble v2", true, 2),
				new PlaylistMap("[RANK] Hanamura Temple ZH v1 (2)", "[RANK] Hanamura Temple ZH v1", true, 2),
			}
		) },
		*/

		{ 1, new Playlist(1, "6-8P FFA (Random Armies)", 6, 8, 1, false, -1, false, 30000, new List<PlaylistMap>()
			{
				new PlaylistMap("Defcon6 (6)", "Defcon6", false, 6),
				new PlaylistMap("Beijing Uprise v4 (6)", "Beijing Uprise v4", true, 6),
				new PlaylistMap("Taiga Terror (6)", "Taiga Terror", true, 6),
				new PlaylistMap("Swamp Assault v3 [WBC2021] (7)", "Swamp Assault v3 [WBC2021]", true, 7),
				new PlaylistMap("[RANK] Muddy Madness ZH v1 (8)", "[RANK] Muddy Madness ZH v1", true, 8),
				new PlaylistMap("[RANK] Wastelands Dust ZH v1 (8)", "[RANK] Wastelands Dust ZH v1", true, 8)
			}
		) }
	};

	public static Dictionary<UInt16, Playlist> GetPlaylists() { return g_Playlists; }

	public static int GetTotalQueuedPlayersInPlaylist(UInt16 playlistID)
	{
		int totalPlayers = 0;

		if (m_dictMatchmakingBuckets.ContainsKey(playlistID))
		{
			foreach (MatchmakingBucket bucket in m_dictMatchmakingBuckets[playlistID])
			{
				totalPlayers += bucket.CurrentMemberCount();
			}
		}

		return totalPlayers;
	}

	public static async Task Tick()
	{
		// TODO_QUICKMATCH: Move to init func, maybe dont use static for matchmakingmanager
		if (m_dictMatchmakingBuckets.Count == 0)
		{
			foreach (var kvPair in g_Playlists)
			{
				m_dictMatchmakingBuckets.TryAdd(kvPair.Key, new ConcurrentBag<MatchmakingBucket>());
			}
		}

		// tick mm buckets
		// TODO_QUICKMATCH: This is slow
		List<MatchmakingBucket> lstBucketsMergedNeedingDeleted = new();
		foreach (var kvPair in m_dictMatchmakingBuckets)
		{
			foreach (MatchmakingBucket mmBucket in kvPair.Value)
			{
				// if we've already been merged and are awaiting delayed deletion, dont process it anymore
				if (!lstBucketsMergedNeedingDeleted.Contains(mmBucket) && !mmBucket.IsMergedAway())
				{
					await mmBucket.Tick();

					// try to merge with any other bucket within this playlist
					foreach (MatchmakingBucket mmBucketMergeCandidate in kvPair.Value)
					{
						if (mmBucketMergeCandidate != mmBucket)
						{
							// if either bucket has already been merged and is awaiting delayed deletion, dont process it anymore
							if (lstBucketsMergedNeedingDeleted.Contains(mmBucket) || mmBucket.IsMergedAway())
							{
								break;
							}

							if (!lstBucketsMergedNeedingDeleted.Contains(mmBucketMergeCandidate) && !mmBucketMergeCandidate.IsMergedAway())
							{
								if (mmBucket.CanMergeWithOtherBucket(mmBucketMergeCandidate))
								{
									await mmBucket.MergeWithOtherBucket(mmBucketMergeCandidate);

									lstBucketsMergedNeedingDeleted.Add(mmBucketMergeCandidate);
								}
							}
						}
					}
				}
			}
		}

		// queue for deletion
		m_lstBucketsPendingDeletion.AddRange(lstBucketsMergedNeedingDeleted);

		// cleanup any pending destruction (cannot do this in tick, collection will be modified)
		foreach (MatchmakingBucket bucket in m_lstBucketsPendingDeletion)
		{
			if (m_dictMatchmakingBuckets.TryGetValue(bucket.PlaylistID, out var bucketBag))
			{
				// ConcurrentBag doesn't support Remove, so we filter and rebuild
				var remainingBuckets = bucketBag.Where(b => b != bucket).ToList();
				m_dictMatchmakingBuckets[bucket.PlaylistID] = new ConcurrentBag<MatchmakingBucket>(remainingBuckets);
			}
		}
		m_lstBucketsPendingDeletion.Clear();

		List<WeakReference<UserSession>> lstDestroy = new();
		foreach (WeakReference<UserSession> wrSession in lstSessions)
		{
			if (!wrSession.TryGetTarget(out UserSession? thisSession) || thisSession == null)
			{
				lstDestroy.Add(wrSession);
			}
			else
			{
				SharedUserData? thisSessionUserData = GenOnlineService.WebSocketManager.GetSharedDataForUser(thisSession.m_UserID);

				if (thisSessionUserData == null)
				{
					lstDestroy.Add(wrSession);
				}
				else
				{
					if (g_Playlists.TryGetValue(thisSession.MatchmakingPlaylistID, out Playlist? playlist))
					{

						// TODO_MATCHAMAKING: Better way of tracking this, we need to know who is already in a bucket
						// Was the user in a bucket? if so theres nothing to do in terms of bucket management
						bool bUseInBucket = false;
						MatchmakingBucket? mmBucketUserIsIn = null;
						foreach (var kvBucketPair in m_dictMatchmakingBuckets)
						{
							foreach (MatchmakingBucket mmBucket in kvBucketPair.Value)
							{
								if (mmBucket.HasPlayer(thisSession))
								{
									bUseInBucket = true;
									mmBucketUserIsIn = mmBucket;
									break;
								}
							}

							if (bUseInBucket)
							{
								break;
							}
						}

						if (bUseInBucket)
						{
							// already sorted into a bucket - stop tracking this session here, otherwise a stale entry
							// can sort the same player into a second bucket (and therefore a second lobby) later on
							lstDestroy.Add(wrSession);
						}
						else
						{
							// is there a suitable bucket for us
							// TODO_MATCHMAKING: Optimize lookup
							if (m_dictMatchmakingBuckets.ContainsKey(thisSession.MatchmakingPlaylistID))
							{
								MatchmakingBucket? bucketInUse = null;
								foreach (MatchmakingBucket mmBucket in m_dictMatchmakingBuckets[thisSession.MatchmakingPlaylistID])
								{
									if (mmBucket.IsMergedAway())
									{
										continue;
									}

									// must be within initial elo threshold for a join, otherwise we'll make a bucket and try to merge buckets using the elo iteration expansion algorithm
									int eloExpansionToUse = (mmBucket.GetAvgElo() >= EloConfig.HighEloThreshold || thisSessionUserData.GameStats.EloRating >= EloConfig.HighEloThreshold) ? EloConfig.EloExpansionValue_HighELO : EloConfig.EloExpansionValue_Standard;
									if (mmBucket.IsAvgEloWithinThreshold(thisSessionUserData.GameStats.EloRating, eloExpansionToUse))
									{
										// TODO_MATCHMAKING: Squads
										if (mmBucket.HasSpaceForUsers(1, thisSession.ExeCRC, thisSession.IniCRC, thisSession.AnticheatID))
										{
											// do the maps overlap? if so we can join
											if (mmBucket.DoMapSelectionsIntersect(thisSession.MatchmakingMapIndicies))
											{
												bool bJoined = await mmBucket.Join(thisSession);

												if (bJoined)
												{
													// stop looking - joining more than one bucket would matchmake this player into multiple lobbies
													bucketInUse = mmBucket;
													break;
												}
											}
										}
									}
								}

								// didnt find a bucket? make one
								if (bucketInUse == null)
								{
									MatchmakingBucket newBucket = new MatchmakingBucket(playlist.PlaylistID, thisSession, playlist.MinPlayers, playlist.DesiredPlayers, thisSession.MatchmakingMapIndicies, thisSession.ExeCRC, thisSession.IniCRC, thisSession.AnticheatID);
									m_dictMatchmakingBuckets[thisSession.MatchmakingPlaylistID].Add(newBucket);
									bucketInUse = newBucket;
								}

								// send status to use
								await SendMatchmakingMessage(thisSession, String.Format("You are now matchmaking in playlist \"{0}\". There are currently {1} player(s) searching for a match in this playlist", playlist.Name, GetTotalQueuedPlayersInPlaylist(playlist.PlaylistID)));
								await SendMatchmakingMessage(thisSession, String.Format("Status: {0}/{1} players. ({2} required to start)", bucketInUse.CurrentMemberCount(), bucketInUse.DesiredPlayers, bucketInUse.MinPlayers));

								// now remove us from lstSessions, this list is essentially people who need sorted into a bucket
								lstDestroy.Add(wrSession);
							}
						}
					}
					else
					{
						// invalid playlist somehow
						lstDestroy.Add(wrSession);
					}
				}
			}
		}

		// now remove
		foreach (WeakReference<UserSession> wrSession in lstDestroy)
		{
			if (wrSession != null)
			{
				lstSessions.Remove(wrSession);
			}
		}
	}

	// TODO_MATCHMAKING: Deregister player if they disconnect or leave quickmatch
	private static ConcurrentList<WeakReference<UserSession>> lstSessions = new();

	private static ConcurrentList<MatchmakingBucket> m_lstBucketsPendingDeletion = new();
	public static void DestroyBucket(MatchmakingBucket bucket)
	{
		m_lstBucketsPendingDeletion.Add(bucket);
	}

	public static async Task RegisterPlayer(UserSession plr, UInt16 playlistID, List<int> mapIndices, UInt32 exe_crc, UInt32 ini_crc, EKnownAnticheatID anticheatID)
	{
		// validate the request - a bad playlist or out of range map index from a client must never reach a bucket
		if (!g_Playlists.TryGetValue(playlistID, out Playlist? playlist))
		{
			await SendMatchmakingMessage(plr, "That playlist is not available. Matchmaking was not started.");
			return;
		}

		List<int> validatedMapIndices = mapIndices
			.Where(mapIndex => mapIndex >= 0 && mapIndex < playlist.Maps.Count)
			.Distinct()
			.ToList();

		int minSelectedMaps = Math.Max(1, playlist.MinSelectedMaps);
		if (validatedMapIndices.Count < minSelectedMaps)
		{
			await SendMatchmakingMessage(plr, String.Format("You must select at least {0} valid map(s) to matchmake in this playlist.", minSelectedMaps));
			return;
		}

		// make sure a re-register (or a duplicate request) cannot leave the player queued twice, or queued while
		// still sat in an existing bucket - either would matchmake them into two lobbies at once
		RemoveSessionFromPendingList(plr);
		RemovePlayerFromAllBuckets(plr);

		plr.MatchmakingPlaylistID = playlistID;
		plr.MatchmakingMapIndicies = new ConcurrentList<int>(validatedMapIndices);
		plr.ExeCRC = exe_crc;
		plr.IniCRC = ini_crc;
		plr.AnticheatID = anticheatID;
		lstSessions.Add(new WeakReference<UserSession>(plr));

        await SendMatchmakingMessage(plr, "Started matchmaking... Searching for players...");
	}

	// NOTE: WeakReference does not implement value equality, so entries must be matched by their target session
	private static void RemoveSessionFromPendingList(UserSession plr)
	{
		foreach (WeakReference<UserSession> wrSession in lstSessions)
		{
			if (!wrSession.TryGetTarget(out UserSession? thisSession) || thisSession == null || thisSession == plr)
			{
				lstSessions.Remove(wrSession);
			}
		}
	}

	private static void RemovePlayerFromAllBuckets(UserSession plr)
	{
		var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();

		// also remove from any bucket we are in to avoid ghost buckets
		foreach (var kvPair in m_dictMatchmakingBuckets)
		{
			foreach (MatchmakingBucket mmBucket in kvPair.Value)
			{
				if (mmBucket.HasPlayer(plr))
				{
					// remove from QM lobby too
					Lobby? lobby = lobbyManager.GetLobby(mmBucket.GetLobbyID());
					if (lobby != null)
					{
						LobbyMember? lobbyMember = lobby.GetMemberFromUserID(plr.m_UserID);
						if (lobbyMember != null)
						{
							Console.WriteLine("User {0} Leave MM Lobby", plr.m_UserID);
							lobby.RemoveMember(lobbyMember);
						}
					}

					// remove player
					mmBucket.RemovePlayer(plr);

					// if we're the last player, destroy the bucket
					if (mmBucket.CurrentMemberCount() == 0)
					{
						DestroyBucket(mmBucket);
					}
				}
			}
		}
	}

	public static void DeregisterPlayer(UserSession plr)
	{
		var lobbyManager = ServiceLocator.Services.GetRequiredService<LobbyManager>();
		RemoveSessionFromPendingList(plr);

		// TODO_QUICKMATCH: What happens if the game is going to start? we should handle that, right now people probably goto game solo

		RemovePlayerFromAllBuckets(plr);

		// leave QM lobby too
		Console.WriteLine("[Source 4] User {0} Leave Any Lobby", plr.m_UserID);
		lobbyManager.LeaveAnyLobby(plr.m_UserID);
	}
}