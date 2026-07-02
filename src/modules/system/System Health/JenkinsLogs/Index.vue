<template>
  <section class="health-panel">
    <div class="filter-row jenkins-log-toolbar">
      <label>
        <span>Application</span>
        <select v-model="filter.application" @change="load">
          <option v-for="app in data?.applications || []" :key="app.key" :value="app.key">{{ app.label ?? app.key }}</option>
        </select>
      </label>
      <label>
        <span>Environment</span>
        <select v-model="filter.environment" @change="load">
          <option v-for="environment in environmentOptions" :key="environment.value" :value="environment.value">{{ environment.label }}</option>
        </select>
      </label>
      <button class="download-log-button" type="button" :disabled="!data?.logText" @click="downloadLog">Download Log</button>
    </div>
    <div class="status-line">
      <span v-if="data?.status" :class="['deployment-status-pill', statusClass(data.status)]">{{ data.status }}</span>
      <span v-if="data?.statusDetail">{{ data.statusDetail }}</span>
    </div>
    <div class="inline-facts jenkins-log-facts">
      <span v-if="data?.buildStatus" :class="['deployment-status-pill', statusClass(data.buildStatus)]">{{ data.buildStatus }}</span>
      <span>Job: {{ data?.jobName || 'Not configured' }}</span>
      <span>Build: {{ data?.buildId || '-' }}</span>
    </div>
    <pre class="log-output">{{ data?.logText || 'No Jenkins log was returned.' }}</pre>
  </section>
</template>

<script setup lang="ts">
import { computed, reactive } from 'vue'
import { healthEnvironmentOptions, statusClass, useFilteredHealthChildPage, type HealthChildPageEmits, type HealthChildPageProps } from '../shared'

const props = defineProps<HealthChildPageProps>()
const emit = defineEmits<HealthChildPageEmits>()
const filter = reactive({ application: 'my-life-story-vault', environment: 'Development' })
const { data, load } = useFilteredHealthChildPage({
  props,
  emit,
  key: 'jenkinsLogs',
  label: 'Jenkins Logs',
  path: '/api/system-health/jenkins-log',
  filter
})
const environmentOptions = computed(() => healthEnvironmentOptions(data.value))

function downloadLog() {
  if (!data.value?.logText) return

  const blob = new Blob([data.value.logText], { type: 'text/plain;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = `jenkins-${data.value.jobName || 'log'}-${data.value.buildId || 'lastBuild'}.log`
  document.body.appendChild(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}
</script>
