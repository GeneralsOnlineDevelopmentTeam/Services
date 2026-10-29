# Services
GeneralsOnline Game Services Code provides RESTful web services which act as a replacement and modernization of the GameSpy functionality found in the original release of Command & Conquer: Generals - Zero Hour

# Build Status: Windows
[![Windows (x64 + arm64)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Windows.yml/badge.svg)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Windows.yml)

# Build Status: Linux
[![Linux (x64 + arm64)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Linux-MUSL.yml/badge.svg)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Linux-MUSL.yml)
[![Linux MUSL (x64 + arm64)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Linux.yml/badge.svg)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/Linux.yml)

# Build Status: MacOS
[![MacOS (x64 + arm64)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/MacOS.yml/badge.svg)](https://github.com/GeneralsOnlineDevelopmentTeam/Services/actions/workflows/MacOS.yml)

# Original Game Functionality Implemented
- Quick Match
- Custom Match
- Ladders
- Rooms & Chat
- Profiles
- Player Stats
- Friends / Buddies / Social System
- Global / Daily Stats
- Auto-update

# Required Dependencies
- Microsoft Visual Studio (2026 Community Edition recommended)
- A database server, one of:
  - PostgreSQL (PostgreSQL 15 or higher recommended)
  - MySQL/MariaDB (MariaDB 12.1.2 or higher recommended). **Deprecated**: MySQL/MariaDB support is kept for this major version only, to give existing installs time to move, and is removed in the next major version
- A database management tool or command line (e.g. pgAdmin, DBeaver, HeidiSQL, psql or mariadb)
- .NET SDK 10

# Optional Dependencies (not required for development, required for live)
- Discord App ID
- S3 compatible storage
- STUN + TURN servers

# How To Build
- Sync the repository
- Open GenServices.sln
- Build the solution for x64, Windows (or your architecture & OS if different)
- Edit appsettings.json and fill out any TODO sections (e.g. token settings, database settings)
- Choose the database in the `Database` section of appsettings.json:
  - `db_provider` is `"mysql"` (MySQL/MariaDB, the default when the key is missing) or `"postgres"` (PostgreSQL). Any other value stops the service at startup
  - Create an empty database and a user with full access to it, then fill in `db_host`, `db_port` (3306 for MySQL/MariaDB, 5432 for PostgreSQL), `db_name`, `db_username` and `db_password`
  - New installs should use PostgreSQL
- Create the database schema by running `GenOnlineService migrate` (there is no SQL to import)
- Run GenOnlineService.exe. The service never changes the schema itself and refuses to start while migrations are pending
- When upgrading, stop the service, run `GenOnlineService migrate --yes`, then start the new version. With multiple instances, the old instances may keep running during the migration only if it is backward compatible, then roll the instances

# Upgrading An Existing MySQL/MariaDB Install
Installs from before migrations existed have their schema from the old `structure.sql`. They keep working on MySQL/MariaDB without recreating anything, but need a one-time baseline so the migrations know the existing tables are already there. `db_provider` can stay unset (it defaults to `"mysql"`).
- Production databases that were kept up to date already match the schema of the first migration. Installs that were built from the old `structure.sql` may lack some of it (e.g. the `external_publication` table of `upgrade_20260816_external_leaderboard_publication.sql`, the `ac_reviews_*` anti cheat tables, indexes added later, or the `google_id`, `google_email` and `permissionslevel` columns of `users` and the `web_sessions` table the website uses). `--baseline` lists everything that is missing, you add those items by hand and run it again
- Stop the service
- Back up the database
- Run `GenOnlineService migrate --baseline`. It checks that every table, every column (including whether it may be NULL) and every named index the first migration creates exists, then records that migration as applied without running it. It changes no table or data. If anything is missing it lists all of it grouped by table, changes nothing and exits with an error. Extra tables, columns and indexes are left alone. Without `--yes` it asks for confirmation and refuses to run when input is redirected
- Run `GenOnlineService migrate`. Normally there is nothing left to apply, later releases add their migrations after the first one
- Start the new version

The service refuses to start on such a database until this is done, and says so. Moving an existing install to PostgreSQL is not automated: create a PostgreSQL database with `migrate` and copy the data yourself.

# Changing The Database Schema
The schema is defined by the EF Core model (`GenOnlineService\Database`) and applied through code-first migrations. Migrations are only applied by `GenOnlineService migrate`, never by the service itself.

The model is shared, but every provider has its own context (`MySqlAppDbContext`, `PostgresAppDbContext`) and its own migrations (`GenOnlineService\Database\Migrations\MySql` and `...\Postgres`). **Every model change needs a migration for both contexts.** The MySQL schema mirrors the old `structure.sql` (unsigned integers, `datetime`, JSON as `longtext`) so existing installs can be baselined. Provider specific column types belong in the derived contexts (`Database.Providers.cs`), not in the shared configuration classes. Do not write SQL outside generated migrations.
- Restore the local tools once: `dotnet tool restore`
- After changing the model, create the migrations from the `GenOnlineService` folder:
  - `dotnet ef migrations add <Name> --context MySqlAppDbContext -o Database/Migrations/MySql -n GenOnlineService.Migrations.MySql`
  - `dotnet ef migrations add <Name> --context PostgresAppDbContext -o Database/Migrations/Postgres -n GenOnlineService.Migrations.Postgres`
- Keep the `-n` argument, otherwise the generated namespace collides with the `Database` namespace used by the data access code and the build fails
- The first `migrations add` for a context writes its model snapshot to a stray `GenOnlineService\GenOnlineService\Migrations\...` folder. Move that file next to the migration (later migrations update it in place) and delete the stray folder
- Check that nothing is missing with `dotnet ef migrations has-pending-model-changes --context <MySqlAppDbContext|PostgresAppDbContext>`
- Commit the generated migration files together with the model change
- Keep schema changes backward compatible: add new columns/tables first and drop old ones in a later release, so an older instance keeps working while a newer one has already migrated

## The migrate command
- `GenOnlineService migrate` uses the configured `db_provider`, lists the applied and pending migrations and asks for confirmation. It does not start the service. Without `--yes` it refuses to run when input is redirected (e.g. in CI)
- `GenOnlineService migrate --baseline` is the one-time step for existing MySQL/MariaDB installs described above

# Observability

The service can export OpenTelemetry logs, metrics, and traces over OTLP, with Sentry as an optional destination for errors, logs, and traces. See [docs/observability.md](docs/observability.md) for configuration, coverage, metric names, privacy controls, and an Alloy example.
