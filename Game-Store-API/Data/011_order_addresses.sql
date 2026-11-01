BEGIN;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS protected_delivery_address text;
CREATE INDEX IF NOT EXISTS orders_created_at ON orders(created_at);
COMMIT;
