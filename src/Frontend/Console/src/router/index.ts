import { createRouter, createWebHistory } from "vue-router";
import MasterPage from "../views/MasterPage.vue";
import AppsManage from "../views/AppsManage.vue";
import BotDetailsPage from "@/components/BotDetailsPage.vue";
import OAuthCallback from "@/views/OAuthCallback.vue";
const routes = [
    {
        path: "/",
        name: "MasterPage",
        component: MasterPage,
    },
    {
        path: "/apps",
        name: "AppsManage",
        component: AppsManage,
    },
    {
        path: "/teams/:teamId/apps/:appId",
        name: "AppDetails",
        component: BotDetailsPage,
        props: true,
    },
    {
        path: "/callback",
        name: "OAuthCallback",
        component: OAuthCallback,
    },
];

const router = createRouter({
    history: createWebHistory(),
    routes,

    scrollBehavior() {
        return { top: 0 };
    },
});

router.beforeEach((to, from, next) => {
    next();
});

export default router;
