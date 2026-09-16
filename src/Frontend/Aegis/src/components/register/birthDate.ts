/**
 * The shape the date-of-birth input hands back.
 *
 * A structural type rather than `CalendarDate` from `@internationalized/date`, and not by taste:
 * that class carries a private field, and Vue strips the private brand when it derives a `v-model`
 * type from `defineEmits`. The emitted type and the declared prop type then differ by exactly that
 * brand and are never assignable — an error that says `#private is missing` and nothing about what
 * to do.
 *
 * `CalendarDate` satisfies this, so the input still builds and validates real dates; what changes is
 * only what the two components promise each other. `toString()` is the ISO `yyyy-MM-dd` the
 * registration call sends.
 */
export interface BirthDate {
  readonly year: number;
  readonly month: number;
  readonly day: number;
  toString(): string;
}
