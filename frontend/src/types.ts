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

