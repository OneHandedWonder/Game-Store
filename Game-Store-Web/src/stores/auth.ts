import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { api, ApiError, json } from '../api'
import type { TokenResponse, User } from '../types'

export const useAuth = defineStore('auth', () => {
  const token = ref(''), user = ref<User | null>(null), notice = ref('')
  let timer: ReturnType<typeof setTimeout> | undefined
  const generation = ref(0)
  const isStaff = computed(() => user.value?.role === 'employee' || user.value?.role === 'admin')
  const isAdmin = computed(() => user.value?.role === 'admin')
  function clear(message = '') { generation.value++; clearTimeout(timer); token.value = ''; user.value = null; notice.value = message }
  async function refresh() {
    const version = generation.value, currentToken = token.value
    if (!currentToken) throw new ApiError('Please sign in again.', 401)
    const result = await api<User>('/auth/me')
    if (version !== generation.value || currentToken !== token.value) throw new ApiError('The session changed. Please try again.', 401)
    user.value = result
  }
  async function login(email: string, password: string) {
    clear()
    const version = generation.value
    const result = await api<TokenResponse>('/auth/login', json('POST', { email, password }), false)
    if (version !== generation.value) throw new ApiError('Sign-in was cancelled because the session changed.', 401)
    const remaining = Date.parse(result.expiresAtUtc) - Date.now()
    if (!Number.isFinite(remaining) || remaining <= 0) throw new Error('The returned session has expired. Please sign in again.')
    token.value = result.accessToken
    try { await refresh() } catch (error) { if (version === generation.value) clear(); throw error }
    timer = setTimeout(() => { if (version === generation.value) clear('Your session expired. Please sign in again.') },
      Math.max(0, Date.parse(result.expiresAtUtc) - Date.now()))
  }
  return { token, user, notice, generation, isStaff, isAdmin, clear, refresh, login }
})
