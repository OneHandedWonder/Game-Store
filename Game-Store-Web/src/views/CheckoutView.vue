<script setup lang="ts">
import { onMounted, onUnmounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, ApiError, json } from '../api'
import { useCart } from '../stores/cart'
import { useAuth } from '../stores/auth'
import { money, type Cart } from '../types'
const cart = useCart(), auth = useAuth(), router = useRouter()
const snapshot = ref<Cart | null>(null), error = ref(''), busy = ref(false), loading = ref(false)
const cardNumber = ref(''), cardholderName = ref(''), expiry = ref(''), securityCode = ref('')
const address = reactive({ recipient: '', line1: '', line2: '', postalCode: '', city: '', countryCode: 'DK' })
let checkoutKey = crypto.randomUUID(), active = true
function clearCard() { cardNumber.value = ''; cardholderName.value = ''; expiry.value = ''; securityCode.value = '' }
async function review() {
  loading.value = true; error.value = ''; snapshot.value = null
  try { await cart.load(); if (active) { snapshot.value = cart.cart; checkoutKey = crypto.randomUUID() } }
  catch (e) { if (active) error.value = (e as Error).message }
  finally { loading.value = false }
}
async function submit() {
  if (busy.value || !snapshot.value) return
  busy.value = true; error.value = ''
  const generation = auth.generation
  try {
    const number = cardNumber.value.replace(/\s/g, '')
    if (!['4242424242424242', '4000000000000002'].includes(number)) throw new Error('Use one of the displayed test cards. Never enter real card details.')
    const [year, month] = expiry.value.split('-').map(Number)
    const result = await api<{ id: string }>('/orders/checkout', json('POST', {
      checkoutKey, expectedTotal: snapshot.value.total,
      deliveryAddress: { ...address, countryCode: address.countryCode.trim().toUpperCase(), line2: address.line2 || null },
      items: snapshot.value.items.map(item => ({ productId: item.productId, quantity: item.quantity, price: item.price })),
      cardNumber: number, cardholderName: cardholderName.value, expiryYear: year, expiryMonth: month, securityCode: securityCode.value,
    }))
    if (!active || generation !== auth.generation) return
    clearCard(); cart.clearLocal()
    await router.push(`/orders/${result.id}`)
  } catch (e) {
    if (active && generation === auth.generation) {
      error.value = (e as Error).message
      if (e instanceof ApiError && e.status === 409) snapshot.value = null
    }
  } finally { securityCode.value = ''; busy.value = false }
}
onMounted(review)
onUnmounted(() => { active = false; clearCard(); Object.keys(address).forEach(key => { address[key as keyof typeof address] = '' }) })
</script>

<template><section class="section"><p class="eyebrow">SIMULATED PAYMENT</p><h1>Checkout.</h1>
  <p class="notice">Demo only. No money will be charged and no goods will be shipped. Never enter real card details.</p>
  <div v-if="error" class="error" role="alert">{{ error }}</div>
  <p v-if="loading" role="status">Loading your checkout…</p>
  <template v-else-if="snapshot?.items.length"><div class="two-panels"><div class="panel"><h2>Review your order</h2>
    <p v-for="item in snapshot.items" :key="item.productId">{{ item.quantity }} × {{ item.name }} — {{ money(item.lineTotal) }}</p>
    <div class="cart-total"><strong>Total (DKK)</strong><strong>{{ money(snapshot.total) }}</strong></div>
    <p class="muted">Stock is checked when you place the order. Use a fictional delivery address for this demo.</p>
    <RouterLink to="/cart" class="back-link">Edit cart</RouterLink>
  </div><form class="panel" autocomplete="off" @submit.prevent="submit"><h2>Dummy payment</h2>
    <p class="field-hint">Success: <code>4242 4242 4242 4242</code><br>Decline: <code>4000 0000 0000 0002</code><br>Use a fictional name, a future expiry date, and a made-up 3–4 digit security code. Card details are not saved.</p>
    <fieldset :disabled="busy"><h2>Delivery address</h2><p class="field-hint">Saved securely with this order and visible to you and authorized staff. No goods are shipped.</p>
    <label>Recipient name<input v-model="address.recipient" autocomplete="off" required minlength="2" maxlength="100"></label>
    <label>Street address<input v-model="address.line1" autocomplete="off" required maxlength="200"></label>
    <label>Apartment, suite, etc. (optional)<input v-model="address.line2" autocomplete="off" maxlength="200"></label>
    <div class="form-grid"><label>Postal code<input v-model="address.postalCode" autocomplete="off" required maxlength="20"></label><label>City<input v-model="address.city" autocomplete="off" required maxlength="100"></label></div>
    <label>Country code<input v-model="address.countryCode" autocomplete="off" required pattern="[A-Za-z]{2}" maxlength="2" placeholder="DK"><span class="field-hint">Two-letter country code, for example DK or SE.</span></label>
    <h2>Test card</h2><label>Test cardholder name<input v-model="cardholderName" autocomplete="off" required minlength="2" maxlength="100"></label>
    <label>Test card number<input v-model="cardNumber" type="text" inputmode="numeric" autocomplete="off" required pattern="[0-9 ]{13,23}" maxlength="23"></label>
    <div class="form-grid"><label>Expiry date<input v-model="expiry" type="month" required></label><label>Test security code<input v-model="securityCode" type="password" inputmode="numeric" autocomplete="off" required pattern="[0-9]{3,4}" maxlength="4"></label></div>
    <button class="button" type="submit">{{ busy ? 'Processing…' : `Place demo order — ${money(snapshot.total)}` }}</button></fieldset>
  </form></div></template>
  <div v-else class="panel"><p>{{ snapshot ? 'Your cart is empty.' : 'Reload and review your cart before placing an order.' }}</p><RouterLink class="button secondary" to="/cart">Back to cart</RouterLink></div>
  <button class="text-button" :disabled="busy || loading" @click="review">Reload checkout</button>
</section></template>
