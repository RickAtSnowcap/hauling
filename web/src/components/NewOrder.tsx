import { useState, useEffect, useRef } from 'react';
import type { ItemResult, OrderItemInput, ConfigResponse } from '../types';
import { searchItems, getJitaPrice, getConfig, createOrder } from '../api';
import './NewOrder.css';

export default function NewOrder() {
  const [shopRequested, setShopRequested] = useState(false);
  const [items, setItems] = useState<OrderItemInput[]>([]);
  const [searchQuery, setSearchQuery] = useState('');
  const [searchResults, setSearchResults] = useState<ItemResult[]>([]);
  const [showResults, setShowResults] = useState(false);
  const [config, setConfig] = useState<ConfigResponse | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [success, setSuccess] = useState<number | null>(null);
  const [error, setError] = useState('');
  const searchRef = useRef<HTMLDivElement>(null);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  useEffect(() => { getConfig().then(setConfig); }, []);

  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (searchRef.current && !searchRef.current.contains(e.target as Node)) setShowResults(false);
    }
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, []);

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
    if (items.some(i => i.type_id === item.type_id)) return;
    setShowResults(false);
    setSearchQuery('');
    const price = await getJitaPrice(item.type_id);
    setItems(prev => [...prev, {
      type_id: item.type_id,
      type_name: item.type_name,
      quantity: 1,
      volume_per_unit: item.volume,
      estimated_price: price
    }]);
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
  const haulingFee = config ? totalM3 * config.hauling_rate_per_m3 : 0;
  const shopperFee = shopRequested && config ? totalEstIsk * (config.shopper_fee_pct / 100) : 0;
  const grandTotal = totalEstIsk + haulingFee + shopperFee;

  async function handleSubmit() {
    if (items.length === 0) return;
    setSubmitting(true);
    setError('');
    try {
      const orderId = await createOrder(shopRequested, items);
      setSuccess(orderId);
      setItems([]);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to submit order');
    } finally {
      setSubmitting(false);
    }
  }

  function formatIsk(n: number): string {
    return n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  }

  function formatM3(n: number): string {
    return n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  }

  return (
    <div className="new-order">
      {success && (
        <div className="success-banner">
          Order #{success} submitted successfully!
          <button onClick={() => setSuccess(null)}>Dismiss</button>
        </div>
      )}

      <div className="order-options">
        <label className="shop-toggle">
          <input type="checkbox" checked={shopRequested} onChange={e => setShopRequested(e.target.checked)} />
          <span>Personal Shopper</span>
          <span className="shop-hint">{shopRequested ? `We buy items for you (+${config?.shopper_fee_pct ?? 10}% fee)` : 'You provide items in Jita, we haul'}</span>
        </label>
      </div>

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

      {items.length > 0 && (
        <>
          <table className="order-table">
            <thead>
              <tr>
                <th>Item</th>
                <th>Qty</th>
                <th>Vol/Unit</th>
                <th>Line m3</th>
                <th>Est. Price</th>
                <th>Line Total</th>
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
                  <td>{formatIsk(item.estimated_price)}</td>
                  <td>{formatIsk(item.estimated_price * item.quantity)}</td>
                  <td><button className="remove-btn" onClick={() => removeItem(item.type_id)}>x</button></td>
                </tr>
              ))}
            </tbody>
          </table>

          <div className="order-totals">
            <div className="total-row"><span>Total Volume:</span><span>{formatM3(totalM3)} m3</span></div>
            <div className="total-row"><span>Estimated Item Cost:</span><span>{formatIsk(totalEstIsk)} ISK</span></div>
            <div className="total-row"><span>Hauling Fee ({config?.hauling_rate_per_m3 ?? 0} ISK/m3):</span><span>{formatIsk(haulingFee)} ISK</span></div>
            {shopRequested && <div className="total-row"><span>Shopper Fee ({config?.shopper_fee_pct ?? 0}%):</span><span>{formatIsk(shopperFee)} ISK</span></div>}
            <div className="total-row grand-total"><span>Grand Total:</span><span>{formatIsk(grandTotal)} ISK</span></div>
          </div>

          {error && <div className="order-error">{error}</div>}

          <button className="submit-btn" onClick={handleSubmit} disabled={submitting || items.length === 0}>
            {submitting ? 'Submitting...' : 'Submit Order'}
          </button>
        </>
      )}
    </div>
  );
}
