import { mount, flushPromises } from '@vue/test-utils'
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import App from './App.vue'

const application = { key: 'my-life-story-vault', label: 'My Life Story Vault' }

function jsonResponse(body: unknown) {
  return Promise.resolve(new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' }
  }))
}

function responseFor(url: string) {
  if (url.startsWith('/api/system-health/critical-events')) {
    return jsonResponse({ sections: [] })
  }

  if (url.startsWith('/api/system-health/code-quality-security')) {
    return jsonResponse({
      status: 'Warning',
      statusDetail: 'Provider configuration is required at runtime.',
      selectedApplicationKey: 'my-life-story-vault',
      selectedEnvironment: 'Development',
      applications: [application],
      environments: ['Development'],
      providerStatuses: [
        { provider: 'MSSQL', status: 'Unavailable', detail: 'MSSQL is intentionally not used.' }
      ],
      sonarMetrics: [],
      gitHubSeverityCounts: [],
      gitHubCodeScanningSeverityCounts: [],
      gitHubSecretScanningCounts: [],
      dependencyCheckSeverityCounts: [],
      lintStatus: 'Unavailable',
      dependencyCheckStatus: 'Unavailable',
      cycloneDxStatus: 'Unavailable',
      playwrightStatus: 'Unavailable'
    })
  }

  return jsonResponse({
    status: 'Warning',
    statusDetail: 'Runtime provider not configured.',
    selectedApplicationKey: 'my-life-story-vault',
    selectedEnvironment: 'Development',
    applications: [application],
    environments: ['Development']
  })
}

describe('standalone SystemHealth app', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => responseFor(String(input))))
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('lands directly on System Health without login chrome', async () => {
    const wrapper = mount(App)
    await flushPromises()

    expect(wrapper.text()).toContain('Code Quality & Security')
    expect(wrapper.text()).toContain('My Life Story Vault')
    expect(wrapper.find('input[type="password"]').exists()).toBe(false)
  })

  it('requests only the MyLifeStoryVault application scope by default', async () => {
    mount(App)
    await flushPromises()

    const urls = (fetch as unknown as ReturnType<typeof vi.fn>).mock.calls.map(([url]) => String(url))
    expect(urls.some(url => url.includes('/api/system-health/code-quality-security'))).toBe(true)
    expect(urls.some(url => url.includes('applicationKey=my-life-story-vault'))).toBe(true)
    expect(urls.every(url => !url.includes('applicationKey=') || url.includes('applicationKey=my-life-story-vault'))).toBe(true)
  })
})
