import { defineStore } from "pinia";
import { ref } from "vue";
import { useToast } from "@argon/ui/toast";
import { useUrlSearchParams } from "@vueuse/core";
import { startAuthentication } from "@/composables/useWebAuthn";
import { useLocale } from "@/store/localeStore";

const { toast } = useToast();

const FETCH_TIMEOUT_MS = 15_000;
const MAX_RETRIES = 2;
const RETRY_DELAY_MS = 1_000;

async function fetchWithTimeout(url: string, options: RequestInit, timeoutMs = FETCH_TIMEOUT_MS): Promise<Response> {
    const controller = new AbortController();
    const id = setTimeout(() => controller.abort(), timeoutMs);
    try {
        return await fetch(url, { ...options, signal: controller.signal });
    } finally {
        clearTimeout(id);
    }
}

async function fetchWithRetry(url: string, options: RequestInit, retries = MAX_RETRIES): Promise<Response> {
    for (let attempt = 0; attempt <= retries; attempt++) {
        try {
            const response = await fetchWithTimeout(url, options);

            // Don't retry on 429 — surface it immediately
            if (response.status === 429) {
                return response;
            }

            // Retry on 5xx server errors
            if (response.status >= 500 && attempt < retries) {
                await new Promise(r => setTimeout(r, RETRY_DELAY_MS * (attempt + 1)));
                continue;
            }

            return response;
        } catch (error) {
            if (attempt === retries) throw error;
            await new Promise(r => setTimeout(r, RETRY_DELAY_MS * (attempt + 1)));
        }
    }
    throw new Error("Max retries exceeded");
}

