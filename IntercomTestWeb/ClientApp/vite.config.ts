import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The SPA builds into the host project's wwwroot, which Kestrel serves as static files (single
// origin). `npm run dev` runs the Vite dev server with HMR and proxies the API + WebSocket to the
// .NET backend on :8080.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': 'http://localhost:8080',
      '/ws': { target: 'ws://localhost:8080', ws: true },
    },
  },
});
