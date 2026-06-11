export type GitHubAccountValidationStatus = 'Unknown' | 'Valid' | 'Invalid' | 'Error';
export type GitHubAccountQuotaState = 'Unknown' | 'Healthy' | 'Warning' | 'Limited' | 'Unavailable';

export interface GitHubAccount {
  id: string;
  displayName: string;
  username: string;
  plan: string;
  validationStatus: GitHubAccountValidationStatus;
  quotaState: GitHubAccountQuotaState;
  activeCodespaceCount: number;
  totalCodespaceCount: number;
  validationMessage?: string | null;
  lastError?: string | null;
  lastValidatedAt?: string | null;
  lastSyncedAt?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface GitHubAccountForm {
  displayName: string;
  username: string;
  personalAccessToken: string;
  plan: string;
}

export interface GitHubAccountStatusCheck {
  accounts: GitHubAccount[];
  results: GitHubAccountStatusCheckResult[];
}

export interface GitHubAccountStatusCheckResult {
  accountId: string;
  succeeded: boolean;
  message: string;
}

export type LocalProxyProfileStatus = 'Stopped' | 'Starting' | 'Running' | 'Error';
export type LocalProxySessionStatus = 'Starting' | 'Running' | 'Stopping' | 'Stopped' | 'Error';

export interface LocalProxyProfile {
  id: string;
  name: string;
  bindHost: string;
  localPort: number;
  socksPort: number;
  proxyUsername?: string | null;
  requiresAuthentication: boolean;
  idleShutdownMinutes: number;
  notes?: string | null;
  status: LocalProxyProfileStatus;
  createdAt: string;
  updatedAt: string;
}

export interface LocalProxyProfileForm {
  name: string;
  bindHost: string;
  localPort: number;
  socksPort: number;
  proxyUsername: string;
  proxyPassword: string;
  idleShutdownMinutes: number;
  notes: string;
}

export interface LocalProxySettingsForm {
  bindHost: string;
  localPort: number;
  proxyUsername: string;
  proxyPassword: string;
  idleShutdownMinutes: number;
}

export interface LocalProxySession {
  id: string;
  profileId: string;
  profileName: string;
  status: LocalProxySessionStatus;
  bindHost: string;
  localPort: number;
  socksPort: number;
  proxyUrl: string;
  httpProxyUrl: string;
  socksProxyUrl: string;
  startedAt: string;
  lastActivityAt: string;
  lastRequestAt?: string | null;
  idleShutdownAt: string;
  stoppedAt?: string | null;
  lastError?: string | null;
  totalRequests: number;
  totalConnectTunnels: number;
  totalBytesReceived: number;
  totalBytesSent: number;
  activeConnections: number;
  accountId?: string | null;
  codespaceName?: string | null;
  remoteProxyPort?: number | null;
  localTunnelPort?: number | null;
}

export interface LocalProxyAutomationStatus {
  settings: LocalProxyProfile;
  session?: LocalProxySession | null;
  phase: string;
  selectedAccountId?: string | null;
  selectedAccount?: string | null;
  selectedCodespace?: string | null;
  warning?: string | null;
  nextRetryAt?: string | null;
  lastError?: string | null;
  availability: string;
  message: string;
  severity: 'success' | 'info' | 'warning' | 'error' | 'muted' | string;
  publicPortOpen: boolean;
  retryInSeconds?: number | null;
  lastRequestAt?: string | null;
  idleWakePaused: boolean;
  idleWakeRequestCount: number;
  idleWakeRequestThreshold: number;
  idleWakeWindowExpiresAt?: string | null;
  latestRequests: LocalProxyGatewayRequest[];
}

export interface LocalProxyGatewayRequest {
  id: string;
  observedAt: string;
  protocol: string;
  targetHost?: string | null;
  targetPort?: number | null;
  outcome: string;
  sessionId?: string | null;
  accountId?: string | null;
  codespaceName?: string | null;
  errorMessage?: string | null;
  durationMs?: number | null;
}

export interface LocalProxyResult {
  succeeded: boolean;
  message: string;
  session?: LocalProxySession | null;
}

export type LocalProxyStatisticsPeriod = '24h' | '7d' | '30d';

export interface LocalProxyStatistics {
  period: LocalProxyStatisticsPeriod;
  rangeStart: string;
  rangeEnd: string;
  timeZone: string;
  totals: LocalProxyStatisticsTotals;
  hourlyBuckets: LocalProxyStatisticsBucket[];
  dailyBuckets: LocalProxyStatisticsBucket[];
  sessions: LocalProxyStatisticsSession[];
  gitHubSamples: CodespaceStateSample[];
  mismatches: LocalProxyStatisticsMismatch[];
}

export interface LocalProxyStatisticsTotals {
  activeSeconds: number;
  offSeconds: number;
  errorSeconds: number;
  activePercent: number;
  errorPercent: number;
  sessionCount: number;
  averageActiveSecondsPerDay: number;
}

export interface LocalProxyStatisticsBucket {
  start: string;
  end: string;
  label: string;
  activeSeconds: number;
  offSeconds: number;
  errorSeconds: number;
  activePercent: number;
  errorPercent: number;
  sessionCount: number;
  segments: LocalProxyStatisticsSegment[];
}

export interface LocalProxyStatisticsSegment {
  start: string;
  end: string;
  state: 'up' | 'error' | 'off' | 'future';
  seconds: number;
  percent: number;
}

export interface LocalProxyStatisticsSession {
  sessionId: string;
  accountId?: string | null;
  accountUsername?: string | null;
  codespaceName?: string | null;
  startedAt: string;
  endedAt: string;
  activeSeconds: number;
  status: string;
  lastError?: string | null;
}

export interface CodespaceStateSample {
  accountId: string;
  accountUsername?: string | null;
  codespaceName: string;
  state: string;
  observedAt: string;
  source: string;
  isActive: boolean;
}

export interface LocalProxyStatisticsMismatch {
  observedAt: string;
  accountId: string;
  accountUsername?: string | null;
  codespaceName: string;
  gitHubState: string;
  message: string;
}

export interface CodespaceSnapshot {
  id: string;
  accountId: string;
  name: string;
  state: string;
  repositoryFullName?: string | null;
  machineDisplayName?: string | null;
  location?: string | null;
  webUrl?: string | null;
  billableOwner?: string | null;
  createdAt?: string | null;
  updatedAt?: string | null;
  lastUsedAt?: string | null;
  lastSyncedAt: string;
}

export interface CreateCodespaceForm {
  repositoryOwner: string;
  repositoryName: string;
  ref: string;
  geo: string;
  machine: string;
  displayName: string;
  idleTimeoutMinutes: number;
}

export interface GitHubUsage {
  state: GitHubAccountQuotaState;
  message: string;
  quantity?: number | null;
  unitType?: string | null;
  netAmount?: number | null;
  billingUrl: string;
  quotas: GitHubUsageQuotaSummary[];
  billingPeriodYear?: number | null;
  billingPeriodMonth?: number | null;
  resetAt?: string | null;
}

export interface GitHubUsageQuotaSummary {
  name: string;
  used: number;
  limit?: number | null;
  remaining?: number | null;
  percentUsed?: number | null;
  unit: string;
}

export interface GitHubUsageForecast {
  generatedAt: string;
  resetAt?: string | null;
  daysUntilReset: number;
  totalComputeUsed: number;
  totalComputeLimit: number;
  totalComputeRemaining: number;
  average7DayComputeUsage: number;
  average14DayComputeUsage: number;
  average30DayComputeUsage: number;
  estimatedDailyComputeUsage: number;
  estimatedQuotaDays?: number | null;
  estimatedUsableDays?: number | null;
  status: string;
  message: string;
  includedAccountCount: number;
  unavailableAccountCount: number;
  defaultMachineCoreCount: number;
  warnings: string[];
}

export interface GitHubLifecycleResult {
  succeeded: boolean;
  message: string;
  codespace?: CodespaceSnapshot | null;
  export?: GitHubCodespaceExport | null;
}

export interface GitHubCodespaceExport {
  id?: string | null;
  state?: string | null;
  exportUrl?: string | null;
  htmlUrl?: string | null;
  completedAt?: string | null;
}

export interface OperationalEvent {
  id: string;
  timestamp: string;
  severity: 'Debug' | 'Information' | 'Warning' | 'Error';
  eventType: string;
  message: string;
  nodeId?: string | null;
  sessionId?: string | null;
  correlationId?: string | null;
  commandKind?: string | null;
  commandDisplay?: string | null;
  exitCode?: number | null;
  durationMs?: number | null;
  timedOut: boolean;
  standardOutputSnippet?: string | null;
  standardErrorSnippet?: string | null;
  detailsJson?: string | null;
}

export interface ActivitySummary {
  recentCount: number;
  errorCount: number;
  warningCount: number;
  commandFailureCount: number;
  averageCommandDurationMs?: number | null;
  lastError?: OperationalEvent | null;
}

export interface ActivityClearResult {
  deletedCount: number;
  deletedFileCount: number;
}

export interface RuntimeDiagnostics {
  databaseAvailable: boolean;
  tools: ToolDiagnostic[];
}

export interface ToolDiagnostic {
  name: string;
  available: boolean;
  message: string;
}

export interface ActivityFilters {
  severity: string;
  eventType: string;
  correlationId: string;
  search: string;
  limit: number;
}
