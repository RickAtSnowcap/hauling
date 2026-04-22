-- 003_import_sde.sql
-- Import EVE Online item type data from Fuzzwork SDE dumps

BEGIN;

-- 1. Create staging tables
CREATE TEMP TABLE staging_types (
    type_id       INTEGER,
    group_id      INTEGER,
    type_name     TEXT,
    mass          NUMERIC,
    volume        NUMERIC,
    capacity      NUMERIC,
    portion_size  INTEGER,
    race_id       INTEGER,
    base_price    NUMERIC,
    published     INTEGER,
    market_group_id INTEGER,
    icon_id       INTEGER,
    sound_id      INTEGER,
    graphic_id    INTEGER
);

CREATE TEMP TABLE staging_groups (
    group_id                INTEGER,
    category_id             INTEGER,
    group_name              TEXT,
    icon_id                 TEXT,
    use_base_price          INTEGER,
    anchored                INTEGER,
    anchorable              INTEGER,
    fittable_non_singleton  INTEGER,
    published               INTEGER
);

-- 2. Load CSVs
COPY staging_types FROM '/tmp/sde/invTypes.csv' WITH (FORMAT csv, NULL '\N');
COPY staging_groups FROM '/tmp/sde/invGroups.csv' WITH (FORMAT csv, HEADER true, NULL 'None');

-- 3. Insert into hauling.eve_types
INSERT INTO hauling.eve_types (type_id, type_name, volume, market_group_id, group_id, category_id)
SELECT t.type_id, t.type_name, COALESCE(t.volume, 0), t.market_group_id, t.group_id, g.category_id
FROM staging_types t
LEFT JOIN staging_groups g ON t.group_id = g.group_id
WHERE t.market_group_id IS NOT NULL AND t.published = 1
ON CONFLICT (type_id) DO UPDATE SET
    type_name       = EXCLUDED.type_name,
    volume          = EXCLUDED.volume,
    market_group_id = EXCLUDED.market_group_id,
    group_id        = EXCLUDED.group_id,
    category_id     = EXCLUDED.category_id;

COMMIT;
