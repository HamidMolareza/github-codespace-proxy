export type VpsNodeStatus = 'Unknown' | 'Ready' | 'Running' | 'Stopped' | 'Error';
export type ProxySessionStatus = 'Starting' | 'Running' | 'Stopping' | 'Stopped' | 'Error';

export interface VpsNode {
  id: string;
  name: string;
  host: string;
  sshPort: number;
  sshUsername: string;
  sshKeyPath: string;
  region?: string | null;
  notes?: string | null;
  localPort: number;
  remoteHttpPort: number;
  remoteSocksPort: number;
  proxyUsername: string;
  status: VpsNodeStatus;
  createdAt: string;
  updatedAt: string;
}

export interface VpsNodeForm {
  name: string;
  host: string;
  sshPort: number;
  sshUsername: string;
  sshKeyPath: string;
  region: string;
  notes: string;
  localPort: number;
  remoteHttpPort: number;
  remoteSocksPort: number;
  proxyUsername: string;
  proxyPassword: string;
}

export interface ProxySession {
  id: string;
  nodeId: string;
  nodeName: string;
  status: ProxySessionStatus;
  tunnelProcessId?: number | null;
  localPort: number;
  remotePort: number;
  startedAt: string;
  lastActivityAt: string;
  stoppedAt?: string | null;
  lastError?: string | null;
}

export interface RuntimeResult {
  succeeded: boolean;
  message: string;
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
