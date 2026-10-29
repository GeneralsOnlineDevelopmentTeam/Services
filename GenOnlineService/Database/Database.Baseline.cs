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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

#pragma warning disable EF1001 // ProductInfo is the same value the generated migrations history rows use

public sealed class BaselinePlan
{
	// Set when the database cannot be baselined
	public string? Error { get; init; }
	public string MigrationId { get; init; } = string.Empty;
	public int ExpectedTableCount { get; init; }
	public int ExpectedColumnCount { get; init; }
	public int ExpectedIndexCount { get; init; }
}

// Databases created from the old structure.sql have the first migration's schema but no migration history.
// Baselining records that migration as applied without running it.
public static class MigrationBaseline
{
	private sealed record ExpectedColumn(string Name, bool Nullable);
	private sealed record ExpectedTable(string Name, List<ExpectedColumn> Columns, List<string> Indexes);

	private sealed class ColumnRow
	{
		public string TableName { get; set; } = string.Empty;
		public string ColumnName { get; set; } = string.Empty;
		public string IsNullable { get; set; } = string.Empty;
	}

	private sealed class IndexRow
	{
		public string TableName { get; set; } = string.Empty;
		public string IndexName { get; set; } = string.Empty;
	}

	// Read from the migration itself so it can never drift from the model
	private static (string MigrationId, List<ExpectedTable> Tables) GetFirstMigration(AppDbContext db)
	{
		var migrationsAssembly = db.GetService<IMigrationsAssembly>();

		string migrationId = migrationsAssembly.Migrations.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
		Migration migration = migrationsAssembly.CreateMigration(migrationsAssembly.Migrations[migrationId], db.Database.ProviderName!);

		var indexesByTable = migration.UpOperations.OfType<CreateIndexOperation>()
			.ToLookup(o => o.Table, StringComparer.OrdinalIgnoreCase);

		var tables = migration.UpOperations.OfType<CreateTableOperation>()
			.Select(t => new ExpectedTable(
				t.Name,
				t.Columns.Select(c => new ExpectedColumn(c.Name, c.IsNullable)).ToList(),
				indexesByTable[t.Name].Select(i => i.Name).ToList()))
			.ToList();

		return (migrationId, tables);
	}

	// The only hand-written SQL outside generated migrations: reading the provider's catalog
	private static async Task<HashSet<string>> GetExistingTables(AppDbContext db)
	{
		string sql = db is PostgresAppDbContext
			? "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE'"
			: "SELECT table_name AS `Value` FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'";

		var names = await db.Database.SqlQueryRaw<string>(sql).ToListAsync();
		return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
	}

	private static async Task<Dictionary<string, bool>> GetExistingColumns(AppDbContext db, string table)
	{
		string sql = db is PostgresAppDbContext
			? "SELECT table_name AS \"TableName\", column_name AS \"ColumnName\", is_nullable AS \"IsNullable\" FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = {0}"
			: "SELECT table_name AS `TableName`, column_name AS `ColumnName`, is_nullable AS `IsNullable` FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = {0}";

		var rows = await db.Database.SqlQueryRaw<ColumnRow>(sql, table).ToListAsync();
		return rows.ToDictionary(r => r.ColumnName, r => r.IsNullable.Equals("YES", StringComparison.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
	}

	private static async Task<HashSet<string>> GetExistingIndexes(AppDbContext db, string table)
	{
		string sql = db is PostgresAppDbContext
			? "SELECT tablename AS \"TableName\", indexname AS \"IndexName\" FROM pg_indexes WHERE schemaname = current_schema() AND tablename = {0}"
			: "SELECT DISTINCT table_name AS `TableName`, index_name AS `IndexName` FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = {0}";

		var rows = await db.Database.SqlQueryRaw<IndexRow>(sql, table).ToListAsync();
		return new HashSet<string>(rows.Select(r => r.IndexName), StringComparer.OrdinalIgnoreCase);
	}

	private static async Task<int> CountAppliedMigrations(AppDbContext db)
	{
		var history = db.GetService<IHistoryRepository>();
		return await history.ExistsAsync() ? (await history.GetAppliedMigrationsAsync()).Count : 0;
	}

	public static async Task<bool> LooksLikeUnbaselinedInstall(AppDbContext db)
	{
		if (await CountAppliedMigrations(db) > 0)
		{
			return false;
		}

		var existing = await GetExistingTables(db);
		return GetFirstMigration(db).Tables.Any(t => existing.Contains(t.Name));
	}

	// Read-only check that the database already has everything the first migration would create
	public static async Task<BaselinePlan> Plan(AppDbContext db)
	{
		int applied = await CountAppliedMigrations(db);
		if (applied > 0)
		{
			return new BaselinePlan { Error = $"The database already has {applied} recorded migration(s). --baseline is only for databases from before migrations existed, run \"GenOnlineService migrate\" instead." };
		}

		var (migrationId, tables) = GetFirstMigration(db);
		var existingTables = await GetExistingTables(db);

		if (!tables.Any(t => existingTables.Contains(t.Name)))
		{
			return new BaselinePlan { Error = "The database contains none of the service's tables, so there is nothing to baseline. Run \"GenOnlineService migrate\" to create the schema." };
		}

		var missing = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

		foreach (var table in tables)
		{
			if (!existingTables.Contains(table.Name))
			{
				missing[table.Name] = new List<string> { "the whole table" };
				continue;
			}

			var items = new List<string>();

			var columns = await GetExistingColumns(db, table.Name);
			foreach (var column in table.Columns)
			{
				if (!columns.TryGetValue(column.Name, out bool isNullable))
				{
					items.Add($"column {column.Name}");
				}
				else if (isNullable != column.Nullable)
				{
					items.Add($"column {column.Name} (expected {(column.Nullable ? "NULL" : "NOT NULL")}, found {(isNullable ? "NULL" : "NOT NULL")})");
				}
			}

			var indexes = await GetExistingIndexes(db, table.Name);
			items.AddRange(table.Indexes.Where(i => !indexes.Contains(i)).Select(i => $"index {i}"));

			if (items.Count > 0)
			{
				missing[table.Name] = items;
			}
		}

		if (missing.Count > 0)
		{
			int itemCount = missing.Sum(kv => kv.Value.Count);
			string list = string.Join(Environment.NewLine, missing.Select(kv => $"  {kv.Key}:{Environment.NewLine}{string.Join(Environment.NewLine, kv.Value.Select(v => $"    {v}"))}"));

			return new BaselinePlan
			{
				Error = $"Cannot baseline, the database does not match the schema of migration {migrationId}. {itemCount} item(s) are missing or different in {missing.Count} table(s):{Environment.NewLine}{list}{Environment.NewLine}"
					+ "Add the listed items first (see README.md), then run --baseline again. Nothing was changed."
			};
		}

		return new BaselinePlan
		{
			MigrationId = migrationId,
			ExpectedTableCount = tables.Count,
			ExpectedColumnCount = tables.Sum(t => t.Columns.Count),
			ExpectedIndexCount = tables.Sum(t => t.Indexes.Count)
		};
	}

	public static async Task Apply(AppDbContext db, BaselinePlan plan)
	{
		var history = db.GetService<IHistoryRepository>();

		await using var transaction = await db.Database.BeginTransactionAsync();

		await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript());
		await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(plan.MigrationId, ProductInfo.GetVersion())));

		await transaction.CommitAsync();
	}
}

#pragma warning restore EF1001
