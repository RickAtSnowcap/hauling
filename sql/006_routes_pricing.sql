-- Route and pricing model update
-- Source of truth — apply via: cat sql/006_routes_pricing.sql | sudo -u postgres psql -d hauling

-- Add origin/destination to orders
ALTER TABLE hauling.orders ADD COLUMN IF NOT EXISTS origin_system TEXT NOT NULL DEFAULT 'Jita';
ALTER TABLE hauling.orders ADD COLUMN IF NOT EXISTS destination_system TEXT NOT NULL DEFAULT 'E-BYOS';

-- Update config entries
UPDATE hauling.config SET value = '350000' WHERE key = 'max_order_m3';

-- Remove old pricing config
DELETE FROM hauling.config WHERE key IN ('hauling_rate_per_m3', 'shopper_fee_pct');

-- Add new pricing config
INSERT INTO hauling.config (key, value, description) VALUES
('jita_rate_per_m3', '1050', 'All-in hauling rate for Jita-origin orders (includes Evola + fuel + service)'),
('odebeinn_rate_per_m3', '650', 'Hauling rate for Odebeinn-origin orders (fuel + service only)'),
('shopper_fee_per_item', '1000000', 'Personal shopper fee per distinct line item in ISK'),
('shopper_fee_minimum', '10000000', 'Minimum personal shopper fee in ISK')
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, description = EXCLUDED.description;
