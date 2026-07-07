import { useState, useEffect } from 'react';
import { copyText } from '../copyText';
import type { UserInfo, OrderSummary, OrderDetail } from '../types';
import { listArchivedOrders, getOrder } from '../api';
import './OrderList.css';
import './ArchivedOrdersPage.css';

interface Props {
  user: UserInfo;
  onLogout: () => void;
}

function statusClass(status: string): string {
  return `status-${status.replace('_', '-')}`;
}

function formatIsk(n: number | null): string {
  if (n === null) return '--';
  return n.toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 0 });
}

function formatDate(iso: string | null): string {
  if (!iso) return '--';
  return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric', hour: '2-digit', minute: '2-digit' });
}

export default function ArchivedOrdersPage({ user, onLogout }: Props) {
  const [orders, setOrders] = useState<OrderSummary[]>([]);
  const [selectedOrder, setSelectedOrder] = useState<OrderDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [copiedItemId, setCopiedItemId] = useState<number | null>(null);
  const [copiedList, setCopiedList] = useState(false);

  useEffect(() => {
    (async () => {
      try { setOrders(await listArchivedOrders()); } catch { /* ignore */ }
      setLoading(false);
    })();
  }, []);

  async function selectOrder(id: number) {
    try { setSelectedOrder(await getOrder(id)); } catch { /* ignore */ }
  }

  if (loading) return <div className="loading">Loading archived orders...</div>;

  return (
    <div className="archived-page">
      <header className="top-bar">
        <div className="top-left">
          <h1>Angry Hauling — Archived Orders</h1>
        </div>
        <div className="top-right">
          <a className="archived-link" href="/">← Back to Angry Hauling</a>
          <span className="char-name">{user.character_name}</span>
          <span className={`role-badge role-${user.role}`}>{user.role}</span>
          <button className="logout-btn" onClick={onLogout}>Logout</button>
        </div>
      </header>

      <div className="archived-note">
        Archived orders are view-only. They no longer appear on the main orders page.
      </div>

      <div className="order-list">
        <div className="orders-panel">
          <h3>Archived Orders ({orders.length})</h3>
          {orders.length === 0 && <p className="no-orders">No archived orders yet.</p>}
          {orders.map(order => (
            <div key={order.order_id}
              className={`order-card ${selectedOrder?.order_id === order.order_id ? 'selected' : ''}`}
              onClick={() => selectOrder(order.order_id)}>
              <div className="order-card-top">
                <span className="order-id">#{order.order_id}</span>
                <span className={`status-badge ${statusClass(order.status)}`}>{order.status.replace('_', ' ')}</span>
                <span className="archived-tag">archived {formatDate(order.archived_at)}</span>
              </div>
              <div className="order-card-meta">
                <span>{order.character_name}</span>
                <span>{order.total_m3.toFixed(0)} m3</span>
                <span>{order.origin_system} → {order.destination_system}</span>
                <span>{order.shop_requested ? 'Shop+Haul' : 'Haul Only'}</span>
                {order.expedite && <span>Expedite</span>}
                {order.assigned_to_name && <span>Hauler: {order.assigned_to_name}</span>}
              </div>
              <div className="order-card-date">Created {formatDate(order.created_at)}</div>
            </div>
          ))}
        </div>

        {selectedOrder && (
          <div className="order-detail">
            <div className="detail-header">
              <h3>Order #{selectedOrder.order_id}</h3>
              <span className={`status-badge ${statusClass(selectedOrder.status)}`}>{selectedOrder.status.replace('_', ' ')}</span>
              <span className="archived-tag">archived {formatDate(selectedOrder.archived_at)}</span>
            </div>
            <div className="detail-info">
              <span>By: {selectedOrder.character_name}</span>
              <span>Route: {selectedOrder.origin_system} → {selectedOrder.destination_system}</span>
              <span>Type: {selectedOrder.shop_requested ? 'Shop + Haul' : 'Haul Only'}</span>
              {selectedOrder.expedite && <span>Expedite: Yes</span>}
              {selectedOrder.assigned_to_name && <span>Hauler: {selectedOrder.assigned_to_name}</span>}
              <span>Created: {formatDate(selectedOrder.created_at)}</span>
            </div>

            {selectedOrder.notes && (
              <div className="order-notes">
                <span className="notes-label">Notes:</span>
                <p>{selectedOrder.notes}</p>
              </div>
            )}

            <button className="copy-list-btn" onClick={() => {
              const text = selectedOrder.items.map(i => `${i.type_name} ${i.quantity}`).join('\n');
              copyText(text);
              setCopiedList(true);
              setTimeout(() => setCopiedList(false), 2000);
            }}>
              {copiedList ? '✓ Copied' : 'Copy Order'}
            </button>

            <table className="detail-table">
              <thead>
                <tr>
                  <th>Item</th>
                  <th>Qty</th>
                  <th>m³</th>
                  {selectedOrder.shop_requested && <th>Est. Price</th>}
                  {selectedOrder.shop_requested && <th>Actual (per unit)</th>}
                </tr>
              </thead>
              <tbody>
                {selectedOrder.items.map(item => (
                  <tr key={item.item_id}>
                    <td>
                      {item.type_name}
                      <button className="copy-item-btn" title="Copy item name" onClick={() => { copyText(item.type_name); setCopiedItemId(item.item_id); setTimeout(() => setCopiedItemId(null), 1500); }}>
                        {copiedItemId === item.item_id ? '✓' : '⧉'}
                      </button>
                    </td>
                    <td>{item.quantity}</td>
                    <td>{item.line_m3.toFixed(2)}</td>
                    {selectedOrder.shop_requested && <td>{formatIsk(item.estimated_price)}</td>}
                    {selectedOrder.shop_requested && <td>{formatIsk(item.actual_price)}</td>}
                  </tr>
                ))}
              </tbody>
            </table>

            <div className="detail-totals">
              <div><span>Total m³:</span><span>{selectedOrder.total_m3.toFixed(2)}</span></div>
              {selectedOrder.shop_requested && <div><span>Est. Cost:</span><span>{formatIsk(selectedOrder.total_estimated_isk)} ISK</span></div>}
              {selectedOrder.shop_requested && selectedOrder.total_actual_isk !== null && <div><span>Actual Cost:</span><span>{formatIsk(selectedOrder.total_actual_isk)} ISK</span></div>}
              <div><span>Hauling Fee:</span><span>{formatIsk(selectedOrder.hauling_fee)} ISK</span></div>
              {selectedOrder.shop_requested && selectedOrder.shopper_fee > 0 && <div><span>Shopper Fee (flat):</span><span>{formatIsk(selectedOrder.shopper_fee)} ISK</span></div>}
              {selectedOrder.expedite && selectedOrder.expedite_fee > 0 && <div><span>Expedite Fee:</span><span>{formatIsk(selectedOrder.expedite_fee)} ISK</span></div>}
              <div className="total-row grand-total"><span>Delivery Contract Amount:</span><span>{formatIsk(
                (selectedOrder.shop_requested
                  ? (selectedOrder.total_actual_isk ?? selectedOrder.total_estimated_isk) + selectedOrder.hauling_fee + selectedOrder.shopper_fee
                  : selectedOrder.hauling_fee)
                + selectedOrder.expedite_fee
              )} ISK</span></div>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
