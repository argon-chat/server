<script setup lang="ts">
/**
 * Date of birth, typed rather than navigated.
 *
 * A calendar is the wrong instrument for this one field: a birth date is known exactly and is
 * decades away from the month a picker opens on, so choosing one means paging or scrubbing a year
 * dropdown before the day is even visible. Three short numeric boxes take the same keystrokes the
 * person would say the date in, and nothing needs to be aimed at.
 *
 * Day-Month-Year with each box labelled: the labels, not the order, say which is which, so no
 * locale reads it as the wrong date. The model stays a `CalendarDate`, so callers keep the shape the
 * calendar gave them.
 */
import { computed, ref, watch } from "vue";
import { CalendarDate, getLocalTimeZone, today } from "@internationalized/date";
import { useLocale } from "@/store/localeStore";
import type { BirthDate } from "@/components/register/birthDate";

const props = withDefaults(
  defineProps<{
    modelValue?: BirthDate;
    disabled?: boolean;
    /** Youngest age the form accepts. */
    minAge?: number;
    /** Oldest year worth offering — anything before it is a typo, not a birth year. */
    minYear?: number;
  }>(),
  { minAge: 14, minYear: 1900 },
);

const emit = defineEmits<{ (e: "update:modelValue", value: BirthDate | undefined): void }>();

const { t } = useLocale();

const day = ref(props.modelValue ? String(props.modelValue.day).padStart(2, "0") : "");
const month = ref(props.modelValue ? String(props.modelValue.month).padStart(2, "0") : "");
const year = ref(props.modelValue ? String(props.modelValue.year) : "");

const dayEl = ref<HTMLInputElement | null>(null);
const monthEl = ref<HTMLInputElement | null>(null);
const yearEl = ref<HTMLInputElement | null>(null);

// Nothing is complained about until every box has been filled in — an error next to a date the
// person is still halfway through typing is just noise.
const error = ref<string | null>(null);

const isComplete = computed(
  () => day.value.length > 0 && month.value.length > 0 && year.value.length === 4,
);

/** Calendar-real, not just numerically in range: 31 February is three digits of nonsense. */
function isRealDate(y: number, m: number, d: number): boolean {
  const probe = new Date(Date.UTC(y, m - 1, d));
  return (
    probe.getUTCFullYear() === y && probe.getUTCMonth() === m - 1 && probe.getUTCDate() === d
  );
}

function validate(): BirthDate | undefined {
  if (!isComplete.value) {
    error.value = null;
    return undefined;
  }

  const d = Number(day.value);
  const m = Number(month.value);
  const y = Number(year.value);

  if (!isRealDate(y, m, d) || y < props.minYear) {
    error.value = t("dob_invalid");
    return undefined;
  }

  const value = new CalendarDate(y, m, d);
  if (value.compare(today(getLocalTimeZone()).subtract({ years: props.minAge })) > 0) {
    error.value = t("dob_too_young");
    return undefined;
  }

  error.value = null;
  return value;
}

/**
 * True while this component is the reason `modelValue` changed.
 *
 * A half-typed or impossible date is emitted as `undefined`, which `v-model` writes straight back —
 * so the watcher below sees the same `undefined` the form would send to clear the field, and the two
 * mean opposite things: one must leave the boxes exactly as the person is typing them, the other
 * must empty them. Consumed by the first prop change that follows, which is that echo.
 */
let selfEmitted = false;

function emitValue() {
  selfEmitted = true;
  emit("update:modelValue", validate());
}

watch([day, month, year], emitValue);

// A value set from outside (a restored draft, a reset) is written back into the boxes.
watch(
  () => props.modelValue,
  (value) => {
    const isEcho = selfEmitted;
    selfEmitted = false;

    if (!value) {
      if (isEcho) return; // our own "not a date yet" — the boxes hold what is being typed
      day.value = "";
      month.value = "";
      year.value = "";
      error.value = null;
      return;
    }

    // Field by field rather than `compare`, which belongs to the concrete CalendarDate class this
    // no longer names — see birthDate.ts. For a date the two answer the same question.
    const current = validate();
    if (current && current.year === value.year && current.month === value.month && current.day === value.day) return;
    day.value = String(value.day).padStart(2, "0");
    month.value = String(value.month).padStart(2, "0");
    year.value = String(value.year);
  },
);

const digitsOnly = (raw: string, max: number) => raw.replace(/\D/g, "").slice(0, max);

/**
 * Moves on once a box cannot take another digit — either it is full, or a second digit would put it
 * past what the box can hold (a day starting 4, a month starting 2). Typing "1" for January or the
 * 1st waits, because "12" is still coming.
 */
