import { createApp } from "vue";
import App from "./App.vue";
import "./argon-ds.css";
import { refresh } from "./store";

/**
 * What the browser runs.
 *
 * The stylesheet is imported rather than linked so that Vite emits it as `app.css` beside `app.js` —
 * two names, which is what `server.ts` serves by. See `vite.config.ts` for why the names are fixed.
 *
 * The first state is fetched before mounting so the page does not paint a sign-in form and then replace
 * it a tick later for an operator who already has a session. A 401 here is the ordinary case — nobody
 * has signed in yet — and it renders the code form rather than an error.
 */
await refresh().catch(() => undefined);

createApp(App).mount("#app");
