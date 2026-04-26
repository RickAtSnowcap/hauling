import { useState, useEffect, useRef } from 'react';
import type { ItemResult, OrderItemInput, ConfigResponse, OrderDetail } from '../types';
import { searchItems, getJitaPrice, getConfig, createOrder, matchItems, updateOrderItems } from '../api';
import { parsePyfaFit, parseContainerContents } from '../parsers';
import './NewOrder.css';

interface Props {
  editingOrder?: OrderDetail | null;
  onEditComplete?: () => void;
}

export default function NewOrder({ editingOrder, onEditComplete }: Props) {
  const [origin, setOrigin] = useState('Jita');
  const [destination, setDestination] = useState('E-BYOS');
  const [shopRequested, setShopRequested] = useState(false);
  const [items, setItems] = useState<OrderItemInput[]>([]);
  const [searchQuery, setSearchQuery] = useState('');
  const [searchResults, setSearchResults] = useState<ItemResult[]>([]);
  const [showResults, setShowResults] = useState(false);
  const [config, setConfig] = useState<ConfigResponse | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [success, setSuccess] = useState<number | null>(null);
  const [error, setError] = useState('');
  const [warning, setWarning] = useState('');
  const [notes, setNotes] = useState('');
  const [inputMode, setInputMode] = useState<'search' | 'paste'>('search');
  const [pasteText, setPasteText] = useState('');
  const [fitCopies, setFitCopies] = useState(1);
  const [importing, setImporting] = useState(false);
  const [unmatchedItems, setUnmatchedItems] = useState<string[]>([]);
  const searchRef = useRef<HTMLDivElement>(null);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  const isEditing = !!editingOrder;

  useEffect(() => { getConfig().then(setConfig); }, []);

  // Load editing order items
  useEffect(() => {
    if (editingOrder) {
      setOrigin(editingOrder.origin_system);
      setDestination(editingOrder.destination_system);
      setShopRequested(editingOrder.shop_requested);
      setNotes(editingOrder.notes || '');
      setItems(editingOrder.items.map(i => ({
        type_id: i.type_id,
        type_name: i.type_name,
        quantity: i.quantity,
        volume_per_unit: i.volume_per_unit,
        estimated_price: i.estimated_price
      })));
    }
  }, [editingOrder]);

  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (searchRef.current && !searchRef.current.contains(e.target as Node)) setShowResults(false);
    }
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, []);

  // Fetch prices retroactively when Personal Shopper is toggled on
  useEffect(() => {
    if (!shopRequested || items.length === 0) return;
    const needsPrices = items.filter(i => i.estimated_price === 0 && i.type_id > 0);
    if (needsPrices.length === 0) return;

    (async () => {
      const updated = [...items];
      for (const item of needsPrices) {
        const price = await getJitaPrice(item.type_id);
        const idx = updated.findIndex(i => i.type_id === item.type_id);
        if (idx >= 0) updated[idx] = { ...updated[idx], estimated_price: price };
      }
      setItems(updated);
    })();
  }, [shopRequested]);

  function handleSearch(q: string) {
    setSearchQuery(q);
    if (debounceRef.current) clearTimeout(debounceRef.current);
    if (q.length < 2) { setSearchResults([]); setShowResults(false); return; }
    debounceRef.current = setTimeout(async () => {
      const results = await searchItems(q);
      setSearchResults(results);
      setShowResults(true);
    }, 300);
  }

  async function addItem(item: ItemResult) {
    if (items.some(i => i.type_id === item.type_id)) {
      setWarning(`${item.type_name} is already in the order. Adjust the quantity instead.`);
      setTimeout(() => setWarning(''), 3000);
      setShowResults(false);
      setSearchQuery('');
      return;
    }
    setShowResults(false);
    setSearchQuery('');
    const price = shopRequested ? await getJitaPrice(item.type_id) : 0;
    setItems(prev => [...prev, {
      type_id: item.type_id,
      type_name: item.type_name,
      quantity: 1,
      volume_per_unit: item.volume,
      estimated_price: price
    }]);
  }

  async function handleImport() {
    if (!pasteText.trim()) return;
    setImporting(true);
    setError('');
    setUnmatchedItems([]);

    try {
      // Auto-detect format: starts with [ = Pyfa fit, contains tabs = inventory
      const isFit = pasteText.trimStart().startsWith('[');
      const raw = isFit
        ? parsePyfaFit(pasteText)
        : parseContainerContents(pasteText);
      const multiplier = isFit ? fitCopies : 1;
      const parsed = raw.map(p => ({ ...p, quantity: p.quantity * multiplier }));

      if (parsed.length === 0) {
        setError('No items found in pasted text.');
        setImporting(false);
        return;
      }

      // Handle special items that aren't in the SDE
      const specialItems: { name: string; type_id: number; volume: number; qty: number }[] = [];
      const normalParsed = parsed.filter(p => {
        if (p.name === 'Corpse') {
          const existing = specialItems.find(s => s.name === 'Corpse');
          if (existing) existing.qty += p.quantity;
          else specialItems.push({ name: 'Corpse', type_id: 25, volume: 2, qty: p.quantity });
          return false;
        }
        return true;
      });

      const uniqueNames = [...new Set(normalParsed.map(p => p.name))];
      const matched = await matchItems(uniqueNames);

      // Build a map of matched names (case-insensitive)
      const matchMap = new Map<string, ItemResult>();
      for (const m of matched) {
        matchMap.set(m.type_name.toLowerCase(), m);
      }

      const unmatched: string[] = [];
      const toAdd: { item: ItemResult; qty: number }[] = [];

      for (const p of normalParsed) {
        const found = matchMap.get(p.name.toLowerCase());
        if (found) {
          const existing = toAdd.find(a => a.item.type_id === found.type_id);
          if (existing) {
            existing.qty += p.quantity;
          } else {
            toAdd.push({ item: found, qty: p.quantity });
          }
        } else {
          if (!unmatched.includes(p.name)) unmatched.push(p.name);
        }
      }

      // Fetch prices and add to order
      const newItems: OrderItemInput[] = [...items];
      for (const { item, qty } of toAdd) {
        const existingIdx = newItems.findIndex(i => i.type_id === item.type_id);
        if (existingIdx >= 0) {
          newItems[existingIdx] = { ...newItems[existingIdx], quantity: newItems[existingIdx].quantity + qty };
        } else {
          const price = shopRequested ? await getJitaPrice(item.type_id) : 0;
          newItems.push({
            type_id: item.type_id,
            type_name: item.type_name,
            quantity: qty,
            volume_per_unit: item.volume,
            estimated_price: price
          });
        }
      }

      // Add special items (corpses etc.)
      for (const special of specialItems) {
        const existingIdx = newItems.findIndex(i => i.type_name === special.name);
        if (existingIdx >= 0) {
          newItems[existingIdx] = { ...newItems[existingIdx], quantity: newItems[existingIdx].quantity + special.qty };
        } else {
          newItems.push({
            type_id: special.type_id,
            type_name: special.name,
            quantity: special.qty,
            volume_per_unit: special.volume,
            estimated_price: 0
          });
        }
      }

      setItems(newItems);
      if (unmatched.length > 0) setUnmatchedItems(unmatched);
      setPasteText('');
      setInputMode('search');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Import failed');
    } finally {
      setImporting(false);
    }
  }

  function updateQuantity(typeId: number, qty: number) {
    if (qty < 1) return;
    setItems(prev => prev.map(i => i.type_id === typeId ? { ...i, quantity: qty } : i));
  }

  function removeItem(typeId: number) {
    setItems(prev => prev.filter(i => i.type_id !== typeId));
  }

  const totalM3 = items.reduce((sum, i) => sum + i.volume_per_unit * i.quantity, 0);
  const totalEstIsk = items.reduce((sum, i) => sum + i.estimated_price * i.quantity, 0);
  const ratePerM3 = origin === 'Jita' ? (config?.jita_rate_per_m3 ?? 1050) : (config?.odebeinn_rate_per_m3 ?? 650);
  const haulingFee = totalM3 * ratePerM3;
  const itemCount = items.length;
  const shopperFee = shopRequested && config
    ? Math.max(itemCount * config.shopper_fee_per_item, config.shopper_fee_minimum)
    : 0;
  const grandTotal = (shopRequested ? totalEstIsk : 0) + haulingFee + shopperFee;
  const maxM3 = config?.max_order_m3 ?? 300000;
  const overCapacity = totalM3 > maxM3;

  async function handleSubmit() {
    if (items.length === 0 || overCapacity) return;
    setSubmitting(true);
    setError('');
    try {
      if (isEditing && editingOrder) {
        await updateOrderItems(editingOrder.order_id, shopRequested, items, origin, destination, notes);
        onEditComplete?.();
      } else {
        const orderId = await createOrder(shopRequested, items, origin, destination, notes);
        setSuccess(orderId);
        setItems([]);
        setNotes('');
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to submit order');
    } finally {
      setSubmitting(false);
    }
  }

  function formatIsk(n: number): string {
    return n.toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 0 });
  }

  function formatM3(n: number): string {
    return n.toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 0 });
  }

  return (
    <div className="new-order">
      {success && (
        <div className="success-banner">
          Order #{success} submitted successfully!
          <button onClick={() => setSuccess(null)}>Dismiss</button>
        </div>
      )}

      {isEditing && (
        <div className="editing-banner">
          Editing Order #{editingOrder!.order_id}
          <button onClick={onEditComplete}>Cancel Edit</button>
        </div>
      )}

      {unmatchedItems.length > 0 && (
        <div className="unmatched-warning">
          <div className="unmatched-header">
            <span>Could not match {unmatchedItems.length} item(s):</span>
            <button onClick={() => setUnmatchedItems([])}>Dismiss</button>
          </div>
          <ul>
            {unmatchedItems.map((name, idx) => <li key={idx}>{name}</li>)}
          </ul>
        </div>
      )}

      <div className="pricing-link">
        <a href="/pricing">How are fees calculated?</a>
      </div>

      <div className="route-selection">
        <div className="route-field">
          <label>Origin:</label>
          <select value={origin} onChange={e => { setOrigin(e.target.value); if (e.target.value !== 'Jita') setShopRequested(false); }}>
            <option value="Jita">Jita (market hub)</option>
            <option value="Odebeinn">Odebeinn (asset safety)</option>
          </select>
        </div>
        <div className="route-field">
          <label>Destination:</label>
          <select value={destination} onChange={e => setDestination(e.target.value)}>
            <option value="E-B957">E-B957 (Builders Edge)</option>
            <option value="E-BYOS">E-BYOS (The Forum)</option>
          </select>
        </div>
      </div>

      {origin === 'Jita' && (
        <div className="order-options">
          <label className="shop-toggle">
            <input type="checkbox" checked={shopRequested} onChange={e => setShopRequested(e.target.checked)} />
            <span>Personal Shopper</span>
            <span className="shop-hint">{shopRequested ? 'We buy items for you (per-item fee)' : 'You provide items at origin, we haul'}</span>
          </label>
        </div>
      )}

      <textarea
        className="notes-input"
        placeholder="Add notes for the hauler (optional)"
        value={notes}
        onChange={e => {
          setNotes(e.target.value);
          e.target.style.height = 'auto';
          e.target.style.height = e.target.scrollHeight + 'px';
        }}
        rows={2}
        maxLength={4000}
      />

      {warning && <div className="order-warning">{warning}</div>}

      <div className="input-mode-bar">
        <button className={inputMode === 'search' ? 'active' : ''} onClick={() => { setInputMode('search'); setPasteText(''); }}>Search Items</button>
        <button className={inputMode === 'paste' ? 'active' : ''} onClick={() => { setInputMode('paste'); setPasteText(''); }}>Paste Fit or Inventory</button>
      </div>

      {inputMode === 'search' && (
        <div className="item-search" ref={searchRef}>
          <input
            type="text"
            placeholder="Search for items..."
            value={searchQuery}
            onChange={e => handleSearch(e.target.value)}
            onFocus={() => searchResults.length > 0 && setShowResults(true)}
          />
          {showResults && searchResults.length > 0 && (
            <div className="search-dropdown">
              {searchResults.map(item => (
                <div key={item.type_id} className="search-item" onClick={() => addItem(item)}>
                  <span className="item-name">{item.type_name}</span>
                  <span className="item-vol">{item.volume.toFixed(2)} m3</span>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {inputMode === 'paste' && (
        <div className="paste-area">
          <textarea
            rows={10}
            placeholder={'Paste a Pyfa/EFT fit or inventory contents here...\n\nFits: [ShipName, FitName] followed by modules\nInventory: tab-delimited item list from EVE'}
            value={pasteText}
            onChange={e => setPasteText(e.target.value)}
          />
          {pasteText.trimStart().startsWith('[') && (
            <div className="fit-copies">
              <label>Copies:</label>
              <input type="number" min={1} value={fitCopies} onChange={e => setFitCopies(Math.max(1, parseInt(e.target.value) || 1))} />
            </div>
          )}
          <button className="import-btn" onClick={handleImport} disabled={importing || !pasteText.trim()}>
            {importing ? 'Importing...' : 'Import'}
          </button>
        </div>
      )}

      {items.length > 0 && (
        <>
          <table className="order-table">
            <thead>
              <tr>
                <th>Item</th>
                <th>Qty</th>
                <th>Vol/Unit</th>
                <th>Line m³</th>
                {shopRequested && <th>Est. Price</th>}
                {shopRequested && <th>Line Total</th>}
                <th></th>
              </tr>
            </thead>
            <tbody>
              {items.map(item => (
                <tr key={item.type_id}>
                  <td>{item.type_name}</td>
                  <td>
                    <input type="number" min={1} value={item.quantity}
                      onChange={e => updateQuantity(item.type_id, parseInt(e.target.value) || 1)} />
                  </td>
                  <td>{item.volume_per_unit.toFixed(2)}</td>
                  <td>{formatM3(item.volume_per_unit * item.quantity)}</td>
                  {shopRequested && <td>{formatIsk(item.estimated_price)}</td>}
                  {shopRequested && <td>{formatIsk(item.estimated_price * item.quantity)}</td>}
                  <td><button className="remove-btn" onClick={() => removeItem(item.type_id)}>x</button></td>
                </tr>
              ))}
            </tbody>
          </table>

          <div className="order-totals">
            <div className={`total-row ${overCapacity ? 'over-capacity' : ''}`}><span>Total Volume:</span><span>{formatM3(totalM3)} / {formatM3(maxM3)} m³</span></div>
            {shopRequested && <div className="total-row"><span>Estimated Item Cost:</span><span>{formatIsk(totalEstIsk)} ISK</span></div>}
            <div className="total-row"><span>Hauling Fee ({origin === 'Jita' ? config?.jita_rate_per_m3 : config?.odebeinn_rate_per_m3} ISK/m³):</span><span>{formatIsk(haulingFee)} ISK</span></div>
            {shopRequested && <div className="total-row"><span>Shopper Fee ({itemCount} items × {formatIsk(config?.shopper_fee_per_item ?? 0)} ISK, min {formatIsk(config?.shopper_fee_minimum ?? 0)}):</span><span>{formatIsk(shopperFee)} ISK</span></div>}
            <div className="total-row grand-total"><span>{shopRequested ? 'Grand Total:' : 'Total Fee:'}</span><span>{formatIsk(shopRequested ? grandTotal : haulingFee)} ISK</span></div>
          </div>

          {overCapacity && <div className="order-error">Order exceeds maximum JF cargo capacity of {formatM3(maxM3)} m³. Remove items or reduce quantities.</div>}
          {error && <div className="order-error">{error}</div>}

          <button className="submit-btn" onClick={handleSubmit} disabled={submitting || items.length === 0 || overCapacity}>
            {submitting ? 'Submitting...' : isEditing ? 'Update Order' : 'Submit Order'}
          </button>
        </>
      )}
    </div>
  );
}
