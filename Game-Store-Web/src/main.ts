import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { router } from './router'
import { configureApi } from './api'
import { useAuth } from './stores/auth'
import './style.css'

const app = createApp(App)
app.use(createPinia())
const auth = useAuth()
configureApi(() => auth.token, () => auth.clear('Your session is no longer valid. Please sign in again.'), () => auth.generation)
app.use(router)
app.mount('#app')
