-- Hauling app schema
-- Source of truth — apply via: cat sql/001_schema.sql | sudo -u postgres psql -d hauling

CREATE SCHEMA IF NOT EXISTS hauling;

-- Config table for fee rates and settings
CREATE TABLE hauling.config (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    description TEXT
);

-- Users from EVE SSO
CREATE TABLE hauling.users (
    character_id BIGINT PRIMARY KEY,
    character_name TEXT NOT NULL,
    corporation_id BIGINT NOT NULL,
    alliance_id BIGINT,
    role TEXT NOT NULL DEFAULT 'member',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_login TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- EVE item types from SDE (market-tradeable items only)
CREATE TABLE hauling.eve_types (
    type_id INT PRIMARY KEY,
    type_name TEXT NOT NULL,
    volume NUMERIC(20,4) NOT NULL DEFAULT 0,
    market_group_id INT,
    group_id INT,
    category_id INT
);

-- Orders (hauling requests)
CREATE TABLE hauling.orders (
    order_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    character_id BIGINT NOT NULL REFERENCES hauling.users(character_id),
    status TEXT NOT NULL DEFAULT 'pending',
    shop_requested BOOLEAN NOT NULL DEFAULT false,
    total_m3 NUMERIC(20,4) NOT NULL DEFAULT 0,
    total_estimated_isk NUMERIC(20,2) NOT NULL DEFAULT 0,
    total_actual_isk NUMERIC(20,2),
    hauling_fee NUMERIC(20,2) NOT NULL DEFAULT 0,
    shopper_fee NUMERIC(20,2) NOT NULL DEFAULT 0,
    assigned_to BIGINT REFERENCES hauling.users(character_id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Order line items
CREATE TABLE hauling.order_items (
    item_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_id BIGINT NOT NULL REFERENCES hauling.orders(order_id),
    type_id INT NOT NULL REFERENCES hauling.eve_types(type_id),
    quantity INT NOT NULL,
    volume_per_unit NUMERIC(20,4) NOT NULL,
    line_m3 NUMERIC(20,4) NOT NULL,
    estimated_price NUMERIC(20,2) NOT NULL DEFAULT 0,
    actual_price NUMERIC(20,2),
    sort_order INT NOT NULL DEFAULT 0
);

-- Initial config
INSERT INTO hauling.config VALUES ('hauling_rate_per_m3', '800', 'ISK per m3 for hauling fee');
INSERT INTO hauling.config VALUES ('shopper_fee_pct', '10', 'Personal shopper fee as percentage of total order value');
INSERT INTO hauling.config VALUES ('alliance_id', '99012532', 'Required alliance ID for access (Angry Miners Alliance.)');
