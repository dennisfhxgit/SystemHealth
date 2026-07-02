<template>
  <section class="health-panel test-results-page">
    <div class="filter-row test-results-toolbar">
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
      <span v-if="data?.status" :class="['deployment-status-pill', statusClass(data.status)]">{{ data.status }}</span>
      <span v-if="data?.statusDetail">{{ data.statusDetail }}</span>
    </div>

    <div class="inline-facts test-result-facts">
      <span>Job: {{ data?.jobName || 'Not configured' }}</span>
      <span>Build: {{ selectedBuildLabel }}</span>
      <span>Application: {{ selectedApplicationLabel }}</span>
      <span>Environment: {{ selectedEnvironmentLabel }}</span>
    </div>

    <div class="metric-grid">
      <article v-for="item in metrics" :key="item.label" class="metric-card">
        <span>{{ item.label }}</span>
        <strong>{{ item.value }}</strong>
      </article>
    </div>

    <section class="table-section">
      <h4>API Functional Results</h4>
      <div class="table-wrap">
        <table class="test-results-table api-functional-table">
          <thead><tr><th>Test</th><th>Suite</th><th>Status</th><th>Duration</th><th>Commit</th><th>Branch</th></tr></thead>
          <tbody>
            <tr v-for="(row, index) in data?.apiFunctionalResults || []" :key="row.id ?? row.key ?? `${row.suite}-${row.name}-${index}`">
              <td>{{ row.name ?? '-' }}</td>
              <td>{{ row.suite ?? '-' }}</td>
              <td><span :class="['deployment-status-pill', statusClass(row.status)]">{{ row.status ?? '-' }}</span></td>
              <td>{{ row.duration ?? '-' }}</td>
              <td>{{ row.commit ?? '-' }}</td>
              <td>{{ row.branch ?? '-' }}</td>
            </tr>
            <tr v-if="!(data?.apiFunctionalResults || []).length"><td colspan="6">No API functional results were returned.</td></tr>
          </tbody>
        </table>
      </div>
    </section>

    <section class="table-section">
      <h4>API Performance Results</h4>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Suite</th><th>TPS</th><th>Average Response</th><th>Max Response</th><th>Error %</th><th>Throughput</th></tr></thead>
          <tbody>
            <tr v-for="(row, index) in data?.apiPerformanceResults || []" :key="row.id ?? row.key ?? `${row.suite}-${index}`">
              <td>{{ row.suite ?? '-' }}</td>
              <td>{{ row.tps ?? '-' }}</td>
              <td>{{ formatMilliseconds(row.avgResponse) }}</td>
              <td>{{ formatMilliseconds(row.maxResponse) }}</td>
              <td>{{ formatPercent(row.errorPercent) }}</td>
              <td>{{ row.throughput ?? '-' }}</td>
            </tr>
            <tr v-if="!(data?.apiPerformanceResults || []).length"><td colspan="6">No API performance results were returned.</td></tr>
          </tbody>
        </table>
      </div>
    </section>

    <section class="table-section">
      <h4>UI Test Results</h4>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Scenario</th><th>Step</th><th>Browser</th><th>Status</th><th>Duration</th><th>Screenshot</th></tr></thead>
          <tbody>
            <tr v-for="(row, index) in data?.uiTestResults || []" :key="row.id ?? row.key ?? `${row.scenario}-${row.step}-${index}`">
              <td>{{ row.scenario ?? '-' }}</td>
              <td>{{ row.step ?? '-' }}</td>
              <td>{{ row.browser ?? '-' }}</td>
              <td><span :class="['deployment-status-pill', statusClass(row.status)]">{{ row.status ?? '-' }}</span></td>
              <td>{{ formatSeconds(row.duration) }}</td>
              <td><a v-if="row.screenshot" :href="row.screenshot" target="_blank" rel="noopener noreferrer">Open</a><span v-else>-</span></td>
            </tr>
            <tr v-if="!(data?.uiTestResults || []).length"><td colspan="6">No UI test results were returned.</td></tr>
          </tbody>
        </table>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, reactive } from 'vue'
import { displayValue, formatSeconds, healthEnvironmentLabel, healthEnvironmentOptions, metric, statusClass, useFilteredHealthChildPage, type HealthChildPageEmits, type HealthChildPageProps } from '../shared'

const props = defineProps<HealthChildPageProps>()
const emit = defineEmits<HealthChildPageEmits>()
const filter = reactive({ application: 'my-life-story-vault', environment: 'Development' })
const { data, load } = useFilteredHealthChildPage({
  props,
  emit,
  key: 'testResults',
  label: 'Test Results',
  path: '/api/system-health/test-results',
  filter
})
const environmentOptions = computed(() => healthEnvironmentOptions(data.value))
const metrics = computed(() => [
  metric('Total', data.value?.totalTests ?? 0),
  metric('Passed', data.value?.passedTests ?? 0),
  metric('Failed', data.value?.failedTests ?? 0),
  metric('Skipped', data.value?.skippedTests ?? 0)
])
const selectedApplicationLabel = computed(() => {
  const applications = data.value?.applications as Array<{ key?: string, label?: string }> | undefined
  return applications?.find(app => app.key === filter.application)?.label || filter.application || '-'
})
const selectedEnvironmentLabel = computed(() => healthEnvironmentLabel(filter.environment))
const selectedBuildLabel = computed(() => {
  const buildId = data.value?.buildId
  return typeof buildId === 'string' && buildId.toLowerCase() === 'lastbuild' ? 'Last Build' : buildId || '-'
})


function formatMilliseconds(value: unknown) {
  if (typeof value !== 'number') return displayValue(value)
  return `${value} ms`
}

function formatPercent(value: unknown) {
  if (typeof value !== 'number') return displayValue(value)
  return `${value}%`
}
</script>
