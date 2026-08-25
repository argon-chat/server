<script setup lang="ts">
import { computed } from "vue";
import { store } from "./store";
import SignIn from "./components/SignIn.vue";
import Wizard from "./components/Wizard.vue";
import Working from "./components/Working.vue";
import Degraded from "./components/Degraded.vue";
import Blocked from "./components/Blocked.vue";
import Panel from "./components/Panel.vue";
import Finished from "./components/Finished.vue";

/**
 * Which screen, and nothing else.
 *
 * Every branch is on the stage the server reported. There is no client-side route and no step counter:
 * an operator who closes the tab mid-install and comes back an hour later from another machine lands on
 * the same screen, because the screen was never a fact about this browser.
 *
 * The failures get screens of their own rather than a banner on the wizard, and that is the whole point
 * of having four of them. They differ by what to do next, not by wording — and the difference that
 * matters most is whether anything is running. An earlier version fell through to the wizard for every
 * unhandled stage, which put an Install button under a machine with live containers on it.
 */

type Screen = "signin" | "wizard" | "working" | "degraded" | "blocked" | "panel" | "finished";

const screen = computed<Screen>(() => {
    if (!store.authed) return "signin";

    const stage = store.state?.stage;

    if (stage === undefined || stage === "unavailable") return "blocked";

    if (stage === "applying" || stage === "configured" || stage === "starting") return "working";
    if (stage === "degraded") return "degraded";
    if (stage === "blocked") return "blocked";

    // Up, and therefore no longer a wizard. `Finished` shows for the moment the overview takes to
    // arrive — the install's last frame is not a screen anybody should be left on.
    if (stage === "running") return store.overview === undefined ? "finished" : "panel";

    return "wizard";
});
</script>

<template>
  <SignIn v-if="screen === 'signin'" />
  <Wizard v-else-if="screen === 'wizard'" />
  <Working v-else-if="screen === 'working'" />
  <Degraded v-else-if="screen === 'degraded'" />
  <Blocked v-else-if="screen === 'blocked'" />
  <Finished v-else-if="screen === 'finished'" />
  <Panel v-else />
</template>
