export type Role = 'default' | 'employee' | 'admin'
export interface User { id: string; role: Role }
export interface Product { id: string; name: string; description: string; price: number; stock: number; imageUrl: string | null; isActive: boolean; version: string }
export type ProductInput = Omit<Product, 'id' | 'version'>
export interface TokenResponse { accessToken: string; tokenType: string; expiresAtUtc: string }
export interface CartItem { productId: string; name: string; price: number; stock: number; imageUrl: string | null; isActive: boolean; quantity: number; lineTotal: number }
export interface Cart { items: CartItem[]; total: number; totalQuantity: number }
export interface OrderSummary { id: string; createdAt: string; total: number; currency: string; paymentStatus: string; customerEmail?: string; fulfillmentStatus?: 'fulfilled' | 'unfulfilled'; fulfillmentVersion?: string }
export interface DeliveryAddress { recipient: string; line1: string; line2: string | null; postalCode: string; city: string; countryCode: string }
export interface OrderDetail extends OrderSummary { items: { productId: string; name: string; price: number; quantity: number }[]; deliveryAddress?: DeliveryAddress | null }
export const money = (value: number) => new Intl.NumberFormat('da-DK', { style: 'currency', currency: 'DKK' }).format(value)
