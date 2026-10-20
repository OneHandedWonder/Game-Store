import { createRouter, createWebHistory } from 'vue-router'
import { ref } from 'vue'
import { ApiError } from './api'
import { useAuth } from './stores/auth'
import CatalogView from './views/CatalogView.vue'
import ProductView from './views/ProductView.vue'
import AuthView from './views/AuthView.vue'
import InventoryView from './views/InventoryView.vue'
import TeamView from './views/TeamView.vue'
import NotFoundView from './views/NotFoundView.vue'
import CartView from './views/CartView.vue'
import MonitoringView from './views/MonitoringView.vue'

export const navigationFailure = ref<{ path: string; message: string } | null>(null)
export const router = createRouter({ history: createWebHistory(), scrollBehavior: () => ({ top: 0 }), routes: [
  { path: '/', component: CatalogView },
  { path: '/products/:id', component: ProductView },
  { path: '/cart', component: CartView, meta: { authenticated: true } },
  ...['login', 'signup', 'verify', 'forgot-password', 'reset-password'].map(mode => ({ path: `/${mode}`, component: AuthView, props: { mode } })),
  { path: '/staff/products', component: InventoryView, meta: { staff: true } },
  { path: '/staff/team', component: TeamView, meta: { admin: true } },
  { path: '/staff/monitoring', component: MonitoringView, meta: { admin: true } },
  { path: '/:pathMatch(.*)*', component: NotFoundView },
] })
router.beforeEach(async to => {
  navigationFailure.value = null
  if (!to.meta.staff && !to.meta.admin && !to.meta.authenticated) return
  const auth = useAuth()
  if (!auth.token) return { path: '/login', query: { redirect: to.fullPath } }
  const generation = auth.generation
  try { await auth.refresh() } catch (error) {
    if (generation !== auth.generation && auth.token) return false
    if (!auth.token || (error instanceof ApiError && error.status === 401)) return '/login'
    navigationFailure.value = { path: to.fullPath, message: (error as Error).message }
    return false
  }
  if ((to.meta.admin && !auth.isAdmin) || (to.meta.staff && !auth.isStaff)) return '/'
})
