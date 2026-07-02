<template>
  <section class="system-health-module" aria-label="System Health">
    <nav class="health-menu" aria-label="System Health sections">
      <button
        v-for="section in sections"
        :key="section.key"
        type="button"
        :class="{ active: currentSection === section.key, critical: isSectionCritical(section.key) }"
        @click="currentSection = section.key"
      >
        {{ section.label }}
      </button>
    </nav>

    <div
      v-if="activeLoading"
      class="health-loading-bar"
    >
      <progress class="health-loading-progress" aria-label="Loading System Health section"></progress>
      <span class="health-loading-indicator" aria-hidden="true"></span>
    </div>

    <div class="health-content">
      <div v-if="activeError" class="error-banner">{{ activeError }}</div>

      <component
        :is="activeSection.component"
        :refresh-token="refreshToken"
        @state-change="setSectionState"
        @loading-change="setActiveLoading"
        @error-change="setActiveError"
      />
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { getSystemHealthCriticalSections, loadSystemHealthCriticalSections, reportSystemHealthCriticalSections, updateSystemHealthCriticalSection, type CriticalHealthSection } from '../systemHealthCriticalState'
import AdminEnvironmentPage from './AdminEnvironment/Index.vue'
import AiCodeAnalysisPage from './AiCodeAnalysis/Index.vue'
import ArtifactHistoryPage from './ArtifactHistory/Index.vue'
import CodeQualitySecurityPage from './CodeQualitySecurity/Index.vue'
import EmailWorkersPage from './EmailWorkers/Index.vue'
import JenkinsLogsPage from './JenkinsLogs/Index.vue'
import SystemAlertsPage from './SystemAlerts/Index.vue'
import TestResultsPage from './Test Results/Index.vue'
import { isCriticalStatus, type HealthSectionState, type SectionKey } from './shared'

const sections: Array<{ key: SectionKey; label: string; detail: string; component: unknown }> = [
  { key: 'codeQuality', label: 'Code Quality & Security', detail: 'SonarQube, security findings, and coverage status.', component: CodeQualitySecurityPage },
  { key: 'jenkinsLogs', label: 'Jenkins Logs', detail: 'Latest Jenkins job log and build status.', component: JenkinsLogsPage },
  { key: 'testResults', label: 'Test Results', detail: 'API, performance, and UI test result summary.', component: TestResultsPage },
  { key: 'artifactHistory', label: 'Artifact History', detail: 'Jenkins artifacts returned for selected builds.', component: ArtifactHistoryPage },
  { key: 'aiCodeAnalysis', label: 'AI Code Analysis', detail: 'AI analysis provider readiness and checks.', component: AiCodeAnalysisPage },
  { key: 'systemAlerts', label: 'System Alerts', detail: 'Server health, drive status, live checks, and active alerts.', component: SystemAlertsPage },
  { key: 'adminEnvironment', label: 'Admin & Environment', detail: 'Environment URL status, latency, uptime, and mode.', component: AdminEnvironmentPage },
  { key: 'emailWorkers', label: 'Email Workers', detail: 'System and marketing email worker health.', component: EmailWorkersPage }
]

const currentSection = ref<SectionKey>('codeQuality')
const refreshToken = ref(0)
const autoRefreshSections = new Set<SectionKey>([
  'systemAlerts',
  'adminEnvironment',
  'emailWorkers'
])
const loading = reactive<Record<SectionKey, boolean>>({
  codeQuality: false,
  jenkinsLogs: false,
  testResults: false,
  aiCodeAnalysis: false,
  systemAlerts: false,
  artifactHistory: false,
  adminEnvironment: false,
  emailWorkers: false
})
const errors = reactive<Record<SectionKey, string>>({
  codeQuality: '',
  jenkinsLogs: '',
  testResults: '',
  aiCodeAnalysis: '',
  systemAlerts: '',
  artifactHistory: '',
  adminEnvironment: '',
  emailWorkers: ''
})
const sectionStates = reactive<Record<SectionKey, HealthSectionState | null>>({
  codeQuality: null,
  jenkinsLogs: null,
  testResults: null,
  aiCodeAnalysis: null,
  systemAlerts: null,
  artifactHistory: null,
  adminEnvironment: null,
  emailWorkers: null
})

