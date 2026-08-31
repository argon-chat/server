<script setup lang="ts">
import { useSimpleAuthForm } from "@/composables/useSimpleAuthForm";
import { useSimpleAuthStore } from "@/store/simpleAuthStore";

import LoginForm from "./LoginForm.vue";
import OtpForm from "./OtpForm.vue";
import ConsentScreen from "./ConsentScreen.vue";
import AccountPicker from "./AccountPicker.vue";
import ErrorScreen from "./ErrorScreen.vue";
import CertificateAuthScreen from "./CertificateAuthScreen.vue";
import { computed } from "vue";

const auth = useSimpleAuthForm();
const authStore = useSimpleAuthStore();

const tabValueForTabs = computed({
  get: () => auth.tabValue.value,
  set: (val: string) => { auth.tabValue.value = val as any }
});
</script>

<template>
  <div class="mx-auto flex w-full flex-col justify-center space-y-6" aria-live="polite">
    <ErrorScreen
      v-if="authStore.errorMessage"
      :title="authStore.errorTitle || 'Error'"
      :message="authStore.errorMessage"
      @dismiss="authStore.clearError"
    />
    <AccountPicker
      v-else-if="authStore.requiresAccountSelection && authStore.accounts.length > 0"
      :accounts="authStore.accounts"
      :is-loading="authStore.isLoading"
      @select="authStore.selectAccount"
      @add-account="authStore.addAnotherAccount"
    />
    <ConsentScreen 
      v-else-if="authStore.requiresConsent && authStore.consentInfo"
      :consent-info="authStore.consentInfo"
      :is-loading="auth.isLoading.value"
      @approve="authStore.approveConsent"
      @deny="authStore.denyConsent"
      @switch-account="authStore.switchAccountFromConsent"
    />
    <CertificateAuthScreen
      v-else-if="authStore.requiresOperatorAuth"
      :is-loading="authStore.isLoading"
      @verify="authStore.verifyCertificate"
      @cancel="authStore.requiresOperatorAuth = false"
    />
    <LoginForm v-else-if="tabValueForTabs == 'login'" :auth="auth" />
    <OtpForm v-else-if="tabValueForTabs == 'otp-code'" :auth="auth" />
    <div v-else>error {{ tabValueForTabs }}</div>
  </div>
</template>
