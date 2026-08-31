import { createApp } from "vue";
import "@argon/assets/styles";
import App from "./App.vue";
import { createPinia } from "pinia";
import router from "./router";
import { MotionPlugin } from "@vueuse/motion";
import { createI18n } from "vue-i18n";
import { coreMessages, type SupportedLocale, type CoreLocaleSchema } from "@argon/i18n";

export const i18n = createI18n<[CoreLocaleSchema], SupportedLocale>({
  legacy: false,
  locale: "en",
  fallbackLocale: "en",
  messages: coreMessages as any,
  silentTranslationWarn: true,
  missingWarn: false,
  fallbackWarn: false,
  warnHtmlMessage: false,
});
const pinia = createPinia();
const app = createApp(App);

app.use(i18n);
app.use(router);
app.use(pinia);
app.use(MotionPlugin);
app.mount("#app");
