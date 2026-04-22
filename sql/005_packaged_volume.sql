-- Add packaged_volume column and import from SDE invVolumes
-- Source of truth — apply via: cat sql/005_packaged_volume.sql | sudo -u postgres psql -d hauling

-- Add column (nullable — only ships/some items have packaged volumes)
ALTER TABLE hauling.eve_types ADD COLUMN IF NOT EXISTS packaged_volume NUMERIC(20,4);

-- Staging table for invVolumes import
CREATE TEMP TABLE staging_volumes (
    type_id INT,
    volume NUMERIC(20,4)
);

-- Import (has header row)
\copy staging_volumes FROM '/tmp/sde/invVolumes.csv' WITH (FORMAT csv, HEADER true);

-- Update eve_types with packaged volumes
UPDATE hauling.eve_types e
SET packaged_volume = s.volume
FROM staging_volumes s
WHERE e.type_id = s.type_id;

DROP TABLE staging_volumes;
