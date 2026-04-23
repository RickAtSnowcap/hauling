export interface ItemResult {
  type_id: number;
  type_name: string;
  volume: number;
}

export interface PriceResponse {
  type_id: number;
  jita_sell_price: number;
}

export interface ConfigResponse {
  jita_rate_per_m3: number;
  odebeinn_rate_per_m3: number;
  shopper_fee_per_item: number;
  shopper_fee_minimum: number;
  max_order_m3: number;
}

export interface OrderItemInput {
  type_id: number;
  type_name: string;
  quantity: number;
  volume_per_unit: number;
  estimated_price: number;
}

export interface OrderSummary {
  order_id: number;
  character_id: number;
  character_name: string;
  status: string;
  shop_requested: boolean;
  total_m3: number;
  total_estimated_isk: number;
  total_actual_isk: number | null;
  hauling_fee: number;
  shopper_fee: number;
  created_at: string;
  updated_at: string;
  origin_system: string;
  destination_system: string;
  assigned_to: number | null;
  assigned_to_name: string | null;
}

export interface OrderItemDetail {
  item_id: number;
  type_id: number;
  type_name: string;
  quantity: number;
  volume_per_unit: number;
  line_m3: number;
  estimated_price: number;
  actual_price: number | null;
}

export interface OrderDetail extends OrderSummary {
  items: OrderItemDetail[];
}

export interface UserInfo {
  character_id: number;
  character_name: string;
  role: string;
}
