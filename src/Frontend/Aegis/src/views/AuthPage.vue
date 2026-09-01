<script setup lang="ts">
import router from "@/router";
import { useSimpleAuthStore } from "@/store/simpleAuthStore";
import { onMounted } from "vue";
import AuthTabs from "@/components/login/AuthTabs.vue";
import IconSw from "@argon/assets/icons/icon_cat.svg"

const authStore = useSimpleAuthStore();
onMounted(async () => {
  await authStore.checkExistingSession();
});

</script>

<template>
  <div v-motion-slide-visible-once-top :duration="200" style="overflow: hidden;"
    class="container relative h-screen flex flex-col items-center justify-center max-w-none px-0">
    <div class="relative h-full w-full flex flex-col p-4 sm:p-10 text-white dark:border-r">
      <div class="z-20 flex items-center text-lg font-medium justify-center sm:justify-start sm:absolute sm:top-10 sm:left-10 mb-4 sm:mb-0">
        <IconSw class="w-10 h-10 sm:w-12 sm:h-12 pr-2 fill-blue-500" />
        <span class="text-base sm:text-lg">Argon Chat</span>
      </div>
      <div v-if="authStore.isCheckingSession" class="flex items-center justify-center flex-1">
        <div class="text-white text-lg">Checking session...</div>
      </div>
      <AuthTabs v-else />
    </div>
  </div>
</template>
