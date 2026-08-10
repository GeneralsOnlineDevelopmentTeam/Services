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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class FriendEntry
{
	public long UserId1 { get; set; }
	public long UserId2 { get; set; }
}
public class BlockedUserEntry
{
	public long SourceUserId { get; set; }
	public long TargetUserId { get; set; }
}

public class FriendRequestEntry
{
	public long SourceUserId { get; set; }
	public long TargetUserId { get; set; }
}

public class FriendConfiguration : IEntityTypeConfiguration<FriendEntry>
{
	public void Configure(EntityTypeBuilder<FriendEntry> builder)
	{
		builder.ToTable("friends");

		builder.HasKey(f => new { f.UserId1, f.UserId2 });

		builder.Property(f => f.UserId1)
			.HasColumnName("user_id_1");

		builder.Property(f => f.UserId2)
			.HasColumnName("user_id_2");
	}
}

public class FriendRequestConfiguration : IEntityTypeConfiguration<FriendRequestEntry>
{
	public void Configure(EntityTypeBuilder<FriendRequestEntry> builder)
	{
		builder.ToTable("friends_requests");

		builder.HasKey(f => new { f.SourceUserId, f.TargetUserId });

		builder.Property(f => f.SourceUserId)
			.HasColumnName("source_user_id");

		builder.Property(f => f.TargetUserId)
			.HasColumnName("target_user_id");
	}
}


public class BlockedUserConfiguration : IEntityTypeConfiguration<BlockedUserEntry>
{
	public void Configure(EntityTypeBuilder<BlockedUserEntry> builder)
	{
		builder.ToTable("friends_blocked");

		builder.HasKey(f => new { f.SourceUserId, f.TargetUserId });

		builder.Property(f => f.SourceUserId)
			.HasColumnName("source_user_id");

		builder.Property(f => f.TargetUserId)
			.HasColumnName("target_user_id");
	}
}




namespace Database
{
	public static class Social
	{
		private static readonly Func<AppDbContext, long, IAsyncEnumerable<FriendEntry>> _getFriends =
		EF.CompileAsyncQuery(
			(AppDbContext db, long userId) =>
				db.Friends
				  .Where(f => f.UserId1 == userId || f.UserId2 == userId)
		);

		private static readonly Func<AppDbContext, long, Task<int>> _countFriends =
		EF.CompileAsyncQuery(
			(AppDbContext db, long userId) =>
				db.Friends
				  .Count(f => f.UserId1 == userId || f.UserId2 == userId)
		);

		private static readonly Func<AppDbContext, long, IAsyncEnumerable<long>> _getBlocked =
		EF.CompileAsyncQuery(
			(AppDbContext db, long userId) =>
				db.BlockedUsers
				  .Where(b => b.SourceUserId == userId)
				  .Select(b => b.TargetUserId)
		);
		private static readonly Func<AppDbContext, long, IAsyncEnumerable<long>> _getPendingRequests =
		EF.CompileAsyncQuery(
			(AppDbContext db, long targetUserId) =>
				db.FriendRequests
				  .Where(r => r.TargetUserId == targetUserId)
				  .Select(r => r.SourceUserId)
		);




		public static async Task<HashSet<long>> GetFriends(AppDbContext db, long userId)
		{
			HashSet<long> result = new();

			try
			{
				await foreach (var f in _getFriends(db, userId))
				{
					result.Add(f.UserId1 == userId ? f.UserId2 : f.UserId1);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] GetFriends failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}

			return result;
		}


		public static async Task<int> CountFriends(AppDbContext db, long userId)
		{
			try
			{
				return await _countFriends(db, userId);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] CountFriends failed: {ex.Message}");
				SentrySdk.CaptureException(ex);

				// Treat an unreadable count as full so the friends limit is never bypassed.
				return int.MaxValue;
			}
		}


		public static async Task<HashSet<long>> GetBlocked(AppDbContext db, long sourceUserId)
		{
			HashSet<long> result = new();

			try
			{
				await foreach (var id in _getBlocked(db, sourceUserId))
					result.Add(id);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] GetBlocked failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}

			return result;
		}


		public static async Task<HashSet<long>> GetPendingFriendsRequests(AppDbContext db, long targetUserId)
		{
			HashSet<long> result = new();

			try
			{
				await foreach (var id in _getPendingRequests(db, targetUserId))
					result.Add(id);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] GetPendingFriendsRequests failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}

			return result;
		}


		public static async Task RemovePendingFriendRequest(AppDbContext db, long sourceUserId, long targetUserId)
		{
			try
			{
				await db.FriendRequests
					.Where(r =>
						(r.SourceUserId == sourceUserId && r.TargetUserId == targetUserId) ||
						(r.SourceUserId == targetUserId && r.TargetUserId == sourceUserId))
					.ExecuteDeleteAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] RemovePendingFriendRequest failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		public static async Task CreateFriendship(AppDbContext db, long userId1, long userId2)
		{
			try
			{
				// Check if friendship already exists (handles duplicate calls / race conditions)
				bool alreadyExists = await db.Friends.AnyAsync(f =>
					(f.UserId1 == userId1 && f.UserId2 == userId2) ||
					(f.UserId1 == userId2 && f.UserId2 == userId1));

				if (alreadyExists)
					return;

				db.Friends.Add(new FriendEntry
				{
					UserId1 = userId1,
					UserId2 = userId2
				});

				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				// If two concurrent calls both passed the existence check, MySQL will throw a
				// duplicate-key error (ER_DUP_ENTRY, code 1062). The friendship was already
				// created by the other call, so this is not an error worth reporting.
				if (ex.InnerException is MySqlConnector.MySqlException mysqlEx && mysqlEx.Number == 1062)
					return;

				Console.WriteLine($"[ERROR] CreateFriendship failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		public static async Task RemoveFriendship(AppDbContext db, long userId1, long userId2)
		{
			try
			{
				await db.Friends
					.Where(f =>
						(f.UserId1 == userId1 && f.UserId2 == userId2) ||
						(f.UserId1 == userId2 && f.UserId2 == userId1))
					.ExecuteDeleteAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] RemoveFriendship failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		public static async Task AddBlock(AppDbContext db, long sourceUserId, long targetUserId)
		{
			try
			{
				db.BlockedUsers.Add(new BlockedUserEntry
				{
					SourceUserId = sourceUserId,
					TargetUserId = targetUserId
				});

				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] AddBlock failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		public static async Task RemoveBlock(AppDbContext db, long sourceUserId, long targetUserId)
		{
			try
			{
				await db.BlockedUsers
					.Where(b => b.SourceUserId == sourceUserId && b.TargetUserId == targetUserId)
					.ExecuteDeleteAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] RemoveBlock failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}

		public static async Task AddPendingFriendRequest(AppDbContext db, long sourceUserId, long targetUserId)
		{
			try
			{
				db.FriendRequests.Add(new FriendRequestEntry
				{
					SourceUserId = sourceUserId,
					TargetUserId = targetUserId
				});

				await db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ERROR] AddPendingFriendRequest failed: {ex.Message}");
				SentrySdk.CaptureException(ex);
			}
		}


	}
}