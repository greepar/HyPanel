<!-- Panel ⇄ Agent ⇄ services overview; boxes use theme variables so it follows light/dark mode. -->
<template>
  <div class="arch">
    <div class="arch-top">
      <div class="arch-box">浏览器 / 管理员</div>
      <div class="arch-link"><span>HTTPS</span></div>
      <div class="arch-box arch-primary">面板<code>hypanel-server</code></div>
    </div>
    <div class="arch-sync">
      <span class="arch-arrow">↑</span>
      Agent 主动同步配置（节点无需开放管理端口）
      <span class="arch-arrow">↑</span>
    </div>
    <div class="arch-nodes">
      <div v-for="node in nodes" :key="node.name" class="arch-node">
        <div class="arch-box">{{ node.name }}<code>hypanel-agent</code></div>
        <div class="arch-services">
          <span v-for="service in node.services" :key="service">{{ service }}</span>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
const nodes = [
  { name: '节点 A', services: ['Hysteria2', 'Xray REALITY'] },
  { name: '节点 B', services: ['Shadowsocks 2022'] },
]
</script>

<style scoped>
.arch {
  display: grid;
  gap: 14px;
  margin: 20px 0;
  padding: 24px 20px;
  border: 1px solid var(--vp-c-divider);
  border-radius: 12px;
  background: var(--vp-c-bg-soft);
  font-size: 14px;
}
.arch-top {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 12px;
  flex-wrap: wrap;
}
.arch-box {
  display: grid;
  gap: 2px;
  min-width: 140px;
  padding: 10px 14px;
  border: 1px solid var(--vp-c-divider);
  border-radius: 10px;
  background: var(--vp-c-bg);
  font-weight: 600;
  text-align: center;
}
.arch-box code {
  font-size: 12px;
  font-weight: 400;
  color: var(--vp-c-text-2);
  background: none;
  padding: 0;
}
.arch-primary {
  border-color: var(--vp-c-brand-1);
  box-shadow: 0 0 0 3px var(--vp-c-brand-soft);
}
.arch-link {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--vp-c-text-2);
  font-size: 12px;
}
.arch-link::before,
.arch-link::after {
  content: '';
  width: 28px;
  border-top: 2px solid var(--vp-c-divider);
}
.arch-sync {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  color: var(--vp-c-text-2);
  font-size: 12px;
  text-align: center;
}
.arch-arrow {
  color: var(--vp-c-brand-1);
  font-size: 16px;
}
.arch-nodes {
  display: flex;
  justify-content: center;
  gap: 16px;
  flex-wrap: wrap;
}
.arch-node {
  display: grid;
  justify-items: center;
  gap: 8px;
}
.arch-services {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  justify-content: center;
}
.arch-services span {
  padding: 2px 10px;
  border-radius: 999px;
  background: var(--vp-c-brand-soft);
  color: var(--vp-c-brand-1);
  font-size: 12px;
  font-weight: 500;
}
</style>
