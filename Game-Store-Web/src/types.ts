export type Role = 'default' | 'employee' | 'admin'
export interface User { id: string; role: Role }
export interface Product { id: string; name: string; description: string; price: number; stock: number; imageUrl: string | null; isActive: boolean; version: string }
export type ProductInput = Omit<Product, 'id' | 'version'>
export interface TokenResponse { accessToken: string; tokenType: string; expiresAtUtc: string }
export interface CartItem { productId: string; name: string; price: number; stock: number; imageUrl: string | null; isActive: boolean; quantity: number; lineTotal: number }
export interface Cart { items: CartItem[]; total: number; totalQuantity: number }
export const money = (value: number) => new Intl.NumberFormat('da-DK', { style: 'currency', currency: 'DKK' }).format(value)
