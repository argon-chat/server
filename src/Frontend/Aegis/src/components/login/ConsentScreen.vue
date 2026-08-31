<script setup lang="ts">
import { Button } from "@argon/ui/button";
import { Badge } from "@argon/ui/badge";
import { ShieldCheckIcon, ShieldAlertIcon, CheckIcon, ArrowRightLeftIcon, Loader2Icon, XIcon } from "lucide-vue-next";
import { computed, ref, onMounted } from "vue";

interface ConsentInfo {
  appName: string;
  appDescription?: string;
  appAvatarFileId?: string;
  developerName: string;
  websiteUrl?: string;
  isVerified: boolean;
  requestedScopes: string[];
}

const props = defineProps<{
  consentInfo: ConsentInfo;
  isLoading?: boolean;
}>();

const emit = defineEmits<{
  approve: [];
  deny: [];
  switchAccount: [];
}>();

const entered = ref(false);
onMounted(() => {
  requestAnimationFrame(() => (entered.value = true));
});

const scopeDescriptions: Record<string, { name: string; description: string; icon?: string }> = {
  "identity": { name: "Basic profile", description: "Username and user ID" },
  "email": { name: "Email address", description: "Your email address" },
  "user:read": { name: "Profile info", description: "Display name and avatar" },
  "offline_access": { name: "Stay signed in", description: "Persist session between visits" },
};

const readableScopes = computed(() => {
  return props.consentInfo.requestedScopes
    .map(scope => scopeDescriptions[scope] || { name: scope, description: "Access to this permission" });
});

const appInitial = computed(() => props.consentInfo.appName?.[0]?.toUpperCase() ?? "?");
</script>

<template>
  <div class="flex justify-center items-center min-h-screen px-4">
    <Transition
      enter-active-class="transition-all duration-500 ease-out"
      enter-from-class="opacity-0 scale-95 translate-y-4"
      enter-to-class="opacity-100 scale-100 translate-y-0"
    >
      <div
        v-if="entered"
        class="w-full max-w-sm rounded-3xl border border-white/[0.08] bg-black/60 backdrop-blur-2xl shadow-[0_0_80px_-20px_rgba(59,130,246,0.12)] p-8 space-y-7 relative overflow-hidden"
      >
        <!-- Ambient glow -->
        <div class="absolute -top-20 left-1/2 -translate-x-1/2 w-60 h-60 bg-blue-500/[0.04] rounded-full blur-3xl pointer-events-none" />

        <!-- Close -->
        <button
          class="absolute top-4 right-4 p-1.5 rounded-lg text-white/30 hover:text-white/60 hover:bg-white/5 transition-all"
          :disabled="isLoading"
          @click="emit('deny')"
        >
          <XIcon :size="16" />
        </button>

        <!-- App identity -->
        <div class="flex flex-col items-center space-y-4 pt-2">
          <div class="relative">
            <div
              class="w-16 h-16 rounded-2xl bg-gradient-to-br from-blue-500/20 to-indigo-500/20 border border-white/[0.08] flex items-center justify-center text-2xl font-bold text-white shadow-lg"
            >
              {{ appInitial }}
            </div>
            <!-- Verified badge overlay -->
            <div
              v-if="consentInfo.isVerified"
              class="absolute -bottom-1 -right-1 w-6 h-6 rounded-full bg-blue-500 border-2 border-black flex items-center justify-center"
            >
              <CheckIcon :size="12" class="text-white" stroke-width="3" />
            </div>
          </div>

          <div class="text-center space-y-1.5">
            <h2 class="text-lg font-semibold text-white tracking-tight">{{ consentInfo.appName }}</h2>
            <p class="text-xs text-white/35">
              by <span class="text-white/50">{{ consentInfo.developerName }}</span>
            </p>
          </div>
        </div>

        <!-- Unverified warning -->
        <div
          v-if="!consentInfo.isVerified"
          class="flex items-center gap-3 px-4 py-3 rounded-xl bg-red-500/[0.06] border border-red-500/[0.12]"
        >
          <ShieldAlertIcon :size="16" class="text-red-400 shrink-0" />
          <p class="text-[12px] text-red-300/80 leading-relaxed">
            Unverified application. Only authorize if you trust this developer.
          </p>
        </div>

        <!-- Permissions -->
        <div class="space-y-3">
          <div class="flex items-center justify-between px-1">
            <span class="text-[11px] font-medium text-white/30 uppercase tracking-wider">Permissions</span>
            <span class="text-[11px] text-white/20">{{ readableScopes.length }}</span>
          </div>

          <div class="space-y-1.5">
            <div
              v-for="(scope, i) in readableScopes"
              :key="scope.name"
              class="flex items-center gap-3 px-4 py-2.5 rounded-xl bg-white/[0.03] border border-white/[0.04] transition-all duration-300 hover:bg-white/[0.05]"
              :style="{ transitionDelay: `${i * 50}ms` }"
            >
              <div class="w-5 h-5 rounded-full bg-blue-500/10 border border-blue-500/20 flex items-center justify-center shrink-0">
                <CheckIcon :size="10" class="text-blue-400" stroke-width="3" />
              </div>
              <div class="min-w-0 flex-1">
                <p class="text-[13px] font-medium text-white/80">{{ scope.name }}</p>
                <p class="text-[11px] text-white/25">{{ scope.description }}</p>
              </div>
            </div>
          </div>
        </div>

        <!-- Actions -->
        <div class="space-y-2.5">
          <Button
            @click="emit('approve')"
            :disabled="isLoading"
            class="w-full h-11 text-sm font-semibold rounded-xl transition-all duration-300"
            :class="isLoading
              ? 'bg-blue-500/20 text-blue-300 cursor-wait'
              : 'bg-blue-500 hover:bg-blue-400 text-white hover:shadow-[0_0_24px_-4px_rgba(59,130,246,0.4)]'"
          >
            <Loader2Icon v-if="isLoading" :size="16" class="mr-2 animate-spin" />
            <ShieldCheckIcon v-else :size="16" class="mr-2" />
            {{ isLoading ? "Authorizing..." : "Authorize" }}
          </Button>

          <div class="flex gap-2">
            <button
              @click="emit('deny')"
              :disabled="isLoading"
              class="flex-1 h-9 text-xs text-white/30 hover:text-white/60 hover:bg-white/[0.04] rounded-xl transition-all border border-transparent hover:border-white/[0.06]"
            >
              Deny
            </button>
            <button
              @click="emit('switchAccount')"
              :disabled="isLoading"
              class="flex-1 h-9 text-xs text-white/30 hover:text-white/60 hover:bg-white/[0.04] rounded-xl transition-all border border-transparent hover:border-white/[0.06] flex items-center justify-center gap-1.5"
            >
              <ArrowRightLeftIcon :size="11" />
              Switch account
            </button>
          </div>
        </div>

        <!-- Footer -->
        <p class="text-[10px] text-center text-white/15 pt-1">
          You can revoke access anytime in account settings
        </p>
      </div>
    </Transition>
  </div>
</template>
