import { describe, expect, mock, test } from 'bun:test';
import { effectScope, ref } from 'vue';
import { useLoginStep } from '../src/composables/useLoginStep';

function form(scenario = 'EmailPassword') {
  const auth = {
    email: ref('member@example.com'), password: ref(''), isLoading: ref(false), authError: ref(''),
    getScenario: mock(async (_email: string) => scenario),
    onSubmit: mock(async () => {}), beginPasskeyLogin: mock(async (_email: string) => {}),
    userNotFound: () => 'Account not found',
  };
  const scope = effectScope();
  const step = scope.run(() => useLoginStep(auth))!;
  return { auth, ...step, stop: () => scope.stop() };
}

describe('Enter follows the login step', () => {
  test('repeated Enter on an unknown email never attempts sign-in', async () => {
    const f = form('');
    await f.handleFormSubmit();
    await f.handleFormSubmit();
    expect(f.auth.getScenario).toHaveBeenCalledTimes(2);
    expect(f.auth.onSubmit).not.toHaveBeenCalled();
    expect(f.step.value).toBe('email');
    expect(f.auth.authError.value).toBe('Account not found');
    f.stop();
  });
  test('Enter resolves the scenario, then requires a password before signing in', async () => {
    const f = form();
    await f.handleFormSubmit();
    expect(f.step.value).toBe('password');
    await f.handleFormSubmit();
    expect(f.auth.onSubmit).not.toHaveBeenCalled();
    f.auth.password.value = 'password';
    await f.handleFormSubmit();
    expect(f.auth.onSubmit).toHaveBeenCalledTimes(1);
    f.stop();
  });
  test('an in-flight lookup is shared and its answer cannot advance an edited email', async () => {
    const f = form();
    let resolve!: (s: string) => void;
    f.auth.getScenario.mockImplementation(() => new Promise((r) => { resolve = r; }));
    const pending = f.handleFormSubmit();
    await f.handleFormSubmit();
    expect(f.auth.getScenario).toHaveBeenCalledTimes(1);
    f.auth.email.value = 'other@example.com';
    resolve('EmailPassword');
    await pending;
    expect(f.step.value).toBe('email');
    expect(f.auth.onSubmit).not.toHaveBeenCalled();
    f.stop();
  });
  test('OTP and passkey accounts follow their resolved scenario', async () => {
    const otp = form('EmailOtp');
    await otp.handleFormSubmit();
    expect(otp.auth.onSubmit).toHaveBeenCalledTimes(1);
    const passkey = form('PasskeyOnly');
    await passkey.handleFormSubmit();
    expect(passkey.auth.beginPasskeyLogin).toHaveBeenCalledWith('member@example.com');
    expect(passkey.auth.onSubmit).not.toHaveBeenCalled();
    otp.stop(); passkey.stop();
  });
});
