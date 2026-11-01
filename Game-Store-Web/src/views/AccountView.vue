<script setup lang="ts">
import { onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, json } from '../api'
import { useAuth } from '../stores/auth'
import PasswordStrength from '../components/PasswordStrength.vue'
const auth = useAuth(), router = useRouter(), current = ref(''), password = ref(''), confirm = ref(''), error = ref(''), busy = ref(false)
let active = true
async function submit() {
  if (busy.value) return
  error.value = ''; busy.value = true
  const generation = auth.generation
  try {
    if (password.value !== confirm.value) throw new Error('The passwords do not match.')
    if (current.value === password.value) throw new Error('Choose a different new password.')
    await api('/auth/change-password', json('POST', { currentPassword: current.value, newPassword: password.value }))
    if (!active || generation !== auth.generation) return
    auth.clear('Password changed. All sessions have been revoked. Sign in again.')
    await router.push('/login')
  } catch (e) { if (active && generation === auth.generation) error.value = (e as Error).message }
  finally { current.value = ''; busy.value = false }
}
onUnmounted(() => { active = false; current.value = ''; password.value = ''; confirm.value = '' })
</script>
<template><section class="section"><h1>Account security.</h1><div class="two-panels"><form class="panel" @submit.prevent="submit"><h2>Change password</h2><p class="muted">Requires your current password. A successful change signs out all sessions and invalidates pending password reset codes.</p>
  <div v-if="error" class="error" role="alert">{{ error }}</div><fieldset :disabled="busy">
    <label>Current password<input v-model="current" type="password" autocomplete="current-password" required maxlength="128"></label>
    <label>New password<input v-model="password" type="password" autocomplete="new-password" required minlength="10" maxlength="128" pattern="[\s\S]*[^\p{L}\p{N}\s][\s\S]*" aria-describedby="new-password-strength"></label>
    <PasswordStrength id="new-password-strength" :password="password" />
    <label>Confirm new password<input v-model="confirm" type="password" autocomplete="new-password" required maxlength="128"></label>
    <button class="button" type="submit">{{ busy ? 'Changing…' : 'Change password' }}</button>
  </fieldset></form></div></section></template>
