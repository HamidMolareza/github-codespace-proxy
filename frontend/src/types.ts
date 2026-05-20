export type GitHubAccountValidationStatus = 'Unknown' | 'Valid' | 'Invalid' | 'Error';
export type GitHubAccountQuotaState = 'Unknown' | 'Healthy' | 'Warning' | 'Limited' | 'Unavailable';

export interface GitHubAccount {
  id: string;
  displayName: string;
  username: string;
  plan: string;
  validationStatus: GitHubAccountValidationStatus;
  quotaState: GitHubAccountQuotaState;
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

export type LocalProxyProfileStatus = 'Stopped' | 'Starting' | 'Running' | 'Error';
export type LocalProxySessionStatus = 'Starting' | 'Running' | 'Stopping' | 'Stopped' | 'Error';

export interface LocalProxyProfile {
  id: string;
  name: string;
  bindHost: string;
  localPort: number;
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
  proxyUsername: string;
  proxyPassword: string;
  idleShutdownMinutes: number;
  notes: string;
}

export interface LocalProxySession {
  id: string;
  profileId: string;
  profileName: string;
  status: LocalProxySessionStatus;
  bindHost: string;
  localPort: number;
  proxyUrl: string;
  startedAt: string;
  lastActivityAt: string;
  idleShutdownAt: string;
  stoppedAt?: string | null;
  lastError?: string | null;
  totalRequests: number;
  totalConnectTunnels: number;
  totalBytesReceived: number;
  totalBytesSent: number;
  activeConnections: number;
}

export interface LocalProxyResult {
  succeeded: boolean;
  message: string;
  session?: LocalProxySession | null;
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
}

export interface GitHubLifecycleResult {
  succeeded: boolean;
  message: string;
  codespace?: CodespaceSnapshot | null;
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
