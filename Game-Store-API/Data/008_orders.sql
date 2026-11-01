BEGIN;
CREATE TABLE IF NOT EXISTS orders (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id),
    checkout_key uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    total numeric(18,2) NOT NULL CHECK (total >= 0),
    currency text NOT NULL DEFAULT 'DKK' CHECK (currency = 'DKK'),
    payment_status text NOT NULL DEFAULT 'simulated' CHECK (payment_status = 'simulated'),
    UNIQUE (user_id, checkout_key)
);
CREATE INDEX IF NOT EXISTS orders_user_created ON orders(user_id, created_at DESC, id DESC);
CREATE TABLE IF NOT EXISTS order_items (
    order_id uuid NOT NULL REFERENCES orders(id),
    product_id uuid NOT NULL,
    name varchar(200) NOT NULL,
    price numeric(10,2) NOT NULL CHECK (price >= 0),
    quantity integer NOT NULL CHECK (quantity BETWEEN 1 AND 99),
    PRIMARY KEY (order_id, product_id)
);
COMMIT;
