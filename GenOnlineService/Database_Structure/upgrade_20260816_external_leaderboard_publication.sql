CREATE TABLE IF NOT EXISTS `external_publication` (
  `match_id` bigint(20) NOT NULL,
  `next_attempt_at` datetime DEFAULT NULL,
  `published_at` datetime DEFAULT NULL,
  `attempts` int(11) NOT NULL DEFAULT 0,
  `last_error` varchar(512) DEFAULT NULL,
  PRIMARY KEY (`match_id`),
  KEY `ix_external_publication_pending` (`published_at`,`next_attempt_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
