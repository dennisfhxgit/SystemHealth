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
        { provider: 'SonarQube', status: 'Unavailable', detail: 'Runtime SonarQube configuration is required.' },
        { provider: 'GitHub Dependabot', status: 'Unavailable', detail: 'Runtime GitHub token is required.' },
        { provider: 'GitHub CodeQL', status: 'Unavailable', detail: 'Runtime GitHub token is required.' },
        { provider: 'GitHub Secret Scanning', status: 'Unavailable', detail: 'Runtime GitHub token is required.' }
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

  if (url.startsWith('/api/system-health/artifact-history')) {
    return jsonResponse({
      status: 'Healthy',
      statusDetail: 'Jenkins artifact history loaded.',
      selectedApplicationKey: 'my-life-story-vault',
      selectedEnvironment: 'Development',
      selectedBuildCount: 1,
      jobName: 'SystemHealth',
      applications: [application],
      environments: ['Development'],
      buildCounts: [1, 10, 30],
      artifacts: [
        {
          fileName: 'index.html',
          displayName: 'index.html',
          relativePath: '_rollback/25/website-current/index.html',
          artifactType: 'Rollback Snapshot',
          isRollbackArtifact: true,
          size: '1 KB',
          created: '2026-07-02T10:00:00Z',
          buildNumber: '25',
          downloadUrl: 'https://jenkins.fhx.co.nz/job/SystemHealth/25/artifact/_rollback/25/website-current/index.html',
          logUrl: 'https://jenkins.fhx.co.nz/job/SystemHealth/25/'
        },
        {
          fileName: 'tests.junit.xml',
          displayName: 'tests.junit.xml',
          relativePath: 'TestResults/tests.junit.xml',
          artifactType: 'Build Artifact',
          isRollbackArtifact: false,
          size: '1 KB',
          created: '2026-07-02T10:00:00Z',
          buildNumber: '25',
          downloadUrl: 'https://jenkins.fhx.co.nz/job/SystemHealth/25/artifact/TestResults/tests.junit.xml',
          logUrl: 'https://jenkins.fhx.co.nz/job/SystemHealth/25/'
        }
      ]
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

  it('renders rollback snapshots separately from built artifacts', async () => {
    const wrapper = mount(App)
    await flushPromises()

    const artifactButton = wrapper.findAll('button').find(button => button.text() === 'Artifact History')
    expect(artifactButton).toBeTruthy()
    await artifactButton!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Rollback Files: 1')
    expect(wrapper.text()).toContain('Rollback Snapshots: 1')
    expect(wrapper.text()).toContain('Build Artifacts: 1')
    expect(wrapper.text()).toContain('_rollback/25/website-current/index.html')
    expect(wrapper.text()).toContain('TestResults/tests.junit.xml')
  })
})
