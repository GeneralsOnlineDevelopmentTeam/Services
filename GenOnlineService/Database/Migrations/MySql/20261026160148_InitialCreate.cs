using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.MySql
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ac_reviews_crc",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    original_crc = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    diff_crc = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    most_recent_match = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ac_reviews_crc", x => new { x.user_id, x.original_crc, x.diff_crc, x.most_recent_match });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ac_reviews_modules",
                columns: table => new
                {
                    report_id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    user_id = table.Column<ulong>(type: "bigint(20) unsigned", nullable: false),
                    match_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    module_name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    module_path = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    module_size = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ac_reviews_modules", x => x.report_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ac_reviews_new_account_games",
                columns: table => new
                {
                    report_id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    user_id = table.Column<ulong>(type: "bigint(20) unsigned", nullable: false),
                    match_id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ac_reviews_new_account_games", x => x.report_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ac_reviews_probes",
                columns: table => new
                {
                    report_id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    user_id = table.Column<ulong>(type: "bigint(20) unsigned", nullable: false),
                    match_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    reason = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ac_reviews_probes", x => x.report_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "connection_outcomes",
                columns: table => new
                {
                    day_of_year = table.Column<int>(type: "int", nullable: false),
                    ipv4_count = table.Column<int>(type: "int", nullable: true),
                    ipv6_count = table.Column<int>(type: "int", nullable: true),
                    success_count = table.Column<int>(type: "int", nullable: true),
                    failed_count = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_outcomes", x => x.day_of_year);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "daily_stats",
                columns: table => new
                {
                    day_of_year = table.Column<int>(type: "int", nullable: false),
                    stats_structure = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_stats", x => x.day_of_year);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "external_publication",
                columns: table => new
                {
                    match_id = table.Column<long>(type: "bigint", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "datetime", nullable: true),
                    published_at = table.Column<DateTime>(type: "datetime", nullable: true),
                    attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_publication", x => x.match_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "friends",
                columns: table => new
                {
                    user_id_1 = table.Column<long>(type: "bigint", nullable: false),
                    user_id_2 = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friends", x => new { x.user_id_1, x.user_id_2 });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "friends_blocked",
                columns: table => new
                {
                    source_user_id = table.Column<long>(type: "bigint", nullable: false),
                    target_user_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friends_blocked", x => new { x.source_user_id, x.target_user_id });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "friends_requests",
                columns: table => new
                {
                    source_user_id = table.Column<long>(type: "bigint", nullable: false),
                    target_user_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_friends_requests", x => new { x.source_user_id, x.target_user_id });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "leaderboard_daily",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    day_of_year = table.Column<int>(type: "int", nullable: false),
                    year = table.Column<int>(type: "int", nullable: false),
                    points = table.Column<int>(type: "int", nullable: true),
                    wins = table.Column<int>(type: "int", nullable: true),
                    losses = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leaderboard_daily", x => new { x.user_id, x.day_of_year, x.year });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "leaderboard_monthly",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    month_of_year = table.Column<int>(type: "int", nullable: false),
                    year = table.Column<int>(type: "int", nullable: false),
                    points = table.Column<int>(type: "int", nullable: true),
                    wins = table.Column<int>(type: "int", nullable: true),
                    losses = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leaderboard_monthly", x => new { x.user_id, x.month_of_year, x.year });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "leaderboard_yearly",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    year = table.Column<int>(type: "int", nullable: false),
                    points = table.Column<int>(type: "int", nullable: true),
                    wins = table.Column<int>(type: "int", nullable: true),
                    losses = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leaderboard_yearly", x => new { x.user_id, x.year });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "match_history",
                columns: table => new
                {
                    match_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    owner = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    finished = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    started = table.Column<DateTime>(type: "datetime", nullable: false),
                    time_finished = table.Column<DateTime>(type: "datetime", nullable: false),
                    map_name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    map_official = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    match_roster_type = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false, defaultValue: "")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    lobby_type = table.Column<byte>(type: "tinyint unsigned", nullable: true),
                    vanilla_teams = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    starting_cash = table.Column<uint>(type: "int unsigned", nullable: false),
                    limit_superweapons = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    track_stats = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    allow_observers = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    max_cam_height = table.Column<ushort>(type: "smallint(6) unsigned", nullable: false),
                    exe_crc = table.Column<uint>(type: "int unsigned", nullable: false),
                    ini_crc = table.Column<uint>(type: "int unsigned", nullable: false),
                    map_path = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    member_slot_0 = table.Column<string>(type: "longtext", nullable: true, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    member_slot_1 = table.Column<string>(type: "longtext", nullable: true, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    member_slot_2 = table.Column<string>(type: "longtext", nullable: true, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    member_slot_3 = table.Column<string>(type: "longtext", nullable: true, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    member_slot_4 = table.Column<string>(type: "longtext", nullable: true, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    member_slot_5 = table.Column<string>(type: "longtext", nullable: true, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    member_slot_6 = table.Column<string>(type: "longtext", nullable: true, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    member_slot_7 = table.Column<string>(type: "longtext", nullable: true, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_history", x => x.match_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "service_stats",
                columns: table => new
                {
                    day_of_year = table.Column<int>(type: "int", nullable: false),
                    hour_of_day = table.Column<int>(type: "int", nullable: false),
                    player_peak = table.Column<int>(type: "int", nullable: false),
                    lobbies_peak = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_stats", x => new { x.day_of_year, x.hour_of_day });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "user_devices",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    hwid_0 = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    hwid_1 = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    hwid_2 = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ip_addr = table.Column<string>(type: "varchar(45)", maxLength: 45, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    hwid_3 = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    hwid_4 = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    hwid_5 = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_devices", x => new { x.user_id, x.hwid_0, x.hwid_1, x.hwid_2, x.ip_addr });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "user_stats_v2",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    stats = table.Column<string>(type: "longtext", nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_stats_v2", x => x.user_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "user_token_state",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    session_type = table.Column<int>(type: "int", nullable: false),
                    token_generation = table.Column<int>(type: "int", nullable: false),
                    refresh_jti = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    previous_refresh_jti = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    previous_refresh_jti_expires = table.Column<DateTime>(type: "datetime", nullable: false),
                    updated = table.Column<DateTime>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_token_state", x => new { x.user_id, x.session_type });
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    account_type = table.Column<int>(type: "int(1)", nullable: false),
                    steam_id = table.Column<long>(type: "bigint", nullable: true),
                    discord_id = table.Column<long>(type: "bigint", nullable: true),
                    discord_username = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    gamereplays_id = table.Column<long>(type: "bigint", nullable: true),
                    gamereplays_username = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    displayname = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    lastlogin = table.Column<DateTime>(type: "datetime", nullable: false),
                    last_ip = table.Column<string>(type: "varchar(45)", maxLength: 45, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    client_id = table.Column<ushort>(type: "smallint(5) unsigned", nullable: false),
                    favorite_color = table.Column<int>(type: "int", nullable: false),
                    favorite_side = table.Column<int>(type: "int", nullable: false),
                    favorite_map = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    favorite_starting_money = table.Column<int>(type: "int", nullable: false),
                    favorite_limit_superweapons = table.Column<int>(type: "int(11)", nullable: false),
                    admin = table.Column<sbyte>(type: "tinyint(4)", nullable: false),
                    banned = table.Column<sbyte>(type: "tinyint(4)", nullable: false),
                    elo_rating = table.Column<int>(type: "int", nullable: false),
                    monthly_elo_rating = table.Column<int>(type: "int", nullable: false),
                    elo_num_matches = table.Column<int>(type: "int", nullable: false),
                    ban_reason = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    banned_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ban_verified_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ban_aliases = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    google_email = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    google_id = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    permissionslevel = table.Column<int>(type: "int(11)", nullable: true, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.user_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "web_sessions",
                columns: table => new
                {
                    session_id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    created = table.Column<DateTime>(type: "timestamp", nullable: false),
                    expiration = table.Column<DateTime>(type: "timestamp", nullable: false),
                    last_used = table.Column<DateTime>(type: "timestamp", nullable: false),
                    ip_addr = table.Column<string>(type: "varchar(45)", maxLength: 45, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    os = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    browser = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_web_sessions", x => x.session_id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "user_id_match_id_module_name",
                table: "ac_reviews_modules",
                columns: new[] { "user_id", "match_id", "module_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_external_publication_pending",
                table: "external_publication",
                columns: new[] { "published_at", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_friends_user_id_2",
                table: "friends",
                column: "user_id_2");

            migrationBuilder.CreateIndex(
                name: "ix_friends_requests_target_user_id",
                table: "friends_requests",
                column: "target_user_id");

            migrationBuilder.CreateIndex(
                name: "idx_finished_time",
                table: "match_history",
                columns: new[] { "finished", "time_finished", "match_id" });

            migrationBuilder.CreateIndex(
                name: "idx_user_devices_hwid_0_user",
                table: "user_devices",
                columns: new[] { "hwid_0", "user_id" });

            migrationBuilder.CreateIndex(
                name: "idx_user_devices_hwid_1_user",
                table: "user_devices",
                columns: new[] { "hwid_1", "user_id" });

            migrationBuilder.CreateIndex(
                name: "idx_user_devices_hwid_2_user",
                table: "user_devices",
                columns: new[] { "hwid_2", "user_id" });

            migrationBuilder.CreateIndex(
                name: "idx_user_devices_hwid_triplet_user",
                table: "user_devices",
                columns: new[] { "hwid_0", "hwid_1", "hwid_2", "user_id" });

            migrationBuilder.CreateIndex(
                name: "idx_user_devices_ip_user",
                table: "user_devices",
                columns: new[] { "ip_addr", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_users_banned",
                table: "users",
                column: "banned");

            migrationBuilder.CreateIndex(
                name: "user_id",
                table: "web_sessions",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ac_reviews_crc");

            migrationBuilder.DropTable(
                name: "ac_reviews_modules");

            migrationBuilder.DropTable(
                name: "ac_reviews_new_account_games");

            migrationBuilder.DropTable(
                name: "ac_reviews_probes");

            migrationBuilder.DropTable(
                name: "connection_outcomes");

            migrationBuilder.DropTable(
                name: "daily_stats");

            migrationBuilder.DropTable(
                name: "external_publication");

            migrationBuilder.DropTable(
                name: "friends");

            migrationBuilder.DropTable(
                name: "friends_blocked");

            migrationBuilder.DropTable(
                name: "friends_requests");

            migrationBuilder.DropTable(
                name: "leaderboard_daily");

            migrationBuilder.DropTable(
                name: "leaderboard_monthly");

            migrationBuilder.DropTable(
                name: "leaderboard_yearly");

            migrationBuilder.DropTable(
                name: "match_history");

            migrationBuilder.DropTable(
                name: "service_stats");

            migrationBuilder.DropTable(
                name: "user_devices");

            migrationBuilder.DropTable(
                name: "user_stats_v2");

            migrationBuilder.DropTable(
                name: "user_token_state");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "web_sessions");
        }
    }
}
