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

    // Normalize player corpses to generic "Corpse"
    const corpseName = name.endsWith("'s Frozen Corpse") ? "Corpse" : name;
    const existing = items.find(i => i.name === corpseName);
    if (existing) {
      existing.quantity += qty;
    } else {
      items.push({ name: corpseName, quantity: qty });
    }
  }

  return items;
}
