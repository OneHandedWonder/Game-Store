<script setup lang="ts">
import { ref } from 'vue'
import { api, json } from '../api'
import { useAuth } from '../stores/auth'
import type { Role } from '../types'
import PasswordStrength from '../components/PasswordStrength.vue'
const auth = useAuth(), email = ref(''), password = ref(''), userId = ref(''), role = ref<Role>('employee')
const error = ref(''), success = ref(''), busy = ref(false)
async function createStaff() {
  busy.value = true; error.value = ''; success.value = ''
  try {
    const result = await api<{id: string; message: string}>('/auth/users/staff', json('POST', { email: email.value.trim(), password: password.value }))
    password.value = ''; success.value = `${result.message} User ID: ${result.id}`; userId.value = result.id
  } catch (e) { error.value = (e as Error).message }
  finally { busy.value = false }
}
async function changeRole() {
  busy.value = true; error.value = ''; success.value = ''
  try {
    const id = userId.value.trim()
    await api(`/auth/users/${encodeURIComponent(id)}/role`, json('PUT', { role: role.value }))
    success.value = 'Role updated. The user must sign in again if their role changed.'
    if (id.toLowerCase() === auth.user?.id.toLowerCase() && role.value !== auth.user.role) auth.clear('Your role changed. Sign in again to continue.')
  } catch (e) { error.value = (e as Error).message }
  finally { busy.value = false }
}
</script>
<template><section class="section"><p class="eyebrow">ADMIN WORKSPACE</p><h1>The team<span class="accent">.</span></h1><p class="muted">Create staff accounts and manage account permissions.</p><p v-if="error" class="error" role="alert">{{ error }}</p><p v-if="success" class="success" role="status">{{ success }}</p><div class="two-panels"><section class="panel"><h2>Add an employee</h2><p class="muted">They’ll need to verify their email before signing in. Share the initial password through a secure channel.</p><form @submit.prevent="createStaff"><fieldset :disabled="busy"><label>Email address<input v-model="email" type="email" required maxlength="254" autocomplete="off"></label><label>Initial password<input v-model="password" aria-label="Initial password" aria-describedby="staff-password-help staff-password-strength" type="password" required minlength="10" maxlength="128" autocomplete="new-password" pattern="[\s\S]*[^\p{L}\p{N}\s][\s\S]*"><span id="staff-password-help" class="field-hint">10–128 characters with at least one special character.</span></label><PasswordStrength id="staff-password-strength" :password="password" :email="email" /><button class="button" type="submit">{{ busy ? 'Please wait…' : 'Create employee' }}</button></fieldset></form></section><section class="panel"><h2>Change a role</h2><p class="muted">Enter the account’s user ID. Users can find their ID in the account API; new staff IDs are shown after creation.</p><form @submit.prevent="changeRole"><fieldset :disabled="busy"><label>User ID<input v-model="userId" required pattern="[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}" placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" spellcheck="false"></label><label>Role<select v-model="role" aria-label="Role"><option value="default">Default · customer</option><option value="employee">Employee · product management</option><option value="admin">Admin · product and team management</option></select></label><p class="field-hint">Changing your own role signs you out. Keep at least one admin account.</p><button class="button secondary" type="submit">Update role</button></fieldset></form></section></div><p v-if="auth.user" class="muted">Your user ID: <code>{{ auth.user.id }}</code></p></section></template>
