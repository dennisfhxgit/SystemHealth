<template>
  <section class="health-panel">
    <div class="status-line">
      <span v-if="data?.status" :class="['deployment-status-pill', statusClass(data.status)]">{{ data.status }}</span>
      <span v-if="data?.statusDetail">{{ data.statusDetail }}</span>
    </div>
    <div class="metric-grid">
      <article v-for="item in metrics" :key="item.label" class="metric-card"><span>{{ item.label }}</span><strong>{{ item.value }}</strong></article>
    </div>
    <div class="two-column">
      <section class="drive-panel">
        <h4>Application Server Drives</h4>
        <div v-if="(data?.applicationDrives || []).length" class="drive-list">
          <article v-for="drive in data?.applicationDrives || []" :key="drive.name" class="drive-row">
            <div class="drive-row-heading"><strong>{{ drive.name }}</strong><span :class="['deployment-status-pill', statusClass(drive.status)]">{{ drive.usagePercent }}%</span></div>
            <div class="drive-meter"><span :class="statusClass(drive.status)" :style="{ width: `${drive.usagePercent || 0}%` }"></span></div>
            <p>{{ drive.used }} used of {{ drive.total }}; {{ drive.free }} free.</p>
          </article>
        </div>
        <p v-else>No drives were returned.</p>
      </section>
      <section class="drive-panel">
        <h4>Data Server Drives</h4>
        <div v-if="(data?.dataServerDrives || []).length" class="drive-list">
          <article v-for="drive in data?.dataServerDrives || []" :key="drive.name" class="drive-row">
            <div class="drive-row-heading"><strong>{{ drive.name }}</strong><span :class="['deployment-status-pill', statusClass(drive.status)]">{{ drive.usagePercent }}%</span></div>
            <div class="drive-meter"><span :class="statusClass(drive.status)" :style="{ width: `${drive.usagePercent || 0}%` }"></span></div>
            <p>{{ drive.used }} used of {{ drive.total }}; {{ drive.free }} free.</p>
          </article>
        </div>
        <p v-else>No drives were returned.</p>
      </section>
    </div>
    <section v-for="table in checkTables" :key="table.title" class="table-section">
      <h4>{{ table.title }}</h4>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Check</th><th>Category</th><th>Status</th><th>Detail</th></tr></thead>
          <tbody>
            <tr v-for="(row, index) in table.rows" :key="row.id ?? row.key ?? index">
              <td>{{ row.name ?? '-' }}</td><td>{{ row.category ?? '-' }}</td>
              <td><span :class="['deployment-status-pill', statusClass(row.status)]">{{ row.status ?? '-' }}</span></td>
              <td>{{ row.detail ?? '-' }}</td>
            </tr>
            <tr v-if="!table.rows.length"><td colspan="4">{{ table.emptyText }}</td></tr>
          </tbody>
        </table>
      </div>
    </section>
    <section class="table-section">
      <h4>Combined System Alerts</h4>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Source</th><th>Severity</th><th>Message</th><th>Timestamp</th><th>Acknowledged</th></tr></thead>
          <tbody>
            <tr v-for="(row, index) in data?.alerts || []" :key="row.id ?? row.key ?? index">
              <td>{{ row.source ?? '-' }}</td><td><span :class="['deployment-status-pill', statusClass(row.severity)]">{{ row.severity ?? '-' }}</span></td>
              <td>{{ row.message ?? '-' }}</td><td>{{ formatDateTime(row.timestampUtc) }}</td><td>{{ row.acknowledged ? 'Yes' : 'No' }}</td>
            </tr>
            <tr v-if="!(data?.alerts || []).length"><td colspan="5">No active system alerts were returned.</td></tr>
          </tbody>
        </table>
      </div>
    </section>
    <section class="table-section">
      <h4>Critical Alert History</h4>
      <div v-if="historyError" class="inline-error">{{ historyError }}</div>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Status</th><th>Summary</th><th>Detected</th><th>Last Seen</th><th>Resolved</th><th>Acknowledged</th><th>Action</th></tr></thead>
          <tbody>
            <tr v-for="row in criticalHistory" :key="row.id">
              <td><span :class="['deployment-status-pill', row.active ? 'critical' : 'healthy']">{{ row.active ? 'Active' : 'Resolved' }}</span></td>
              <td>{{ row.summary || '-' }}</td>
              <td>{{ formatDateTime(row.detectedAtUtc) }}</td>
              <td>{{ formatDateTime(row.lastSeenAtUtc) }}</td>
              <td>{{ formatDateTime(row.resolvedAtUtc) }}</td>
              <td>{{ row.acknowledged ? `${row.acknowledgedBy || 'Yes'} ${formatDateTime(row.acknowledgedAtUtc)}` : 'No' }}</td>
              <td><button v-if="!row.acknowledged" type="button" class="table-action" @click="acknowledgeAlert(row.id)">Acknowledge</button><span v-else>-</span></td>
            </tr>
            <tr v-if="!criticalHistory.length"><td colspan="7">No critical alert history was returned.</td></tr>
          </tbody>
        </table>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { endpoint, fetchHealthJson, formatDateTime, metric, percent, statusClass, useHealthChildPage, type ApiRecord, type HealthChildPageEmits, type HealthChildPageProps } from '../shared'

const props = defineProps<HealthChildPageProps>()
const emit = defineEmits<HealthChildPageEmits>()
const criticalHistory = ref<ApiRecord[]>([])
const historyError = ref('')
const { data } = useHealthChildPage({
  props,
  emit,
  key: 'systemAlerts',
  label: 'System Alerts',
  path: '/api/system-health/system-alerts',
  afterLoad: () => { void loadCriticalHistory() }
})
const metrics = computed(() => [
  metric('Critical', data.value?.summary?.critical ?? 0),
  metric('Warnings', data.value?.summary?.warnings ?? 0),
  metric('Application CPU', percent(data.value?.summary?.processCpuPercent)),
  metric('Application Memory', percent(data.value?.summary?.memoryUsagePercent)),
  metric('Data CPU', percent(data.value?.summary?.dataServerCpuPercent)),
  metric('Data Memory', percent(data.value?.summary?.dataServerMemoryUsagePercent))
])
const checkTables = computed(() => [
  { title: 'Application Server Live Checks', rows: data.value?.checks || [], emptyText: 'No application server live checks were returned.' },
  { title: 'Data Server Live Checks', rows: data.value?.dataServerChecks || [], emptyText: 'No data server live checks were returned.' }
])

async function loadCriticalHistory() {
  historyError.value = ''
  try {
    const result = await fetchHealthJson(endpoint('/api/system-health/critical-events/history', { count: 50 }))
    criticalHistory.value = Array.isArray(result) ? result : []
  } catch {
    historyError.value = 'Unable to load critical alert history.'
  }
}

async function acknowledgeAlert(id: unknown) {
  if (typeof id !== 'number') return
  historyError.value = ''
  try {
    const response = await fetch(`/api/system-health/critical-events/history/${id}/acknowledge`, {
      method: 'POST',
      cache: 'no-store',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({ acknowledgedBy: 'SystemHealth user', note: 'Acknowledged from System Alerts.' })
    })
    if (!response.ok) throw new Error(`Acknowledge returned ${response.status}`)
    await loadCriticalHistory()
  } catch {
    historyError.value = 'Unable to acknowledge critical alert.'
  }
}

watch(() => props.refreshToken, () => {
  void loadCriticalHistory()
})
</script>
