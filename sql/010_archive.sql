-- Archive feature — admin-only soft-archive for completed orders
-- Source of truth — apply via: cat sql/010_archive.sql | sudo -u postgres psql -d hauling
--
-- Archived orders disappear from the main orders page (for all roles) but remain queryable
-- via a dedicated admin-only view. Once archived, an order becomes view-only forever — no
-- unarchive, no status changes, no edits.

ALTER TABLE hauling.orders
    ADD COLUMN IF NOT EXISTS archived BOOLEAN NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS archived_at TIMESTAMPTZ;
