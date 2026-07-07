-- Relocation to The Spire — Z19-B8 Black Canary fortizar
-- Source of truth — apply via: cat sql/008_relocate_spire.sql | sudo -u postgres psql -d hauling
--
-- Alliance relocated from Cobalt Edge to The Spire. All trips now return to Z19-B8.
-- Service fee waived during settle-in (re-add ~50M ISK once dust settles).

-- Default destination for new orders
ALTER TABLE hauling.orders ALTER COLUMN destination_system SET DEFAULT 'Z19-B8';

-- Evola expedite option — flat fee, Jita-origin only (Evola operates Leg 1)
-- expedite_fee is a per-order snapshot (like hauling_fee / shopper_fee) so changing the
-- config value later doesn't rewrite historical order totals.
ALTER TABLE hauling.orders
    ADD COLUMN IF NOT EXISTS expedite BOOLEAN NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS expedite_fee NUMERIC(20,2) NOT NULL DEFAULT 0;

-- Updated rates (service fee waived during settle-in)
--   Jita:     Evola 400 + fuel 263 + service 0 = 663 → rounded to 700
--   Odebeinn: fuel 263 + service 0 = 263 → rounded to 275
-- Fuel: 139,142 isotopes round-trip (69,571 one-way) × 662 ISK/isotope (live Jita 4-4 sell)
UPDATE hauling.config SET value = '700' WHERE key = 'jita_rate_per_m3';
UPDATE hauling.config SET value = '275' WHERE key = 'odebeinn_rate_per_m3';

-- Expedite fee — passes through Evola's expedited courier option (2-3 days → hours)
INSERT INTO hauling.config (key, value, description) VALUES
    ('expedite_fee', '150000000', 'Flat ISK fee for Evola expedite option')
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, description = EXCLUDED.description;
