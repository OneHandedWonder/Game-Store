<script setup lang="ts">
import { onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuth } from '../stores/auth'
import { useCart } from '../stores/cart'
import ProductArt from '../components/ProductArt.vue'
import { api } from '../api'
import { money, type Product } from '../types'
const route = useRoute(), product = ref<Product | null>(null), error = ref(''), loading = ref(true)
const router = useRouter(), auth = useAuth(), cart = useCart(), quantity = ref(1), cartMessage = ref(''), cartError = ref('')
async function addToCart() {
  if (!product.value) return
  if (!auth.user) { await router.push({ path: '/login', query: { redirect: route.path } }); return }
  cartMessage.value = ''; cartError.value = ''
  const current = requestId
  try { await cart.add(product.value.id, quantity.value); if (current === requestId) cartMessage.value = 'Added to your cart.' }
  catch (e) { if (current === requestId) cartError.value = (e as Error).message }
}
let requestId = 0
onUnmounted(() => requestId++)
async function load() {
  const current = ++requestId
  quantity.value = 1; cartMessage.value = ''; cartError.value = ''
  loading.value = true; error.value = ''; product.value = null
  try {
    const result = await api<Product>(`/products/${encodeURIComponent(String(route.params.id))}`)
    if (current === requestId) product.value = result
  }
  catch (e) { if (current === requestId) error.value = (e as Error).message }
  finally { if (current === requestId) loading.value = false }
}
watch(() => route.params.id, load, { immediate: true })
</script>
<template><section class="section"><RouterLink class="back-link" to="/">← Back to the collection</RouterLink><p v-if="loading" class="empty" role="status">Loading product…</p><div v-else-if="error" class="error" role="alert">{{ error }} <button class="text-button" @click="load">Try again</button></div><div v-else-if="product" class="product-detail"><ProductArt :name="product.name" :image-url="product.imageUrl" /><div><p class="eyebrow">THE GAMESTORE COLLECTION</p><h1>{{ product.name }}</h1><p class="detail-price">{{ money(product.price) }}</p><span class="badge" :class="{ 'badge-muted': !product.stock }">{{ product.stock ? `${product.stock} in stock` : 'Out of stock' }}</span><p v-if="!product.isActive" class="notice">This product is inactive.</p><p class="description">{{ product.description }}</p><form v-if="product.isActive && product.stock > 0" class="product-cart-form" @submit.prevent="addToCart"><label>Quantity<input v-model.number="quantity" type="number" required min="1" :max="Math.min(99, product.stock)" step="1" :disabled="cart.busy || cart.loading"></label><button class="button" type="submit" :disabled="cart.busy || cart.loading">{{ auth.user ? 'Add to cart' : 'Sign in to add to cart' }}</button></form><p v-if="cartMessage" class="success" role="status">{{ cartMessage }} <RouterLink class="text-button" to="/cart">View cart &rarr;</RouterLink></p><p v-if="cartError" class="error" role="alert">{{ cartError }}</p><p class="muted">Adding items does not reserve stock. Checkout is not available yet.</p></div></div></section></template>
