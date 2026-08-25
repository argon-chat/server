import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

/**
 * The setup panel's build.
 *
 * Three settings here are load-bearing rather than taste, and each one is a failure that only appears
 * after the instance is up — which is the worst time for any of them to appear.
 */
export default defineConfig({
    plugins: [vue()],

    /**
     * Relative, not absolute.
     *
     * The page is served at `/` during setup and at `/panel/` afterwards, with the prefix stripped
     * before this container sees it. Vite's default emits `/assets/…`, which resolves against the
     * origin and therefore reaches Argon rather than the panel the moment `/` stops being the wizard.
     * The trailing slash on `/panel/` is guaranteed by the edge — see PANEL_MIDDLEWARES in compose.ts.
     */
    base: "./",

    build: {
        outDir: "dist",
        emptyOutDir: true,

        // No source maps in the image. They would be the panel's own source served to anyone who can
        // reach it, and the only thing reading them would be somebody looking for a way in.
        sourcemap: false,

        rollupOptions: {
            output: {
                /**
                 * Fixed names, no content hash.
                 *
                 * `server.ts` serves files by name — three routes, one per file — rather than serving a
                 * directory, because that container holds the docker socket and a route that can be
                 * talked into reading `../../etc/passwd` costs the whole machine. Hashed names would
                 * force a directory server; fixed names keep the enumeration.
                 *
                 * What this gives up is cache-busting across upgrades. The panel is one page loaded by
                 * one person occasionally, and `Cache-Control` on those three routes is the answer to
                 * that rather than a filename.
                 */
                entryFileNames: "app.js",
                chunkFileNames: "app.js",
                assetFileNames: "app.[ext]",
            },
        },
    },
});
