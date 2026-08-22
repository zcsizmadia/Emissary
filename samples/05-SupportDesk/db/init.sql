-- Support-desk business data. Mirrors SupportDesk.Seed so offline and Postgres modes match.
CREATE TABLE IF NOT EXISTS orders (
    order_id       TEXT PRIMARY KEY,
    customer_email TEXT    NOT NULL,
    description    TEXT    NOT NULL,
    amount         NUMERIC NOT NULL,
    status         TEXT    NOT NULL,
    tracking_id    TEXT,
    refunded       BOOLEAN NOT NULL DEFAULT FALSE
);

INSERT INTO orders (order_id, customer_email, description, amount, status, tracking_id, refunded) VALUES
    ('ORD-7', 'dana@example.com', 'Aeron office chair', 129.99, 'delivered',  'TRK-7', FALSE),
    ('ORD-9', 'dana@example.com', 'Standing desk mat',   45.00, 'in_transit', 'TRK-9', FALSE)
ON CONFLICT (order_id) DO NOTHING;
