<script setup lang="ts">
import { Button } from "@argon/ui/button";
import { Card, CardContent, CardFooter, CardHeader, CardTitle, CardDescription } from "@argon/ui/card";
import { Input } from "@argon/ui/input";
import { Label } from "@argon/ui/label";
import { Checkbox } from "@argon/ui/checkbox";
import { onMounted, ref, computed } from "vue";
import { useRoute } from "vue-router";
import { useRegisterStore } from "@/store/registerStore";
import InputWithError from "../InputWithError.vue";
import IconSw from "@argon/assets/icons/icon_cat.svg";
import { useLocale } from "@/store/localeStore";

const { t } = useLocale();
const route = useRoute();
const store = useRegisterStore();

const username = ref("");
const password = ref("");
const confirmPassword = ref("");
const displayName = ref("");
const birthDate = ref("");
const tosAgreement = ref(false);
const localErrors = ref<Record<string, string>>({});

onMounted(() => {
  const token = route.query.token as string;
  if (token) {
    store.validateToken(token);
  }
});

const passwordsMatch = computed(() => password.value === confirmPassword.value);

function validate(): boolean {
  localErrors.value = {};

  if (!/^[a-zA-Z0-9_]{4,32}$/.test(username.value)) {
    localErrors.value.username = t("register_error_invalid_username");
    return false;
  }
  if (!password.value || password.value.length < 8) {
    localErrors.value.password = t("register_error_invalid_password");
    return false;
  }
  if (!passwordsMatch.value) {
    localErrors.value.confirmPassword = t("passwords_do_not_match");
    return false;
  }
  if (!displayName.value) {
    localErrors.value.displayName = t("register_error_invalid_displayName");
    return false;
  }
  if (!birthDate.value) {
    localErrors.value.birthDate = t("dob_required");
    return false;
  }
  if (!tosAgreement.value) {
    localErrors.value.tos = t("register_error_invalid_argreeTos");
    return false;
  }
  return true;
}

async function handleSubmit(e?: Event) {
  e?.preventDefault();
  if (!validate()) return;
  await store.register(username.value, password.value, displayName.value, birthDate.value, tosAgreement.value);
}
</script>

