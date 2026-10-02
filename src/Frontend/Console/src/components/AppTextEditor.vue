<script setup lang="ts">
import { computed, ref } from "vue"
import { Check } from "@lucide/vue"
import { useToast } from "@/composables/useToast"
import { useApi } from "@/store/apiStore"
import { GlassBadge, GlassButton, GlassCard } from "@/components/base"
import type { AppDetails, LocaleValue } from "@/lib/glue/accountConsole"
import {
  APP_TEXT_LOCALES, appTextErrorMessage, appTextLength, normalizeAppText, type AppTextKeyInfo,
} from "@/lib/appTexts"

/** Every locale of one localized string of an application, edited and saved together. */
const props = defineProps<{
  app: AppDetails
  textKey: AppTextKeyInfo
  placeholder?: string
}>()

defineSlots<{
  hint(): any
  preview(props: { value: string }): any
}>()

const api = useApi()
const { toast } = useToast()

function toDraft(values: readonly { locale: string; value: string }[]): Record<string, string> {
  const draft: Record<string, string> = Object.fromEntries(APP_TEXT_LOCALES.map(l => [l.code, ""]))
  for (const v of values) draft[v.locale] = v.value
  return draft
}

const initial = (props.app.texts ?? []).filter(t => t.key === props.textKey.key)
const saved = ref(toDraft(initial))
const draft = ref(toDraft(initial))
const isSaving = ref(false)
const errorLocale = ref<string | null>(null)
const previewLocale = ref("en")

const max = computed(() => props.textKey.maxLength)
const normalize = (raw: string) => normalizeAppText(raw, props.textKey.singleLine)

// Languages the server holds that this list does not offer are shown and kept as they are.
const rows = computed(() => [
  ...APP_TEXT_LOCALES,
  ...Object.keys(draft.value)
    .filter(code => !APP_TEXT_LOCALES.some(l => l.code === code))
    .map(code => ({ code, name: code })),
])

const lengths = computed(() =>
  Object.fromEntries(Object.entries(draft.value).map(([code, text]) => [code, appTextLength(text, props.textKey.singleLine)])))

const anyFilled = computed(() => Object.values(lengths.value).some(n => n > 0))
const englishMissing = computed(() => anyFilled.value && lengths.value.en === 0)
const tooLong = computed(() => Object.values(lengths.value).some(n => n > max.value))

const hasChanges = computed(() =>
  Object.keys(draft.value).some(code => normalize(draft.value[code]) !== normalize(saved.value[code] ?? "")))

const canSave = computed(() => hasChanges.value && !englishMissing.value && !tooLong.value)

const preview = computed(() => normalize(draft.value[previewLocale.value] ?? "") || normalize(draft.value.en ?? ""))

function counterClass(code: string) {
  const n = lengths.value[code] ?? 0
  if (n > max.value) return "text-red-400"
  if (n > max.value * 0.9) return "text-amber-400"
  return "text-text-muted"
}

function isInvalid(code: string) {
  return errorLocale.value === code
    || (code === "en" && englishMissing.value)
    || (lengths.value[code] ?? 0) > max.value
}

async function save() {
  const values: LocaleValue[] = Object.entries(draft.value)
    .map(([locale, value]) => ({ locale, value: normalize(value) }))
    .filter(v => v.value.length > 0)

  try {
    isSaving.value = true
    errorLocale.value = null
    const result = await api.appsManagement.SetAppText(props.app.teamId, props.app.appId, props.textKey.key, values)
    if (result.isFailedSetAppText()) {
      errorLocale.value = result.locale
      toast({ title: "Failed to save", description: appTextErrorMessage(result.error, result.locale, max.value), variant: "destructive" })
      return
    }
    if (result.isSuccessSetAppText()) {
      saved.value = toDraft(result.values)
      draft.value = toDraft(result.values)
    }
    toast({ title: values.length ? "Saved" : "Cleared", description: values.length ? "People see it on their next look." : "Nothing is shown any more." })
  } catch (err: any) {
    toast({ title: "Failed to save", description: err?.message ?? "Error", variant: "destructive" })
  } finally {
    isSaving.value = false
  }
}
</script>

<template>
  <GlassCard class="space-y-5">
    <div class="flex items-start justify-between gap-4">
      <p class="text-xs text-text-muted">
        <slot name="hint" />
        Up to {{ max }} characters per language. English is required; people whose language you leave
        empty see English.
      </p>
      <GlassButton size="sm" variant="accent" :loading="isSaving" :disabled="!canSave" @click="save">
        <Check class="w-3.5 h-3.5" /> Save
      </GlassButton>
    </div>

    <slot name="preview" :value="preview" />

    <div class="space-y-2">
      <div v-for="row in rows" :key="row.code" class="flex items-center gap-3">
        <div class="w-32 shrink-0 flex items-center gap-2">
          <span class="text-sm text-text-primary truncate">{{ row.name }}</span>
          <GlassBadge v-if="row.code === 'en'" variant="accent" class="text-[9px]">Required</GlassBadge>
        </div>
        <input
          v-if="textKey.singleLine"
          v-model="draft[row.code]"
          type="text"
          dir="auto"
          :placeholder="row.code === 'en' ? placeholder : 'Falls back to English'"
          class="glass-input flex-1 min-w-0 px-3 py-2 text-sm"
          :class="isInvalid(row.code) ? 'border-danger! shadow-[0_0_0_3px_rgba(239,68,68,0.15)]' : ''"
          @focus="previewLocale = row.code"
          @input="errorLocale = null"
        />
        <textarea
          v-else
          v-model="draft[row.code]"
          rows="3"
          dir="auto"
          :placeholder="row.code === 'en' ? placeholder : 'Falls back to English'"
          class="glass-input flex-1 min-w-0 px-3 py-2 text-sm resize-y"
          :class="isInvalid(row.code) ? 'border-danger! shadow-[0_0_0_3px_rgba(239,68,68,0.15)]' : ''"
          @focus="previewLocale = row.code"
          @input="errorLocale = null"
        />
        <span class="w-14 shrink-0 text-right text-[11px] tabular-nums" :class="counterClass(row.code)">
          {{ lengths[row.code] ?? 0 }}/{{ max }}
        </span>
      </div>
    </div>

    <p v-if="englishMissing" class="text-xs text-red-400">Fill in English as well — it is what everyone else sees.</p>
  </GlassCard>
</template>
