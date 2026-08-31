import { createRouter, createWebHashHistory, RouteLocationNormalized, NavigationGuardNext } from "vue-router";
import LoginPage from "../views/AuthPage.vue";
const routes = [
    {
        path: "/",
        name: "Login",
        component: LoginPage,
    },
    {
        path: "/register",
        name: "Register",
        component: () => import("../views/RegisterPage.vue"),
    },
    {
        path: "/apply",
        redirect: (to: any) => {
            const nonce = String(to.query.nonce ?? "");
            return `/security/apply?nonce=${encodeURIComponent(nonce)}`;
        },
    },
];

const router = createRouter({
    history: createWebHashHistory(),
    routes,
});
export default router;
