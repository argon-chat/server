import { createApp } from "vue";
import "./style.css";
import "./assets/index.css";
import App from "./App.vue";
import { createPinia } from "pinia";
import router from "./router";
import { MotionPlugin } from "@vueuse/motion";

const pinia = createPinia();
const app = createApp(App);

app.use(router);
app.use(pinia);
app.use(MotionPlugin);
app.mount("#app");
