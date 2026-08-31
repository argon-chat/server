<script setup lang="ts">
import { UserCircleIcon, CheckIcon, ChevronRightIcon, PlusIcon, UsersIcon } from "@lucide/vue";
import { ref, onMounted } from "vue";

interface Account {
  userId: string;
  username: string;
  avatarFileId?: string;
  isCurrent: boolean;
}

const props = defineProps<{
  accounts: Account[];
  isLoading?: boolean;
}>();

const emit = defineEmits<{
  select: [userId: string];
  addAccount: [];
}>();

const entered = ref(false);
onMounted(() => {
  requestAnimationFrame(() => (entered.value = true));
});
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
        class="w-full max-w-sm rounded-3xl border border-white/[0.08] bg-black/60 backdrop-blur-2xl shadow-[0_0_80px_-20px_rgba(139,92,246,0.12)] p-8 space-y-7 relative overflow-hidden"
      >
        <!-- Ambient glow -->
        <div class="absolute -top-20 left-1/2 -translate-x-1/2 w-60 h-60 bg-violet-500/[0.04] rounded-full blur-3xl pointer-events-none" />

        <!-- Header -->
        <div class="flex flex-col items-center space-y-4 pt-2">
          <div class="w-14 h-14 rounded-2xl bg-gradient-to-br from-violet-500/15 to-indigo-500/15 border border-white/[0.08] flex items-center justify-center">
            <UsersIcon :size="24" class="text-violet-400" />
          </div>
          <div class="text-center space-y-1.5">
            <h2 class="text-lg font-semibold text-white tracking-tight">Choose an account</h2>
            <p class="text-xs text-white/35">to continue to the application</p>
          </div>
        </div>

        <!-- Accounts list -->
        <div class="space-y-1.5">
          <button
            v-for="(account, i) in accounts"
            :key="account.userId"
            @click="emit('select', account.userId)"
            :disabled="isLoading"
            :aria-label="`Sign in as ${account.username}${account.isCurrent ? ' (currently signed in)' : ''}`"
            class="w-full flex items-center gap-3 px-4 py-3 rounded-xl bg-white/[0.03] border border-white/[0.04] hover:bg-white/[0.06] hover:border-white/[0.08] transition-all duration-200 group disabled:opacity-40 disabled:cursor-not-allowed"
            :style="{ transitionDelay: `${i * 40}ms` }"
          >
            <!-- Avatar -->
            <div class="relative shrink-0">
              <div class="w-10 h-10 rounded-full bg-gradient-to-br from-violet-500/20 to-indigo-500/20 border border-white/[0.08] flex items-center justify-center text-sm font-bold text-white overflow-hidden">
                <img
                  v-if="account.avatarFileId"
                  :src="`https://ru.cdn.argon.gl/${account.avatarFileId}`"
                  :alt="account.username"
                  class="w-full h-full object-cover"
                />
                <UserCircleIcon v-else :size="22" class="text-white/60" />
              </div>
              <!-- Active indicator -->
              <div
                v-if="account.isCurrent"
                class="absolute -bottom-0.5 -right-0.5 w-4 h-4 rounded-full bg-violet-500 border-2 border-black flex items-center justify-center"
              >
                <CheckIcon :size="8" class="text-white" stroke-width="3" />
              </div>
            </div>

            <!-- User info -->
            <div class="flex-1 text-left min-w-0">
              <p class="text-[13px] font-medium text-white/80 truncate group-hover:text-white transition-colors">{{ account.username }}</p>
              <p class="text-[11px] text-white/25">
                {{ account.isCurrent ? 'Currently signed in' : 'Tap to continue' }}
              </p>
            </div>

            <!-- Arrow -->
            <ChevronRightIcon :size="14" class="text-white/15 group-hover:text-white/40 group-hover:translate-x-0.5 transition-all shrink-0" />
          </button>
        </div>

        <!-- Add account -->
        <button
          @click="emit('addAccount')"
          :disabled="isLoading"
          class="w-full flex items-center justify-center gap-2 h-10 text-xs text-white/30 hover:text-white/60 hover:bg-white/[0.04] rounded-xl transition-all border border-dashed border-white/[0.06] hover:border-white/[0.12]"
        >
          <PlusIcon :size="14" />
          Use another account
        </button>

        <!-- Footer -->
        <p class="text-[10px] text-center text-white/15 pt-1">
          Argon will share your name and profile picture
        </p>
      </div>
    </Transition>
  </div>
</template>
