-- Path B pricing — Evola cost pass-through for Jita-origin orders
-- Source of truth — apply via: cat sql/009_evola_passthrough.sql | sudo -u postgres psql -d hauling
--
-- Replaces flat per-m³ rates with a component model that honestly passes through
-- Evola's pricing (400 ISK/m³ base + 1% collateral, 32.5M minimum). The flat rate
-- under-charged on high-ISK-per-m³ cargo (ships, deadspace) because the 1% collateral
-- charge on the value side dwarfed the base m³ charge.
--
-- New Jita formula:
--   max(evola_isk_per_m3 × m³ + evola_collateral_pct × cargo_value, evola_minimum)
--   + fuel_isk_per_m3 × m³
--   + service_isk_per_m3 × m³
--
-- Odebeinn formula (no Evola leg):
--   (fuel_isk_per_m3 + service_isk_per_m3) × m³

-- Retire the flat-rate keys (replaced by the component values below)
DELETE FROM hauling.config WHERE key IN ('jita_rate_per_m3', 'odebeinn_rate_per_m3');

-- Evola pass-through components
INSERT INTO hauling.config (key, value, description) VALUES
    ('evola_isk_per_m3',     '400',      'Evola Deliveries base rate per m³ (Jita-origin only)'),
    ('evola_collateral_pct', '0.01',     'Evola collateral percentage of cargo value (Jita-origin only)'),
    ('evola_minimum',        '32500000', 'Evola Deliveries minimum contract cost (Jita-origin only)'),
    ('fuel_isk_per_m3',      '263',      'Jump freighter fuel cost per m³ (Odebeinn → Z19-B8 round trip)'),
    ('service_isk_per_m3',   '0',        'Hauler service fee per m³ (waived during Spire settle-in; expected ~143 when restored)')
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, description = EXCLUDED.description;