function onSegmentInput(segment: "day" | "month", raw: string) {
  const value = digitsOnly(raw, 2);
  if (segment === "day") day.value = value;
  else month.value = value;

  const limit = segment === "day" ? 3 : 1;
  const settled = value.length === 2 || (value.length === 1 && Number(value) > limit);
  if (!settled) return;

  const next = segment === "day" ? monthEl : yearEl;
  next.value?.focus();
  next.value?.select();
}

function onYearInput(raw: string) {
  year.value = digitsOnly(raw, 4);
}

/** Backspace at the start of an empty box steps back, so a correction never needs the mouse. */
function onBackspace(segment: "month" | "year", event: KeyboardEvent) {
  const value = segment === "month" ? month.value : year.value;
  if (value.length > 0) return;
  event.preventDefault();
  const previous = segment === "month" ? dayEl : monthEl;
  previous.value?.focus();
}

/** Pad a lone digit on the way out, so "5" reads back as "05". */
function padSegment(segment: "day" | "month") {
  const target = segment === "day" ? day : month;
  if (target.value.length === 1) target.value = target.value.padStart(2, "0");
}

/**
 * A date pasted into any box fills all three. Only a full eight digits are taken — a two-digit year
 * would have to be guessed a century for — read as day-month-year, or as year-month-day when the
 * leading four digits can only be a year.
 */
function onPaste(event: ClipboardEvent) {
  const digits = (event.clipboardData?.getData("text") ?? "").replace(/\D/g, "");
  if (digits.length !== 8) return;

  event.preventDefault();

  const isIso = Number(digits.slice(0, 4)) > 31;
  [day.value, month.value, year.value] = isIso
    ? [digits.slice(6, 8), digits.slice(4, 6), digits.slice(0, 4)]
    : [digits.slice(0, 2), digits.slice(2, 4), digits.slice(4)];

  yearEl.value?.focus();
}
</script>

<template>
  <div class="space-y-1.5">
    <!-- The row fills the field's width, as every other input on this form does, so the boxes are
         spaced across it rather than huddled at the left edge. -->
    <div class="flex w-full items-end gap-2">
      <label class="dob-field flex-1">
        <span class="dob-caption">{{ t("dob_day") }}</span>
        <input
          ref="dayEl"
          :value="day"
          :disabled="disabled"
          class="dob-box"
          :class="{ 'is-invalid': error }"
          inputmode="numeric"
          autocomplete="bday-day"
          maxlength="2"
          placeholder="DD"
          @input="onSegmentInput('day', ($event.target as HTMLInputElement).value)"
          @blur="padSegment('day')"
          @paste="onPaste"
        />
      </label>

      <span class="dob-separator">/</span>

      <label class="dob-field flex-1">
        <span class="dob-caption">{{ t("dob_month") }}</span>
        <input
          ref="monthEl"
          :value="month"
          :disabled="disabled"
          class="dob-box"
          :class="{ 'is-invalid': error }"
          inputmode="numeric"
          autocomplete="bday-month"
          maxlength="2"
          placeholder="MM"
          @input="onSegmentInput('month', ($event.target as HTMLInputElement).value)"
          @keydown.backspace="onBackspace('month', $event)"
          @blur="padSegment('month')"
          @paste="onPaste"
        />
      </label>

      <span class="dob-separator">/</span>

      <label class="dob-field flex-[1.6]">
        <span class="dob-caption">{{ t("dob_year") }}</span>
        <input
          ref="yearEl"
          :value="year"
          :disabled="disabled"
          class="dob-box"
          :class="{ 'is-invalid': error }"
          inputmode="numeric"
          autocomplete="bday-year"
          maxlength="4"
          placeholder="YYYY"
          @input="onYearInput(($event.target as HTMLInputElement).value)"
          @keydown.backspace="onBackspace('year', $event)"
          @paste="onPaste"
        />
      </label>
    </div>

    <!-- Only ever says something when there is something to say: the age requirement is not worth
         a standing line under an empty field, it is worth a sentence the moment a date breaks it. -->
    <p v-if="error" class="text-xs text-red-400">{{ error }}</p>
  </div>
</template>

<style scoped>
.dob-field {
  @apply flex flex-col gap-1;
}

/* Centred over its own box, like the digits under it — the three captions are part of the field,
   not a column of left-aligned labels beside it. */
.dob-caption {
  @apply text-center text-[11px] leading-4 text-muted-foreground;
}

.dob-box {
  @apply h-11 w-full rounded-xl border border-border bg-background/50 px-3 text-center text-white
         tabular-nums tracking-[0.08em] outline-none transition-all
         placeholder:tracking-normal placeholder:text-muted-foreground/60
         focus:border-primary focus:ring-2 focus:ring-primary/20
         disabled:cursor-not-allowed disabled:opacity-50;
}

.dob-box.is-invalid {
  @apply border-red-500/70 focus:border-red-500 focus:ring-red-500/30;
}

/* The separators line up with the boxes, not with the captions above them. */
.dob-separator {
  @apply pb-3 text-muted-foreground/60;
}
</style>
