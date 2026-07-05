-- Prison backend schema (PLAN §11.1). Applied automatically by the postgres container
-- (mounted into /docker-entrypoint-initdb.d) and by CI/test databases.

CREATE TABLE IF NOT EXISTS players (
    player_id   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    username    TEXT NOT NULL UNIQUE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS friends (
    player_a    UUID NOT NULL REFERENCES players(player_id),
    player_b    UUID NOT NULL REFERENCES players(player_id),
    status      TEXT NOT NULL DEFAULT 'pending', -- pending | accepted | blocked
    PRIMARY KEY (player_a, player_b),
    CHECK (player_a <> player_b)
);

CREATE TABLE IF NOT EXISTS prisons (
    prison_id         TEXT PRIMARY KEY,
    family_id         TEXT NOT NULL,
    generation        INT  NOT NULL,
    host_type         TEXT NOT NULL,             -- Official | Community
    status            TEXT NOT NULL,             -- PLAN §10.2 state machine
    owner_id          UUID REFERENCES players(player_id),
    visibility        TEXT NOT NULL,
    created_at        TIMESTAMPTZ NOT NULL,
    compromised_at    TIMESTAMPTZ,
    retire_at         TIMESTAMPTZ,
    share_escape_data BOOLEAN NOT NULL,
    parent_prison_id  TEXT
);

-- Per-generation DNA/Doctrine snapshot for reproducibility and Museum mode (§11.1).
CREATE TABLE IF NOT EXISTS prison_versions (
    prison_id   TEXT PRIMARY KEY REFERENCES prisons(prison_id),
    family_json JSONB NOT NULL,
    quality     REAL
);

CREATE TABLE IF NOT EXISTS escapes (
    escape_id    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    prison_id    TEXT NOT NULL REFERENCES prisons(prison_id),
    player_id    UUID REFERENCES players(player_id),
    started_at   TIMESTAMPTZ,
    completed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    legitimate   BOOLEAN                          -- null until admin/automated review (§10.4)
);

-- The granular structured telemetry per escape (§9.1 output), never raw input streams.
CREATE TABLE IF NOT EXISTS escape_events (
    escape_id   UUID NOT NULL REFERENCES escapes(escape_id),
    report_json JSONB NOT NULL
);

CREATE TABLE IF NOT EXISTS statistics (
    scope       TEXT NOT NULL,                    -- prison | family | global
    scope_id    TEXT NOT NULL,
    key         TEXT NOT NULL,
    value       DOUBLE PRECISION NOT NULL,
    PRIMARY KEY (scope, scope_id, key)
);

-- Registry of live dedicated/community servers, for the server browser (§11.1).
CREATE TABLE IF NOT EXISTS servers (
    server_id   TEXT PRIMARY KEY,
    prison_id   TEXT REFERENCES prisons(prison_id),
    address     TEXT NOT NULL,
    players     INT NOT NULL DEFAULT 0,
    last_seen   TIMESTAMPTZ NOT NULL DEFAULT now()
);
