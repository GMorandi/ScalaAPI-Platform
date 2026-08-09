import { defineConfig } from "vite";
import solid from "vite-plugin-solid";
import tailwindcss from "@tailwindcss/vite";

export default defineConfig({
  plugins: [solid(), tailwindcss()],
  server: {
    port: 5174,
    proxy: {
      "/auth": "http://localhost:5001",
      "/user": "http://localhost:5001",
      "/announcements": "http://localhost:5001",
    },
  },
});
