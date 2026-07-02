<template>
  <section class="health-panel">
    <div class="filter-row artifact-history-toolbar">
      <label>
        <span>Application</span>
        <select v-model="filter.application" @change="loadLastBuild">
          <option v-for="app in data?.applications || []" :key="app.key" :value="app.key">{{ app.label ?? app.key }}</option>
        </select>
      </label>
      <label>
        <span>Environment</span>
        <select v-model="filter.environment" @change="loadLastBuild">
          <option v-for="environment in environmentOptions" :key="environment.value" :value="environment.value">{{ environment.label }}</option>
        </select>
      </label>
      <label>
        <span>Builds</span>
        <select v-model.number="filter.buildCount" @change="load">
          <option v-for="count in buildCountOptions" :key="count" :value="count">{{ buildCountLabel(count) }}</option>
        </select>
      </label>
    </div>
    <div class="status-line">
      <span v-if="data?.status" :class="['deployment-status-pill', statusClass(data.status)]">{{ data.status }}</span>
      <span v-if="data?.statusDetail">{{ data.statusDetail }}</span>
    </div>
    <div class="inline-facts">
      <span>Job: {{ data?.jobName || 'Not configured' }}</span>
      <span>Scope: {{ selectedBuildScope }}</span>
      <span>Artifacts: {{ artifacts.length }}</span>
      <span>Rollback Files: {{ rollbackArtifacts.length }}</span>
      <span>Rollback Snapshots: {{ rollbackSnapshotCount }}</span>
      <span>Build Artifacts: {{ buildArtifacts.length }}</span>
    </div>
    <section class="table-section">
      <h4>Rollback Snapshots</h4>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Type</th><th>Filename</th><th>Build</th><th>Size</th><th>Created</th><th>Download</th><th>Build Log</th></tr></thead>
          <tbody>
            <tr v-for="(row, index) in rollbackArtifacts" :key="row.id ?? row.key ?? index">
              <td><span class="artifact-type-pill rollback">{{ row.artifactType ?? 'Rollback Snapshot' }}</span></td>
              <td>
                <span>{{ row.displayName ?? row.fileName ?? '-' }}</span>
                <small v-if="row.relativePath" class="artifact-relative-path">{{ row.relativePath }}</small>
              </td><td>{{ row.buildNumber ?? '-' }}</td><td>{{ row.size ?? '-' }}</td><td>{{ formatDateTime(row.created) }}</td>
              <td><a v-if="row.downloadUrl" :href="row.downloadUrl" target="_blank" rel="noopener noreferrer">Download</a><span v-else>Not available</span></td>
              <td><a v-if="row.logUrl" :href="row.logUrl" target="_blank" rel="noopener noreferrer">Build Log</a><span v-else>Not available</span></td>
            </tr>
            <tr v-if="!rollbackArtifacts.length"><td colspan="7">No rollback snapshots were found for the selected filters.</td></tr>
          </tbody>
        </table>
      </div>
    </section>
    <section class="table-section">
      <h4>Built Artifacts</h4>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Type</th><th>Filename</th><th>Build</th><th>Size</th><th>Created</th><th>Download</th><th>Build Log</th></tr></thead>
          <tbody>
            <tr v-for="(row, index) in buildArtifacts" :key="row.id ?? row.key ?? index">
              <td><span class="artifact-type-pill standard">{{ row.artifactType ?? 'Build Artifact' }}</span></td>
              <td>
                <span>{{ row.displayName ?? row.fileName ?? '-' }}</span>
                <small v-if="row.relativePath" class="artifact-relative-path">{{ row.relativePath }}</small>
              </td><td>{{ row.buildNumber ?? '-' }}</td><td>{{ row.size ?? '-' }}</td><td>{{ formatDateTime(row.created) }}</td>
              <td><a v-if="row.downloadUrl" :href="row.downloadUrl" target="_blank" rel="noopener noreferrer">Download</a><span v-else>Not available</span></td>
              <td><a v-if="row.logUrl" :href="row.logUrl" target="_blank" rel="noopener noreferrer">Build Log</a><span v-else>Not available</span></td>
            </tr>
            <tr v-if="!buildArtifacts.length"><td colspan="7">No built artifacts were found for the selected filters.</td></tr>
          </tbody>
        </table>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, reactive } from 'vue'
import { formatDateTime, healthEnvironmentOptions, statusClass, useFilteredHealthChildPage, type HealthChildPageEmits, type HealthChildPageProps } from '../shared'

const props = defineProps<HealthChildPageProps>()
const emit = defineEmits<HealthChildPageEmits>()
const filter = reactive({ application: 'my-life-story-vault', environment: 'Development', buildCount: 1 })
type ArtifactHistoryRow = {
  id?: string
  key?: string
  artifactType?: string
  isRollbackArtifact?: boolean
  displayName?: string
  fileName?: string
  relativePath?: string
  buildNumber?: string | number
  size?: string
  created?: string
  downloadUrl?: string
  logUrl?: string
}
const { data, load } = useFilteredHealthChildPage({
  props,
  emit,
  key: 'artifactHistory',
  label: 'Artifact History',
  path: '/api/system-health/artifact-history',
  filter,
  extraParams: () => ({ buildCount: filter.buildCount }),
  afterLoad: result => {
    filter.buildCount = Number(result.selectedBuildCount ?? filter.buildCount)
  }
})
const environmentOptions = computed(() => healthEnvironmentOptions(data.value))
const buildCountOptions = computed(() => {
  const counts = data.value?.buildCounts as number[] | undefined
  return counts?.length ? counts : [1, 10, 30, 50, 100]
})
const selectedBuildScope = computed(() => buildCountLabel(Number(data.value?.selectedBuildCount ?? filter.buildCount)))
const artifacts = computed(() => (data.value?.artifacts || []) as ArtifactHistoryRow[])
const rollbackArtifacts = computed(() => artifacts.value.filter((artifact: ArtifactHistoryRow) => Boolean(artifact.isRollbackArtifact)))
const buildArtifacts = computed(() => artifacts.value.filter((artifact: ArtifactHistoryRow) => !artifact.isRollbackArtifact))
const rollbackSnapshotCount = computed(() => {
  const snapshotKeys = new Set<string>()
  for (const artifact of rollbackArtifacts.value) {
    const relativePath = String(artifact.relativePath ?? '')
    const match = relativePath.match(/^_rollback\/([^/]+)\/website-current\//i)
    snapshotKeys.add(match?.[1] ?? String(artifact.buildNumber ?? relativePath))
  }

  return snapshotKeys.size
})

function buildCountLabel(count: number) {
  return count === 1 ? 'Last Build' : `Last ${count} Builds`
}

function loadLastBuild() {
  filter.buildCount = 1
  void load()
}
</script>
