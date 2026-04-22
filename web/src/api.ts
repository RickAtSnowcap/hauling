import type { ItemResult, PriceResponse, ConfigResponse, OrderSummary, OrderDetail, UserInfo, OrderItemInput } from './types';

const BASE = '/hauling';

function getToken(): string | null {
  return sessionStorage.getItem('hauling_token');
}

function authHeaders(): Record<string, string> {
  const token = getToken();
  return token ? { 'Authorization': `Bearer ${token}` } : {};
}

export async function getLoginUrl(): Promise<string> {
  const resp = await fetch(`${BASE}/api/auth/login`);
  const data = await resp.json();
  return data.url;
}

export async function getMe(): Promise<UserInfo | null> {
  const token = getToken();
  if (!token) return null;
  const resp = await fetch(`${BASE}/api/auth/me`, { headers: authHeaders() });
  if (!resp.ok) return null;
  return resp.json();
}

export async function searchItems(query: string, limit = 20): Promise<ItemResult[]> {
  const resp = await fetch(`${BASE}/api/items/search?q=${encodeURIComponent(query)}&limit=${limit}`, { headers: authHeaders() });
  if (!resp.ok) return [];
  return resp.json();
}

export async function getJitaPrice(typeId: number): Promise<number> {
  const resp = await fetch(`${BASE}/api/items/${typeId}/price`, { headers: authHeaders() });
  if (!resp.ok) return 0;
  const data: PriceResponse = await resp.json();
  return data.jita_sell_price;
}

export async function getConfig(): Promise<ConfigResponse> {
  const resp = await fetch(`${BASE}/api/config`);
  return resp.json();
}

export async function createOrder(shopRequested: boolean, items: OrderItemInput[]): Promise<number> {
  const resp = await fetch(`${BASE}/api/orders`, {
    method: 'POST',
    headers: { ...authHeaders(), 'Content-Type': 'application/json' },
    body: JSON.stringify({
      shop_requested: shopRequested,
      items: items.map(i => ({
        type_id: i.type_id,
        quantity: i.quantity,
        volume_per_unit: i.volume_per_unit,
        estimated_price: i.estimated_price
      }))
    })
  });
  if (!resp.ok) throw new Error('Failed to create order');
  const data = await resp.json();
  return data.order_id;
}

export async function listOrders(limit = 20, offset = 0): Promise<OrderSummary[]> {
  const resp = await fetch(`${BASE}/api/orders?limit=${limit}&offset=${offset}`, { headers: authHeaders() });
  if (!resp.ok) throw new Error('Failed to load orders');
  return resp.json();
}

export async function getOrder(id: number): Promise<OrderDetail> {
  const resp = await fetch(`${BASE}/api/orders/${id}`, { headers: authHeaders() });
  if (!resp.ok) throw new Error('Failed to load order');
  return resp.json();
}

export async function updateOrderStatus(orderId: number, status: string): Promise<void> {
  const resp = await fetch(`${BASE}/api/orders/${orderId}/status`, {
    method: 'PUT',
    headers: { ...authHeaders(), 'Content-Type': 'application/json' },
    body: JSON.stringify({ status })
  });
  if (!resp.ok) throw new Error('Failed to update status');
}

export async function updateActualPrice(itemId: number, actualPrice: number): Promise<void> {
  const resp = await fetch(`${BASE}/api/orders/items/${itemId}/actual-price`, {
    method: 'PUT',
    headers: { ...authHeaders(), 'Content-Type': 'application/json' },
    body: JSON.stringify({ actual_price: actualPrice })
  });
  if (!resp.ok) throw new Error('Failed to update price');
}
