export interface ItemResult {
  type_id: number;
  type_name: string;
  volume: number;
}

export interface PriceResponse {
  type_id: number;
  jita_sell_price: number;
}

export interface RouteConfig {
  origin: string;
  destination: string;
  round_trip_isotopes: number;
  fuel_isk_per_m3: number;
  has_pushx: boolean;
  allows_shopping: boolean;
  label_origin: string | null;
  label_destination: string | null;
  pushx_fee_small: number;
  pushx_fee_large: number;
  pushx_rush_fee_small: number;
  pushx_rush_fee_large: number;
  pushx_volume_tier_break: number;
}

export interface ConfigResponse {
  routes: RouteConfig[];
  service_isk_per_m3: number;
  shopper_fee: number;
  max_order_m3: number;
  isotope_price: number;
  cargo_capacity: number;
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
  expedite: boolean;
  expedite_fee: number;
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
  notes: string;
  archived: boolean;
  archived_at: string | null;
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
