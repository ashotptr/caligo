import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

const caligo = process.env.VITE_CALIGO_URL ?? 'http://localhost:8080'
const vod = process.env.VITE_VOD_URL ?? 'http://localhost:8010'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/live/moq': { target: caligo, ws: true },
      '/live/': caligo,
      '/radio/': caligo,
      '/music/': caligo,
      '/mixtape/': caligo,
      '/caligo': caligo,
      '/cgi_bin/get_chat_rooms': caligo,
      '/chat/': { target: caligo, ws: true },
      '/vod/': vod,
    },
  },
})
