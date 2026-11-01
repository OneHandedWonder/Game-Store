BEGIN;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS fulfillment_status text NOT NULL DEFAULT 'unfulfilled' CHECK (fulfillment_status IN ('unfulfilled', 'fulfilled'));
ALTER TABLE orders ADD COLUMN IF NOT EXISTS fulfillment_version uuid NOT NULL DEFAULT gen_random_uuid();
COMMIT;
