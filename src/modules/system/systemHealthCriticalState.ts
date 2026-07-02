import { computed, ref } from 'vue'

export type CriticalHealthSection = {
  key: string
  label: string
  status: string
  detail?: string
}

const criticalSections = ref<CriticalHealthSection[]>([])

export const isSystemHealthCritical = computed(() => criticalSections.value.length > 0)

export function getSystemHealthCriticalSections() {
  return criticalSections.value
}

export function setSystemHealthCriticalSections(sections: CriticalHealthSection[]) {
  criticalSections.value = normalizeSections(sections)
}

export function updateSystemHealthCriticalSection(section: CriticalHealthSection) {
  const normalized = normalizeSections([section])[0]
  const next = criticalSections.value.filter(current => current.key !== section.key)
  if (normalized && isCriticalStatus(normalized.status)) {
    next.push(normalized)
  }

  criticalSections.value = next
}

export async function loadSystemHealthCriticalSections() {
  const response = await fetch('/api/system-health/critical-events', {
    cache: 'no-store',
    headers: { Accept: 'application/json' }
  })

  if (!response.ok) {
    criticalSections.value = []
    return
  }

  const body = await response.json() as { sections?: CriticalHealthSection[] }
  setSystemHealthCriticalSections(body.sections ?? [])
}

export async function reportSystemHealthCriticalSections(sections: CriticalHealthSection[]) {
  setSystemHealthCriticalSections(sections)
  await fetch('/api/system-health/critical-events', {
    method: 'POST',
    cache: 'no-store',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({ sections: criticalSections.value })
  }).catch(() => undefined)
}

function normalizeSections(sections: CriticalHealthSection[]) {
  return sections
    .filter(section => section.key && isCriticalStatus(section.status))
    .map(section => ({
      key: section.key,
      label: section.label || section.key,
      status: section.status,
      detail: section.detail ?? ''
    }))
}

function isCriticalStatus(status: string) {
  return ['critical', 'failure', 'failed', 'error'].includes(status.toLowerCase())
}
