<template>
  <section class="health-panel ai-code-analysis-page">
    <div class="filter-row ai-code-analysis-toolbar">
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
    </div>

    <div class="status-line">
      <span v-if="currentData?.status" :class="['deployment-status-pill', statusClass(currentData.status)]">{{ currentData.status }}</span>
      <span v-if="currentData?.statusDetail">{{ currentData.statusDetail }}</span>
    </div>

    <div class="inline-facts ai-code-analysis-facts">
      <span>Provider: {{ currentData?.providerName || 'Not configured' }}</span>
      <span>Job: {{ currentData?.jobName || 'Not configured' }}</span>
      <span>Build: {{ selectedBuildLabel }}</span>
      <span>Application: {{ selectedApplicationLabel }}</span>
      <span>Environment: {{ selectedEnvironmentLabel }}</span>
      <span>Findings: {{ currentFindings.length }}</span>
      <a v-if="currentData?.providerDashboardUrl" :href="currentData.providerDashboardUrl" target="_blank" rel="noopener noreferrer">Open Provider</a>
      <a v-if="currentData?.buildUrl" :href="currentData.buildUrl" target="_blank" rel="noopener noreferrer">Open Build</a>
      <a v-if="currentData?.artifactUrl" :href="currentData.artifactUrl" target="_blank" rel="noopener noreferrer">Open Artifact</a>
    </div>

    <div class="metric-grid quality-metric-grid ai-severity-grid">
      <article v-for="item in severityMetrics" :key="item.label" :class="['metric-card', 'status-metric-card', item.status]">
        <strong>{{ item.value }}</strong>
        <span>{{ item.label }}</span>
      </article>
    </div>

    <section class="table-section">
      <h4>AI Findings</h4>
      <div class="table-wrap">
        <table class="ai-findings-table">
          <thead><tr><th>Severity</th><th>Confidence</th><th>Category</th><th>File</th><th>Line</th><th>Summary</th><th>Recommendation</th></tr></thead>
          <tbody>
            <tr v-for="(row, index) in currentFindings" :key="`${currentSelectionKey}-${row.id ?? row.key ?? `${row.file}-${row.line}-${index}`}`">
              <td><span :class="['deployment-status-pill', statusClass(row.severity)]">{{ row.severity ?? '-' }}</span></td>
              <td>{{ row.confidence ?? '-' }}</td>
              <td>{{ row.category ?? '-' }}</td>
              <td>{{ row.file ?? '-' }}</td>
              <td>{{ row.line ?? '-' }}</td>
              <td>{{ row.summary ?? '-' }}</td>
              <td>{{ row.recommendation ?? '-' }}</td>
            </tr>
            <tr v-if="!currentFindings.length"><td colspan="7">{{ emptyFindingsMessage }}</td></tr>
          </tbody>
        </table>
      </div>
    </section>

    <section class="table-section">
      <h4>Ingestion Checks</h4>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Check</th><th>Category</th><th>Status</th><th>Detail</th></tr></thead>
          <tbody>
            <tr v-for="(row, index) in currentChecks" :key="`${currentSelectionKey}-${row.id ?? row.key ?? index}`">
              <td>{{ row.name ?? '-' }}</td>
              <td>{{ row.category ?? '-' }}</td>
              <td><span :class="['deployment-status-pill', statusClass(row.status)]">{{ row.status ?? '-' }}</span></td>
              <td>{{ row.detail ?? '-' }}</td>
            </tr>
            <tr v-if="!currentChecks.length"><td colspan="4">No AI code analysis checks were returned.</td></tr>
          </tbody>
        </table>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, reactive } from 'vue'
import { canonicalHealthEnvironment, healthEnvironmentLabel, healthEnvironmentOptions, statusClass, useFilteredHealthChildPage, type ApiRecord, type HealthChildPageEmits, type HealthChildPageProps } from '../shared'

const props = defineProps<HealthChildPageProps>()
const emit = defineEmits<HealthChildPageEmits>()
const filter = reactive({ application: 'my-life-story-vault', environment: 'Development' })
const { data, load } = useFilteredHealthChildPage({
  props,
  emit,
  key: 'aiCodeAnalysis',
  label: 'AI Code Analysis',
  path: '/api/system-health/ai-code-analysis',
  filter
})
const environmentOptions = computed(() => healthEnvironmentOptions(data.value))
const selectedApplicationLabel = computed(() => {
  const applications = data.value?.applications as Array<{ key?: string, label?: string }> | undefined
  return applications?.find(app => app.key === filter.application)?.label || filter.application || '-'
})
const selectedEnvironmentLabel = computed(() => healthEnvironmentLabel(filter.environment))
const currentSelectionKey = computed(() => `${filter.application}:${filter.environment}`)
const currentData = computed(() => isCurrentSelectionData(data.value) ? data.value : null)
const currentFindings = computed(() => currentData.value?.findings as Array<Record<string, any>> | undefined || [])
const currentChecks = computed(() => currentData.value?.checks as Array<Record<string, any>> | undefined || [])
const selectedBuildLabel = computed(() => {
  const buildId = currentData.value?.buildId
  return typeof buildId === 'string' && buildId.toLowerCase() === 'lastbuild' ? 'Last Build' : buildId || 'Last Build'
})
const artifactMissing = computed(() => {
  const detail = `${currentData.value?.statusDetail || ''} ${currentChecks.value.map((check: any) => check?.detail || '').join(' ')}`
  return detail.toLowerCase().includes('ai-code-analysis.json was not found')
})
const emptyFindingsMessage = computed(() => {
  if (!currentData.value) {
    return 'Loading AI findings for the selected application and environment.'
  }

  return artifactMissing.value
    ? 'AI analysis artifact was not found for the selected Jenkins build. No AI findings can be displayed until Jenkins archives ai-code-analysis.json.'
    : 'No AI findings were returned for the selected build.'
})
const severityMetrics = computed(() => {
  const counts = currentData.value?.severityCounts as Array<{ severity?: string, count?: number }> | undefined
  const bySeverity = new Map((counts || []).map(item => [(item.severity || '').toLowerCase(), item.count ?? 0]))
  return [
    severityMetric('Critical', bySeverity.get('critical') ?? 0),
    severityMetric('High', bySeverity.get('high') ?? 0),
    severityMetric('Medium', (bySeverity.get('medium') ?? 0) + (bySeverity.get('moderate') ?? 0)),
    severityMetric('Low', bySeverity.get('low') ?? 0)
  ]
})

function isCurrentSelectionData(source: ApiRecord | null) {
  if (!source) return false
  return String(source.selectedApplicationKey ?? '').toLowerCase() === filter.application.toLowerCase()
    && canonicalHealthEnvironment(source.selectedEnvironment).toLowerCase() === canonicalHealthEnvironment(filter.environment).toLowerCase()
}

function severityStatus(label: string, value: number) {
  if (value <= 0) return 'healthy'
  if (label === 'Critical') return 'critical'
  return 'warning'
}

function severityMetric(label: string, value: number) {
  return {
    label,
    value,
    status: severityStatus(label, value)
  }
}
</script>
