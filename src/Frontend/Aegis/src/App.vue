<script setup lang="ts">
import { Toaster } from "@argon/ui/toast";
import { useTheme } from "@argon/theme";
import { onMounted } from "vue";

// This is what puts `dark` on <html>, and it is the only thing that does now: useColorMode() used
// to apply a class of its own here, which meant two things owning the same one. Mounting is still
// early enough — Vue runs setup, render, patch and the mounted hooks in one task, before the
// browser paints — so there is no flash of the light palette to avoid by moving it any earlier.
// Moving it into setup() is in fact worse: it invalidates layout while the views' v-motion
// entrance observers are being set up, and they can then never fire, leaving a blank page.
const { applyAppearanceSettings } = useTheme();
onMounted(async () => {
  applyAppearanceSettings();
});
</script>

<template>
 <div class="min-h-screen bg-background text-foreground">
    <RouterView />
    <Toaster />
  </div>
</template>