export const useSimpleAuthStore = defineStore("simpleAuth", () => {
    const { t } = useLocale();
    const isAuthenticated = ref(false);
    const isLoading = ref(false);
    const isCheckingSession = ref(false);
    const requiresOtp = ref(false);
    const requiresConsent = ref(false);
    const requiresAccountSelection = ref(false);
    const requiresOperatorAuth = ref(false);
    const consentInfo = ref<any>(null);
    const accounts = ref<any[]>([]);
    const errorMessage = ref<string | null>(null);
    const errorTitle = ref<string | null>(null);
    const passkeyNonce = ref<string | null>(null);
    const passkeyOtpRequired = ref(false);
    
    // Сохраняем OAuth параметры для использования после consent
    const savedOAuthParams = ref<string>("");

    const queryParams = useUrlSearchParams<{
        ref: string;
        client_id: string;
        code_challenge: string;
        code_challenge_method: string;
        redirect_uri: string;
        response_type: string;
        scope: string;
        prompt?: string;
        state?: string;
        nonce?: string;
        response_mode?: string;
    }>();
    
    // Сохраняем параметры при инициализации только если есть client_id
    if (typeof window !== "undefined" && queryParams.client_id) {
        savedOAuthParams.value = window.location.search;
    }

    async function login(email: string, password: string, otpCode?: string) {
        isLoading.value = true;

        try {
            const requestBody = {
                email,
                password,
                otpCode,
                clientId: queryParams.client_id,
                scope: queryParams.scope,
                codeChallenge: queryParams.code_challenge,
                codeChallengeMethod: queryParams.code_challenge_method,
                redirectUri: queryParams.redirect_uri,
            };
            
            const response = await fetchWithRetry("/api/auth/oauth/authorize", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                credentials: "include",
                body: JSON.stringify(requestBody),
            });

            if (response.status === 429) {
                toast({
                    title: "Too Many Attempts",
                    description: "Please wait a moment before trying again",
                    variant: "destructive",
                    duration: 5000,
                });
                return;
            }

            const data = await response.json();

            // Сначала проверяем requiresOtp - это не ошибка, а следующий шаг
            if (data.requiresOtp) {
                requiresOtp.value = true;
                toast({
                    title: "OTP Required",
                    description: "Check your email for the verification code",
                    duration: 3000,
                });
                return;
            }

            if (data.requiresConsent) {
                requiresConsent.value = true;
                consentInfo.value = data.consentInfo;
                return;
            }

            if (data.requiresOperatorAuth) {
                requiresOperatorAuth.value = true;
                return;
            }

            if (data.error) {
                if (data.error === "access_denied") {
                    errorTitle.value = "Access Denied";
                    errorMessage.value = data.error_description || "You don't have permission to access this application.";
                } else {
                    handleError(data.error);
                }
                return;
            }

            // Success - куки установлены автоматически
            if (data.success) {
                isAuthenticated.value = true;
                await completeOAuthFlow();
            }
        } catch (error) {
            const isTimeout = error instanceof DOMException && error.name === "AbortError";
            toast({
                title: isTimeout ? "Request Timeout" : "Login Failed",
                description: isTimeout
                    ? "The server took too long to respond. Please try again."
                    : "An error occurred during login",
                variant: "destructive",
                duration: 3000,
            });
        } finally {
            isLoading.value = false;
        }
    }

    async function approveConsent() {
        isLoading.value = true;
        try {
            const response = await fetchWithRetry("/api/auth/oauth/complete", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                credentials: "include",
            });

            const data = await response.json();

            if (data.success) {
                isAuthenticated.value = true;
                requiresConsent.value = false;
                consentInfo.value = null;

                await completeOAuthFlow();
            } else {
                toast({
                    title: "Authorization Failed",
                    description: data.error || "Failed to complete authorization. Please try again.",
                    variant: "destructive",
                    duration: 5000,
                });
            }
        } catch (error) {
            toast({
                title: "Error",
                description: "An error occurred while processing your request.",
                variant: "destructive",
                duration: 5000,
            });
        } finally {
            isLoading.value = false;
        }
    }

    function denyConsent() {
        requiresConsent.value = false;
        consentInfo.value = null;
        toast({
            title: "Authorization Cancelled",
            description: "You denied access to the application",
            duration: 3000,
        });
    }

    async function selectAccount(userId: string) {
        isLoading.value = true;
        try {
            const response = await fetchWithRetry("/api/auth/accounts/select", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                credentials: "include",
                body: JSON.stringify({ userId }),
            });

            const data = await response.json();

            if (data.success) {
                requiresAccountSelection.value = false;
                accounts.value = [];
                
                // Перепроверяем сессию для выбранного аккаунта
                await checkExistingSession();
            }
        } catch (error) {
            // Account selection failed silently
        } finally {
            isLoading.value = false;
        }
    }

    function addAnotherAccount() {
        requiresAccountSelection.value = false;
        accounts.value = [];
        // Показываем форму логина
    }

    async function switchAccountFromConsent() {
        requiresConsent.value = false;
        consentInfo.value = null;
        
        // Вызываем session check с prompt=select_account чтобы принудительно показать пикер
        isCheckingSession.value = true;
        try {
            const params = new URLSearchParams();
            if (queryParams.client_id) {
                params.append("clientId", queryParams.client_id);
            }
            params.append("prompt", "select_account");
            if (queryParams.scope) {
                params.append("scope", queryParams.scope);
            }

            const response = await fetchWithRetry(`/api/auth/session/check?${params.toString()}`, {
                method: "GET",
                credentials: "include",
            });

            const data = await response.json();
            if (data.requiresAccountSelection && data.accounts) {
                requiresAccountSelection.value = true;
                accounts.value = data.accounts;
            }
        } catch (error) {
            // Switch account failed silently
        } finally {
            isCheckingSession.value = false;
        }
    }

    async function completeOAuthFlow() {
        // Используем сохраненные параметры или текущие если сохраненных нет
        const oauthParams = savedOAuthParams.value || window.location.search;
        
        if (!oauthParams || !oauthParams.includes("client_id")) {
            toast({
                title: "Error",
                description: "OAuth parameters are missing. Please try again.",
                variant: "destructive",
                duration: 5000,
            });
            return;
        }
        
        // Parse query parameters and create form with hidden fields
        const urlParams = new URLSearchParams(oauthParams.startsWith('?') ? oauthParams.slice(1) : oauthParams);
        
        // Allowlist of valid OAuth parameter names to prevent parameter injection
        const allowedParams = new Set([
            'client_id', 'redirect_uri', 'response_type', 'scope', 'state',
            'code_challenge', 'code_challenge_method', 'nonce', 'response_mode', 'prompt'
        ]);

        // Create POST form with hidden fields for each allowed OAuth parameter
        const form = document.createElement("form");
        form.method = "POST";
        form.action = "/";
        
        for (const [key, value] of urlParams.entries()) {
            if (!allowedParams.has(key)) continue;
            const input = document.createElement("input");
            input.type = "hidden";
            input.name = key;
            input.value = value;
            form.appendChild(input);
        }

        document.body.appendChild(form);
        form.submit();
    }

    function handleError(error: string) {
        const errorMessages: Record<string, { title: string; description: string }> = {
            BAD_CREDENTIALS: {
                title: "Invalid Credentials",
                description: "The email or password you entered is incorrect",
            },
            BAD_OTP: {
                title: "Invalid OTP",
                description: "The verification code you entered is incorrect",
            },
            REQUIRED_OTP: {
                title: "OTP Required",
                description: "Please check your email for the verification code",
            },
        };

        const message = errorMessages[error] || {
            title: "Error",
            description: "An unexpected error occurred",
        };

        toast({
            title: message.title,
            description: message.description,
            variant: "destructive",
            duration: 3000,
        });
    }

    async function getScenario(email: string): Promise<string> {
        isLoading.value = true;
        try {
            const response = await fetchWithRetry("/api/auth/scenario", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ email }),
            });

            if (response.status === 429) {
                toast({
                    title: "Too Many Attempts",
                    description: "Please wait a moment before trying again",
                    variant: "destructive",
                    duration: 5000,
                });
                return "";
            }

            const data = await response.json();
            return data.scenario || "";
        } catch {
            return "";
        } finally {
            isLoading.value = false;
        }
    }

    async function checkExistingSession(): Promise<boolean> {
        isCheckingSession.value = true;
        try {
            const params = new URLSearchParams();
            if (queryParams.client_id) {
                params.append("clientId", queryParams.client_id);
            }
            if (queryParams.prompt) {
                params.append("prompt", queryParams.prompt);
            }
            if (queryParams.scope) {
                params.append("scope", queryParams.scope);
            }

            const url = params.toString() 
                ? `/api/auth/session/check?${params.toString()}`
                : "/api/auth/session/check";
                
            const response = await fetchWithRetry(url, {
                method: "GET",
                credentials: "include",
            });

            const data = await response.json();
            
            if (!data.hasSession) {
                return false;
            }

            // Сессия есть

            // Проверяем нужен ли выбор аккаунта
            if (data.requiresAccountSelection && data.accounts) {
                isAuthenticated.value = true;
                requiresAccountSelection.value = true;
                accounts.value = data.accounts;
                return true;
            }

            // Проверяем access denied
            if (data.accessDenied) {
                toast({
                    title: "Access Denied",
                    description: data.denialReason || "You don't have access to this application",
                    variant: "destructive",
                    duration: 5000,
                });
                return false;
            }

            // Проверяем нужен ли consent
            if (data.requiresConsent && data.consentInfo) {
                isAuthenticated.value = true;
                requiresConsent.value = true;
                consentInfo.value = data.consentInfo;
                return true;
            }

            // Проверяем нужна ли авторизация через сертификат оператора
            if (data.requiresOperatorAuth) {
                isAuthenticated.value = true;
                requiresOperatorAuth.value = true;
                return true;
            }

            // Все ок, редиректим
            isAuthenticated.value = true;
            await completeOAuthFlow();
            return true;
        } catch (error) {
            return false;
        } finally {
            isCheckingSession.value = false;
        }
    }

    function clearError() {
        errorMessage.value = null;
        errorTitle.value = null;
    }

    async function verifyCertificate() {
        isLoading.value = true;
        try {
            // Hardcoded mTLS subdomain — never sourced from user-controllable globals
            const mtlsBaseUrl = window.location.origin.replace(/^(https?:\/\/)/, '$1mtls.');
            const verifyUrl = mtlsBaseUrl + '/api/auth/operator/verify';
            const response = await fetch(verifyUrl, {
                method: 'POST',
                credentials: 'include',
            });

            const result = await response.json();

            if (response.ok && result.success) {
                requiresOperatorAuth.value = false;
                toast({
                    title: t('operator_verified'),
                    description: t('operator_cert_accepted'),
                    duration: 3000,
                });
                await checkExistingSession();
            } else {
                toast({
                    title: t('operator_cert_failed'),
                    description: result.error_description || result.error || t('operator_cert_failed_desc'),
                    variant: "destructive",
                    duration: 5000,
                });
            }
        } catch (error) {
            toast({
                title: t('operator_verify_error'),
                description: t('operator_verify_error_desc'),
                variant: "destructive",
                duration: 5000,
            });
        } finally {
            isLoading.value = false;
        }
    }

    async function beginPasskeyLogin(email?: string) {
        isLoading.value = true;
        try {
            const response = await fetchWithRetry("/api/auth/passkey/begin", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                credentials: "include",
                body: JSON.stringify({ email: email || null }),
            });

            if (response.status === 429) {
                toast({ title: t('passkey_too_many_attempts'), description: t('passkey_too_many_attempts_desc'), variant: "destructive", duration: 5000 });
                return false;
            }

            const data = await response.json();
            if (data.error) {
                handlePasskeyError(data.error);
                return false;
            }

            if (!data.success || !data.optionsJson) {
                toast({ title: t('passkey_error'), description: t('passkey_start_failed'), variant: "destructive", duration: 3000 });
                return false;
            }

            // Parse the response: { nonce, options }
            const parsed = JSON.parse(data.optionsJson);
            const challengeNonce = parsed.nonce;
            const optionsJsonStr = JSON.stringify(parsed.options);

            // Call WebAuthn API
            const assertionJson = await startAuthentication(optionsJsonStr);

            // Complete the passkey login
            return await completePasskeyLogin(challengeNonce, assertionJson);
        } catch (error) {
            if (error instanceof DOMException && error.name === "NotAllowedError") {
                toast({ title: t('passkey_cancelled'), description: t('passkey_cancelled_desc'), duration: 3000 });
            } else {
                toast({ title: t('passkey_error'), description: t('passkey_generic_error'), variant: "destructive", duration: 3000 });
            }
            return false;
        } finally {
            isLoading.value = false;
        }
    }

    async function completePasskeyLogin(nonce: string, assertionResponseJson: string) {
        try {
            const response = await fetchWithRetry("/api/auth/passkey/complete", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                credentials: "include",
                body: JSON.stringify({
                    nonce,
                    assertionResponseJson,
                    clientId: queryParams.client_id,
                    scope: queryParams.scope,
                }),
            });

            const data = await response.json();

            if (data.error) {
                handlePasskeyError(data.error);
                return false;
            }

            if (data.requiresOtp) {
                passkeyOtpRequired.value = true;
                passkeyNonce.value = data.passkeyNonce;
                toast({ title: t('passkey_otp_required'), description: t('passkey_otp_required_desc'), duration: 3000 });
                return true;
            }

            if (data.requiresConsent) {
                isAuthenticated.value = true;
                requiresConsent.value = true;
                consentInfo.value = data.consentInfo;
                return true;
            }

            if (data.success) {
                isAuthenticated.value = true;
                await completeOAuthFlow();
                return true;
            }

            return false;
        } catch {
            toast({ title: t('passkey_error'), description: t('passkey_complete_failed'), variant: "destructive", duration: 3000 });
            return false;
        }
    }

    async function confirmPasskeyOtp(otpCode: string) {
        isLoading.value = true;
        try {
            const response = await fetchWithRetry("/api/auth/passkey/confirm-otp", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                credentials: "include",
                body: JSON.stringify({
                    passkeyNonce: passkeyNonce.value,
                    otpCode,
                    clientId: queryParams.client_id,
                    scope: queryParams.scope,
                }),
            });

            const data = await response.json();

            if (data.error) {
                handlePasskeyError(data.error);
                return;
            }

            passkeyOtpRequired.value = false;
            passkeyNonce.value = null;

            if (data.requiresConsent) {
                isAuthenticated.value = true;
                requiresConsent.value = true;
                consentInfo.value = data.consentInfo;
                return;
            }

            if (data.success) {
                isAuthenticated.value = true;
                await completeOAuthFlow();
            }
        } catch {
            toast({ title: t('passkey_error'), description: t('passkey_otp_failed'), variant: "destructive", duration: 3000 });
        } finally {
            isLoading.value = false;
        }
    }

    function handlePasskeyError(error: string) {
        const errorMessages: Record<string, { title: string; description: string }> = {
            NO_PASSKEYS: { title: t('passkey_error_no_passkeys_title'), description: t('passkey_error_no_passkeys') },
            USER_NOT_FOUND: { title: t('passkey_error_user_not_found_title'), description: t('passkey_error_user_not_found') },
            INVALID_ASSERTION: { title: t('passkey_error_invalid_assertion_title'), description: t('passkey_error_invalid_assertion') },
            VERIFICATION_FAILED: { title: t('passkey_error_verification_failed_title'), description: t('passkey_error_verification_failed') },
            CHALLENGE_EXPIRED: { title: t('passkey_error_challenge_expired_title'), description: t('passkey_error_challenge_expired') },
            BAD_OTP: { title: t('passkey_error_bad_otp_title'), description: t('passkey_error_bad_otp') },
            NONCE_EXPIRED: { title: t('passkey_error_nonce_expired_title'), description: t('passkey_error_nonce_expired') },
        };
        const msg = errorMessages[error] || { title: "Error", description: "An unexpected error occurred" };
        toast({ title: msg.title, description: msg.description, variant: "destructive", duration: 3000 });
    }

    return {
        isAuthenticated,
        isLoading,
        isCheckingSession,
        requiresOtp,
        requiresConsent,
        requiresAccountSelection,
        requiresOperatorAuth,
        consentInfo,
        accounts,
        errorMessage,
        errorTitle,
        passkeyNonce,
        passkeyOtpRequired,
        login,
        approveConsent,
        denyConsent,
        selectAccount,
        addAnotherAccount,
        switchAccountFromConsent,
        getScenario,
        checkExistingSession,
        verifyCertificate,
        clearError,
        beginPasskeyLogin,
        confirmPasskeyOtp,
    };
});
