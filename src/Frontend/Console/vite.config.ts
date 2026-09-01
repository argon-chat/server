import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import path from "node:path";
import tailwindcss from "@tailwindcss/vite";
import vueDevTools from "vite-plugin-vue-devtools";
import SvgImporter from "vite-svg-loader";
import { devServerHttps, precompress, versionTag } from "@argon/vite-preset";

export default defineConfig({
  server: {
    // Not 5005: that is the sign-in widget's, and the two are routinely run side by side.
    port: 5006,
    https: devServerHttps(__dirname),
  },
  plugins: [
    tailwindcss(),
    vue(),
    vueDevTools(),
    SvgImporter(),
    precompress(),
    versionTag(),
  ],
  // One favicon, one copy of it. Neither app has any other public asset, and a file in public/
  // is emitted verbatim — there is no import for a workspace link to follow.
  publicDir: path.resolve(__dirname, "../public"),
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  define: {
    __VUE_PROD_DEVTOOLS__: false,
    // The console is written entirely with the Composition API, as is everything it pulls in.
    __VUE_OPTIONS_API__: false,
  },
  worker: {
    format: "es",
  },
});
