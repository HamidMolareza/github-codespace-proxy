import type {
  ActivityFilters,
  ActivitySummary,
  OperationalEvent,
  ProxySession,
  RuntimeDiagnostics,
  RuntimeResult,
  VpsNode,
  VpsNodeForm
} from './types';

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    headers: {
      'Content-Type': 'application/json',
      ...init?.headers
    },
    ...init
  });

  if (!response.ok) {
    const text = await response.text();
    throw new Error(formatError(text, response.status));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return response.json() as Promise<T>;
}

export const api = {
  nodes: () => request<VpsNode[]>('/api/nodes'),
  activeSession: () => request<ProxySession | null>('/api/sessions/active'),
  createNode: (form: VpsNodeForm) =>
    request<VpsNode>('/api/nodes', {
      method: 'POST',
      body: JSON.stringify(form)
    }),
  updateNode: (id: string, form: VpsNodeForm) =>
    request<VpsNode>(`/api/nodes/${id}`, {
      method: 'PUT',
      body: JSON.stringify(form)
    }),
  deleteNode: (id: string) =>
    request<void>(`/api/nodes/${id}`, {
      method: 'DELETE'
    }),
  bootstrapNode: (id: string) =>
    request<RuntimeResult>(`/api/nodes/${id}/bootstrap`, {
      method: 'POST'
    }),
  probeNode: (id: string) =>
    request<RuntimeResult>(`/api/nodes/${id}/status`, {
      method: 'POST'
    }),
  startProxy: (id: string) =>
    request<ProxySession>(`/api/sessions/start/${id}`, {
      method: 'POST'
    }),
  stopProxy: () =>
    request<ProxySession | null>('/api/sessions/stop', {
      method: 'POST'
    }),
  activity: (filters: ActivityFilters) => {
    const params = new URLSearchParams();
    if (filters.severity) {
      params.set('severity', filters.severity);
    }
    if (filters.eventType) {
      params.set('eventType', filters.eventType);
    }
    if (filters.correlationId) {
      params.set('correlationId', filters.correlationId);
    }
    if (filters.search) {
      params.set('search', filters.search);
    }
    params.set('limit', String(filters.limit));
    return request<OperationalEvent[]>(`/api/activity?${params.toString()}`);
  },
  activitySummary: () => request<ActivitySummary>('/api/activity/summary'),
  runtimeDiagnostics: () => request<RuntimeDiagnostics>('/api/diagnostics/runtime')
};

function formatError(text: string, status: number) {
  if (!text) {
    return `Request failed with ${status}`;
  }

  try {
    const body = JSON.parse(text) as { error?: string; correlationId?: string };
    if (body.error && body.correlationId) {
      return `${body.error} Correlation ${body.correlationId}`;
    }
    if (body.error) {
      return body.error;
    }
  } catch {
    return text;
  }

  return text;
}
