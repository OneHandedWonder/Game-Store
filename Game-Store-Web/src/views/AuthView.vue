<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, json } from '../api'
import { useAuth } from '../stores/auth'
import PasswordStrength from '../components/PasswordStrength.vue'
const props = defineProps<{ mode: string }>(), router = useRouter(), auth = useAuth()
const route = useRoute()
const email = ref(''), password = ref(''), confirm = ref(''), code = ref(''), error = ref(''), success = ref(''), busy = ref(false)
const titles: Record<string, string> = { login: 'Welcome back.', signup: 'Join the good times.', verify: 'Check your inbox.', 'forgot-password': 'Let’s get you back in.', 'reset-password': 'A fresh start.' }
const title = computed(() => titles[props.mode])
const needsPassword = computed(() => ['login', 'signup', 'reset-password'].includes(props.mode))
const needsCode = computed(() => ['verify', 'reset-password'].includes(props.mode))
const newPassword = computed(() => ['signup', 'reset-password'].includes(props.mode))
const passwordPattern = String.raw`[\s\S]*[^\p{L}\p{N}\s][\s\S]*`
watch(() => props.mode, () => { password.value = ''; confirm.value = ''; code.value = ''; error.value = ''; success.value = '' })
async function submit() {
  busy.value = true; error.value = ''; success.value = ''
  try {
    if (newPassword.value && password.value !== confirm.value) throw new Error('The passwords do not match.')
    if (props.mode === 'login') {
      await auth.login(email.value.trim(), password.value); password.value = ''
      const redirect = route.query.redirect
      await router.push(typeof redirect === 'string' && /^\/(cart|products\/[a-fA-F0-9-]+)$/.test(redirect)
        ? redirect : auth.isStaff ? '/staff/products' : '/')
      return
    }
    const endpoints: Record<string, string> = { signup: 'register', verify: 'verify-email', 'forgot-password': 'forgot-password', 'reset-password': 'reset-password' }
    const body: Record<string, string> = { email: email.value.trim() }
    if (props.mode === 'signup') body.password = password.value
    if (props.mode === 'reset-password') body.newPassword = password.value
    if (needsCode.value) body.code = code.value.trim().toUpperCase()
    const result = await api<{ message: string }>(`/auth/${endpoints[props.mode]}`, json('POST', body), false)
    success.value = result.message; password.value = ''; confirm.value = ''; code.value = ''
    if (props.mode === 'reset-password') auth.clear()
  } catch (e) { error.value = (e as Error).message }
  finally { busy.value = false }
}
async function resend() {
  if (!email.value.trim()) { error.value = 'Enter your email address first.'; return }
  busy.value = true; error.value = ''; success.value = ''
  try { success.value = (await api<{message: string}>('/auth/resend-verification', json('POST', { email: email.value.trim() }), false)).message }
  catch (e) { error.value = (e as Error).message }
  finally { busy.value = false }
}
</script>
<template><section class="auth-layout"><div class="auth-aside"><p class="eyebrow">A PLACE FOR YOUR NEXT FAVOURITE</p><h2>Great games.<br>Better <em>together.</em></h2><div class="aside-symbol" aria-hidden="true">g<span>+</span></div><p>Your next adventure is just around the corner.</p></div><div class="auth-panel"><p class="eyebrow">YOUR GAMESTORE ACCOUNT</p><h1>{{ title }}</h1><p class="muted">{{ mode === 'verify' || mode === 'reset-password' ? 'Enter the full code from your email. Codes can only be used once.' : mode === 'forgot-password' ? 'We’ll email a reset code if your account is eligible.' : 'Make a little more room for play.' }}</p><div v-if="error" class="error" role="alert">{{ error }}</div><div v-if="success" class="success" role="status">{{ success }}</div><form @submit.prevent="submit"><fieldset :disabled="busy"><label>Email address<input v-model="email" type="email" autocomplete="email" required maxlength="254" placeholder="you@example.com"></label><label v-if="needsCode">Email code<input v-model="code" class="code-input" autocomplete="one-time-code" required pattern="[a-fA-F0-9]{64}" minlength="64" maxlength="64" spellcheck="false"></label><label v-if="needsPassword">{{ mode === 'reset-password' ? 'New password' : 'Password' }}<input v-model="password" :aria-label="mode === 'reset-password' ? 'New password' : 'Password'" :aria-describedby="newPassword ? 'password-help password-strength' : undefined" type="password" :autocomplete="newPassword ? 'new-password' : 'current-password'" required :minlength="newPassword ? 10 : 1" maxlength="128" :pattern="newPassword ? passwordPattern : undefined"><span v-if="newPassword" id="password-help" class="field-hint">10–128 characters, including a special character. Spaces don’t count as special characters.</span></label><PasswordStrength v-if="newPassword" id="password-strength" :password="password" :email="email" /><label v-if="newPassword">Confirm password<input v-model="confirm" type="password" autocomplete="new-password" required maxlength="128"></label><button class="button full" type="submit">{{ busy ? 'Please wait…' : mode === 'login' ? 'Sign in →' : mode === 'signup' ? 'Create account →' : mode === 'verify' ? 'Verify email →' : mode === 'forgot-password' ? 'Send reset code →' : 'Reset password →' }}</button></fieldset></form><div class="auth-links"><template v-if="mode === 'login'"><RouterLink to="/forgot-password">Forgot password?</RouterLink><RouterLink to="/signup">Create an account ↗</RouterLink><RouterLink to="/verify">Verify your email</RouterLink></template><template v-else-if="mode === 'signup'"><RouterLink to="/verify">I have a verification code →</RouterLink><RouterLink to="/login">Already have an account? Sign in</RouterLink></template><template v-else-if="mode === 'verify'"><button class="text-button" :disabled="busy" @click="resend">Resend verification code</button><RouterLink to="/login">Back to sign in →</RouterLink></template><template v-else><RouterLink to="/reset-password">I have a reset code →</RouterLink><RouterLink to="/login">Back to sign in</RouterLink></template></div></div></section></template>
