<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { api } from '../api'
import { useAuth } from '../stores/auth'
import { money } from '../types'
import OrderChart, { type DailyPoint } from '../components/OrderChart.vue'
interface Analytics { daily: DailyPoint[]; topProducts: { productId: string; name: string; units: number; sales: number }[] }
const days = ref(30), analytics = ref<Analytics | null>(null)
interface Metrics { totalOrders: number; fulfilledOrders: number; unfulfilledOrders: number; totalSales: number; averageOrderValue: number; ordersToday: number; orderingCustomers: number; unitsOrdered: number; activeProducts: number; lowStockProducts: number }
const auth = useAuth(), metrics = ref<Metrics | null>(null), loading = ref(false), error = ref(''), updated = ref('')
let revision = 0
async function load() {
  const current = ++revision, generation = auth.generation
  loading.value = true; error.value = ''; metrics.value = null; analytics.value = null
  try {
    const [result, trends] = await Promise.all([api<Metrics>('/staff/orders/metrics'), api<Analytics>(`/staff/orders/analytics?days=${days.value}`)])
    if (current === revision && generation === auth.generation) { metrics.value = result; analytics.value = trends; updated.value = new Date().toLocaleTimeString('da-DK') }
  } catch (e) { if (current === revision && generation === auth.generation) error.value = (e as Error).message }
  finally { if (current === revision) loading.value = false }
}
const cards = computed(() => metrics.value ? [
  { label: 'Total orders', value: metrics.value.totalOrders, hint: 'All time' },
  { label: 'Unfulfilled', value: metrics.value.unfulfilledOrders, hint: 'Orders awaiting completion' },
  { label: 'Fulfilled', value: metrics.value.fulfilledOrders, hint: 'Completed demo orders' },
  { label: 'Simulated sales', value: money(metrics.value.totalSales), hint: 'All time · no actual charges' },
  { label: 'Average order value', value: money(metrics.value.averageOrderValue), hint: 'Across all demo orders' },
  { label: 'Orders today', value: metrics.value.ordersToday, hint: 'Europe/Copenhagen calendar day' },
  { label: 'Ordering customers', value: metrics.value.orderingCustomers, hint: 'Distinct accounts with orders' },
  { label: 'Units ordered', value: metrics.value.unitsOrdered, hint: 'All time · saved order quantities' },
  { label: 'Active products', value: metrics.value.activeProducts, hint: 'Current catalog' },
  { label: 'Low stock', value: metrics.value.lowStockProducts, hint: 'Active products with 5 or fewer units' },
] : [])
onMounted(load)
watch(days, load)
onUnmounted(() => { revision++ })
</script>
<template><section class="section management-dashboard"><div class="section-heading"><div><p class="eyebrow">MANAGEMENT</p><h1>Store overview<span class="accent">.</span></h1><p class="muted">Orders, fulfillment and catalog health in one place.</p></div><button class="button secondary small" :disabled="loading" @click="load">{{ loading ? 'Refreshing…' : 'Refresh metrics' }}</button></div>
  <p class="notice">Demo store: sales and fulfillment are simulated. No money is charged and no goods are shipped.</p>
  <div v-if="error" class="error" role="alert">{{ error }} <button class="text-button" @click="load">Retry</button></div><p v-if="loading" role="status">Loading store metrics…</p>
  <template v-if="metrics"><p class="field-hint">Updated {{ updated }}. Refresh to include recent changes.</p><div class="metrics-grid"><article v-for="card in cards" :key="card.label" class="metric-card"><h2>{{ card.label }}</h2><strong>{{ card.value }}</strong><p>{{ card.hint }}</p></article></div>
  <div v-if="analytics" class="dashboard-analytics"><div class="section-heading"><div><p class="eyebrow">STORE TRENDS</p><h2>A closer look.</h2></div><label class="chart-range">Chart period<select v-model="days"><option :value="7">Last 7 days</option><option :value="30">Last 30 days</option><option :value="90">Last 90 days</option></select></label></div>
    <div class="dashboard-chart-grid"><OrderChart :points="analytics.daily" metric="sales" title="Simulated sales trend"/><OrderChart :points="analytics.daily" metric="orders" title="Daily orders"/></div>
    <div class="dashboard-chart-grid"><article class="panel"><h2>Fulfillment mix</h2><div class="fulfillment-donut" role="img" :aria-label="`${metrics.fulfilledOrders} fulfilled, ${metrics.unfulfilledOrders} unfulfilled`" :style="{ background: metrics.totalOrders ? `conic-gradient(#55783d ${(metrics.fulfilledOrders / metrics.totalOrders) * 360}deg, #e1c380 0)` : '#dedfd5' }"><span>{{ metrics.totalOrders ? Math.round(metrics.fulfilledOrders / metrics.totalOrders * 100) : 0 }}%<small>fulfilled</small></span></div><p class="chart-legend"><span>● {{ metrics.fulfilledOrders }} fulfilled</span><span>○ {{ metrics.unfulfilledOrders }} unfulfilled</span></p><p v-if="!metrics.totalOrders" class="muted">No orders yet.</p></article>
    <article class="panel"><h2>Top products</h2><p class="field-hint">By units ordered in the selected period · saved order data</p><p v-if="!analytics.topProducts.length" class="muted">No products ordered in this period.</p><ol v-else class="top-products"><li v-for="product in analytics.topProducts" :key="product.productId"><div><strong>{{ product.name }}</strong><span>{{ product.units }} units · {{ money(product.sales) }}</span></div><progress :value="product.units" :max="Math.max(1, ...analytics.topProducts.map(p => p.units))" :aria-label="`${product.name}: ${product.units} units`"></progress></li></ol></article></div>
  </div><div class="panel fulfillment-overview"><div><h2>Fulfillment overview</h2><p class="muted">{{ metrics.fulfilledOrders }} of {{ metrics.totalOrders }} orders completed</p></div><progress :value="metrics.fulfilledOrders" :max="metrics.totalOrders || 1" aria-label="Fulfilled orders"></progress><RouterLink class="button" to="/staff/orders">Manage orders</RouterLink></div></template>
</section></template>
