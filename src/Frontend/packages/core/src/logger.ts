// Upstream this is consola. What actually gets called across both front-ends is six levels of
// "write this somewhere", which is not worth 6 kB of log framework on pages this small.
const logger = {
  debug:   (...args: unknown[]) => console.debug(...args),
  info:    (...args: unknown[]) => console.info(...args),
  success: (...args: unknown[]) => console.info(...args),
  warn:    (...args: unknown[]) => console.warn(...args),
  error:   (...args: unknown[]) => console.error(...args),
  fatal:   (...args: unknown[]) => console.error(...args),
};

export { logger };
