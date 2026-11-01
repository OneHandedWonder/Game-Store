<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { api, json } from '../api'
import { useAuth } from '../stores/auth'
import { money, type OrderSummary, type OrderDetail } from '../types'
const route = useRoute(), auth = useAuth(), orders = ref<OrderSummary[]>([]), detail = ref<OrderDetail | null>(null)
const props = defineProps<{ staff?: boolean }>()
const basePath = computed(() => props.staff ? '/staff/orders' : '/orders')
const page = ref(1), error = ref(''), loading = ref(false)
const saving = ref(false)
async function updateFulfillment() {
  if (!detail.value || saving.value) return
  const id = detail.value.id, generation = auth.generation
  saving.value = true; error.value = ''
  try {
    await api(`/staff/orders/${encodeURIComponent(id)}/fulfillment`, json('PUT', {
      status: detail.value.fulfillmentStatus === 'fulfilled' ? 'unfulfilled' : 'fulfilled', version: detail.value.fulfillmentVersion,
    }))
    if (generation === auth.generation && route.params.id === id) await load()
  } catch (e) { if (generation === auth.generation && route.params.id === id) error.value = (e as Error).message }
  finally { saving.value = false }
}
let revision = 0
async function load() {
  const current = ++revision, generation = auth.generation
  error.value = ''; loading.value = true; detail.value = null; orders.value = []
  try {
    const result = await api<OrderDetail | OrderSummary[]>(route.params.id ? `${basePath.value}/${encodeURIComponent(String(route.params.id))}` : `${basePath.value}?page=${page.value}`)
    if (current !== revision || generation !== auth.generation) return
    if (Array.isArray(result)) orders.value = result
    else detail.value = result
  } catch (e) { if (current === revision && generation === auth.generation) error.value = (e as Error).message }
  finally { if (current === revision) loading.value = false }
}
watch(() => props.staff, () => { page.value = 1 })
watch(() => [route.params.id, page.value, props.staff], load, { immediate: true })
onUnmounted(() => { revision++ })
const date = (value: string) => new Date(value).toLocaleString('da-DK')
</script>
<template><section class="section"><p class="eyebrow">{{ staff ? 'STAFF WORKSPACE' : 'YOUR ACCOUNT' }}</p><h1>{{ route.params.id ? 'Order details.' : staff ? 'Customer orders.' : 'Order history.' }}</h1>
  <p class="notice">All orders use simulated payments. No money has been charged and no goods will be shipped.</p>
  <div v-if="error" class="error" role="alert">{{ error }} <button class="text-button" @click="load">Retry</button></div>
  <p v-if="loading" role="status">Loading orders…</p>
  <article v-else-if="detail" class="panel"><h2>Demo order</h2><p><code>{{ detail.id }}</code></p><p>{{ date(detail.createdAt) }} · {{ detail.paymentStatus }} · {{ detail.currency }}</p>
    <p v-if="staff">Customer: {{ detail.customerEmail }}</p>
    <section class="order-address"><h3>Delivery address</h3><address v-if="detail.deliveryAddress">{{ detail.deliveryAddress.recipient }}<br>{{ detail.deliveryAddress.line1 }}<template v-if="detail.deliveryAddress.line2"><br>{{ detail.deliveryAddress.line2 }}</template><br>{{ detail.deliveryAddress.postalCode }} {{ detail.deliveryAddress.city }}<br>{{ detail.deliveryAddress.countryCode }}</address><p v-else class="muted">No delivery address was collected for this older order.</p></section>
    <div v-if="staff && detail.fulfillmentStatus"><p>Fulfillment: <strong>{{ detail.fulfillmentStatus }}</strong></p><button class="button secondary small" :disabled="saving" @click="updateFulfillment">{{ saving ? 'Saving…' : detail.fulfillmentStatus === 'fulfilled' ? 'Mark unfulfilled' : 'Mark fulfilled' }}</button><p class="field-hint">Demo workflow only; this does not ship goods or charge money.</p></div>
    <p v-for="item in detail.items" :key="item.productId">{{ item.quantity }} × {{ item.name }} — {{ money(item.price * item.quantity) }}</p>
    <div class="cart-total"><strong>Total</strong><strong>{{ money(detail.total) }}</strong></div>
  </article>
  <template v-else-if="!route.params.id && !error && !loading"><p v-if="!orders.length" class="empty">No orders on this page.</p>
    <article v-for="order in orders" :key="order.id" class="panel"><RouterLink :to="`${basePath}/${order.id}`"><h2>{{ date(order.createdAt) }}</h2><code>{{ order.id }}</code></RouterLink><p v-if="staff">Customer: {{ order.customerEmail }} · {{ order.fulfillmentStatus }}</p><p>{{ money(order.total) }} · {{ order.paymentStatus }}</p></article>
    <div class="pagination"><button class="button secondary" :disabled="page === 1" @click="page--">Previous</button><span>Page {{ page }}</span><button class="button secondary" :disabled="orders.length < 20" @click="page++">Next</button></div>
  </template><RouterLink v-if="route.params.id" :to="basePath" class="back-link">{{ staff ? 'Back to customer orders' : 'Back to order history' }}</RouterLink>
</section></template>
