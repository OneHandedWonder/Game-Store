<script setup lang="ts">
import { watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuth } from './stores/auth'
import { navigationFailure } from './router'
import { useCart } from './stores/cart'
const auth = useAuth(), route = useRoute(), router = useRouter()
const cart = useCart()
watch(() => auth.user?.id, id => {
  cart.clearLocal()
  if (id) void cart.load().catch(() => { /* Cart errors are available on the cart screen. */ })
}, { immediate: true })
watch(() => auth.user, user => { if (!user && (route.meta.staff || route.meta.admin || route.meta.authenticated)) void router.replace('/login') })
function logout() { auth.clear('You have signed out on this device.'); void router.push('/') }
</script>

<template>
  <a class="skip" href="#main">Skip to content</a>
  <div class="topbar">A little more play. A lot more possibility. <span>Prices in DKK</span></div>
  <header class="header">
    <RouterLink to="/" class="brand"><span class="brand-mark">g<span>+</span></span>game<span class="brand-light">store</span><span class="brand-dot">.</span></RouterLink>
    <nav aria-label="Main navigation">
      <RouterLink to="/">The collection</RouterLink>
      <RouterLink to="/cart">Cart<span v-if="cart.cart && !cart.error"> ({{ cart.count }})</span><span v-if="cart.error" class="sr-only"> — unavailable; open to retry</span></RouterLink>
      <RouterLink v-if="auth.isStaff" to="/staff/products">Inventory</RouterLink>
      <RouterLink v-if="auth.isAdmin" to="/staff/team">Team</RouterLink>
      <RouterLink v-if="auth.isAdmin" to="/staff/monitoring">Monitoring</RouterLink>
    </nav>
    <div class="account-nav"><span v-if="auth.user" class="badge">{{ auth.user.role }}</span>
      <button v-if="auth.user" class="button secondary small" @click="logout">Sign out</button>
      <RouterLink v-else class="button secondary small" to="/login">Sign in <span aria-hidden="true">↗</span></RouterLink>
    </div>
  </header>
  <main id="main"><div v-if="navigationFailure" class="error" role="alert">{{ navigationFailure.message }} <button class="text-button" @click="router.push(navigationFailure.path)">Retry navigation</button></div><div v-if="auth.notice" class="notice" role="status">{{ auth.notice }} <button class="text-button" @click="auth.notice = ''">Dismiss</button></div><RouterView /></main>
  <footer><RouterLink to="/" class="footer-brand">gamestore.</RouterLink><p>Find your next favourite.</p><span>Games. Gear. Good times.</span></footer>
</template>
