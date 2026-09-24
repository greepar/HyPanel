import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import ArchitectureDiagram from './ArchitectureDiagram.vue'
import './style.css'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('ArchitectureDiagram', ArchitectureDiagram)
  },
} satisfies Theme
