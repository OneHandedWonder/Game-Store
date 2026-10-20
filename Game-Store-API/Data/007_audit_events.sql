BEGIN;
CREATE TABLE IF NOT EXISTS audit_events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    event varchar(80) NOT NULL,
    severity varchar(20) NOT NULL,
    actor_id uuid,
    target_id uuid,
    remote_ip inet,
    trace_id varchar(64) NOT NULL,
    method varchar(10) NOT NULL,
    route varchar(200) NOT NULL,
    status_code integer NOT NULL,
    elapsed_ms double precision NOT NULL
);
CREATE INDEX IF NOT EXISTS audit_events_time_idx ON audit_events (occurred_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS audit_events_event_time_idx ON audit_events (event, occurred_at DESC);
REVOKE ALL ON audit_events FROM PUBLIC;
COMMIT;
