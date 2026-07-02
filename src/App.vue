<template>
  <main class="app-shell">
    <SystemHealthModule />
  </main>
</template>

<script setup lang="ts">
import SystemHealthModule from './modules/system/System Health/Index.vue'
</script>

<style>
body {
  margin: 0;
  background: #f5f7fa;
}

.app-shell {
  min-height: 100vh;
  padding: 20px;
  box-sizing: border-box;
}
</style>
