<script setup lang="ts">
/**
 * Lucide, inlined.
 *
 * Drawn from path data held here rather than imported from an icon package or fetched from anywhere:
 * this page is served by a container on a machine that may have no route to the internet at all, and a
 * missing icon is the least of what a blocked request costs — a stylesheet or a script that does not
 * arrive is a blank page during an install.
 *
 * The support portal uses the `lucide` Vue components; these are the same paths, taken by hand, because
 * adding the dependency to pull in five glyphs is weight for nothing.
 */

const PATHS: Record<string, readonly string[]> = {
    check: ["M20 6 9 17l-5-5"],
    alert: [
        "M12 9v4",
        "M12 17h.01",
        "m10.29 3.86-8.47 14.14A2 2 0 0 0 3.53 21h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z",
    ],
    arrow: ["M5 12h14M12 5l7 7-7 7"],
    lock: ["M7 11V7a5 5 0 0 1 10 0v4", "M5 11h14v10H5z"],
    external: ["M15 3h6v6", "M10 14 21 3", "M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"],
};

const props = withDefaults(defineProps<{ name: keyof typeof PATHS | string; size?: number }>(), { size: 15 });

const paths = PATHS[props.name] ?? [];
</script>

<template>
  <svg
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="2"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    style="flex-shrink: 0"
  >
    <path v-for="(d, index) in paths" :key="index" :d="d" />
  </svg>
</template>
