export interface ParsedItem {
  name: string;
  quantity: number;
}

export function parsePyfaFit(text: string): ParsedItem[] {
  const lines = text.trim().split('\n');
  if (lines.length === 0) return [];

  const items: Map<string, number> = new Map();

  // First line: [ShipName, FitName]
  const headerMatch = lines[0].match(/^\[(.+?),/);
  if (headerMatch) {
    const shipName = headerMatch[1].trim();
    items.set(shipName, (items.get(shipName) || 0) + 1);
  }

  for (let i = 1; i < lines.length; i++) {
    let line = lines[i].trim();
    if (!line) continue;
    if (line.startsWith('[Empty ')) continue;

    // Check for xN quantity suffix
    let qty = 1;
    const qtyMatch = line.match(/\s+x(\d+)$/);
    if (qtyMatch) {
      qty = parseInt(qtyMatch[1]);
      line = line.slice(0, -qtyMatch[0].length).trim();
    }

    // Check for charge after comma (module, charge)
    const commaIdx = line.lastIndexOf(', ');
    if (commaIdx > 0) {
      const moduleName = line.slice(0, commaIdx).trim();
      const chargeName = line.slice(commaIdx + 2).trim();
      items.set(moduleName, (items.get(moduleName) || 0) + qty);
      items.set(chargeName, (items.get(chargeName) || 0) + qty);
    } else {
      items.set(line, (items.get(line) || 0) + qty);
    }
  }

  return Array.from(items.entries()).map(([name, quantity]) => ({ name, quantity }));
}

export interface ParsedTransaction {
  name: string;
  unitPrice: number;
}

export function parseTransactionLog(text: string): Map<string, number> {
  // Returns map of item name → highest unit price
  const prices = new Map<string, number>();
  const lines = text.trim().split('\n');

  for (const line of lines) {
    if (!line.trim()) continue;
    const cols = line.split('\t');
    // Columns: Date, Qty, Item Name, Unit Price, Total Price, Seller, Station
    if (cols.length < 4) continue;

    const name = cols[2]?.trim();
    if (!name) continue;

    // Parse unit price — remove "ISK" suffix and commas
    const priceStr = cols[3]?.trim().replace(/\s*ISK\s*$/, '').replace(/,/g, '');
    const price = parseFloat(priceStr);
    if (isNaN(price) || price <= 0) continue;

    // Keep highest price per item
    const existing = prices.get(name);
    if (!existing || price > existing) {
      prices.set(name, price);
    }
  }

  return prices;
}

export function parseContainerContents(text: string): ParsedItem[] {
  const lines = text.trim().split('\n');
  const items: ParsedItem[] = [];

  for (const line of lines) {
    if (!line.trim()) continue;
    const cols = line.split('\t');
    const name = cols[0]?.trim();
    if (!name) continue;

    let qty = 1;
    if (cols[1]?.trim()) {
      qty = parseInt(cols[1].trim().replace(/,/g, '')) || 1;
    }

    // Normalize player corpses to generic "Corpse" (handles both tab-delimited and space-mangled pastes)
    const corpseName = name.includes("'s Frozen Corpse") ? "Corpse" : name;
    const existing = items.find(i => i.name === corpseName);
    if (existing) {
      existing.quantity += qty;
    } else {
      items.push({ name: corpseName, quantity: qty });
    }
  }

  return items;
}
