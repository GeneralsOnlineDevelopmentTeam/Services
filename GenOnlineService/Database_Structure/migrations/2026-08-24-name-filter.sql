CREATE TABLE IF NOT EXISTS `name_filter_rejects` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT,
  `user_id` bigint(20) NOT NULL,
  `attempted_name` varchar(64) NOT NULL DEFAULT '',
  `skeleton` varchar(64) NOT NULL DEFAULT '',
  `rule_id` int(11) NOT NULL DEFAULT -1,
  `action` tinyint(4) NOT NULL DEFAULT 1,
  `source` tinyint(4) NOT NULL DEFAULT 0,
  `created` datetime NOT NULL DEFAULT current_timestamp(),
  PRIMARY KEY (`id`),
  KEY `idx_user` (`user_id`),
  KEY `idx_created` (`created`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

ALTER TABLE `users`
  ADD COLUMN `displayname_skeleton` varchar(32) DEFAULT NULL AFTER `displayname`,
  ADD INDEX `idx_displayname_skeleton` (`displayname_skeleton`);
