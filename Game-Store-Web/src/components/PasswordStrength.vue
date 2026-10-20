<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
const props = defineProps<{ password: string; email?: string }>()
interface Strength { score: number; warning: string | null; suggestions: string[] }
const result = ref<Strength | null>(null), error = ref(''), checking = ref(false)
const labels = ['Very weak', 'Weak', 'Fair', 'Strong', 'Very strong']
const lengthValid = computed(() => props.password.length >= 10 && props.password.length <= 128)
const specialValid = computed(() => /[^\p{L}\p{N}\s]/u.test(props.password))
let worker: Worker | undefined, timer: ReturnType<typeof setTimeout> | undefined, revision = 0
function stopWorker() { worker?.terminate(); worker = undefined }
watch(() => [props.password, props.email], () => {
  const id = ++revision
  clearTimeout(timer); result.value = null; error.value = ''; checking.value = false
  if (!props.password) { stopWorker(); return }
  if (props.password.length > 128) { error.value = 'Use no more than 128 characters.'; stopWorker(); return }
  checking.value = true
  timer = setTimeout(() => {
    try {
      if (!worker) {
        worker = new Worker(new URL('../workers/passwordStrength.ts', import.meta.url), { type: 'module' })
        worker.onmessage = (event: MessageEvent<Strength & { id: number; error?: string }>) => {
          if (event.data.id !== revision) return
          checking.value = false
          if (event.data.error) error.value = event.data.error
          else result.value = { score: event.data.score, warning: event.data.warning, suggestions: event.data.suggestions }
        }
        worker.onerror = () => {
          result.value = null; checking.value = false
          error.value = 'The strength checker is unavailable. Use a long, unique password and try again.'
          stopWorker()
        }
      }
      worker.postMessage({ id, password: props.password, email: props.email ?? '' })
    } catch {
      checking.value = false; error.value = 'The strength checker is unavailable. Please try again.'; stopWorker()
    }
  }, 200)
}, { immediate: true })
onUnmounted(() => { revision++; clearTimeout(timer); stopWorker() })
</script>

<template><div class="password-strength">
  <div class="strength-feedback" aria-live="polite" aria-atomic="true">
    <template v-if="result"><div class="strength-heading"><strong>Password strength</strong><span>{{ labels[result.score] }}</span></div>
      <div class="strength-meter" role="meter" aria-label="Password strength" aria-valuemin="0" aria-valuemax="4" :aria-valuenow="result.score" :aria-valuetext="labels[result.score]" :data-score="result.score"><span :style="{ width: `${(result.score + 1) * 20}%` }"></span></div>
      <p v-if="result.warning" class="strength-warning">{{ result.warning }}</p><ul v-if="result.suggestions.length" class="strength-suggestions"><li v-for="suggestion in result.suggestions" :key="suggestion">{{ suggestion }}</li></ul>
    </template>
    <p v-else-if="checking">Checking password strength…</p><p v-else-if="error" class="danger-text">{{ error }}</p><p v-else>Use several unrelated words or a password manager to create a unique password.</p>
  </div>
  <ul class="password-rules" aria-label="Password requirements"><li :class="{ met: lengthValid }">{{ lengthValid ? '✓' : '○' }} 10–128 characters</li><li :class="{ met: specialValid }">{{ specialValid ? '✓' : '○' }} At least one special character; whitespace does not count</li></ul>
  <p class="field-hint">This is an estimate, not a guarantee or a breach check. Checking happens on your device.</p>
</div></template>
