import { useState, useEffect } from 'react';
import { copyText } from '../copyText';
import type { UserInfo, OrderSummary, OrderDetail } from '../types';
import { listOrders, getOrder, updateOrderStatus, updateActualPrice, deleteOrder, assignHauler, listHaulers } from '../api';
import type { HaulerInfo } from '../api';
import './OrderList.css';

interface Props {
  user: UserInfo;
  onEditOrder: (order: OrderDetail) => void;
}

function statusClass(status: string): string {
  return `status-${status.replace('_', '-')}`;
}

function formatIsk(n: number | null): string {
  if (n === null) return '--';
  return n.toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 0 });
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric', hour: '2-digit', minute: '2-digit' });
}

export default function OrderList({ user, onEditOrder }: Props) {
  const [orders, setOrders] = useState<OrderSummary[]>([]);
  const [selectedOrder, setSelectedOrder] = useState<OrderDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [assignCharId, setAssignCharId] = useState('');
  const [haulers, setHaulers] = useState<HaulerInfo[]>([]);
  const [copiedItemId, setCopiedItemId] = useState<number | null>(null);

  const isPrivileged = user.role === 'hauler' || user.role === 'admin';
  const isAdmin = user.role === 'admin';

  useEffect(() => {
    loadOrders();
    if (isAdmin) listHaulers().then(setHaulers);
  }, []);

  async function loadOrders() {
    setLoading(true);
    try { setOrders(await listOrders()); } catch { /* ignore */ }
    setLoading(false);
  }

  async function selectOrder(id: number) {
    try {
      const order = await getOrder(id);
      setSelectedOrder(order);
      setAssignCharId(order.assigned_to ? String(order.assigned_to) : '');
    } catch { /* ignore */ }
  }

  async function handleStatusUpdate(orderId: number, status: string) {
    await updateOrderStatus(orderId, status);
    await loadOrders();
    if (selectedOrder?.order_id === orderId) setSelectedOrder(await getOrder(orderId));
  }

  const [savedItemId, setSavedItemId] = useState<number | null>(null);

  async function handleActualPrice(itemId: number, price: string) {
    const num = parseFloat(price);
    if (isNaN(num) || num <= 0) return;
    await updateActualPrice(itemId, num);
    setSavedItemId(itemId);
    setTimeout(() => setSavedItemId(null), 1500);
    if (selectedOrder) setSelectedOrder(await getOrder(selectedOrder.order_id));
  }

  async function handleDelete(orderId: number) {
    if (!confirm(`Delete order #${orderId}? This cannot be undone.`)) return;
    try {
      await deleteOrder(orderId);
      setSelectedOrder(null);
      await loadOrders();
    } catch { /* ignore */ }
  }

  async function handleAssignHauler(orderId: number) {
    const charId = parseInt(assignCharId);
    if (isNaN(charId)) return;
    try {
      await assignHauler(orderId, charId);
      setSelectedOrder(await getOrder(orderId));
      setAssignCharId('');
    } catch { /* ignore */ }
  }

  const canEditOrder = (order: OrderDetail) => {
    return order.character_id === user.character_id
      && (order.status === 'pending' || order.status === 'accepted');
  };

  if (loading) return <div>Loading orders...</div>;

  return (
    <div className="order-list">
      <div className="orders-panel">
        <h3>{isPrivileged ? 'All Orders' : 'My Orders'}</h3>
        {orders.length === 0 && <p className="no-orders">No orders yet.</p>}
        {orders.map(order => (
          <div key={order.order_id}
            className={`order-card ${selectedOrder?.order_id === order.order_id ? 'selected' : ''}`}
            onClick={() => selectOrder(order.order_id)}>
            <div className="order-card-top">
              <span className="order-id">#{order.order_id}</span>
              <span className={`status-badge ${statusClass(order.status)}`}>{order.status.replace('_', ' ')}</span>
            </div>
            <div className="order-card-meta">
              {isPrivileged && <span>{order.character_name}</span>}
              <span>{order.total_m3.toFixed(0)} m3</span>
              <span>{order.shop_requested ? 'Shop+Haul' : 'Haul Only'}</span>
              {order.assigned_to_name && <span>Hauler: {order.assigned_to_name}</span>}
            </div>
            <div className="order-card-date">{formatDate(order.created_at)}</div>
          </div>
        ))}
      </div>

      {selectedOrder && (
        <div className="order-detail">
          <div className="detail-header">
            <h3>Order #{selectedOrder.order_id}</h3>
            <span className={`status-badge ${statusClass(selectedOrder.status)}`}>{selectedOrder.status.replace('_', ' ')}</span>
          </div>
          <div className="detail-info">
            <span>By: {selectedOrder.character_name}</span>
            <span>Type: {selectedOrder.shop_requested ? 'Shop + Haul' : 'Haul Only'}</span>
            {selectedOrder.assigned_to_name && <span>Hauler: {selectedOrder.assigned_to_name}</span>}
            <span>Created: {formatDate(selectedOrder.created_at)}</span>
          </div>

          <table className="detail-table">
            <thead>
              <tr>
                <th>Item</th>
                <th>Qty</th>
                <th>m3</th>
                <th>Est. Price</th>
                <th>Actual (per unit)</th>
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
                  <td>{formatIsk(item.estimated_price)}</td>
                  <td>
                    {isPrivileged ? (
                      <span className="actual-price-cell">
                        <input type="number" step="0.01" defaultValue={item.actual_price ?? ''}
                          placeholder="per unit"
                          onBlur={e => handleActualPrice(item.item_id, e.target.value)} />
                        {savedItemId === item.item_id && <span className="saved-indicator">✓</span>}
                      </span>
                    ) : formatIsk(item.actual_price)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          <div className="detail-totals">
            <div><span>Total m3:</span><span>{selectedOrder.total_m3.toFixed(2)}</span></div>
            <div><span>Est. Cost:</span><span>{formatIsk(selectedOrder.total_estimated_isk)} ISK</span></div>
            {selectedOrder.total_actual_isk !== null && <div><span>Actual Cost:</span><span>{formatIsk(selectedOrder.total_actual_isk)} ISK</span></div>}
            <div><span>Hauling Fee:</span><span>{formatIsk(selectedOrder.hauling_fee)} ISK</span></div>
            {selectedOrder.shopper_fee > 0 && <div><span>Shopper Fee:</span><span>{formatIsk(selectedOrder.shopper_fee)} ISK</span></div>}
          </div>

          <div className="detail-actions">
            {isPrivileged && (
              <div className="status-actions">
                <span>Update Status:</span>
                {selectedOrder.status !== 'pending' && <button className="status-btn pending" onClick={() => handleStatusUpdate(selectedOrder.order_id, 'pending')}>Pending</button>}
                {selectedOrder.status !== 'accepted' && <button className="status-btn accepted" onClick={() => handleStatusUpdate(selectedOrder.order_id, 'accepted')}>Accept</button>}
                {selectedOrder.status !== 'in_transit' && <button className="status-btn in-transit" onClick={() => handleStatusUpdate(selectedOrder.order_id, 'in_transit')}>In Transit</button>}
                {selectedOrder.status !== 'delivered' && <button className="status-btn delivered" onClick={() => handleStatusUpdate(selectedOrder.order_id, 'delivered')}>Delivered</button>}
                {selectedOrder.status !== 'cancelled' && <button className="status-btn cancelled" onClick={() => handleStatusUpdate(selectedOrder.order_id, 'cancelled')}>Cancel</button>}
              </div>
            )}

            {isAdmin ? (
              <div className="admin-actions">
                <div className="assign-hauler">
                  <select value={assignCharId} onChange={e => setAssignCharId(e.target.value)}>
                    <option value="">— Assign Hauler —</option>
                    {haulers.map(h => (
                      <option key={h.character_id} value={h.character_id}>
                        {h.character_name} ({h.role})
                      </option>
                    ))}
                  </select>
                  <button onClick={() => handleAssignHauler(selectedOrder.order_id)} disabled={!assignCharId}>Assign</button>
                </div>
                {canEditOrder(selectedOrder) && (
                  <button className="edit-order-btn" onClick={() => onEditOrder(selectedOrder)}>Edit Order</button>
                )}
                <button className="delete-order-btn" onClick={() => handleDelete(selectedOrder.order_id)}>Delete Order</button>
              </div>
            ) : canEditOrder(selectedOrder) && (
              <button className="edit-order-btn" onClick={() => onEditOrder(selectedOrder)}>Edit Order</button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
