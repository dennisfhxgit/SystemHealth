import { onMounted, ref, watch } from 'vue'

export type SectionKey =
  | 'codeQuality'
  | 'jenkinsLogs'
  | 'testResults'
  | 'aiCodeAnalysis'
  | 'systemAlerts'
  | 'artifactHistory'
  | 'adminEnvironment'
  | 'emailWorkers'

export type ApiRecord = Record<string, any>

export type HealthSectionState = {
  key: SectionKey
  label: string
  status: string
  detail: string
}

export type HealthChildPageProps = {
  refreshToken: number
}

export type HealthChildPageEmits = {
  stateChange: [state: HealthSectionState]
  loadingChange: [loading: boolean]
  errorChange: [message: string]
}

type HealthChildPageEmit = {
  (event: 'stateChange', state: HealthSectionState): void
  (event: 'loadingChange', loading: boolean): void
  (event: 'errorChange', message: string): void
}

export type HealthEnvironmentFilter = {
  application: string
  environment: string
}

export type Metric = {
  label: string
  value: unknown
}

const productionEnvironment = 'Production'
const developmentEnvironment = 'Development'
const defaultHealthEnvironments = [developmentEnvironment, productionEnvironment]
const minimumLoadingDurationMs = 350
const unauthorizedMessage = 'Request was not authorized by the provider.'

export class HealthApiError extends Error {
  readonly status: number

  constructor(url: string, status: number) {
    super(status === 401 ? unauthorizedMessage : `${url} returned ${status}`)
    this.name = 'HealthApiError'
    this.status = status
  }
}

export function endpoint(path: string, params?: Record<string, string | number>) {
  const query = new URLSearchParams({ t: String(Date.now()) })
  for (const [key, value] of Object.entries(params ?? {})) {
    if (value !== '') {
      query.set(key, String(value))
    }
  }
  return `${path}?${query.toString()}`
}

export async function fetchHealthJson(url: string): Promise<ApiRecord> {
  const response = await fetch(url, {
    cache: 'no-store',
    headers: { Accept: 'application/json' }
  })

  if (!response.ok) {
    throw new HealthApiError(url, response.status)
  }

  return await response.json() as ApiRecord
}

function stringValue(value: unknown, fallback = '') {
  if (value === null || value === undefined) return fallback
  if (typeof value === 'string') return value
  if (typeof value === 'number' || typeof value === 'boolean' || typeof value === 'bigint') return `${value}`
  if (typeof value === 'symbol' || typeof value === 'function') return value.toString()
  if (Array.isArray(value)) return value.join(',')
  if (value instanceof Date || value instanceof Error || value instanceof RegExp || value instanceof URL) return value.toString()
  return Object.prototype.toString.call(value)
}

export function statusClass(status: unknown) {
  return stringValue(status).toLowerCase().replace(/[^a-z0-9]+/g, '-')
}

export function isCriticalStatus(status: unknown) {
  return ['critical', 'failure', 'failed', 'error'].includes(statusClass(status))
}

export function metric(label: string, value: unknown): Metric {
  return { label, value: value ?? '-' }
}

export function percent(value: unknown) {
  return typeof value === 'number' ? `${value}%` : '-'
}

export function formatDateTime(value: unknown) {
  if (!value) return '-'
  const originalValue = stringValue(value)
  const date = new Date(originalValue)
  if (Number.isNaN(date.getTime())) return originalValue
  return new Intl.DateTimeFormat('en-NZ', {
    dateStyle: 'short',
    timeStyle: 'short'
  }).format(date)
}

export function formatSeconds(value: unknown) {
  if (typeof value !== 'number') return '-'
  return value >= 60 && value % 60 === 0 ? `${value / 60} min` : `${value} sec`
}

export function displayValue(value: unknown) {
  return stringValue(value, '-')
}

type HealthChildPageOptions = {
  props: HealthChildPageProps
  emit: HealthChildPageEmit
  key: SectionKey
  label: string
  path: string
  params?: () => Record<string, string | number>
  status?: (result: ApiRecord) => unknown
  detail?: (result: ApiRecord) => unknown
  afterLoad?: (result: ApiRecord) => void
}