const activeSection = computed(() => sections.find((section) => section.key === currentSection.value) ?? sections[0])
const activeError = computed(() => errors[currentSection.value])
const activeLoading = computed(() => loading[currentSection.value])

watch(currentSection, () => {
  errors[currentSection.value] = ''
})

function setSectionState(state: HealthSectionState) {
  const wasCritical = isSectionCritical(state.key)
    || getSystemHealthCriticalSections().some(criticalSection => criticalSection.key === state.key)
  sectionStates[state.key] = state
  updateSystemHealthCriticalSection(state)
  void publishCriticalState(wasCritical || isCriticalStatus(state.status))
}

function setActiveLoading(value: boolean) {
  loading[currentSection.value] = value
}

function setActiveError(message: string) {
  errors[currentSection.value] = message
}

function isSectionCritical(section: SectionKey) {
  const localState = sectionStates[section]
  if (localState) {
    return isCriticalStatus(localState.status)
  }

  return getSystemHealthCriticalSections().some(criticalSection => criticalSection.key === section)
}

function currentCriticalSections(): CriticalHealthSection[] {
  const criticalByKey = new Map(getSystemHealthCriticalSections().map(section => [section.key, section]))

  for (const state of Object.values(sectionStates)) {
    if (!state) continue
    if (!isCriticalStatus(state.status)) {
      criticalByKey.delete(state.key)
      continue
    }

    criticalByKey.set(state.key, {
      key: state.key,
      label: state.label,
      status: state.status,
      detail: state.detail
    })
  }

  return sections
    .map(section => criticalByKey.get(section.key))
    .filter((section): section is CriticalHealthSection => Boolean(section))
}

let lastCriticalSignature = ''
let autoRefreshTimer: number | undefined

function criticalSignature(sections: CriticalHealthSection[]) {
  return sections
    .map((section) => `${section.key}:${section.status}:${section.detail}`)
    .join('|')
}

async function publishCriticalState(force = false) {
  const storedSignature = criticalSignature(getSystemHealthCriticalSections())
  const criticalSections = currentCriticalSections()
  const signature = criticalSignature(criticalSections)

  if (!force && signature === lastCriticalSignature && signature === storedSignature) {
    return
  }

  lastCriticalSignature = signature
  await reportSystemHealthCriticalSections(criticalSections)
}

onMounted(() => {
  void loadSystemHealthCriticalSections()
  autoRefreshTimer = window.setInterval(() => {
    if (autoRefreshSections.has(currentSection.value)) {
      refreshToken.value += 1
    }
  }, 120000)
})

onBeforeUnmount(() => {
  if (autoRefreshTimer !== undefined) {
    window.clearInterval(autoRefreshTimer)
  }
})
</script>

<style src="./shared-panel.css"></style>

<style scoped>
.system-health-module {
  max-width: 1420px;
  font-family: Arial, sans-serif;
  color: #111827;
}

.health-menu {
  position: relative;
  z-index: 30;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin: 0 0 2px;
  padding: 4px 0 6px;
  overflow-x: auto;
}

.health-menu button {
  flex: 0 0 auto;
  border: 1px solid #b9c6d6;
  border-radius: 4px;
  background: #fff;
  color: #007bff;
  padding: 9px 14px;
  font: inherit;
  cursor: pointer;
}

.health-menu button.active {
  border-color: #007bff;
  color: #000;
}

.health-menu button.critical {
  background: #dc3545;
  border-color: #b42318;
  color: #fff;
}

.health-loading-bar {
  position: relative;
  width: 100%;
  height: 4px;
  margin: -10px 0 14px;
  overflow: hidden;
  background: #e5e7eb;
}

.health-loading-progress {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  clip-path: inset(50%);
  white-space: nowrap;
}

.health-loading-indicator {
  position: absolute;
  inset: 0 auto 0 0;
  width: 42%;
  background: #007bff;
  animation: health-loading-slide 1s ease-in-out infinite;
}

@keyframes health-loading-slide {
  0% {
    transform: translateX(-100%);
  }

  100% {
    transform: translateX(340%);
  }
}

.health-content {
  position: relative;
  z-index: 0;
  max-width: 1420px;
}

.error-banner {
  border: 1px solid #dc3545;
  background: #fde8e8;
  color: #a40000;
  padding: 10px 12px;
  margin-bottom: 12px;
}
</style>
