<template>
  <section class="health-panel">
    <div class="status-line">
      <span v-if="data?.status" :class="['deployment-status-pill', statusClass(data.status)]">{{ data.status }}</span>
      <span v-if="data?.statusDetail">{{ data.statusDetail }}</span>
    </div>
    <section class="table-section">
      <h4>Admin & Environment</h4>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Environment</th><th>URL</th><th>Status</th><th>Latency</th><th>Uptime</th><th>Mode</th><th>Last Checked</th><th>Detail</th></tr></thead>
          <tbody>
            <tr v-for="(row, index) in data?.environments || []" :key="row.id ?? row.key ?? index">
              <td>{{ row.name ?? '-' }}</td>
              <td><a v-if="row.url" :href="row.url" target="_blank" rel="noopener noreferrer">{{ row.url }}</a><span v-else>Not available</span></td>
              <td><span :class="['deployment-status-pill', statusClass(row.status)]">{{ row.status ?? '-' }}</span></td>
              <td>{{ row.latency ?? '-' }}</td><td>{{ row.uptime ?? '-' }}</td><td>{{ row.mode ?? '-' }}</td><td>{{ formatDateTime(row.lastCheckedUtc) }}</td><td>{{ row.detail ?? '-' }}</td>
            </tr>
            <tr v-if="!(data?.environments || []).length"><td colspan="8">No Admin & Environment targets are configured.</td></tr>
          </tbody>
        </table>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { formatDateTime, statusClass, useHealthChildPage, type HealthChildPageEmits, type HealthChildPageProps } from '../shared'
const props = defineProps<HealthChildPageProps>()
const emit = defineEmits<HealthChildPageEmits>()
const { data } = useHealthChildPage({ props, emit, key: 'adminEnvironment', label: 'Admin & Environment', path: '/api/system-health/admin-environment' })
</script>