export function healthEnvironments(data: ApiRecord | null) {
  const environments = data?.environments as string[] | undefined
  const source = environments?.length ? environments : defaultHealthEnvironments
  return Array.from(new Set(source.map(canonicalHealthEnvironment)))
}

export function healthEnvironmentLabel(environment: string) {
  return canonicalHealthEnvironment(environment)
}

export function canonicalHealthEnvironment(environment: unknown) {
  const value = typeof environment === 'string' ? environment.trim() : ''
  if (value.toLowerCase() === 'prod' || value.toLowerCase() === productionEnvironment.toLowerCase()) {
    return productionEnvironment
  }

  if (value.toLowerCase() === 'dev' || value.toLowerCase() === developmentEnvironment.toLowerCase()) {
    return developmentEnvironment
  }

  return value
}

export function healthEnvironmentOptions(data: ApiRecord | null) {
  return healthEnvironments(data).map(environment => ({
    value: environment,
    label: healthEnvironmentLabel(environment)
  }))
}

export function useHealthChildPage(options: HealthChildPageOptions) {
  const data = ref<ApiRecord | null>(null)
  let requestId = 0

  async function load() {
    const activeRequestId = ++requestId
    const loadingStartedAt = Date.now()
    options.emit('loadingChange', true)
    options.emit('errorChange', '')
    try {
      const result = await fetchHealthJson(endpoint(options.path, options.params?.()))
      if (activeRequestId !== requestId) return
      data.value = result
      options.afterLoad?.(result)
      options.emit('stateChange', {
        key: options.key,
        label: options.label,
        status: stringValue(options.status ? options.status(result) : result.status),
        detail: stringValue(options.detail ? options.detail(result) : result.statusDetail)
      })
    } catch (error) {
      if (activeRequestId !== requestId) return
      options.emit('errorChange', error instanceof HealthApiError && error.status === 401
        ? error.message
        : `Unable to load ${options.label}.`)
    } finally {
      if (activeRequestId === requestId) {
        const remainingLoadingMs = minimumLoadingDurationMs - (Date.now() - loadingStartedAt)
        if (remainingLoadingMs > 0) {
          await delay(remainingLoadingMs)
        }

        if (activeRequestId === requestId) {
          options.emit('loadingChange', false)
        }
      }
    }
  }

  onMounted(load)
  watch(() => options.props.refreshToken, load)

  return { data, load }
}

function delay(milliseconds: number) {
  return new Promise(resolve => window.setTimeout(resolve, milliseconds))
}

export function useFilteredHealthChildPage(options: Omit<HealthChildPageOptions, 'params'> & {
  filter: HealthEnvironmentFilter
  extraParams?: () => Record<string, string | number>
}) {
  const userAfterLoad = options.afterLoad
  return useHealthChildPage({
    ...options,
    params: () => ({
      applicationKey: options.filter.application,
      environment: canonicalHealthEnvironment(options.filter.environment),
      ...options.extraParams?.()
    }),
    afterLoad: result => {
      options.filter.application = resolveSelectedFilterValue(
        options.filter.application,
        result.selectedApplicationKey,
        result.applications,
        'key')
      options.filter.environment = resolveSelectedFilterValue(
        canonicalHealthEnvironment(options.filter.environment),
        canonicalHealthEnvironment(result.selectedEnvironment),
        healthEnvironments(result))
      userAfterLoad?.(result)
    }
  })
}

function resolveSelectedFilterValue(
  currentValue: string,
  responseValue: unknown,
  options: unknown,
  optionKey?: string) {
  const availableValues = filterOptionValues(options, optionKey)
  if (currentValue && (availableValues.length === 0 || availableValues.includes(currentValue))) {
    return currentValue
  }

  return typeof responseValue === 'string' && responseValue ? responseValue : currentValue
}

function filterOptionValues(options: unknown, optionKey?: string) {
  if (!Array.isArray(options)) return []
  return options
    .map(option => {
      if (typeof option === 'string') return option
      if (optionKey && option && typeof option === 'object' && optionKey in option) {
        const value = (option as Record<string, unknown>)[optionKey]
        return typeof value === 'string' ? value : ''
      }

      return ''
    })
    .filter(value => value !== '')
}