<template>
  <div class="container relative h-screen flex flex-col items-center justify-center max-w-none px-0">
    <div class="relative h-full w-full flex flex-col p-4 sm:p-10 text-white dark:border-r">
      <div class="z-20 flex items-center text-lg font-medium justify-center sm:justify-start sm:absolute sm:top-10 sm:left-10 mb-4 sm:mb-0">
        <IconSw class="w-10 h-10 sm:w-12 sm:h-12 pr-2 fill-blue-500" />
        <span class="text-base sm:text-lg">Argon Chat</span>
      </div>

      <!-- Loading state -->
      <div v-if="store.isLoading && !store.isTokenChecked" class="flex items-center justify-center flex-1">
        <div class="text-white text-lg">{{ t("invite_validating") }}</div>
      </div>

      <!-- Invalid/expired token -->
      <div v-else-if="store.isTokenChecked && !store.isTokenValid" class="flex items-center justify-center flex-1">
        <Card class="rounded-2xl border border-white/10 bg-black/40 backdrop-blur-xl shadow-2xl w-full max-w-[420px]">
          <CardHeader class="text-center space-y-1">
            <CardTitle class="text-2xl font-bold text-white">{{ t("invite_expired") }}</CardTitle>
            <CardDescription class="text-gray-400">
              {{ store.errorMessage || t("invite_invalid") }}
            </CardDescription>
          </CardHeader>
        </Card>
      </div>

      <!-- Registration success -->
      <div v-else-if="store.isRegistered" class="flex items-center justify-center flex-1">
        <Card class="rounded-2xl border border-white/10 bg-black/40 backdrop-blur-xl shadow-2xl w-full max-w-[420px]">
          <CardHeader class="text-center space-y-1">
            <CardTitle class="text-2xl font-bold text-white">{{ t("account_created") }}</CardTitle>
            <CardDescription class="text-gray-400">{{ t("redirecting_to_app") }}</CardDescription>
          </CardHeader>
        </Card>
      </div>

      <!-- Registration form -->
      <div v-else-if="store.isTokenValid" class="flex items-center justify-center flex-1">
        <Card class="rounded-2xl border border-white/10 bg-black/40 backdrop-blur-xl shadow-2xl w-full max-w-[420px]">
          <form @submit.prevent="handleSubmit" class="p-4 sm:p-6 flex flex-col">
            <CardHeader class="text-center space-y-1">
              <CardTitle class="text-2xl font-bold text-white">{{ t("create_account") }}</CardTitle>
              <CardDescription class="text-gray-400">
                {{ t("invite_join", { app: store.appName }) }}
              </CardDescription>
            </CardHeader>

            <CardContent class="space-y-4 pt-4">
              <!-- Frozen email -->
              <div class="space-y-1">
                <Label for="email" class="text-gray-200">{{ t("email") }}</Label>
                <Input
                  id="email"
                  :model-value="store.frozenEmail"
                  type="email"
                  disabled
                  class="h-11 rounded-xl bg-black/30 border-gray-700 text-gray-400 cursor-not-allowed"
                />
                <p class="text-xs text-gray-500">{{ t("invite_email_locked") }}</p>
              </div>

              <!-- Username -->
              <div class="space-y-1">
                <InputWithError
                  v-model="username"
                  :error="localErrors.username || store.fieldErrors.username || ''"
                  @clear-error="delete localErrors.username; delete store.fieldErrors.username"
                  type="text"
                  :placeholder="t('username_placeholder')"
                  :disabled="store.isLoading"
                  id="username"
                >
                  <template #label>
                    <Label for="username" class="text-gray-200">{{ t("username") }}</Label>
                  </template>
                </InputWithError>
              </div>

              <!-- Display Name -->
              <div class="space-y-1">
                <InputWithError
                  v-model="displayName"
                  :error="localErrors.displayName || ''"
                  @clear-error="delete localErrors.displayName"
                  type="text"
                  :placeholder="t('display_name_placeholder')"
                  :disabled="store.isLoading"
                  id="displayName"
                >
                  <template #label>
                    <Label for="displayName" class="text-gray-200">{{ t("display_name") }}</Label>
                  </template>
                </InputWithError>
              </div>

              <!-- Password -->
              <div class="space-y-1">
                <InputWithError
                  v-model="password"
                  :error="localErrors.password || store.fieldErrors.password || ''"
                  @clear-error="delete localErrors.password; delete store.fieldErrors.password"
                  type="password"
                  placeholder="••••••••"
                  :disabled="store.isLoading"
                  id="password"
                >
                  <template #label>
                    <Label for="password" class="text-gray-200">{{ t("password") }}</Label>
                  </template>
                </InputWithError>
              </div>

              <!-- Confirm Password -->
              <div class="space-y-1">
                <InputWithError
                  v-model="confirmPassword"
                  :error="localErrors.confirmPassword || ''"
                  @clear-error="delete localErrors.confirmPassword"
                  type="password"
                  placeholder="••••••••"
                  :disabled="store.isLoading"
                  id="confirmPassword"
                >
                  <template #label>
                    <Label for="confirmPassword" class="text-gray-200">{{ t("confirm_password") }}</Label>
                  </template>
                </InputWithError>
              </div>

              <!-- Birth Date -->
              <div class="space-y-1">
                <Label for="birthDate" class="text-gray-200">{{ t("dob") }}</Label>
                <Input
                  id="birthDate"
                  v-model="birthDate"
                  type="date"
                  class="h-11 rounded-xl bg-black/50 border-gray-700 text-white focus:border-blue-500 focus:ring focus:ring-blue-500/30"
                  :disabled="store.isLoading"
                />
                <p v-if="localErrors.birthDate" class="text-xs text-red-400">{{ localErrors.birthDate }}</p>
              </div>

              <!-- ToS -->
              <div class="flex items-start space-x-2 pt-2">
                <Checkbox
                  id="tos"
                  :checked="tosAgreement"
                  @update:checked="tosAgreement = $event"
                  :disabled="store.isLoading"
                />
                <Label for="tos" class="text-sm text-gray-300 leading-tight cursor-pointer">
                  {{ t("accept") }} <a href="https://argon.gl/tos" target="_blank" class="text-blue-400 hover:text-blue-300 underline">{{ t("terms") }}</a>
                  {{ t("and") }} <a href="https://argon.gl/privacy" target="_blank" class="text-blue-400 hover:text-blue-300 underline">{{ t("privacy_policy") }}</a>
                </Label>
              </div>
              <p v-if="localErrors.tos" class="text-xs text-red-400">{{ localErrors.tos }}</p>

              <!-- Server error -->
              <p v-if="store.errorMessage" class="text-sm text-red-400 text-center">{{ store.errorMessage }}</p>
            </CardContent>

            <CardFooter class="flex flex-col space-y-2 pt-2">
              <Button
                type="submit"
                :disabled="store.isLoading || !tosAgreement"
                class="w-full hover:opacity-90 transition"
              >
                {{ store.isLoading ? t("creating_account") : t("create_account") }}
              </Button>
            </CardFooter>
          </form>
        </Card>
      </div>
    </div>
  </div>
</template>
