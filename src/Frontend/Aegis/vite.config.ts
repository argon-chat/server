import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import path from "node:path";
import tailwind from "tailwindcss";
import autoprefixer from "autoprefixer";
import vueDevTools from "vite-plugin-vue-devtools";
import SvgImporter from "vite-svg-loader";
import { devServerHttps, precompress, versionTag } from "@argon/vite-preset";

export default defineConfig({
  server: {
    port: 5005,
    https: devServerHttps(__dirname),
  },
  css: {
    postcss: {
      plugins: [tailwind(), autoprefixer()],
    },
  },
  plugins: [
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
    // Nothing here is written with the Options API — not the widget, not @argon/ui, not reka-ui.
    __VUE_OPTIONS_API__: false,
  },
  worker: {
    format: "es",
  },
});
