BEGIN;

ALTER TABLE users ADD COLUMN IF NOT EXISTS role text NOT NULL DEFAULT 'default'
    CHECK (role IN ('default', 'employee', 'admin'));

COMMIT;
