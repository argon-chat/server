import { ref, computed, watch } from "vue";
import { useSimpleAuthStore } from "@/store/simpleAuthStore";

export type TabType = "login" | "otp-code";

export function useSimpleAuthForm() {
    const authStore = useSimpleAuthStore();

    const tabValue = ref<TabType>("login");
    const email = ref("");
    const password = ref("");
    const otpCode = ref("");
    const authError = ref("");

    const isLoading = computed(() => authStore.isLoading);

    // When passkey OTP is required, switch to OTP tab
    watch(() => authStore.passkeyOtpRequired, (val) => {
        if (val) tabValue.value = "otp-code";
    });

    async function onSubmit() {
        authError.value = "";

        if (tabValue.value === "otp-code") {
            if (authStore.passkeyOtpRequired) {
                // Passkey + OTP flow
                await authStore.confirmPasskeyOtp(otpCode.value);
            } else {
                // Regular OTP flow
                await authStore.login(email.value, password.value, otpCode.value);
            }
            if (authStore.isAuthenticated) {
                window.location.reload();
            }
        } else {
            await authStore.login(email.value, password.value);
            if (authStore.requiresOtp) {
                tabValue.value = "otp-code";
            } else if (authStore.isAuthenticated) {
                window.location.reload();
            }
        }
    }

    const goBackToLogin = () => {
        tabValue.value = "login";
        otpCode.value = "";
        authStore.passkeyOtpRequired = false;
        authStore.passkeyNonce = null;
    };

    return {
        authStore,
        tabValue,
        isLoading,
        email,
        password,
        otpCode,
        onSubmit,
        goBackToLogin,
        authError,
    };
}
