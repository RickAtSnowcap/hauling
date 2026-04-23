#!/usr/bin/env python3
"""Update packaged volumes from ESI for items that might have them."""

import json
import urllib.request
import psycopg2
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime

DB_CONN = "host=localhost dbname=hauling user=hauling password=hauling_4f94e5707c72c400 options=-csearch_path=hauling"


def fetch_packaged_volume(type_id):
    """Fetch packaged_volume from ESI for a single type_id."""
    try:
        url = f"https://esi.evetech.net/latest/universe/types/{type_id}/?datasource=tranquility"
        req = urllib.request.Request(url, headers={"Accept": "application/json"})
        with urllib.request.urlopen(req, timeout=10) as resp:
            data = json.loads(resp.read())
            pv = data.get("packaged_volume")
            if pv is not None:
                return (type_id, float(pv))
    except Exception:
        pass
    return None


def main():
    conn = psycopg2.connect(DB_CONN)
    cur = conn.cursor()

    # Only check items with volume > 500 m3 (ships, deployables, containers)
    # Small items like ammo/modules never have packaged volumes
    cur.execute("SELECT type_id FROM hauling.eve_types WHERE volume > 500 ORDER BY type_id")
    type_ids = [row[0] for row in cur.fetchall()]

    print(f"{datetime.now().isoformat()} Checking {len(type_ids)} items with volume > 500 m3...")

    updated = 0
    errors = 0
    with ThreadPoolExecutor(max_workers=20) as pool:
        futures = {pool.submit(fetch_packaged_volume, tid): tid for tid in type_ids}
        for i, future in enumerate(as_completed(futures), 1):
            result = future.result()
            if result:
                type_id, packed_vol = result
                cur.execute(
                    "UPDATE hauling.eve_types SET packaged_volume = %s WHERE type_id = %s AND (packaged_volume IS NULL OR packaged_volume <> %s)",
                    (packed_vol, type_id, packed_vol),
                )
                if cur.rowcount > 0:
                    updated += 1
            if i % 100 == 0:
                print(f"  Checked {i}/{len(type_ids)}...")
                conn.commit()

    conn.commit()
    cur.close()
    conn.close()
    print(
        f"{datetime.now().isoformat()} Complete: checked {len(type_ids)} items, updated {updated} packaged volumes"
    )


if __name__ == "__main__":
    main()
