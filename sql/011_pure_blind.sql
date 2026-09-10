-- Pure Blind relocation — Edge Dancers (98558686) in Pure Blind, PushX courier,
-- data-driven routes + dynamic isotope-based fuel pricing.
-- Source of truth — apply via: cat sql/011_pure_blind.sql | sudo -u postgres psql -d hauling
--
-- Consolidates what was originally developed as two passes (pure_blind +
-- routes_dynamic_fuel) into the single final state: the intermediate flat PushX
-- fee / flat fuel-per-m³ keys are omitted entirely rather than added-then-deleted.
--
-- Major changes vs the prior Spire/Evola model:
-- 1. Corp-only service (Edge Dancers 98558686) replaces alliance-wide gate
-- 2. PushX replaces Evola as third-party courier; routes are data-driven (hauling.routes)
-- 3. Dynamic fuel: store round_trip_isotopes per route; fuel computed at order time from
--    the live Jita isotope price (isotope_price × round_trip_isotopes / cargo_capacity_m3)
-- 4. Tiered PushX pricing — BR/DST (≤62,500 m³) vs Freighter (>62,500 m³)
-- 5. Cargo capacity 370,000 m³ (Rhea); flat 10M shopper fee

-- 1. Retire Spire/Evola-era config keys (and the old flat fuel-per-m³ key, now
--    superseded by the per-route dynamic-fuel model).
DELETE FROM hauling.config WHERE key IN (
    'evola_isk_per_m3', 'evola_collateral_pct', 'evola_minimum',
    'expedite_fee', 'shopper_fee_per_item', 'shopper_fee_minimum',
    'alliance_id', 'fuel_isk_per_m3'
);

-- 2. Data-driven route table — replaces the hardcoded ValidateRoute(). Each route
--    carries its round-trip isotope burn for the dynamic-fuel calculation.
CREATE TABLE IF NOT EXISTS hauling.routes (
    route_id   SERIAL PRIMARY KEY,
    origin     TEXT NOT NULL,
    destination TEXT NOT NULL,
    round_trip_isotopes INT NOT NULL,
    has_pushx  BOOLEAN NOT NULL DEFAULT false,
    allows_shopping BOOLEAN NOT NULL DEFAULT false,
    label_origin TEXT,
    label_destination TEXT,
    UNIQUE(origin, destination)
);

INSERT INTO hauling.routes (origin, destination, round_trip_isotopes, has_pushx, allows_shopping, label_origin, label_destination) VALUES
    -- Jita inbound
    ('Jita',     'RD-G2R',  41642,  true,  true,  'Jita (market hub)',        'RD-G2R (RD-Set-Jump)'),
    ('Jita',     'DK-FXK',  58054,  true,  true,  'Jita (market hub)',        'DK-FXK'),
    -- Jita outbound
    ('RD-G2R',   'Jita',    41642,  true,  false, 'RD-G2R (RD-Set-Jump)',     'Jita 4-4'),
    ('DK-FXK',   'Jita',    58054,  true,  false, 'DK-FXK',                  'Jita 4-4'),
    -- Asset safety inbound
    ('Odebeinn', 'RD-G2R',  172142, false, false, 'Odebeinn (asset safety)',  'RD-G2R (RD-Set-Jump)'),
    ('Odebeinn', 'DK-FXK',  188554, false, false, 'Odebeinn (asset safety)',  'DK-FXK'),
    ('Konora',   'RD-G2R',  172142, false, false, 'Konora (asset safety)',    'RD-G2R (RD-Set-Jump)'),
    ('Konora',   'DK-FXK',  188554, false, false, 'Konora (asset safety)',    'DK-FXK'),
    -- Inter-system shuttle
    ('RD-G2R',   'DK-FXK',  16412,  false, false, 'RD-G2R (RD-Set-Jump)',    'DK-FXK'),
    ('DK-FXK',   'RD-G2R',  16412,  false, false, 'DK-FXK',                  'RD-G2R (RD-Set-Jump)')
ON CONFLICT (origin, destination) DO UPDATE SET
    round_trip_isotopes = EXCLUDED.round_trip_isotopes,
    has_pushx = EXCLUDED.has_pushx,
    allows_shopping = EXCLUDED.allows_shopping,
    label_origin = EXCLUDED.label_origin,
    label_destination = EXCLUDED.label_destination;

GRANT SELECT ON hauling.routes TO hauling;

-- 3. Config — corp gate, flat shopper fee, dynamic-fuel inputs, tiered PushX fees.
INSERT INTO hauling.config (key, value, description) VALUES
    ('corp_id',                  '98558686',  'Edge Dancers corporation ID — login restricted to corp members'),
    ('shopper_fee',              '10000000',  'Flat personal shopper fee per order (Jita-origin only)'),
    ('cargo_capacity_m3',        '370000',    'Rhea cargo capacity in m³ — denominator in the fuel formula'),
    ('isotope_type_id',          '17888',     'Nitrogen Isotopes type_id for ESI price lookup'),
    ('pushx_fee_small',          '9000000',   'PushX standard fee for ≤62,500 m³ (BR/DST tier)'),
    ('pushx_fee_large',          '27000000',  'PushX standard fee for >62,500 m³ (Freighter tier)'),
    ('pushx_rush_fee_small',     '59000000',  'PushX rush fee for ≤62,500 m³ (BR/DST tier)'),
    ('pushx_rush_fee_large',     '77000000',  'PushX rush fee for >62,500 m³ (Freighter tier)'),
    ('pushx_volume_tier_break',  '62500',     'Volume threshold between BR/DST and Freighter PushX tiers')
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, description = EXCLUDED.description;

-- 4. Order volume cap → Rhea capacity.
UPDATE hauling.config SET value = '370000',
    description = 'Maximum order volume in m³ (Rhea cargo capacity)'
WHERE key = 'max_order_m3';
