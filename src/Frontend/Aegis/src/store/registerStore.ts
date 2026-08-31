import { defineStore } from "pinia";
import { ref } from "vue";
import { useToast } from "@argon/ui/toast";

const { toast } = useToast();

const FETCH_TIMEOUT_MS = 15_000;

async function fetchWithTimeout(url: string, options: RequestInit, timeoutMs = FETCH_TIMEOUT_MS): Promise<Response> {
    const controller = new AbortController();
    const id = setTimeout(() => controller.abort(), timeoutMs);
    try {
        return await fetch(url, { ...options, signal: controller.signal });
    } finally {
        clearTimeout(id);
    }
}

export const useRegisterStore = defineStore("register", () => {
    const isLoading = ref(false);
    const inviteToken = ref("");
    const frozenEmail = ref("");
    const appName = ref("");
    const appAvatarFileId = ref<string | null>(null);
    const isTokenValid = ref(false);
    const isTokenChecked = ref(false);
    const errorMessage = ref<string | null>(null);
    const fieldErrors = ref<Record<string, string>>({});
    const isRegistered = ref(false);
    const redirectUrl = ref<string | null>(null);

    async function validateToken(token: string) {
        inviteToken.value = token;
        isLoading.value = true;
        isTokenChecked.value = false;
        try {
            const response = await fetchWithTimeout(`/api/auth/invite/info?token=${encodeURIComponent(token)}`, {
                method: "GET",
            });

            if (!response.ok) {
                const data = await response.json().catch(() => ({}));
                errorMessage.value = data.error_description || "This invitation link has expired or is invalid.";
                isTokenValid.value = false;
                return;
            }

            const data = await response.json();
            frozenEmail.value = data.email;
            appName.value = data.appName;
            appAvatarFileId.value = data.appAvatarFileId;
            isTokenValid.value = true;
        } catch (error) {
            errorMessage.value = "Failed to validate invitation. Please try again.";
            isTokenValid.value = false;
        } finally {
            isLoading.value = false;
            isTokenChecked.value = true;
        }
    }

    async function register(username: string, password: string, displayName: string, birthDate: string, tosAgreement: boolean) {
        isLoading.value = true;
        errorMessage.value = null;
        fieldErrors.value = {};

        try {
            const response = await fetchWithTimeout("/api/auth/invite/register", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                credentials: "include",
                body: JSON.stringify({
                    token: inviteToken.value,
                    username,
                    password,
                    displayName,
                    birthDate,
                    tosAgreement,
                }),
            });

            const data = await response.json();

            if (!response.ok) {
                if (data.field) {
                    fieldErrors.value[data.field] = data.message || "Invalid value";
                }
                errorMessage.value = data.message || data.error_description || "Registration failed.";
                return;
            }

            if (data.success && data.redirectUrl) {
                isRegistered.value = true;
                redirectUrl.value = data.redirectUrl;
                toast({
                    title: "Account Created",
                    description: "Redirecting you to the application...",
                    duration: 3000,
                });

                // Build the POST form for OAuth redirect (same pattern as simpleAuthStore)
                const urlParams = new URLSearchParams(data.redirectUrl.split("?")[1] || "");
                const form = document.createElement("form");
                form.method = "POST";
                form.action = "/";
                for (const [key, value] of urlParams.entries()) {
                    const input = document.createElement("input");
                    input.type = "hidden";
                    input.name = key;
                    input.value = value;
                    form.appendChild(input);
                }
                document.body.appendChild(form);
                form.submit();
            }
        } catch (error) {
            const isTimeout = error instanceof DOMException && error.name === "AbortError";
            errorMessage.value = isTimeout
                ? "The server took too long to respond. Please try again."
                : "An error occurred during registration.";
        } finally {
            isLoading.value = false;
        }
    }

    return {
        isLoading,
        inviteToken,
        frozenEmail,
        appName,
        appAvatarFileId,
        isTokenValid,
        isTokenChecked,
        errorMessage,
        fieldErrors,
        isRegistered,
        redirectUrl,
        validateToken,
        register,
    };
});
