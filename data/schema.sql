PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = FULL;

CREATE TABLE IF NOT EXISTS game_profiles (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    game_name TEXT NOT NULL,
    executable_name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    power_plan_guid TEXT,
    process_priority_class INTEGER,
    cpu_affinity_mask TEXT,
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS profile_service_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    profile_id INTEGER NOT NULL,
    service_name TEXT NOT NULL COLLATE NOCASE,
    desired_state TEXT NOT NULL CHECK (desired_state IN ('STOP', 'NO_CHANGE')),
    user_approved INTEGER NOT NULL DEFAULT 0 CHECK (user_approved IN (0, 1)),
    required INTEGER NOT NULL DEFAULT 0 CHECK (required IN (0, 1)),
    UNIQUE (profile_id, service_name),
    FOREIGN KEY (profile_id) REFERENCES game_profiles(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS performance_sessions (
    id TEXT PRIMARY KEY,
    profile_id INTEGER,
    game_name TEXT NOT NULL,
    executable_name TEXT NOT NULL,
    game_process_id INTEGER NOT NULL,
    start_time TEXT NOT NULL,
    end_time TEXT,
    exit_code INTEGER,
    was_clean_exit INTEGER CHECK (was_clean_exit IN (0, 1)),
    activation_state TEXT NOT NULL,
    recovery_was_required INTEGER NOT NULL DEFAULT 0 CHECK (recovery_was_required IN (0, 1)),
    FOREIGN KEY (profile_id) REFERENCES game_profiles(id)
);

CREATE TABLE IF NOT EXISTS runtime_snapshots (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    restored_at TEXT,
    phase TEXT NOT NULL,
    snapshot_json TEXT NOT NULL,
    last_error TEXT,
    FOREIGN KEY (session_id) REFERENCES performance_sessions(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS optimization_actions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT,
    timestamp TEXT NOT NULL,
    action_kind TEXT NOT NULL,
    target TEXT,
    outcome TEXT NOT NULL,
    details TEXT,
    win32_error INTEGER,
    FOREIGN KEY (session_id) REFERENCES performance_sessions(id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS metric_samples (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    timestamp TEXT NOT NULL,
    metric_type TEXT NOT NULL,
    process_id INTEGER,
    process_name TEXT,
    value REAL NOT NULL,
    unit TEXT NOT NULL,
    FOREIGN KEY (session_id) REFERENCES performance_sessions(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS telemetry_spikes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    timestamp TEXT NOT NULL,
    metric_type TEXT NOT NULL,
    baseline_value REAL,
    spike_value REAL NOT NULL,
    culprit_process TEXT,
    confidence REAL CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
    culprit_impact_description TEXT,
    FOREIGN KEY (session_id) REFERENCES performance_sessions(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS session_comparisons (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    baseline_session_id TEXT NOT NULL,
    candidate_session_id TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    result_json TEXT NOT NULL,
    UNIQUE (baseline_session_id, candidate_session_id),
    FOREIGN KEY (baseline_session_id) REFERENCES performance_sessions(id),
    FOREIGN KEY (candidate_session_id) REFERENCES performance_sessions(id)
);

CREATE INDEX IF NOT EXISTS idx_sessions_profile_start
    ON performance_sessions(profile_id, start_time);
CREATE INDEX IF NOT EXISTS idx_metric_session_type_time
    ON metric_samples(session_id, metric_type, timestamp);
CREATE INDEX IF NOT EXISTS idx_spikes_session_time
    ON telemetry_spikes(session_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_actions_session_time
    ON optimization_actions(session_id, timestamp);
