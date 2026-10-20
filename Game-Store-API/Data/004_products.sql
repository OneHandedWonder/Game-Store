BEGIN;

CREATE TABLE IF NOT EXISTS products (
    id uuid PRIMARY KEY,
    name varchar(200) NOT NULL CHECK (length(trim(name)) > 0),
    description varchar(4000) NOT NULL,
    price numeric NOT NULL CHECK (price >= 0 AND price <= 99999999.99 AND price = trunc(price, 2)),
    stock integer NOT NULL CHECK (stock >= 0),
    image_url varchar(2048),
    is_active boolean NOT NULL DEFAULT true
);

COMMIT;
