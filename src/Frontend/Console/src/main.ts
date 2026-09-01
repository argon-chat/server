import { createApp } from "vue";
import "./style.css";
import "./assets/index.css";
import App from "./App.vue";
import { createPinia } from "pinia";
import router from "./router";
import { MotionPlugin } from "@vueuse/motion";
import { createI18n } from "vue-i18n";
import { locales, type Locale, type LocaleSchema } from "@/locales";

export const i18n = createI18n<[LocaleSchema], Locale>({
  legacy: false,
  locale: "en",
  fallbackLocale: "en",
  messages: locales as any,
});
const pinia = createPinia();
const app = createApp(App);

app.use(i18n);
app.use(router);
app.use(pinia);
app.use(MotionPlugin);
app.mount("#app");
