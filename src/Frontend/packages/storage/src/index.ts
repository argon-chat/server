// @argon/storage - Persisted storage utilities for Vue
import superjson from "superjson";
import {
  type Reactive,
  type Ref,
  type UnwrapRef,
  reactive,
  ref,
  watch,
  watchEffect,
} from "vue";
import { logger } from "@argon/core";

export type PersistedRef<T> = {
  readonly value: UnwrapRef<T>;
  set: (value: T) => void;
  set_key: <K extends keyof T>(key: K, value: T[K]) => void;
  destroy: () => void;
};

export function persisted<T>(id: string, initial_value: T): PersistedRef<T> {
  const get_store = () => {
    const item = localStorage.getItem(id);
    return item ? (JSON.parse(item) as T) : null;
  };

  const store = ref<T>(get_store() ?? initial_value);
  const locked = ref(false);

  function set(value: T) {
    if (locked.value) return;
    store.value = value as UnwrapRef<T>;
    localStorage.setItem(id, JSON.stringify(value));
  }

  function set_key<K extends keyof T>(key: K, value: T[K]) {
    const s = get_store();
    // @ts-expect-error internal
    s[key] = value;
    // @ts-expect-error internal
    set(s);
  }

  return {
    get value() {
      return store.value;
    },
    set,
    set_key,
    destroy() {
      locked.value = true;
      localStorage.removeItem(id);
    },
  };
}

export function persistedValue<T extends object | string | number | boolean>(
  key: string,
  defaultValue: T extends number
    ? number
    : T extends string
      ? string
      : T extends boolean
        ? boolean
        : T,
): T extends object ? Reactive<T> : Ref<T> {
  const storedValue = localStorage.getItem(key);

  let value: any;

  if (storedValue !== null) {
    try {
      const parsed = superjson.parse(storedValue);
      if (typeof parsed === "object" && parsed !== null) {
        value = reactive(parsed) as UnwrapRef<T>;
      } else {
        value = ref(parsed) as Ref<T>;
      }
    } catch (e) {
      logger.error(
        `Error parsing JSON from localStorage for the key "${key}"`,
        e,
      );
      value =
        typeof defaultValue === "object"
          ? (reactive(defaultValue) as UnwrapRef<T>)
          : (ref(defaultValue) as Ref<T>);
    }
  } else {
    value =
      typeof defaultValue === "object"
        ? (reactive(defaultValue) as UnwrapRef<T>)
        : (ref(defaultValue) as Ref<T>);
  }

  if (typeof defaultValue === "object") {
    watchEffect(() => {
      localStorage.setItem(key, superjson.stringify(value));
    });
  } else {
    watch(value, (newValue) => {
      localStorage.setItem(key, superjson.stringify(newValue));
    });
  }

  return value as any;
}
