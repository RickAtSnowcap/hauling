import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  base: '/hauling/',
  plugins: [react()],
  server: {
    proxy: {
      '/hauling/api': {
        target: 'http://127.0.0.1:8400',
        rewrite: (path) => path.replace(/^\/hauling/, '')
      },
      '/hauling/callback': {
        target: 'http://127.0.0.1:8400',
        rewrite: (path) => path.replace(/^\/hauling/, '')
      }
    }
  }
})
