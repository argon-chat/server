// Upstream this is consola. Two call sites here use it — one info, one error — which is not worth
// 6 kB of log framework on a page whose whole job is a sign-in form.
const logger = {
  info: (...args: unknown[]) => console.info(...args),
  warn: (...args: unknown[]) => console.warn(...args),
  error: (...args: unknown[]) => console.error(...args),
  debug: (...args: unknown[]) => console.debug(...args),
};

export { logger };
