<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { money } from '../types'
export interface DailyPoint { date: string; orders: number; sales: number }
const props = defineProps<{ points: DailyPoint[]; metric: 'sales' | 'orders'; title: string }>()
const selected = ref<number | null>(null)
watch(() => props.points, () => { selected.value = null })
const max = computed(() => {
  const value = Math.max(1, ...props.points.map(point => point[props.metric]))
  return props.metric === 'orders' ? Math.ceil(value / 2) * 2 : value
})
const x = (i: number) => 45 + i * 580 / Math.max(1, props.points.length - 1)
const y = (value: number) => 190 - value / max.value * 160
const line = computed(() => props.points.map((p, i) => `${x(i)},${y(p[props.metric])}`).join(' '))
const active = computed(() => selected.value === null ? null : props.points[selected.value])
const display = (value: number) => props.metric === 'sales' ? money(value) : `${value} orders`
const date = (value: string) => new Date(`${value}T12:00:00Z`).toLocaleDateString('da-DK', { day: 'numeric', month: 'short', timeZone: 'Europe/Copenhagen' })
</script>
<template><article class="panel dashboard-chart"><h2>{{ title }}</h2><p class="field-hint">Daily totals {{ metric === 'sales' ? '(DKK)' : '' }} · Europe/Copenhagen. Focus or hover over a point for details.</p>
  <svg viewBox="0 0 650 230" role="img" :aria-label="title" class="order-chart">
    <g v-for="fraction in [0, .5, 1]" :key="fraction"><line x1="45" x2="625" :y1="y(max * fraction)" :y2="y(max * fraction)" class="chart-grid"/><text x="40" :y="y(max * fraction) - 6" class="chart-axis">{{ (max * fraction).toLocaleString('da-DK', { maximumFractionDigits: 2 }) }}</text></g>
    <polygon v-if="metric === 'sales' && points.length" :points="`45,190 ${line} ${x(points.length - 1)},190`" class="chart-area"/>
    <polyline v-if="metric === 'sales'" :points="line" class="chart-line"/>
    <template v-for="(point, i) in points" :key="point.date"><rect v-if="metric === 'orders'" :x="x(i) - Math.min(14, 220 / points.length)" :y="y(point.orders)" :width="Math.min(28, 440 / points.length)" :height="190 - y(point.orders)" rx="2" class="chart-bar"/>
      <circle :cx="x(i)" :cy="y(point[metric])" r="5" tabindex="0" :aria-label="`${date(point.date)}: ${display(point[metric])}`" class="chart-point" @mouseenter="selected = i" @focus="selected = i"><title>{{ date(point.date) }}: {{ display(point[metric]) }}</title></circle>
      <text v-if="i % Math.ceil(points.length / 5) === 0 || i === points.length - 1" :x="x(i)" y="218" text-anchor="middle" class="chart-axis">{{ date(point.date) }}</text>
    </template>
  </svg><p class="chart-readout" aria-live="polite">{{ active ? `${date(active.date)}: ${display(active[metric])}` : points.some(p => p[metric] > 0) ? 'Select a day to inspect its total.' : 'No orders in this period.' }}</p>
  <details><summary>View daily data</summary><div class="table-wrap"><table><thead><tr><th>Date</th><th>{{ metric === 'sales' ? 'Simulated sales (DKK)' : 'Orders' }}</th></tr></thead><tbody><tr v-for="point in points" :key="point.date"><td>{{ point.date }}</td><td>{{ display(point[metric]) }}</td></tr></tbody></table></div></details>
</article></template>
