import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { api, json } from '../api'
import { useAuth } from './auth'
import type { Cart } from '../types'

export const useCart = defineStore('cart', () => {
  const auth = useAuth(), cart = ref<Cart | null>(null), error = ref(''), loading = ref(false), busy = ref(false)
  let revision = 0
  const count = computed(() => cart.value?.totalQuantity ?? 0)
  function clearLocal() { revision++; cart.value = null; error.value = ''; loading.value = false; busy.value = false }
  async function load() {
    const current = ++revision, generation = auth.generation
    if (!auth.token) { clearLocal(); return }
    loading.value = true; error.value = ''
    try {
      const result = await api<Cart>('/cart')
      if (current === revision && generation === auth.generation) cart.value = result
    } catch (e) { if (current === revision && generation === auth.generation) { error.value = (e as Error).message; throw e } }
    finally { if (current === revision) loading.value = false }
  }
  async function mutate(path: string, options: RequestInit) {
    if (busy.value) throw new Error('A cart update is already in progress.')
    const generation = auth.generation
    busy.value = true; error.value = ''
    try {
      await api(path, options)
      if (generation !== auth.generation) throw new Error('Your session changed. Reload your cart.')
      await load()
      if (generation !== auth.generation) throw new Error('Your session changed. Reload your cart.')
    } catch (e) { if (generation === auth.generation) error.value = (e as Error).message; throw e }
    finally { if (generation === auth.generation) busy.value = false }
  }
  function setQuantity(id: string, quantity: number) { return mutate(`/cart/items/${encodeURIComponent(id)}`, json('PUT', { quantity })) }
  async function add(id: string, quantity: number) {
    const generation = auth.generation
    await load()
    if (generation !== auth.generation) throw new Error('Your session changed. Please sign in again.')
    const existing = cart.value?.items.find(item => item.productId === id)?.quantity ?? 0
    return setQuantity(id, existing + quantity)
  }
  function remove(id: string) { return mutate(`/cart/items/${encodeURIComponent(id)}`, { method: 'DELETE' }) }
  function empty() { return mutate('/cart', { method: 'DELETE' }) }
  return { cart, count, error, loading, busy, load, clearLocal, setQuantity, add, remove, empty }
})
