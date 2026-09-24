import { defineConfig } from 'vitepress'

// Published at https://greepar.github.io/HyPanel/ by .github/workflows/docs.yml.
export default defineConfig({
  lang: 'zh-CN',
  title: 'HyPanel',
  description: '一个面板集中管理多台节点上的 Hysteria2 / Xray 代理服务',
  base: '/HyPanel/',
  cleanUrls: true,
  lastUpdated: true,
  head: [['link', { rel: 'icon', type: 'image/svg+xml', href: '/HyPanel/logo.svg' }]],
  themeConfig: {
    logo: '/logo.svg',
    nav: [
      { text: '指南', link: '/guide/introduction' },
      { text: '下载', link: 'https://github.com/greepar/HyPanel/releases' },
    ],
    sidebar: [
      {
        text: '开始',
        items: [
          { text: '介绍', link: '/guide/introduction' },
          { text: '安装面板', link: '/guide/install' },
          { text: '添加节点', link: '/guide/nodes' },
        ],
      },
      {
        text: '使用',
        items: [
          { text: '创建服务', link: '/guide/services' },
          { text: '用户与订阅', link: '/guide/users' },
          { text: 'TLS 证书', link: '/guide/certificates' },
          { text: '更新与备份', link: '/guide/maintenance' },
        ],
      },
      {
        text: '其他',
        items: [{ text: '常见问题', link: '/guide/faq' }],
      },
    ],
    socialLinks: [{ icon: 'github', link: 'https://github.com/greepar/HyPanel' }],
    search: {
      provider: 'local',
      options: {
        translations: {
          button: { buttonText: '搜索', buttonAriaLabel: '搜索' },
          modal: {
            noResultsText: '没有找到相关结果',
            resetButtonTitle: '清除',
            footer: { selectText: '选择', navigateText: '切换', closeText: '关闭' },
          },
        },
      },
    },
    outline: { label: '本页目录' },
    docFooter: { prev: '上一页', next: '下一页' },
    lastUpdated: { text: '最后更新' },
    returnToTopLabel: '回到顶部',
    sidebarMenuLabel: '菜单',
    darkModeSwitchLabel: '主题',
    lightModeSwitchTitle: '切换到浅色',
    darkModeSwitchTitle: '切换到深色',
    footer: {
      message: '基于 GPL-3.0 许可发布',
      copyright: 'Copyright © greepar',
    },
  },
})
