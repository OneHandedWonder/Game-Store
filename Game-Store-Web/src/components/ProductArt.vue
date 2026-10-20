<script setup lang="ts">
import { computed, ref, watch } from 'vue'
const props = defineProps<{ name: string; imageUrl?: string | null }>()
const failed = ref(false)
watch(() => props.imageUrl, () => failed.value = false)
const initials = computed(() => props.name.split(/[\s-]+/).slice(0, 2).map(word => word[0]).join(''))
const tone = computed(() => [...props.name].reduce((sum, char) => sum + char.charCodeAt(0), 0) % 5)
const safeUrl = computed(() => /^https?:\/\//i.test(props.imageUrl || '') ? props.imageUrl : null)
</script>
<template><div class="product-art" :class="`tone-${tone}`">
  <img v-if="safeUrl && !failed" :src="safeUrl" :alt="name" loading="lazy" referrerpolicy="no-referrer" @error="failed = true" />
  <div v-else class="art-label" aria-hidden="true"><span class="art-cross">+</span><strong>{{ initials }}</strong><span>GAMESTORE COLLECTION</span></div>
  <span v-if="failed" class="image-note">Image unavailable</span>
</div></template>
