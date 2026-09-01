import { reactive } from "vue"

export interface ToastItem {
  id: number
  title: string
  description?: string
  variant: "default" | "destructive"
  duration: number
  leaving: boolean
}

interface ToastOptions {
  title: string
  description?: string
  variant?: "default" | "destructive"
  duration?: number
}

const MAX_VISIBLE = 5
let nextId = 0

export const toasts = reactive<ToastItem[]>([])

export function dismiss(id: number) {
  const t = toasts.find(x => x.id === id)
  if (!t) return
  t.leaving = true
  setTimeout(() => {
    const idx = toasts.findIndex(x => x.id === id)
    if (idx !== -1) toasts.splice(idx, 1)
  }, 300)
}

function addToast(options: ToastOptions) {
  const item: ToastItem = {
    id: ++nextId,
    title: options.title,
    description: options.description,
    variant: options.variant ?? "default",
    duration: options.duration ?? 4000,
    leaving: false,
  }
  toasts.push(item)
  if (toasts.length > MAX_VISIBLE) {
    dismiss(toasts[0].id)
  }
}

export function useToast() {
  return { toast: addToast }
}
