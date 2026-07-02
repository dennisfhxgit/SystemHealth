<template>
  <section class="health-panel code-quality-security-page">
    <div class="code-quality-toolbar">
      <p class="last-updated">Last Updated: <span>{{ lastUpdated }}</span></p>
      <div class="filter-row compact-filter-row">
        <label>
          <span>Environment</span>
          <select v-model="filter.environment" @change="load">
            <option v-for="environment in environments" :key="environment.value" :value="environment.value">{{ environment.label }}</option>
          </select>
        </label>
        <label>
          <span>Application</span>
          <select v-model="filter.application" @change="load">
            <option v-for="app in applications" :key="app.key" :value="app.key">{{ app.label }}</option>
          </select>
        </label>
      </div>
    </div>
    <div v-if="displayStatusDetail" class="status-line">
      <span v-if="data?.status" :class="['deployment-status-pill', statusClass(data.status)]">{{ data.status }}</span>
      <span>{{ displayStatusDetail }}</span>
    </div>

    <section class="quality-section github-scanner-section sonar-code-analysis-section">
      <h2>{{ codeAnalysisHeading }}</h2>
      <p v-if="sonarProviderWarning" class="provider-state-line sonar-provider-state-line">{{ sonarProviderWarning }}</p>
      <div class="inline-facts sonar-analysis-facts">
        <span>Project: {{ selectedCodeQualityProjectLabel }}</span>
        <span>Analysis: {{ sonarAnalysisVersion }}</span>
        <span>Run: {{ formatCodeQualityDate(data?.sonarAnalysisDateUtc) }}</span>
        <span>Revision: {{ formatCommitSha(data?.sonarAnalysisRevision) }}</span>
      </div>
      <div class="metric-grid quality-metric-grid">
        <article v-for="item in sonarMetrics" :key="item.label" :class="['metric-card', 'status-metric-card', item.status]">
          <strong>{{ item.value }}</strong>
          <span>{{ item.label }}</span>
        </article>
      </div>

      <div class="table-section github-scanner-details sonar-code-analysis-details detail-disclosure">
        <div class="table-heading-row">
          <button type="button" class="detail-toggle" :aria-expanded="isDetailSectionExpanded('sonar-vulnerabilities')" aria-controls="sonar-vulnerabilities-details" @click="toggleDetailSection('sonar-vulnerabilities')">
            <span class="detail-toggle-icon">{{ detailToggleIcon('sonar-vulnerabilities') }}</span>
            <h3>SonarQube Vulnerabilities Details ({{ sonarVulnerabilityDetailLabel }})</h3>
          </button>
        </div>
        <div v-if="showExpandedDetailTable('sonar-vulnerabilities', sonarVulnerabilityDetailCount)" id="sonar-vulnerabilities-details" class="table-wrap">
          <table>
            <thead>
              <tr><th>Severity</th><th>Message</th><th>Component</th><th>Line</th><th>View</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in sonarVulnerabilities" :key="readRecordValue(row, 'id') || readRecordValue(row, 'key') || index">
                <td><span :class="['deployment-status-pill', sonarSeverityClass(row)]">{{ sonarSeverityLabel(row) }}</span></td>
                <td>{{ resultValue(row, 'message') }}</td>
                <td>{{ resultValue(row, 'component') }}</td>
                <td>{{ resultValue(row, 'line') }}</td>
                <td><a v-if="readRecordValue(row, 'url')" :href="readRecordValue(row, 'url')" target="_blank" rel="noopener noreferrer">View</a><span v-else>Not available</span></td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <div class="table-section github-scanner-details sonar-code-analysis-details detail-disclosure">
        <div class="table-heading-row">
          <button type="button" class="detail-toggle" :aria-expanded="isDetailSectionExpanded('sonar-bugs')" aria-controls="sonar-bugs-details" @click="toggleDetailSection('sonar-bugs')">
            <span class="detail-toggle-icon">{{ detailToggleIcon('sonar-bugs') }}</span>
            <h3>SonarQube Bug Details ({{ sonarBugDetailLabel }})</h3>
          </button>
        </div>
        <div v-if="showExpandedDetailTable('sonar-bugs', sonarBugDetailCount)" id="sonar-bugs-details" class="table-wrap">
          <table>
            <thead>
              <tr><th>Severity</th><th>Message</th><th>Component</th><th>Line</th><th>View</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in sonarBugs" :key="readRecordValue(row, 'id') || readRecordValue(row, 'key') || index">
                <td><span :class="['deployment-status-pill', sonarSeverityClass(row)]">{{ sonarSeverityLabel(row) }}</span></td>
                <td>{{ resultValue(row, 'message') }}</td>
                <td>{{ resultValue(row, 'component') }}</td>
                <td>{{ resultValue(row, 'line') }}</td>
                <td><a v-if="readRecordValue(row, 'url')" :href="readRecordValue(row, 'url')" target="_blank" rel="noopener noreferrer">View</a><span v-else>Not available</span></td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <div class="table-section github-scanner-details sonar-code-analysis-details detail-disclosure">
        <div class="table-heading-row">
          <button type="button" class="detail-toggle" :aria-expanded="isDetailSectionExpanded('sonar-code-smells')" aria-controls="sonar-code-smells-details" @click="toggleDetailSection('sonar-code-smells')">
            <span class="detail-toggle-icon">{{ detailToggleIcon('sonar-code-smells') }}</span>
            <h3>SonarQube Code Smell Details ({{ sonarCodeSmellDetailLabel }})</h3>
          </button>
        </div>
        <div v-if="showExpandedDetailTable('sonar-code-smells', sonarCodeSmellDetailCount)" id="sonar-code-smells-details" class="table-wrap">
          <table>
            <thead>
              <tr><th>Severity</th><th>Message</th><th>Component</th><th>Line</th><th>View</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in sonarCodeSmells" :key="readRecordValue(row, 'id') || readRecordValue(row, 'key') || index">
                <td><span :class="['deployment-status-pill', sonarSeverityClass(row)]">{{ sonarSeverityLabel(row) }}</span></td>
                <td>{{ resultValue(row, 'message') }}</td>
                <td>{{ resultValue(row, 'component') }}</td>
                <td>{{ resultValue(row, 'line') }}</td>
                <td><a v-if="readRecordValue(row, 'url')" :href="readRecordValue(row, 'url')" target="_blank" rel="noopener noreferrer">View</a><span v-else>Not available</span></td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </section>

    <section class="quality-section github-scanner-section lint-standards-section">
      <h2>Lint & Standards</h2>
      <p class="section-summary">{{ lintSummary }}</p>
      <p v-if="showArtifactProviderDetail('Lint & Standards')" class="provider-state-line">{{ providerUnavailableDetail('Lint & Standards') }}</p>

      <div class="inline-facts lint-standards-facts">
        <span>Application: {{ selectedApplicationLabel }}</span>
        <span>Environment: {{ filter.environment }}</span>
        <span>Report: {{ formatCodeQualityDate(data?.lintGeneratedAtUtc) }}</span>
      </div>

      <div class="metric-grid lint-standards-grid">
        <article v-for="item in lintMetrics" :key="item.label" :class="['metric-card', 'status-metric-card', item.status]">
          <strong>{{ item.value }}</strong>
          <span>{{ item.label }}</span>
        </article>
      </div>

      <div class="table-section github-scanner-details lint-standards-details detail-disclosure">
        <div class="table-heading-row">
          <button type="button" class="detail-toggle" :aria-expanded="isDetailSectionExpanded('lint-standards')" aria-controls="lint-standards-details" @click="toggleDetailSection('lint-standards')">
            <span class="detail-toggle-icon">{{ detailToggleIcon('lint-standards') }}</span>
            <h3>Lint & Standards Details ({{ lintFindingDetailLabel }})</h3>
          </button>
        </div>
        <div v-if="showExpandedDetailTable('lint-standards', lintFindingCount)" id="lint-standards-details" class="table-wrap">
          <table>
            <thead>
              <tr><th>Tool</th><th>Severity</th><th>Rule</th><th>File</th><th>Line</th><th>Message</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in lintFindings" :key="`${lintFindingValue(row, 'tool')}-${lintFindingValue(row, 'ruleId')}-${lintFindingValue(row, 'file')}-${lintFindingValue(row, 'line')}-${index}`">
                <td>{{ lintFindingValue(row, 'tool') }}</td>
                <td><span :class="['deployment-status-pill', lintSeverityClass(row)]">{{ lintSeverityLabel(row) }}</span></td>
                <td>{{ lintFindingValue(row, 'ruleId') }}</td>
                <td>{{ lintFindingValue(row, 'file') }}</td>
                <td>{{ lintFindingValue(row, 'line') }}</td>
                <td>{{ lintFindingValue(row, 'message', 'rawSummary') }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </section>

    <section class="quality-section github-scanner-section dependabot-scanner-section">
      <h2>Dependabot Vulnerability Alerts</h2>
      <p class="section-summary">{{ dependabotSummary }}</p>
      <p v-if="!isProviderAvailable('GitHub Dependabot')" class="provider-state-line">{{ providerUnavailableDetail('GitHub Dependabot') }}</p>
      <div class="metric-grid github-security-grid">
        <article v-for="item in githubSecurityMetrics" :key="item.label" :class="['metric-card', 'status-metric-card', item.status]">
          <strong>{{ item.value }}</strong>
          <span>{{ item.label }}</span>
        </article>
      </div>

      <div class="table-section github-scanner-details detail-disclosure">
        <div class="table-heading-row">
          <button type="button" class="detail-toggle" :aria-expanded="isDetailSectionExpanded('dependabot')" aria-controls="dependabot-details" @click="toggleDetailSection('dependabot')">
            <span class="detail-toggle-icon">{{ detailToggleIcon('dependabot') }}</span>
            <h3>Dependabot Vulnerability Details ({{ detailCountLabel(dependabotDetailCount) }})</h3>
          </button>
          <a v-if="data?.gitHubDashboardUrl" :href="data.gitHubDashboardUrl" target="_blank" rel="noopener noreferrer">Open GitHub Security</a>
        </div>
        <div v-if="showExpandedDetailTable('dependabot', dependabotDetailCount)" id="dependabot-details" class="table-wrap">
          <table>
            <thead>
              <tr><th>Severity</th><th>Identifier</th><th>Summary</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in data?.gitHubAlerts || []" :key="row.identifier ?? index">
                <td><span :class="['deployment-status-pill', statusClass(row.severity)]">{{ row.severity ?? '-' }}</span></td>
                <td>{{ row.identifier ?? '-' }}</td>
                <td>{{ row.summary ?? '-' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </section>

    <section class="quality-section github-scanner-section codeql-scanner-section">
      <h2>Github CodeQL Alerts</h2>
      <p class="section-summary">{{ codeQlSummary }}</p>
      <p v-if="data?.gitHubCodeScanningRef" class="section-summary">CodeQL ref: {{ data.gitHubCodeScanningRef }}</p>
      <p v-if="!isProviderAvailable('GitHub CodeQL')" class="provider-state-line">{{ providerUnavailableDetail('GitHub CodeQL') }}</p>
      <div class="metric-grid github-codeql-grid">
        <article v-for="item in githubCodeQlMetrics" :key="item.label" :class="['metric-card', 'status-metric-card', item.status]">
          <strong>{{ item.value }}</strong>
          <span>{{ item.label }}</span>
        </article>
      </div>

      <div class="table-section github-scanner-details detail-disclosure">
        <div class="table-heading-row">
          <button type="button" class="detail-toggle" :aria-expanded="isDetailSectionExpanded('codeql')" aria-controls="codeql-details" @click="toggleDetailSection('codeql')">
            <span class="detail-toggle-icon">{{ detailToggleIcon('codeql') }}</span>
            <h3>GitHub CodeQL Details ({{ detailCountLabel(codeQlDetailCount) }})</h3>
          </button>
          <a v-if="data?.gitHubDashboardUrl" :href="data.gitHubDashboardUrl" target="_blank" rel="noopener noreferrer">Open GitHub Security</a>
        </div>
        <div v-if="showExpandedDetailTable('codeql', codeQlDetailCount)" id="codeql-details" class="table-wrap">
          <table>
            <thead>
              <tr><th>Severity</th><th>Rule</th><th>Message</th><th>File</th><th>Line</th><th>Ref</th><th>Commit</th><th>View</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in data?.gitHubCodeScanningAlerts || []" :key="row.url ?? row.ruleId ?? index">
                <td><span :class="['deployment-status-pill', statusClass(row.severity)]">{{ row.severity ?? '-' }}</span></td>
                <td>{{ row.ruleId ?? '-' }}</td>
                <td>{{ row.message ?? '-' }}</td>
                <td>{{ row.path ?? '-' }}</td>
                <td>{{ row.line ?? '-' }}</td>
                <td>{{ row.ref ?? '-' }}</td>
                <td>{{ formatCommitSha(row.commitSha) }}</td>
                <td><a v-if="row.url" :href="row.url" target="_blank" rel="noopener noreferrer">View</a><span v-else>Not available</span></td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </section>

    <section class="quality-section github-scanner-section secret-scanner-section">
      <h2>GitHub Secret Scanning Alerts</h2>
      <p class="section-summary">{{ secretScanningSummary }}</p>
      <p v-if="!isProviderAvailable('GitHub Secret Scanning')" class="provider-state-line">{{ providerUnavailableDetail('GitHub Secret Scanning') }}</p>
      <div class="metric-grid github-secret-scanning-grid">
        <article v-for="item in githubSecretScanningMetrics" :key="item.label" :class="['metric-card', 'status-metric-card', item.status]">
          <strong>{{ item.value }}</strong>
          <span>{{ item.label }}</span>
        </article>
      </div>

      <div class="table-section github-scanner-details detail-disclosure">
        <div class="table-heading-row">
          <button type="button" class="detail-toggle" :aria-expanded="isDetailSectionExpanded('secret-scanning')" aria-controls="secret-scanning-details" @click="toggleDetailSection('secret-scanning')">
            <span class="detail-toggle-icon">{{ detailToggleIcon('secret-scanning') }}</span>
            <h3>Secret Scanning Details ({{ detailCountLabel(secretScanningDetailCount) }})</h3>
          </button>
          <a v-if="data?.gitHubDashboardUrl" :href="data.gitHubDashboardUrl" target="_blank" rel="noopener noreferrer">Open GitHub Security</a>
        </div>
        <div v-if="showExpandedDetailTable('secret-scanning', secretScanningDetailCount)" id="secret-scanning-details" class="table-wrap">
          <table>
            <thead>
              <tr><th>Pattern</th><th>Type</th><th>State</th><th>Created</th><th>View</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in data?.gitHubSecretScanningAlerts || []" :key="row.url ?? row.secretType ?? index">
                <td>{{ formatSecretPattern(row.pattern) }}</td>
                <td>{{ row.secretTypeDisplayName ?? row.secretType ?? '-' }}</td>
                <td><span :class="['deployment-status-pill', statusClass(row.state)]">{{ row.state ?? '-' }}</span></td>
                <td>{{ formatCodeQualityDate(row.createdAtUtc) }}</td>
                <td><a v-if="row.url" :href="row.url" target="_blank" rel="noopener noreferrer">View</a><span v-else>Not available</span></td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </section>

    <section class="quality-section github-scanner-section cyclone-dx-section">
      <h2>CycloneDX SBOM Results</h2>
      <p class="section-summary">{{ cycloneDxSummary }}</p>
      <p v-if="showArtifactProviderDetail(cycloneDxProviderName)" class="provider-state-line">{{ providerUnavailableDetail(cycloneDxProviderName) }}</p>

      <div class="inline-facts cyclone-dx-facts">
        <span>Format: {{ data?.cycloneDxBomFormat || '-' }}</span>
        <span>Spec: {{ data?.cycloneDxSpecVersion || '-' }}</span>
        <span>Generated: {{ formatCodeQualityDate(data?.cycloneDxGeneratedAtUtc) }}</span>
        <span>Serial: {{ data?.cycloneDxSerialNumber || '-' }}</span>
      </div>

      <div class="metric-grid cyclone-dx-grid">
        <article v-for="item in cycloneDxMetrics" :key="item.label" :class="['metric-card', 'status-metric-card', item.status]">
          <strong>{{ item.value }}</strong>
          <span>{{ item.label }}</span>
        </article>
      </div>

      <div class="table-section github-scanner-details cyclone-dx-details detail-disclosure">
        <div class="table-heading-row">
          <button type="button" class="detail-toggle" :aria-expanded="isDetailSectionExpanded('cyclone-dx-components')" aria-controls="cyclone-dx-components-details" @click="toggleDetailSection('cyclone-dx-components')">
            <span class="detail-toggle-icon">{{ detailToggleIcon('cyclone-dx-components') }}</span>
            <h3>CycloneDX Component Details ({{ detailCountLabel(cycloneDxComponentCount) }})</h3>
          </button>
        </div>
        <div v-if="showExpandedDetailTable('cyclone-dx-components', cycloneDxComponentCount)" id="cyclone-dx-components-details" class="table-wrap">
          <table>
            <thead>
              <tr><th>Type</th><th>Name</th><th>Version</th><th>Scope</th><th>Package URL</th><th>BOM Ref</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in cycloneDxComponents" :key="cycloneDxComponentKey(row, index)">
                <td>{{ resultValue(row, 'type') }}</td>
                <td>{{ resultValue(row, 'name') }}</td>
                <td>{{ resultValue(row, 'version') }}</td>
                <td>{{ resultValue(row, 'scope') }}</td>
                <td>{{ resultValue(row, 'packageUrl') }}</td>
                <td>{{ resultValue(row, 'bomRef') }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </section>

    <section class="quality-section github-scanner-section code-quality-test-results-section">
      <h2>Code Quality Test Results</h2>
      <p class="section-summary">{{ testResultsSummary }}</p>
      <div class="status-line compact-status-line">
        <span v-if="testResultsData?.status" :class="['deployment-status-pill', statusClass(testResultsData.status)]">{{ testResultsData.status }}</span>
        <span v-if="testResultsData?.statusDetail">{{ testResultsData.statusDetail }}</span>
        <span v-else-if="testResultsLoading">Loading Jenkins test results.</span>
        <span v-else-if="testResultsError">{{ testResultsError }}</span>
      </div>

      <div class="inline-facts test-result-facts">
        <span>Job: {{ testResultsData?.jobName || 'Not configured' }}</span>
        <span>Build: {{ selectedTestBuildLabel }}</span>
        <span>Application: {{ selectedApplicationLabel }}</span>
        <span>Environment: {{ filter.environment }}</span>
      </div>

      <div class="metric-grid code-quality-test-results-grid">
        <article v-for="item in testResultMetrics" :key="item.label" :class="['metric-card', 'status-metric-card', item.status]">
          <strong>{{ item.value }}</strong>
          <span>{{ item.label }}</span>
        </article>
      </div>

      <div class="table-section github-scanner-details code-quality-test-results-details detail-disclosure">
        <div class="table-heading-row">
          <button type="button" class="detail-toggle" :aria-expanded="isDetailSectionExpanded('api-functional-results')" aria-controls="api-functional-results-details" @click="toggleDetailSection('api-functional-results')">
            <span class="detail-toggle-icon">{{ detailToggleIcon('api-functional-results') }}</span>
            <h3>Failed or Skipped API Functional Results ({{ detailCountLabel(apiFunctionalTestCount) }})</h3>
          </button>
        </div>
        <div v-if="showExpandedDetailTable('api-functional-results', apiFunctionalTestCount)" id="api-functional-results-details" class="table-wrap">
          <table>
            <thead>
              <tr><th>Test</th><th>Suite</th><th>Status</th><th>Duration</th><th>Commit</th><th>Branch</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in apiFunctionalProblemResults" :key="testResultRowKey(row, index, 'api')">
                <td>{{ resultValue(row, 'name') }}</td>
                <td>{{ resultValue(row, 'suite') }}</td>
                <td><span :class="['deployment-status-pill', resultStatusClass(row)]">{{ resultStatusLabel(row) }}</span></td>
                <td>{{ resultValue(row, 'duration') }}</td>
                <td>{{ resultValue(row, 'commit') }}</td>
                <td>{{ resultValue(row, 'branch') }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <div class="table-section github-scanner-details code-quality-test-results-details detail-disclosure">
        <div class="table-heading-row">
          <button type="button" class="detail-toggle" :aria-expanded="isDetailSectionExpanded('ui-test-results')" aria-controls="ui-test-results-details" @click="toggleDetailSection('ui-test-results')">
            <span class="detail-toggle-icon">{{ detailToggleIcon('ui-test-results') }}</span>
            <h3>Failed or Skipped UI Test Results ({{ detailCountLabel(uiTestCount) }})</h3>
          </button>
        </div>
        <div v-if="showExpandedDetailTable('ui-test-results', uiTestCount)" id="ui-test-results-details" class="table-wrap">
          <table>
            <thead>
              <tr><th>Scenario</th><th>Step</th><th>Browser</th><th>Status</th><th>Duration</th><th>Screenshot</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in uiProblemResults" :key="testResultRowKey(row, index, 'ui')">
                <td>{{ resultValue(row, 'scenario') }}</td>
                <td>{{ resultValue(row, 'step') }}</td>
                <td>{{ resultValue(row, 'browser') }}</td>
                <td><span :class="['deployment-status-pill', resultStatusClass(row)]">{{ resultStatusLabel(row) }}</span></td>
                <td>{{ resultDuration(row) }}</td>
                <td><a v-if="readRecordValue(row, 'screenshot')" :href="readRecordValue(row, 'screenshot')" target="_blank" rel="noopener noreferrer">Open</a><span v-else>-</span></td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </section>

    <section class="quality-section github-scanner-section dependency-check-section">
      <h2>OWASP Dependency-Check</h2>
      <p class="section-summary">{{ dependencyCheckSummary }}</p>
      <p v-if="showArtifactProviderDetail('OWASP Dependency-Check')" class="provider-state-line">{{ providerUnavailableDetail('OWASP Dependency-Check') }}</p>

      <div class="inline-facts dependency-check-facts">
        <span>Project: {{ data?.dependencyCheckProjectName || selectedApplicationLabel }}</span>
        <span>Engine: {{ data?.dependencyCheckEngineVersion || '-' }}</span>
        <span>Dependencies: {{ data?.dependencyCheckDependenciesScanned ?? 0 }}</span>
        <span>Report: {{ formatCodeQualityDate(data?.dependencyCheckReportDateUtc) }}</span>
      </div>

      <div class="metric-grid dependency-check-grid">
        <article v-for="item in dependencyCheckMetrics" :key="item.label" :class="['metric-card', 'status-metric-card', item.status]">
          <strong>{{ item.value }}</strong>
          <span>{{ item.label }}</span>
        </article>
      </div>

      <div class="table-section github-scanner-details dependency-check-details detail-disclosure">
        <div class="table-heading-row">
          <button type="button" class="detail-toggle" :aria-expanded="isDetailSectionExpanded('dependency-check')" aria-controls="dependency-check-details" @click="toggleDetailSection('dependency-check')">
            <span class="detail-toggle-icon">{{ detailToggleIcon('dependency-check') }}</span>
            <h3>OWASP Dependency-Check Details ({{ detailCountLabel(dependencyCheckFindingCount) }})</h3>
          </button>
        </div>
        <div v-if="showExpandedDetailTable('dependency-check', dependencyCheckFindingCount)" id="dependency-check-details" class="table-wrap">
          <table>
            <thead>
              <tr><th>Severity</th><th>Identifier</th><th>Dependency</th><th>CVSS</th><th>Summary</th><th>View</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in dependencyCheckFindings" :key="readRecordValue(row, 'identifier') || index">
                <td><span :class="['deployment-status-pill', dependencySeverityClass(row)]">{{ dependencySeverityLabel(row) }}</span></td>
                <td>{{ resultValue(row, 'identifier') }}</td>
                <td>{{ resultValue(row, 'dependency') }}</td>
                <td>{{ resultValue(row, 'cvssScore') }}</td>
                <td>{{ resultValue(row, 'summary') }}</td>
                <td><a v-if="readRecordValue(row, 'url')" :href="readRecordValue(row, 'url')" target="_blank" rel="noopener noreferrer">View</a><span v-else>Not available</span></td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </section>

    <section class="quality-section github-scanner-section playwright-results-section">
      <h2>Playwright Workflow Gate</h2>
      <p class="section-summary">{{ playwrightSummary }}</p>
      <p v-if="!isProviderAvailable('Playwright')" class="provider-state-line">{{ providerUnavailableDetail('Playwright') }}</p>

      <div class="inline-facts playwright-facts">
        <span>Project: {{ data?.playwrightProjectName || selectedApplicationLabel }}</span>
        <span>Target: {{ data?.playwrightBaseUrl || '-' }}</span>
        <span>Run: {{ formatCodeQualityDate(data?.playwrightGeneratedAtUtc) }}</span>
        <span>Duration: {{ formatDurationSeconds(data?.playwrightDurationSeconds) }}</span>
      </div>

      <div class="metric-grid playwright-results-grid">
        <article v-for="item in playwrightMetrics" :key="item.label" :class="['metric-card', 'status-metric-card', item.status]">
          <strong>{{ item.value }}</strong>
          <span>{{ item.label }}</span>
        </article>
      </div>

      <div v-if="playwrightWorkflowContractCount > 0" class="table-section github-scanner-details playwright-workflow-contracts detail-disclosure">
        <div class="table-heading-row">
          <button type="button" class="detail-toggle" :aria-expanded="isDetailSectionExpanded('playwright-workflow-contracts')" aria-controls="playwright-workflow-contracts-details" @click="toggleDetailSection('playwright-workflow-contracts')">
            <span class="detail-toggle-icon">{{ detailToggleIcon('playwright-workflow-contracts') }}</span>
            <h3>Workflow Gate ({{ detailCountLabel(playwrightWorkflowContractCount) }})</h3>
          </button>
        </div>
        <div v-if="showExpandedDetailTable('playwright-workflow-contracts', playwrightWorkflowContractCount)" id="playwright-workflow-contracts-details" class="table-wrap">
          <table>
            <thead>
              <tr><th>Intent</th><th>Direct page</th><th>Dropdown</th><th>Boundary</th><th>Status</th><th>Detail</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in playwrightWorkflowContracts" :key="readRecordValue(row, 'key') || index">
                <td>{{ resultValue(row, 'label') }}</td>
                <td><span :class="['deployment-status-pill', statusClass(resultValue(row, 'directScenarioStatus'))]">{{ resultValue(row, 'directScenarioStatus') }}</span></td>
                <td><span :class="['deployment-status-pill', statusClass(resultValue(row, 'dropdownScenarioStatus'))]">{{ resultValue(row, 'dropdownScenarioStatus') }}</span></td>
                <td><span :class="['deployment-status-pill', statusClass(resultValue(row, 'authenticatedScenarioStatus'))]">{{ resultValue(row, 'authenticatedScenarioStatus') }}</span></td>
                <td><span :class="['deployment-status-pill', resultStatusClass(row)]">{{ resultStatusLabel(row) }}</span></td>
                <td>{{ resultValue(row, 'statusDetail') }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <div class="table-section github-scanner-details playwright-results-details detail-disclosure">
        <div class="table-heading-row">
          <button type="button" class="detail-toggle" :aria-expanded="isDetailSectionExpanded('playwright-results')" aria-controls="playwright-results-details" @click="toggleDetailSection('playwright-results')">
            <span class="detail-toggle-icon">{{ detailToggleIcon('playwright-results') }}</span>
            <h3>Playwright Details ({{ detailCountLabel(playwrightResultCount) }})</h3>
          </button>
        </div>
        <div v-if="showExpandedDetailTable('playwright-results', playwrightResultCount)" id="playwright-results-details" class="table-wrap">
          <table>
            <thead>
              <tr><th>Scenario</th><th>Step</th><th>Browser</th><th>Status</th><th>Duration</th><th>Error</th><th>Screenshot</th></tr>
            </thead>
            <tbody>
              <tr v-for="(row, index) in playwrightResults" :key="testResultRowKey(row, index, 'playwright')">
                <td>{{ resultValue(row, 'scenario') }}</td>
                <td>{{ resultValue(row, 'step') }}</td>
                <td>{{ resultValue(row, 'browser') }}</td>
                <td><span :class="['deployment-status-pill', resultStatusClass(row)]">{{ resultStatusLabel(row) }}</span></td>
                <td>{{ resultDuration(row) }}</td>
                <td>{{ resultValue(row, 'error') }}</td>
                <td><a v-if="readRecordValue(row, 'screenshot')" :href="readRecordValue(row, 'screenshot')" target="_blank" rel="noopener noreferrer">Open</a><span v-else>-</span></td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { canonicalHealthEnvironment, endpoint, fetchHealthJson, statusClass, useFilteredHealthChildPage, type ApiRecord, type HealthChildPageEmits, type HealthChildPageProps } from '../shared'

const defaultApplication = { key: 'my-life-story-vault', label: 'My Life Story Vault' }
const defaultEnvironments = ['Development', 'Production']

const sonarMetricOrder = [
  'vulnerabilities',
  'coverage',
  'reliability_rating',
  'bugs',
  'duplicated_lines_density',
  'code_smells',
  'security_hotspots',
  'security_rating',
  'sqale_rating'
] as const

const sonarMetricLabels: Record<string, string> = {
  vulnerabilities: 'Vulnerabilities',
  coverage: 'Sonar Coverage (%)',
  reliability_rating: 'Reliability Rating',
  bugs: 'Bugs',
  duplicated_lines_density: 'Duplication (%)',
  code_smells: 'Code Smells',
  security_hotspots: 'Security Hotspots',
  security_rating: 'Security Rating',
  sqale_rating: 'Maintainability Rating'
}

const githubSeverityLabels: Record<string, string> = {
  CRITICAL: 'Critical CVEs',
  HIGH: 'High CVEs',
  MODERATE: 'Medium CVEs',
  LOW: 'Low CVEs'
}

const codeQlSeverityLabels: Record<string, string> = {
  CRITICAL: 'Critical CodeQL',
  HIGH: 'High CodeQL',
  MEDIUM: 'Medium CodeQL',
  LOW: 'Low CodeQL',
  ERROR: 'Error CodeQL',
  WARNING: 'Warning CodeQL',
  NOTE: 'Note CodeQL'
}

const secretScanningLabels: Record<string, string> = {
  OPEN: 'Open Secrets',
  DEFAULT: 'Default Pattern Alerts',
  GENERIC: 'Generic Pattern Alerts'
}

const dependencyCheckSeverityLabels: Record<string, string> = {
  CRITICAL: 'Critical Dependencies',
  HIGH: 'High Dependencies',
  MEDIUM: 'Medium Dependencies',
  LOW: 'Low Dependencies',
  UNKNOWN: 'Unknown Severity'
}

const props = defineProps<HealthChildPageProps>()
const emit = defineEmits<HealthChildPageEmits>()
const filter = reactive({ application: defaultApplication.key, environment: 'Development' })
const expandedDetailSections = reactive<Record<string, boolean>>({})
const cycloneDxProviderName = 'CycloneDX SBOM'
const { data, load } = useFilteredHealthChildPage({
  props,
  emit,
  key: 'codeQuality',
  label: 'Code Quality & Security',
  path: '/api/system-health/code-quality-security',
  filter,
  afterLoad: () => {
    void loadTestResults()
  }
})
const testResultsData = ref<ApiRecord | null>(null)
const testResultsLoading = ref(false)
const testResultsError = ref('')

const environments = computed(() => {
  const source = data.value?.environments as string[] | undefined
  return Array.from(new Set((source?.length ? source : defaultEnvironments).map(canonicalHealthEnvironment)))
    .map(environment => ({ value: environment, label: environment }))
})
const applications = computed(() => {
  const values = new Map<string, { key: string, label: string }>()
  values.set(defaultApplication.key, defaultApplication)

  const source = data.value?.applications as Array<{ key?: string, label?: string }> | undefined
  for (const app of source ?? []) {
    if (!app.key) continue
    values.set(app.key, { key: app.key, label: app.label ?? app.key })
  }

  return Array.from(values.values())
})
const lastUpdated = computed(() => formatCodeQualityDate(data.value?.generatedAtUtc))
const displayStatusDetail = computed(() => removeLintGateStatusDetail(providerText(data.value?.statusDetail)))
const sonarVulnerabilityDetailCount = computed(() => Number(data.value?.vulnerabilityDisplayedCount ?? ((data.value?.vulnerabilities as unknown[] | undefined)?.length ?? 0)))
const sonarVulnerabilityTotalCount = computed(() => Number(data.value?.vulnerabilityTotalCount ?? sonarVulnerabilityDetailCount.value))
const sonarVulnerabilityDetailLabel = computed(() => detailCountLabel(sonarVulnerabilityTotalCount.value, sonarVulnerabilityDetailCount.value))
const sonarVulnerabilities = computed(() => (data.value?.vulnerabilities as ApiRecord[] | undefined) ?? [])
const sonarBugDetailCount = computed(() => Number(data.value?.bugDisplayedCount ?? ((data.value?.bugs as unknown[] | undefined)?.length ?? 0)))
const sonarBugTotalCount = computed(() => Number(data.value?.bugTotalCount ?? sonarBugDetailCount.value))
const sonarBugDetailLabel = computed(() => detailCountLabel(sonarBugTotalCount.value, sonarBugDetailCount.value))
const sonarBugs = computed(() => (data.value?.bugs as ApiRecord[] | undefined) ?? [])
const sonarCodeSmellDetailCount = computed(() => Number(data.value?.codeSmellDisplayedCount ?? ((data.value?.codeSmells as unknown[] | undefined)?.length ?? 0)))
const sonarCodeSmellTotalCount = computed(() => Number(data.value?.codeSmellTotalCount ?? sonarCodeSmellDetailCount.value))
const sonarCodeSmellDetailLabel = computed(() => detailCountLabel(sonarCodeSmellTotalCount.value, sonarCodeSmellDetailCount.value))
const sonarCodeSmells = computed(() => (data.value?.codeSmells as ApiRecord[] | undefined) ?? [])
const dependabotAlertCount = computed(() => sumSeverityCounts(data.value?.gitHubSeverityCounts))
const dependabotDetailCount = computed(() => (data.value?.gitHubAlerts as unknown[] | undefined)?.length ?? 0)
const codeQlAlertCount = computed(() => sumSeverityCounts(data.value?.gitHubCodeScanningSeverityCounts))
const codeQlDetailCount = computed(() => (data.value?.gitHubCodeScanningAlerts as unknown[] | undefined)?.length ?? 0)
const secretScanningOpenCount = computed(() => findKeyCount(data.value?.gitHubSecretScanningCounts, 'OPEN'))
const secretScanningDetailCount = computed(() => (data.value?.gitHubSecretScanningAlerts as unknown[] | undefined)?.length ?? 0)
const lintFindings = computed(() => isProviderAvailable('Lint & Standards')
  ? ((data.value?.lintFindings as ApiRecord[] | undefined) ?? [])
  : [])
const lintFindingDisplayedCount = computed(() => isProviderAvailable('Lint & Standards')
  ? Number(data.value?.lintDisplayedCount ?? lintFindings.value.length)
  : 0)
const lintFindingTotalCount = computed(() => isProviderAvailable('Lint & Standards')
  ? Number(data.value?.lintTotalFindings ?? lintFindings.value.length)
  : 0)
const lintFindingCount = computed(() => lintFindingTotalCount.value)
const lintFindingDetailLabel = computed(() => detailCountLabel(lintFindingTotalCount.value, lintFindingDisplayedCount.value))
const dependencyCheckFindingCount = computed(() => Number(data.value?.dependencyCheckVulnerabilityCount ?? ((data.value?.dependencyCheckFindings as unknown[] | undefined)?.length ?? 0)))
const dependencyCheckFindings = computed(() => (data.value?.dependencyCheckFindings as ApiRecord[] | undefined) ?? [])
const cycloneDxComponents = computed(() => isProviderAvailable(cycloneDxProviderName)
  ? ((data.value?.cycloneDxComponents as ApiRecord[] | undefined) ?? [])
  : [])
const cycloneDxComponentCount = computed(() => isProviderAvailable(cycloneDxProviderName)
  ? Number(data.value?.cycloneDxComponentCount ?? cycloneDxComponents.value.length)
  : 0)
const playwrightResults = computed(() => (data.value?.playwrightResults as ApiRecord[] | undefined) ?? [])
const playwrightResultCount = computed(() => playwrightResults.value.length)
const playwrightWorkflowContracts = computed(() => (data.value?.playwrightWorkflowContracts as ApiRecord[] | undefined) ?? [])
const playwrightWorkflowContractCount = computed(() => playwrightWorkflowContracts.value.length)
const apiFunctionalProblemResults = computed(() => ((testResultsData.value?.apiFunctionalResults as ApiRecord[] | undefined) ?? [])
  .filter(row => isFailedOrSkippedTestStatus(readRecordValue(row, 'status'))))
const apiFunctionalTestCount = computed(() => apiFunctionalProblemResults.value.length)
const uiProblemResults = computed(() => ((testResultsData.value?.uiTestResults as ApiRecord[] | undefined) ?? [])
  .filter(row => isFailedOrSkippedTestStatus(readRecordValue(row, 'status'))))
const uiTestCount = computed(() => uiProblemResults.value.length)
const dependabotSummary = computed(() => isProviderAvailable('GitHub Dependabot')
  ? `Dependabot Vulnerability Alerts: ${dependabotAlertCount.value} open CVEs`
  : `Dependabot Vulnerability Alerts: ${providerStatusLabel('GitHub Dependabot')}`)
const codeQlSummary = computed(() => isProviderAvailable('GitHub CodeQL')
  ? `Github CodeQL Alerts: ${codeQlAlertCount.value} open alerts`
  : `Github CodeQL Alerts: ${providerStatusLabel('GitHub CodeQL')}`)
const secretScanningSummary = computed(() => isProviderAvailable('GitHub Secret Scanning')
  ? `GitHub Secret Scanning: ${secretScanningOpenCount.value} open secrets`
  : `GitHub Secret Scanning: ${providerStatusLabel('GitHub Secret Scanning')}`)
const lintSummary = computed(() => isProviderAvailable('Lint & Standards')
  ? `Lint & Standards: ${data.value?.lintErrorCount ?? 0} errors, ${data.value?.lintWarningCount ?? 0} warnings`
  : `Lint & Standards: ${providerStatusLabel('Lint & Standards')}`)
const dependencyCheckSummary = computed(() => isProviderAvailable('OWASP Dependency-Check')
  ? `OWASP Dependency-Check: ${dependencyCheckFindingCount.value} vulnerabilities`
  : `OWASP Dependency-Check: ${providerStatusLabel('OWASP Dependency-Check')}`)
const cycloneDxSummary = computed(() => isProviderAvailable(cycloneDxProviderName)
  ? `CycloneDX SBOM Results: ${cycloneDxComponentCount.value} components`
  : `CycloneDX SBOM Results: ${providerStatusLabel(cycloneDxProviderName)}`)
const playwrightSummary = computed(() => isProviderAvailable('Playwright')
  ? `Playwright Workflow Gate: ${data.value?.playwrightPassedTests ?? 0}/${data.value?.playwrightTotalTests ?? 0} workflow checks passed`
  : `Playwright: ${providerStatusLabel('Playwright')}`)
const selectedApplicationLabel = computed(() => {
  const applications = data.value?.applications as Array<{ key?: string, label?: string }> | undefined
  return applications?.find(app => app.key === filter.application)?.label || filter.application || '-'
})
const selectedCodeQualityProjectLabel = computed(() => {
  const applicationLabel = selectedApplicationLabel.value
  const environment = canonicalHealthEnvironment(filter.environment)
  return applicationLabel === '-' ? environment : `${applicationLabel} ${environment}`
})
const selectedTestBuildLabel = computed(() => {
  const buildId = testResultsData.value?.buildId
  return typeof buildId === 'string' && buildId.toLowerCase() === 'lastbuild' ? 'Last Build' : buildId || '-'
})
const testResultsSummary = computed(() => {
  if (testResultsLoading.value) return 'Code Quality Test Results: loading'
  if (testResultsError.value) return `Code Quality Test Results: ${testResultsError.value}`
  if (!testResultsData.value) return 'Code Quality Test Results: not loaded'
  return `Code Quality Test Results: ${testResultsData.value.passedTests ?? 0}/${testResultsData.value.totalTests ?? 0} passed`
})
const hasRealSonarApiData = computed(() =>
  providerStatus('SonarQube')?.status?.toLowerCase() === 'success'
  || Boolean(data.value?.sonarAnalysisDateUtc || data.value?.sonarAnalysisRevision || data.value?.sonarAnalysisVersion)
  || ((data.value?.sonarMetrics as unknown[] | undefined)?.length ?? 0) > 3)
const sonarProviderWarning = computed(() => {
  const status = providerStatus('SonarQube')
  if (!status || status.status?.toLowerCase() === 'success') return ''

  return status.detail ?? ''
})
const codeAnalysisHeading = computed(() => hasRealSonarApiData.value ? 'SonarQube Code Analysis' : 'Code Analysis')
const sonarAnalysisVersion = computed(() => {
  const version = providerText(data.value?.sonarAnalysisVersion)
  if (version && !isMissingSonarAnalysisVersion(version)) return version

  const revision = formatCommitSha(data.value?.sonarAnalysisRevision)
  return revision === '-' ? '-' : `revision ${revision}`
})
const sonarMetrics = computed(() => sonarMetricOrder.map(metricKey => {
  const metric = findSonarMetric(data.value, metricKey)
  return {
    label: sonarMetricLabels[metricKey],
    value: metric ? formatMetricValue(metric.value) : missingSonarMetricValue(metricKey),
    status: sonarMetricStatus(metricKey, metric)
  }
}))
const githubSecurityMetrics = computed(() => {
  const counts = data.value?.gitHubSeverityCounts as Array<{ severity?: string, count?: number }> | undefined
  return ['CRITICAL', 'HIGH', 'MODERATE', 'LOW'].map(severity => {
    const count = counts?.find(item => item.severity?.toUpperCase() === severity)?.count ?? 0
    return {
      label: githubSeverityLabels[severity],
      value: isProviderAvailable('GitHub Dependabot') ? count : '-',
      status: isProviderAvailable('GitHub Dependabot') ? severityCountStatus(severity, count) : 'unknown'
    }
  })
})
const githubCodeQlMetrics = computed(() => {
  const counts = data.value?.gitHubCodeScanningSeverityCounts as Array<{ severity?: string, count?: number }> | undefined
  return ['CRITICAL', 'HIGH', 'MEDIUM', 'LOW', 'ERROR', 'WARNING', 'NOTE'].map(severity => {
    const count = counts?.find(item => item.severity?.toUpperCase() === severity)?.count ?? 0
    return {
      label: codeQlSeverityLabels[severity],
      value: isProviderAvailable('GitHub CodeQL') ? count : '-',
      status: isProviderAvailable('GitHub CodeQL') ? severityCountStatus(severity, count) : 'unknown'
    }
  })
})
const githubSecretScanningMetrics = computed(() => {
  const counts = data.value?.gitHubSecretScanningCounts as Array<{ key?: string, label?: string, count?: number }> | undefined
  const providerAvailable = isProviderAvailable('GitHub Secret Scanning')
  return ['OPEN', 'DEFAULT', 'GENERIC'].map(key => {
    const matchingCount = counts?.find(item => item.key?.toUpperCase() === key)
    const count = matchingCount?.count ?? 0
    return {
      label: matchingCount?.label ?? secretScanningLabels[key],
      value: providerAvailable ? count : '-',
      status: secretScanningMetricStatus(providerAvailable, count)
    }
  })
})
const lintMetrics = computed(() => {
  const providerAvailable = isProviderAvailable('Lint & Standards')
  const errors = Number(data.value?.lintErrorCount ?? 0)
  const warnings = Number(data.value?.lintWarningCount ?? 0)
  const toolsTotal = Number(data.value?.lintToolsTotal ?? 0)
  const toolsPassed = Number(data.value?.lintToolsPassed ?? 0)
  const toolsFailed = Number(data.value?.lintToolsFailed ?? 0)
  const toolsNotApplicable = Number(data.value?.lintToolsNotApplicable ?? 0)
  const toolsNotConfigured = Number(data.value?.lintToolsNotConfigured ?? 0)
  return [
    {
      label: 'Status',
      value: providerStatusLabel('Lint & Standards'),
      status: artifactStatusMetric(data.value?.lintStatus)
    },
    {
      label: 'Errors',
      value: providerAvailable ? errors : '-',
      status: lintCountMetricStatus(providerAvailable, errors, 'warning')
    },
    {
      label: 'Warnings',
      value: providerAvailable ? warnings : '-',
      status: lintCountMetricStatus(providerAvailable, warnings, 'warning')
    },
    {
      label: 'Categories',
      value: providerAvailable ? toolsTotal : '-',
      status: lintCategoriesMetricStatus(providerAvailable, toolsTotal)
    },
    {
      label: 'Tools Passed',
      value: providerAvailable ? toolsPassed : '-',
      status: lintToolsPassedMetricStatus(providerAvailable, toolsPassed)
    },
    {
      label: 'Tools Failed',
      value: providerAvailable ? toolsFailed : '-',
      status: lintCountMetricStatus(providerAvailable, toolsFailed, 'warning')
    },
    {
      label: 'Not Configured',
      value: providerAvailable ? toolsNotConfigured : '-',
      status: lintCountMetricStatus(providerAvailable, toolsNotConfigured, 'warning')
    },
    {
      label: 'Not Applicable',
      value: providerAvailable ? toolsNotApplicable : '-',
      status: 'unknown'
    }
  ]
})
const dependencyCheckMetrics = computed(() => {
  const counts = data.value?.dependencyCheckSeverityCounts as Array<{ severity?: string, count?: number }> | undefined
  const providerAvailable = isProviderAvailable('OWASP Dependency-Check')
  return [
    {
      label: 'Scan Trust',
      value: providerStatusLabel('OWASP Dependency-Check'),
      status: artifactStatusMetric(data.value?.dependencyCheckStatus)
    },
    ...['CRITICAL', 'HIGH', 'MEDIUM', 'LOW', 'UNKNOWN'].map(severity => {
      const count = counts?.find(item => item.severity?.toUpperCase() === severity)?.count ?? 0
      return {
        label: dependencyCheckSeverityLabels[severity],
        value: providerAvailable ? count : '-',
        status: providerAvailable ? severityCountStatus(severity, count) : 'unknown'
      }
    })]
})
const cycloneDxMetrics = computed(() => {
  const providerAvailable = isProviderAvailable(cycloneDxProviderName)
  return [
    {
      label: 'SBOM Status',
      value: providerStatusLabel(cycloneDxProviderName),
      status: artifactStatusMetric(data.value?.cycloneDxStatus)
    },
    {
      label: 'Components',
      value: providerAvailable ? cycloneDxComponentCount.value : '-',
      status: providerAvailable && cycloneDxComponentCount.value > 0 ? 'healthy' : 'unknown'
    },
    {
      label: 'Format',
      value: providerAvailable ? (data.value?.cycloneDxBomFormat || '-') : '-',
      status: providerText(data.value?.cycloneDxBomFormat).toLowerCase() === 'cyclonedx' ? 'healthy' : 'unknown'
    },
    {
      label: 'Spec Version',
      value: providerAvailable ? (data.value?.cycloneDxSpecVersion || '-') : '-',
      status: providerText(data.value?.cycloneDxSpecVersion) ? 'healthy' : 'unknown'
    }
  ]
})
const playwrightMetrics = computed(() => {
  const total = Number(data.value?.playwrightTotalTests ?? 0)
  const passed = Number(data.value?.playwrightPassedTests ?? 0)
  const failed = Number(data.value?.playwrightFailedTests ?? 0)
  const skipped = Number(data.value?.playwrightSkippedTests ?? 0)
  const providerAvailable = isProviderAvailable('Playwright')
  return [
    { label: 'Total Browser Checks', value: providerAvailable ? total : '-', status: providerAvailable && total > 0 ? 'healthy' : 'unknown' },
    { label: 'Passed Browser Checks', value: providerAvailable ? passed : '-', status: providerAvailable && failed === 0 && total > 0 ? 'healthy' : 'warning' },
    { label: 'Failed Browser Checks', value: providerAvailable ? failed : '-', status: providerAvailable && failed > 0 ? 'critical' : 'healthy' },
    { label: 'Skipped Browser Checks', value: providerAvailable ? skipped : '-', status: providerAvailable && skipped > 0 ? 'warning' : 'healthy' }
  ]
})
const testResultMetrics = computed(() => {
  const failed = Number(testResultsData.value?.failedTests ?? 0)
  const total = Number(testResultsData.value?.totalTests ?? 0)
  const passed = Number(testResultsData.value?.passedTests ?? 0)
  const skipped = Number(testResultsData.value?.skippedTests ?? 0)
  return [
    { label: 'Total Tests', value: total, status: total > 0 ? 'healthy' : 'unknown' },
    { label: 'Passed Tests', value: passed, status: failed > 0 || total === 0 ? 'warning' : 'healthy' },
    { label: 'Failed Tests', value: failed, status: failed > 0 ? 'critical' : 'healthy' },
    { label: 'Skipped Tests', value: skipped, status: skipped > 0 ? 'warning' : 'healthy' }
  ]
})

async function loadTestResults() {
  testResultsLoading.value = true
  testResultsError.value = ''
  try {
    testResultsData.value = await fetchHealthJson(endpoint('/api/system-health/test-results', {
      applicationKey: filter.application,
      environment: canonicalHealthEnvironment(filter.environment)
    }))
  } catch {
    testResultsError.value = 'Unable to load Jenkins test results.'
    testResultsData.value = null
  } finally {
    testResultsLoading.value = false
  }
}

function isDetailSectionExpanded(key: string) {
  return Boolean(expandedDetailSections[key])
}

function toggleDetailSection(key: string) {
  expandedDetailSections[key] = !expandedDetailSections[key]
}

function detailToggleIcon(key: string) {
  return isDetailSectionExpanded(key) ? '-' : '+'
}

function showExpandedDetailTable(key: string, count: number) {
  return isDetailSectionExpanded(key) && count > 0
}

function detailCountLabel(totalCount: number, displayedCount = totalCount) {
  return totalCount > displayedCount
    ? `showing ${formatCount(displayedCount)} of ${formatCount(totalCount)}`
    : formatCount(totalCount)
}

function formatCount(count: number) {
  return count.toLocaleString('en-US')
}

function lintFindingValue(row: ApiRecord, ...keys: string[]) {
  for (const key of keys) {
    const value = readRecordValue(row, key)
    if (value !== '') return value
  }

  return '-'
}

function resultValue(row: ApiRecord, key: string) {
  return readRecordValue(row, key) || '-'
}

function resultStatusLabel(row: ApiRecord) {
  const status = readRecordValue(row, 'status')
  const normalized = status.toUpperCase()
  if (normalized === 'FAILED' || normalized === 'FAILURE' || normalized === 'ERROR') return 'Failed'
  if (normalized === 'SKIPPED' || normalized === 'SKIP') return 'Skipped'
  if (normalized === 'PASSED' || normalized === 'PASS' || normalized === 'SUCCESS') return 'Passed'
  return status || '-'
}

function resultStatusClass(row: ApiRecord) {
  return statusClass(resultStatusLabel(row))
}

const dependencySeverityLabels: Record<string, string> = {
  CRITICAL: 'Critical',
  HIGH: 'High',
  MEDIUM: 'Medium',
  MODERATE: 'Medium',
  LOW: 'Low',
  UNKNOWN: 'Unknown'
}

const sonarSeverityLabels: Record<string, string> = {
  BLOCKER: 'Critical',
  CRITICAL: 'Critical',
  MAJOR: 'High',
  HIGH: 'High',
  MINOR: 'Medium',
  MEDIUM: 'Medium',
  MODERATE: 'Medium',
  INFO: 'Note',
  NOTE: 'Note',
  LOW: 'Low'
}

const lintSeverityLabels: Record<string, string> = {
  ERROR: 'Warning',
  CRITICAL: 'Critical',
  BLOCKER: 'Critical',
  HIGH: 'High',
  MAJOR: 'High',
  MEDIUM: 'Medium',
  MODERATE: 'Medium',
  MINOR: 'Medium',
  LOW: 'Low',
  WARNING: 'Warning',
  WARN: 'Warning',
  NOTE: 'Note',
  INFO: 'Note',
  INFORMATION: 'Note'
}

function dependencySeverityLabel(row: ApiRecord) {
  return mappedSeverityLabel(row, dependencySeverityLabels)
}

function dependencySeverityClass(row: ApiRecord) {
  return statusClass(dependencySeverityLabel(row))
}

function sonarSeverityLabel(row: ApiRecord) {
  return mappedSeverityLabel(row, sonarSeverityLabels)
}

function sonarSeverityClass(row: ApiRecord) {
  return statusClass(sonarSeverityLabel(row))
}

function resultDuration(row: ApiRecord) {
  const value = readRecordValue(row, 'duration')
  if (value === '') return '-'

  const numericValue = Number(value)
  return Number.isFinite(numericValue) ? formatDurationSeconds(numericValue) : value
}

function testResultRowKey(row: ApiRecord, index: number, prefix: string) {
  return readRecordValue(row, 'id')
    || readRecordValue(row, 'key')
    || `${prefix}-${resultValue(row, 'suite')}-${resultValue(row, 'name')}-${resultValue(row, 'scenario')}-${resultValue(row, 'step')}-${index}`
}

function cycloneDxComponentKey(row: ApiRecord, index: number) {
  return readRecordValue(row, 'bomRef')
    || readRecordValue(row, 'packageUrl')
    || `${resultValue(row, 'type')}-${resultValue(row, 'name')}-${resultValue(row, 'version')}-${index}`
}

function lintSeverityLabel(row: ApiRecord) {
  return mappedSeverityLabel(row, lintSeverityLabels)
}

function mappedSeverityLabel(row: ApiRecord, labels: Record<string, string>) {
  const severity = readRecordValue(row, 'severity')
  const normalized = severity.toUpperCase()
  return labels[normalized] ?? (severity || '-')
}

function lintSeverityClass(row: ApiRecord) {
  return statusClass(lintSeverityLabel(row))
}

function readRecordValue(row: ApiRecord, key: string) {
  const value = row[key] ?? row[toPascalCase(key)]
  if (value !== null && value !== undefined && value !== '') return String(value)

  const matchingKey = Object.keys(row).find(candidate => candidate.toLowerCase() === key.toLowerCase())
  const matchingValue = matchingKey ? row[matchingKey] : undefined
  return matchingValue === null || matchingValue === undefined ? '' : String(matchingValue)
}

function toPascalCase(value: string) {
  return value.length === 0 ? value : `${value[0].toUpperCase()}${value.slice(1)}`
}

function secretScanningMetricStatus(providerAvailable: boolean, count: number) {
  if (!providerAvailable) return 'unknown'
  return count > 0 ? 'warning' : 'healthy'
}

function lintCountMetricStatus(providerAvailable: boolean, count: number, activeStatus: 'critical' | 'warning') {
  if (!providerAvailable) return 'unknown'
  return count > 0 ? activeStatus : 'healthy'
}

function lintToolsPassedMetricStatus(providerAvailable: boolean, toolsPassed: number) {
  if (!providerAvailable) return 'unknown'
  return toolsPassed > 0 ? 'healthy' : 'unknown'
}

function lintCategoriesMetricStatus(providerAvailable: boolean, toolsTotal: number) {
  if (!providerAvailable) return 'unknown'
  return toolsTotal >= 7 ? 'healthy' : 'warning'
}

function isFailedOrSkippedTestStatus(status: unknown) {
  const normalized = String(status ?? '').trim().toLowerCase()
  return normalized === 'failed' || normalized === 'skipped'
}

function findSonarMetric(source: ApiRecord | null, key: string) {
  return (source?.sonarMetrics as Array<{ key?: string, metric?: string, label?: string, value?: string, bestValue?: boolean }> | undefined)
    ?.find(item => isSonarMetricMatch(item, key))
}

function isSonarMetricMatch(metric: { key?: string, metric?: string, label?: string }, key: string) {
  const normalizedKey = normalizeSonarMetricKey(key)
  const aliases = sonarMetricAliases(normalizedKey)
  return [metric.key, metric.metric, metric.label]
    .map(normalizeSonarMetricKey)
    .some(candidate => aliases.includes(candidate))
}

function sonarMetricAliases(key: string) {
  if (key === 'coverage') return ['coverage', 'sonar_coverage', 'line_coverage', 'overall_coverage']
  return [key]
}

function normalizeSonarMetricKey(value: unknown) {
  const text = String(value ?? '').trim().toLowerCase()
  let normalized = ''
  let lastWasSeparator = true

  for (const char of text) {
    const code = char.charCodeAt(0)
    const isLetter = code >= 97 && code <= 122
    const isNumber = code >= 48 && code <= 57

    if (isLetter || isNumber) {
      normalized += char
      lastWasSeparator = false
    } else if (!lastWasSeparator) {
      normalized += '_'
      lastWasSeparator = true
    }
  }

  return lastWasSeparator ? normalized.slice(0, -1) : normalized
}

function missingSonarMetricValue(key: string) {
  return key === 'coverage' ? 'Not reported' : '-'
}

function formatMetricValue(value: unknown) {
  return value === null || value === undefined || value === '' ? '-' : value
}

function sumSeverityCounts(value: unknown) {
  if (!Array.isArray(value)) return 0
  return value.reduce((total, item) => total + Number((item as { count?: number }).count ?? 0), 0)
}

function findKeyCount(value: unknown, key: string) {
  if (!Array.isArray(value)) return 0
  const match = value.find(item => String((item as { key?: string }).key ?? '').toUpperCase() === key)
  return Number((match as { count?: number } | undefined)?.count ?? 0)
}

function providerStatus(provider: string) {
  const statuses = data.value?.providerStatuses as Array<{ provider?: string, status?: string, detail?: string }> | undefined
  return statuses?.find(item => item.provider?.toLowerCase() === provider.toLowerCase())
}

function isProviderAvailable(provider: string) {
  const artifactStatus = artifactProviderStatus(provider)
  if (artifactStatus) {
    return artifactStatus.toLowerCase() !== 'unavailable'
  }

  const status = providerStatus(provider)
  return !status || status.status?.toLowerCase() === 'success'
}

function providerStatusLabel(provider: string) {
  const artifactStatus = artifactProviderStatus(provider)
  if (artifactStatus) return artifactStatus

  const status = providerStatus(provider)?.status
  if (!status) return 'Unavailable'
  if (status.toLowerCase() === 'ratelimited') return 'Rate limited'
  return status
}

function providerUnavailableDetail(provider: string) {
  const artifactDetail = artifactProviderStatusDetail(provider)
  if (artifactDetail) return artifactDetail

  return providerStatus(provider)?.detail ?? `${provider} data is unavailable.`
}

function showArtifactProviderDetail(provider: string) {
  const artifactStatus = artifactProviderStatus(provider).toLowerCase()
  if (provider === 'Lint & Standards') {
    return artifactStatus !== '' && artifactStatus !== 'healthy' && artifactStatus !== 'success' && artifactStatus !== 'warning'
  }

  return artifactStatus !== '' && artifactStatus !== 'healthy' && artifactStatus !== 'success'
}

function artifactProviderStatus(provider: string) {
  if (provider === 'Lint & Standards') {
    return providerText(data.value?.lintStatus)
  }

  if (provider === 'OWASP Dependency-Check') {
    return providerText(data.value?.dependencyCheckStatus)
  }

  if (provider === cycloneDxProviderName) {
    return providerText(data.value?.cycloneDxStatus)
  }

  if (provider === 'Playwright') {
    return providerText(data.value?.playwrightStatus)
  }

  return ''
}

function artifactProviderStatusDetail(provider: string) {
  if (provider === 'Lint & Standards') {
    return providerText(data.value?.lintStatusDetail)
  }

  if (provider === 'OWASP Dependency-Check') {
    return providerText(data.value?.dependencyCheckStatusDetail)
  }

  if (provider === cycloneDxProviderName) {
    return providerText(data.value?.cycloneDxStatusDetail)
  }

  if (provider === 'Playwright') {
    return providerText(data.value?.playwrightStatusDetail)
  }

  return ''
}

function providerText(value: unknown) {
  return typeof value === 'string' ? value.trim() : ''
}

function removeLintGateStatusDetail(value: string) {
  return value
    .replace(/\bAll Phase 1 lint and standards gates passed\.\s*/gi, '')
    .replace(/\b\d+\s+Phase 1 lint and standards gate\(s\) failed\.\s*/gi, '')
    .replace(/\b\d+\s+Phase 1 lint and standards gates? failed\.\s*/gi, '')
    .replace(/\s{2,}/g, ' ')
    .trim()
}

function isMissingSonarAnalysisVersion(value: string) {
  return value.trim().toLowerCase() === 'not provided'
}

function artifactStatusMetric(value: unknown) {
  const normalized = providerText(value).toLowerCase()
  if (normalized === 'healthy' || normalized === 'success') return 'healthy'
  if (normalized === 'warning') return 'warning'
  if (normalized === 'critical') return 'critical'
  return 'unknown'
}

function sonarMetricStatus(key: string, metric: { value?: string, bestValue?: boolean } | undefined) {
  if (!metric) return 'unknown'
  if (metric.bestValue) return 'healthy'

  const numericValue = Number(metric.value)
  if (Number.isNaN(numericValue)) return 'warning'

  return numericSonarMetricStatus(key, numericValue)
}

function numericSonarMetricStatus(key: string, numericValue: number) {
  if (['vulnerabilities', 'bugs', 'code_smells', 'security_hotspots'].includes(key)) return numericValue > 0 ? 'critical' : 'healthy'
  if (key === 'coverage') return numericValue >= 90 ? 'healthy' : 'critical'
  if (key === 'duplicated_lines_density') return numericValue <= 0 ? 'healthy' : 'warning'
  if (['security_rating', 'sqale_rating', 'reliability_rating'].includes(key)) return numericValue <= 1 ? 'healthy' : 'warning'

  return 'warning'
}

function severityCountStatus(severity: string, count: number) {
  if (count <= 0) return 'healthy'
  return severity === 'CRITICAL' ? 'critical' : 'warning'
}

function formatSecretPattern(value: unknown) {
  if (!value) return '-'
  const text = String(value).toUpperCase()
  if (text === 'DEFAULT') return 'Default'
  if (text === 'GENERIC') return 'Generic'
  return String(value)
}

function formatCodeQualityDate(value: unknown) {
  if (!value) return '-'
  const date = new Date(String(value))
  if (Number.isNaN(date.getTime())) return String(value)
  const pad = (part: number) => String(part).padStart(2, '0')
  const hours = date.getHours()
  const clockHours = hours % 12 || 12
  const period = hours >= 12 ? 'pm' : 'am'
  return `${pad(date.getDate())}/${pad(date.getMonth() + 1)}/${date.getFullYear()} ${pad(clockHours)}:${pad(date.getMinutes())}:${pad(date.getSeconds())} ${period}`
}

function formatCommitSha(value: unknown) {
  return typeof value === 'string' && value.length >= 7 ? value.slice(0, 7) : '-'
}

function formatDurationSeconds(value: unknown) {
  if (typeof value !== 'number') return '-'
  return value >= 60 && value % 60 === 0 ? `${value / 60} min` : `${value} sec`
}
</script>

<style scoped>
.detail-disclosure {
  padding: 8px 12px;
}

.detail-disclosure .table-heading-row {
  align-items: center;
  gap: 12px;
  margin-bottom: 0;
}

.detail-disclosure .table-wrap {
  margin-top: 8px;
}

.detail-toggle {
  align-items: center;
  background: transparent;
  border: 0;
  color: inherit;
  cursor: pointer;
  display: inline-flex;
  font: inherit;
  gap: 8px;
  min-height: 24px;
  padding: 0;
  text-align: left;
}

.detail-toggle h3 {
  font-size: 15px;
  font-weight: 700;
  line-height: 1.25;
  margin: 0;
}

.detail-toggle-icon {
  align-items: center;
  border: 1px solid #b6c4d2;
  border-radius: 3px;
  display: inline-flex;
  flex: 0 0 20px;
  font-weight: 700;
  height: 20px;
  justify-content: center;
  line-height: 1;
  width: 20px;
}
</style>
