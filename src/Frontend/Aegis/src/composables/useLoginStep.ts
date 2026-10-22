import { ref, watch, type Ref } from 'vue';

/** The Continue button and Enter must resolve the same step before any credentials are sent. */
export function useLoginStep(auth: {
  email: Ref<string>;
  password: Ref<string>;
  isLoading: Readonly<Ref<boolean>>;
  authError: Ref<string>;
  getScenario(email: string): Promise<string>;
  onSubmit(): Promise<void>;
  beginPasskeyLogin(email: string): Promise<unknown>;
  userNotFound(): string;
}) {
  const step = ref<'email' | 'password'>('email');
  const stepBusy = ref(false);

  watch(auth.email, () => {
    step.value = 'email';
    auth.authError.value = '';
  }, { flush: 'sync' });

  async function handleFormSubmit() {
    if (stepBusy.value || auth.isLoading.value) return;
    if (step.value === 'password') {
      if (auth.password.value) await auth.onSubmit();
      return;
    }
    const email = auth.email.value.trim();
    if (!email) return;
    auth.email.value = email;
    auth.authError.value = '';
    stepBusy.value = true;
    try {
      const scenario = await auth.getScenario(email);
      if (auth.email.value !== email) return;
      if (!scenario) {
        auth.authError.value = auth.userNotFound();
      } else if (scenario === 'EmailOtp') {
        await auth.onSubmit();
      } else if (scenario === 'PasskeyOnly' || scenario === 'PasskeyWithOtp') {
        await auth.beginPasskeyLogin(email);
      } else {
        step.value = 'password';
      }
    } finally {
      stepBusy.value = false;
    }
  }

  return { step, stepBusy, handleFormSubmit };
}
