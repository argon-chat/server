<script setup lang="ts">
import { onMounted, ref, watch, onUnmounted, nextTick, computed } from "vue";
import type { Options } from "qr-code-styling";
import CatIconUrl from "@argon/assets/icons/icon_cat_colored.svg?url";
import { useTheme } from "@argon/theme";

const props = defineProps<{ value: string }>();
const { currentTheme } = useTheme();

function makeQrUrl(base: string) {
  const salt = Math.random().toString(36).slice(2, 8);
  return `${base}&_=${salt}`;
}

const timeRefresh = 30; 
const timeLeft = ref(timeRefresh);
const activeIndex = ref<0 | 1>(0);
const isReady = ref(false);
const isRefreshing = ref(false);

const container0 = ref<HTMLDivElement | null>(null);
const container1 = ref<HTMLDivElement | null>(null);

let intervalId: number | null = null;

const config = computed<Options>(() => {
  const isLightTheme = currentTheme.value === "light";
  
  return {
    type: "canvas",
    shape: "square",
    width: 220,
    height: 220,
    margin: 2,
    qrOptions: {
      typeNumber: 0,
      mode: "Byte",
      errorCorrectionLevel: "M"
    },
    image: CatIconUrl,
    imageOptions: {
      hideBackgroundDots: true,
      imageSize: 0.4,
      margin: 0
    },
    dotsOptions: {
      type: "extra-rounded",
      roundSize: true,
      gradient: {
        type: "radial",
        rotation: 0.017,
        colorStops: isLightTheme
          ? [
              { offset: 0, color: "#3b82f6" },
              { offset: 1, color: "#1e40af" }
            ]
          : [
              { offset: 0, color: "#3b82f6" },
              { offset: 1, color: "#475c7f" }
            ]
      }
    },
    backgroundOptions: {
      color: isLightTheme ? "#ffffff" : "#000000"
    },
    cornersSquareOptions: {
      type: "dot",
      gradient: {
        type: "radial",
        rotation: 0.017,
        colorStops: isLightTheme
          ? [{ offset: 1, color: "#1e40af" }]
          : [{ offset: 1, color: "#475c7f" }]
      }
    },
    cornersDotOptions: {
      type: "rounded",
      gradient: {
        type: "radial",
        rotation: 0.017,
        colorStops: isLightTheme
          ? [{ offset: 1, color: "#1e40af" }]
          : [{ offset: 1, color: "#475c7f" }]
      }
    }
  };
});

async function renderQr(targetIndex: 0 | 1) {
  const data = makeQrUrl(props.value);
  isRefreshing.value = true;

  await nextTick();
  const el = targetIndex === 0 ? container0.value : container1.value;
  if (!el) return;

  el.innerHTML = "";
  // 47 kB that the e-mail form next to it does not need in order to be typed into.
  const { default: QRCodeStyling } = await import("qr-code-styling");
  const qr = new QRCodeStyling({ ...config.value, data });
  qr.append(el);

  setTimeout(() => {
    activeIndex.value = targetIndex;
    isReady.value = true;
    isRefreshing.value = false;
    timeLeft.value = timeRefresh;
  }, 300);
}

function initQr() {
  renderQr(0);
}

function startTimer() {
  intervalId = window.setInterval(() => {
    if (timeLeft.value > 0) {
      timeLeft.value -= 1;
    } else {
      const nextIndex = activeIndex.value === 0 ? 1 : 0;
      renderQr(nextIndex);
    }
  }, 1000);
}

onMounted(() => {
  initQr();
  startTimer();
});

onUnmounted(() => {
  if (intervalId) clearInterval(intervalId);
});

watch(
  () => props.value,
  () => {
    const nextIndex = activeIndex.value === 0 ? 1 : 0;
    renderQr(nextIndex);
  }
);

watch(
  currentTheme,
  () => {
    const nextIndex = activeIndex.value === 0 ? 1 : 0;
    renderQr(nextIndex);
  }
);
</script>

<template>
  <div class="flex flex-col items-center justify-center space-y-3 relative">
    <div class="relative w-[220px] h-[220px] flex items-center justify-center">
      <div
        ref="container0"
        class="absolute inset-0 transition-opacity duration-300"
        :class="{
          'opacity-100 z-10': activeIndex === 0,
          'opacity-0 z-0': activeIndex !== 0
        }"
      ></div>

      <div
        ref="container1"
        class="absolute inset-0 transition-opacity duration-300"
        :class="{
          'opacity-100 z-10': activeIndex === 1,
          'opacity-0 z-0': activeIndex !== 1
        }"
      ></div>

      <transition name="fade">
        <div
          v-if="!isReady || isRefreshing"
          class="absolute inset-0 flex items-center justify-center backdrop-blur-sm bg-black/40 rounded-md z-20"
        >
          <div
            class="animate-spin rounded-full h-10 w-10 border-t-2 border-b-2 border-blue-500"
          ></div>
        </div>
      </transition>
    </div>
  </div>
</template>

<style scoped>
.fade-enter-active,
.fade-leave-active {
  transition: opacity 0.3s;
}
.fade-enter-from,
.fade-leave-to {
  opacity: 0;
}
</style>
