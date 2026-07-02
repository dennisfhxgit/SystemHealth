<template>
  <section class="health-panel">
    <div class="status-line">
      <span v-if="data?.overallStatus" :class="['deployment-status-pill', statusClass(data.overallStatus)]">{{ data.overallStatus }}</span>
    </div>
    <div class="worker-grid">
      <article v-for="worker in data?.workers || []" :key="worker.key" class="worker-card">
        <div class="worker-header">
          <div><h4>{{ worker.name }}</h4><p>{{ worker.statusDetail }}</p></div>
          <span :class="['health-state', statusClass(worker.status)]">{{ worker.status }}</span>
        </div>
        <dl>
          <div><dt>Enabled</dt><dd>{{ worker.enabled ? 'Yes' : 'No' }}</dd></div>
          <div><dt>Poll interval</dt><dd>{{ formatSeconds(worker.pollIntervalSeconds) }}</dd></div>
          <div v-if="worker.leaseMinutes"><dt>Lease</dt><dd>{{ worker.leaseMinutes }} min</dd></div>
          <div v-if="worker.batchSize"><dt>Batch size</dt><dd>{{ worker.batchSize }}</dd></div>
          <div v-if="worker.dailyLimit"><dt>Daily limit</dt><dd>{{ worker.dailyLimit }}</dd></div>
          <div v-if="worker.remainingToday !== null && worker.remainingToday !== undefined"><dt>Remaining today</dt><dd>{{ worker.remainingToday }}</dd></div>
          <div v-if="worker.audienceCount !== null && worker.audienceCount !== undefined"><dt>Audiences</dt><dd>{{ worker.audienceCount }}</dd></div>
          <div><dt>Last started</dt><dd>{{ formatDateTime(worker.lastRunStartedAtUtc) }}</dd></div>
          <div><dt>Last completed</dt><dd>{{ formatDateTime(worker.lastRunCompletedAtUtc) }}</dd></div>
        </dl>
        <div class="worker-metrics">
          <div v-for="metric in worker.metrics || []" :key="metric.label" :class="['worker-metric', statusClass(metric.status)]">
            <span>{{ metric.label }}</span><strong>{{ metric.value }}</strong>
          </div>
        </div>
      </article>
      <p v-if="data && !(data.workers || []).length" class="empty-state">No email workers were returned.</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { formatDateTime, formatSeconds, statusClass, useHealthChildPage, type HealthChildPageEmits, type HealthChildPageProps } from '../shared'
const props = defineProps<HealthChildPageProps>()
const emit = defineEmits<HealthChildPageEmits>()
const { data } = useHealthChildPage({
  props,
  emit,
  key: 'emailWorkers',
  label: 'Email Workers',
  path: '/api/system-health/email-workers',
  status: result => result.overallStatus
})
</script>
