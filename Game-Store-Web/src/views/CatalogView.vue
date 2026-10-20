<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from 'vue'
import { api } from '../api'
import { money, type Product } from '../types'
import ProductArt from '../components/ProductArt.vue'
const products = ref<Product[]>([]), loading = ref(true), error = ref(''), search = ref(''), sort = ref('name'), page = ref(1)
let requestId = 0
let searchTimer: ReturnType<typeof setTimeout> | undefined
watch([search, sort], () => {
  page.value = 1; requestId++; clearTimeout(searchTimer); loading.value = true
  searchTimer = setTimeout(load, 250)
})
onUnmounted(() => { requestId++; clearTimeout(searchTimer) })
async function load() {
  const current = ++requestId
  loading.value = true; error.value = ''
  try {
    const query = new URLSearchParams({ page: String(page.value), pageSize: '12', search: search.value.trim(), sort: sort.value })
    const result = await api<Product[]>(`/products?${query}`)
    if (current === requestId) products.value = result
  }
  catch (e) { if (current === requestId) error.value = (e as Error).message }
  finally { if (current === requestId) loading.value = false }
}
async function changePage(step: number) { clearTimeout(searchTimer); page.value += step; await load() }
onMounted(load)
</script>
<template>
  <section class="hero"><div><p class="eyebrow"><span class="live-dot"></span> YOUR NEXT CHAPTER STARTS HERE</p><h1>Less scrolling.<br>More <em>playing.</em></h1><p class="hero-copy">Discover your next great game, upgrade your setup,<br class="desktop-only"> and make a little room for play.</p><a class="button" href="#collection">Explore the collection <span aria-hidden="true">↘</span></a></div><div class="hero-art" aria-hidden="true"><div class="orbit orbit-one"></div><div class="orbit orbit-two"></div><div class="controller"><div class="dpad">+</div><span class="controller-center">g<span>+</span></span><div class="controller-buttons"><i></i><i></i><i></i><i></i></div><div class="stick left"></div><div class="stick right"></div></div><span class="art-caption">GOOD GAMES. GREAT ESCAPES.</span><span class="hero-star">✳</span></div></section>
  <section id="collection" class="section"><div class="section-heading"><div><p class="eyebrow">THE GOOD STUFF</p><h2>The collection<span class="accent">.</span></h2></div><p>Games, consoles & everything in between.</p></div>
    <div class="toolbar"><label class="search"><span aria-hidden="true">⌕</span><input v-model="search" type="search" aria-label="Search the catalog" placeholder="Find something to play…"></label><label class="sort">Sort by <select v-model="sort"><option value="name">Name</option><option value="low">Price: low to high</option><option value="high">Price: high to low</option></select></label></div>
    <p v-if="search" class="muted">Searching the entire catalog.</p>
    <p v-if="loading" class="empty" role="status">Loading the collection…</p>
    <div v-else-if="error" class="error" role="alert">{{ error }} <button class="text-button" @click="load">Try again</button></div>
    <div v-else-if="!products.length" class="empty"><h3>Nothing here just yet.</h3><p>{{ search ? 'Try a different search.' : 'Check back soon for more products.' }}</p></div>
    <div v-else class="product-grid"><RouterLink v-for="product in products" :key="product.id" :to="`/products/${product.id}`" class="product-card"><div class="art-wrap"><ProductArt :name="product.name" :image-url="product.imageUrl" /><span v-if="product.stock === 0" class="stock-label">Out of stock</span><span v-else class="card-arrow" aria-hidden="true">↗</span></div><div class="product-meta"><h3>{{ product.name }}</h3><span>{{ money(product.price) }}</span></div><p>{{ product.stock > 0 ? 'In stock · Ready for your next adventure' : 'Currently unavailable' }}</p></RouterLink></div>
    <div class="pagination"><button class="button secondary small" :disabled="page === 1 || loading" @click="changePage(-1)">← Previous</button><span>Page {{ page }}</span><button class="button secondary small" :disabled="products.length < 12 || loading || !!error" @click="changePage(1)">Next →</button></div>
  </section>
</template>
