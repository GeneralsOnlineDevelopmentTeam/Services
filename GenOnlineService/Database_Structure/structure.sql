/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET NAMES utf8 */;
/*!50503 SET NAMES utf8mb4 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;

-- Dumping structure for table go_production.connection_outcomes
CREATE TABLE IF NOT EXISTS `connection_outcomes` (
  `day_of_year` int(11) NOT NULL DEFAULT 0,
  `ipv4_count` int(11) DEFAULT NULL,
  `ipv6_count` int(11) DEFAULT NULL,
  `success_count` int(11) DEFAULT NULL,
  `failed_count` int(11) DEFAULT NULL,
  PRIMARY KEY (`day_of_year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- Data exporting was unselected.

-- Dumping structure for table go_production.daily_stats
CREATE TABLE IF NOT EXISTS `daily_stats` (
  `day_of_year` int(11) NOT NULL,
  `stats_structure` longtext NOT NULL CHECK (json_valid(`stats_structure`)),
  PRIMARY KEY (`day_of_year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

-- Data exporting was unselected.

-- Dumping structure for table go_production.friends
CREATE TABLE IF NOT EXISTS `friends` (
  `user_id_1` bigint(20) NOT NULL,
  `user_id_2` bigint(20) NOT NULL,
  PRIMARY KEY (`user_id_1`,`user_id_2`) USING BTREE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3 COLLATE=utf8mb3_general_ci;

-- Data exporting was unselected.

-- Dumping structure for table go_production.friends_blocked
CREATE TABLE IF NOT EXISTS `friends_blocked` (
  `source_user_id` bigint(20) NOT NULL,
  `target_user_id` bigint(20) NOT NULL,
  PRIMARY KEY (`source_user_id`,`target_user_id`) USING BTREE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3 COLLATE=utf8mb3_general_ci;

-- Data exporting was unselected.

-- Dumping structure for table go_production.friends_requests
CREATE TABLE IF NOT EXISTS `friends_requests` (
  `source_user_id` bigint(20) NOT NULL,
  `target_user_id` bigint(20) NOT NULL,
  PRIMARY KEY (`source_user_id`,`target_user_id`) USING BTREE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3 COLLATE=utf8mb3_general_ci;

-- Data exporting was unselected.

-- Dumping structure for table go_production.leaderboard_daily
CREATE TABLE IF NOT EXISTS `leaderboard_daily` (
  `user_id` bigint(20) NOT NULL,
  `points` int(11) DEFAULT NULL,
  `day_of_year` int(11) NOT NULL,
  `year` int(11) NOT NULL,
  `wins` int(11) DEFAULT NULL,
  `losses` int(11) DEFAULT NULL,
  PRIMARY KEY (`user_id`,`day_of_year`,`year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

-- Data exporting was unselected.

-- Dumping structure for table go_production.leaderboard_monthly
CREATE TABLE IF NOT EXISTS `leaderboard_monthly` (
  `user_id` bigint(20) NOT NULL,
  `points` int(11) DEFAULT NULL,
  `month_of_year` int(11) NOT NULL,
  `year` int(11) NOT NULL,
  `wins` int(11) DEFAULT NULL,
  `losses` int(11) DEFAULT NULL,
  PRIMARY KEY (`user_id`,`month_of_year`,`year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin ROW_FORMAT=DYNAMIC;

-- Data exporting was unselected.

-- Dumping structure for table go_production.leaderboard_yearly
CREATE TABLE IF NOT EXISTS `leaderboard_yearly` (
  `user_id` bigint(20) NOT NULL,
  `points` int(11) DEFAULT NULL,
  `year` int(11) NOT NULL,
  `wins` int(11) DEFAULT NULL,
  `losses` int(11) DEFAULT NULL,
  PRIMARY KEY (`user_id`,`year`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin ROW_FORMAT=DYNAMIC;

-- Data exporting was unselected.

-- Dumping structure for table go_production.match_history
CREATE TABLE IF NOT EXISTS `match_history` (
  `match_id` bigint(20) NOT NULL AUTO_INCREMENT,
  `owner` bigint(20) NOT NULL,
  `name` varchar(64) NOT NULL,
  `finished` tinyint(1) NOT NULL DEFAULT 0,
  `started` datetime NOT NULL DEFAULT current_timestamp(),
  `time_finished` datetime NOT NULL DEFAULT current_timestamp(),
  `map_name` varchar(128) NOT NULL,
  `map_official` tinyint(1) NOT NULL,
  `match_roster_type` varchar(32) NOT NULL DEFAULT '',
  `vanilla_teams` tinyint(1) NOT NULL,
  `starting_cash` int(10) unsigned NOT NULL,
  `limit_superweapons` tinyint(1) NOT NULL,
  `track_stats` tinyint(1) NOT NULL,
  `allow_observers` tinyint(1) NOT NULL,
  `max_cam_height` smallint(6) unsigned NOT NULL,
  `exe_crc` int(10) unsigned NOT NULL DEFAULT 0,
  `ini_crc` int(10) unsigned NOT NULL DEFAULT 0,
  `map_path` varchar(128) DEFAULT NULL,
  `member_slot_0` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(`member_slot_0`)),
  `member_slot_1` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(`member_slot_1`)),
  `member_slot_2` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(`member_slot_2`)),
  `member_slot_3` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(`member_slot_3`)),
  `member_slot_4` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(`member_slot_4`)),
  `member_slot_5` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(`member_slot_5`)),
  `member_slot_6` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(`member_slot_6`)),
  `member_slot_7` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(`member_slot_7`)),
  PRIMARY KEY (`match_id`)
) ENGINE=InnoDB AUTO_INCREMENT=563766 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- Data exporting was unselected.

-- Dumping structure for table go_production.pending_logins
CREATE TABLE IF NOT EXISTS `pending_logins` (
  `code` varchar(32) NOT NULL,
  `state` int(1) NOT NULL,
  `created` datetime NOT NULL DEFAULT current_timestamp(),
  `user_id` bigint(20) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- Data exporting was unselected.

-- Dumping structure for table go_production.service_stats
CREATE TABLE IF NOT EXISTS `service_stats` (
  `day_of_year` int(11) NOT NULL DEFAULT 0,
  `hour_of_day` int(11) NOT NULL DEFAULT 0,
  `player_peak` int(11) NOT NULL DEFAULT 0,
  `lobbies_peak` int(11) NOT NULL DEFAULT 0,
  PRIMARY KEY (`day_of_year`,`hour_of_day`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- Data exporting was unselected.

-- Dumping structure for table go_production.user_devices
CREATE TABLE IF NOT EXISTS `user_devices` (
  `user_id` bigint(20) NOT NULL,
  `hwid_0` varchar(128) NOT NULL,
  `hwid_1` varchar(128) NOT NULL,
  `hwid_2` varchar(128) NOT NULL,
  `ip_addr` varchar(45) NOT NULL,
  `hwid_3` varchar(50) DEFAULT 'blank',
  `hwid_4` varchar(50) DEFAULT NULL,
  `hwid_5` varchar(50) DEFAULT NULL,
  PRIMARY KEY (`user_id`,`hwid_0`,`hwid_1`,`hwid_2`,`ip_addr`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

-- Data exporting was unselected.

-- Dumping structure for table go_production.user_stats_v2
CREATE TABLE IF NOT EXISTS `user_stats_v2` (
  `user_id` bigint(20) NOT NULL,
  `stats` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL CHECK (json_valid(`stats`)),
  PRIMARY KEY (`user_id`) USING BTREE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3 COLLATE=utf8mb3_general_ci;

-- Data exporting was unselected.

-- Dumping structure for table go_production.users
CREATE TABLE IF NOT EXISTS `users` (
  `user_id` bigint(20) NOT NULL AUTO_INCREMENT,
  `account_type` int(1) NOT NULL,
  `steam_id` bigint(20) DEFAULT NULL,
  `discord_id` bigint(20) DEFAULT NULL,
  `discord_username` varchar(32) DEFAULT NULL,
  `gamereplays_id` bigint(20) DEFAULT NULL,
  `gamereplays_username` varchar(32) DEFAULT NULL,
  `displayname` varchar(32) DEFAULT NULL,
  `relay_region` smallint(6) NOT NULL DEFAULT -1,
  `portmapping_tech` smallint(6) NOT NULL DEFAULT -2,
  `ipv4` tinyint(1) NOT NULL DEFAULT 0,
  `ipv6` tinyint(1) NOT NULL DEFAULT 0,
  `active` tinyint(1) NOT NULL DEFAULT 0,
  `lastlogin` datetime NOT NULL DEFAULT current_timestamp(),
  `last_ip` varchar(45) NOT NULL DEFAULT '',
  `client_id` smallint(5) unsigned NOT NULL DEFAULT 0,
  `favorite_color` int(11) NOT NULL DEFAULT -1,
  `favorite_side` int(11) NOT NULL DEFAULT -1,
  `favorite_map` varchar(128) DEFAULT NULL,
  `favorite_starting_money` int(11) NOT NULL DEFAULT -1,
  `favorite_limit_superweapons` int(11) NOT NULL DEFAULT -1,
  `admin` tinyint(4) NOT NULL DEFAULT 0,
  `banned` tinyint(4) NOT NULL DEFAULT 0,
  `elo_rating` int(11) NOT NULL DEFAULT 1000,
  `elo_num_matches` int(11) NOT NULL DEFAULT 0,
  `ban_reason` varchar(128) DEFAULT NULL,
  `banned_by` varchar(50) DEFAULT NULL,
  `ban_verified_by` varchar(50) DEFAULT NULL,
  `ban_aliases` varchar(50) DEFAULT NULL,
  PRIMARY KEY (`user_id`)
) ENGINE=InnoDB AUTO_INCREMENT=34621 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

-- Data exporting was unselected.

/*!40103 SET TIME_ZONE=IFNULL(@OLD_TIME_ZONE, 'system') */;
/*!40101 SET SQL_MODE=IFNULL(@OLD_SQL_MODE, '') */;
/*!40014 SET FOREIGN_KEY_CHECKS=IFNULL(@OLD_FOREIGN_KEY_CHECKS, 1) */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40111 SET SQL_NOTES=IFNULL(@OLD_SQL_NOTES, 1) */;
