BEGIN;
ALTER TABLE products ADD COLUMN IF NOT EXISTS version uuid NOT NULL DEFAULT gen_random_uuid();
COMMIT;
