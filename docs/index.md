---
layout: home

hero:
  name: HyPanel
  text: 轻量的多节点代理管理面板
  tagline: 一个面板集中管理多台节点上的 Hysteria2 / Xray 代理服务，节点一条命令接入，用户和订阅统一分发。
  image:
    src: /logo.svg
    alt: HyPanel
  actions:
    - theme: brand
      text: 快速开始
      link: /guide/install
    - theme: alt
      text: 介绍
      link: /guide/introduction
    - theme: alt
      text: GitHub
      link: https://github.com/greepar/HyPanel

features:
  - title: 多节点、多服务
    details: 一个面板管理多台节点，每台节点可同时运行 Hysteria2、Xray REALITY、Shadowsocks 2022 等多个服务。
  - title: 一键接入
    details: 一条命令安装 Agent，节点主动连接面板，无需开放管理端口；卸载同样一条命令。
  - title: 用户与订阅
    details: 按用户组分配服务，支持流量上限、到期时间和每月重置；订阅为 Clash / Mihomo 格式，自带分流规则。
  - title: 证书管理
    details: 支持手动上传、路径映射和 ACME 自动续期，证书更新后自动下发到节点。
  - title: 自动更新与备份
    details: 面板、Agent 和代理内核都可在线更新，失败自动回滚；支持在线备份与恢复。
  - title: 轻量部署
    details: Server 和 Agent 都是单个可执行文件，无需 .NET 运行时，数据库使用 SQLite。
---
