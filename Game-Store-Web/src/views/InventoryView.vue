<script setup lang="ts">
import { nextTick, onMounted, ref, watch } from 'vue'
import { api, json } from '../api'
import { money, type Product, type ProductInput } from '../types'
const products = ref<Product[]>([]), loading = ref(false), busy = ref(false), error = ref(''), success = ref(''), page = ref(1)
const editing = ref<string | null>(null), showForm = ref(false), deleting = ref<Product | null>(null)
const empty = (): ProductInput => ({ name: '', description: '', price: 0, stock: 0, imageUrl: null, isActive: true })
const form = ref<ProductInput>(empty())
const editingVersion = ref('')
const deleteDialog = ref<HTMLDialogElement | null>(null)
watch(deleting, async product => {
  await nextTick()
  if (product && !deleteDialog.value?.open) deleteDialog.value?.showModal()
  else if (!product && deleteDialog.value?.open) deleteDialog.value?.close()
})
async function load() {
  loading.value = true; error.value = ''
  try { products.value = await api<Product[]>(`/products?includeInactive=true&page=${page.value}&pageSize=50`) }
  catch (e) { error.value = (e as Error).message }
  finally { loading.value = false }
}
function edit(product?: Product) { editing.value = product?.id ?? null; editingVersion.value = product?.version ?? ''; form.value = product ? { ...product } : empty(); showForm.value = true; success.value = ''; error.value = '' }
async function reloadEditor() {
  if (!editing.value) return
  busy.value = true
  try { edit(await api<Product>(`/products/${editing.value}`)) }
  catch (e) { error.value = (e as Error).message }
  finally { busy.value = false }
}
async function save() {
  busy.value = true; error.value = ''; success.value = ''
  try {
    const body = { ...form.value, imageUrl: form.value.imageUrl?.trim() || null }
    await api<Product>(editing.value ? `/products/${editing.value}` : '/products', {
      ...json(editing.value ? 'PUT' : 'POST', body),
      ...(editing.value ? { headers: { 'If-Match': `"${editingVersion.value}"` } } : {}),
    })
    showForm.value = false; await load(); success.value = 'Product saved.'
  } catch (e) { error.value = (e as Error).message }
  finally { busy.value = false }
}
async function remove() {
  if (!deleting.value) return
  busy.value = true; error.value = ''; success.value = ''
  try { await api(`/products/${deleting.value.id}`, { method: 'DELETE', headers: { 'If-Match': `"${deleting.value.version}"` } }); deleting.value = null; await load(); success.value = 'Product deleted.' }
  catch (e) { error.value = (e as Error).message; deleting.value = null }
  finally { busy.value = false }
}
async function changePage(step: number) { page.value += step; await load() }
onMounted(load)
</script>
<template><section class="section"><div class="section-heading"><div><p class="eyebrow">STAFF WORKSPACE</p><h1>Inventory<span class="accent">.</span></h1></div><button class="button" :disabled="busy" @click="edit()">+ Add product</button></div><div v-if="error" class="error" role="alert">{{ error }} <button class="text-button" @click="load">Reload inventory</button></div><p v-if="success" class="success" role="status">{{ success }}</p>
  <section v-if="showForm" class="panel"><button v-if="editing" class="text-button" :disabled="busy" @click="reloadEditor">Reload product and discard my edits</button><h2>{{ editing ? 'Edit product' : 'New product' }}</h2><form @submit.prevent="save"><fieldset :disabled="busy" class="form-grid"><label class="span-two">Name<input v-model="form.name" required maxlength="200"></label><label class="span-two">Description<textarea v-model="form.description" maxlength="4000" rows="3"></textarea></label><label>Price (DKK)<input v-model.number="form.price" required type="number" min="0" max="99999999.99" step="0.01"></label><label>Stock<input v-model.number="form.stock" required type="number" min="0" max="2147483647" step="1"></label><label class="span-two">Image URL (optional)<input v-model="form.imageUrl" type="url" pattern="https?://.*" maxlength="2048" placeholder="https://…"></label><label class="checkbox span-two"><input v-model="form.isActive" type="checkbox">Active · visible in the public catalog</label><div class="actions span-two"><button class="button" type="submit">{{ busy ? 'Saving…' : 'Save product' }}</button><button class="button secondary" type="button" @click="showForm = false">Cancel</button></div></fieldset></form></section>
  <p v-if="loading" class="empty" role="status">Loading inventory…</p><div v-else-if="!products.length && !error" class="empty">No products on this page.</div><div v-else-if="!error" class="table-wrap"><table><thead><tr><th>Product</th><th>Price</th><th>Stock</th><th>Status</th><th><span class="sr-only">Actions</span></th></tr></thead><tbody><tr v-for="product in products" :key="product.id"><td><RouterLink :to="`/products/${product.id}`">{{ product.name }}</RouterLink><small>{{ product.id }}</small></td><td>{{ money(product.price) }}</td><td>{{ product.stock }}</td><td><span class="badge" :class="{ 'badge-muted': !product.isActive }">{{ product.isActive ? 'Active' : 'Inactive' }}</span></td><td class="row-actions"><button class="text-button" :disabled="busy" @click="edit(product)">Edit<span class="sr-only"> {{ product.name }}</span></button><button class="text-button danger-text" :disabled="busy" @click="deleting = product">Delete<span class="sr-only"> {{ product.name }}</span></button></td></tr></tbody></table></div><div class="pagination"><button class="button secondary small" :disabled="page === 1 || loading" @click="changePage(-1)">← Previous</button><span>Page {{ page }}</span><button class="button secondary small" :disabled="products.length < 50 || loading || !!error" @click="changePage(1)">Next →</button></div>
  <dialog ref="deleteDialog" role="alertdialog" aria-labelledby="delete-title" class="panel modal" @cancel.prevent="!busy && (deleting = null)"><template v-if="deleting"><h2 id="delete-title">Delete {{ deleting.name }}?</h2><p>This permanently removes the product. You can mark it inactive instead.</p><div class="actions"><button class="button danger" :disabled="busy" @click="remove">{{ busy ? 'Deleting…' : 'Delete product' }}</button><button autofocus class="button secondary" :disabled="busy" @click="deleting = null">Cancel</button></div></template></dialog>
</section></template>
