import type {
  ActivityFilters,
  ActivitySummary,
  CodespaceSnapshot,
  CreateCodespaceForm,
  GitHubAccount,
  GitHubAccountForm,
  GitHubLifecycleResult,
  GitHubUsage,
  LocalProxyProfile,
  LocalProxyProfileForm,
  LocalProxyResult,
  LocalProxySession,
  OperationalEvent,
  RuntimeDiagnostics
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
  localProxyProfiles: () => request<LocalProxyProfile[]>('/api/local-proxy/profiles'),
  createLocalProxyProfile: (form: LocalProxyProfileForm) =>
    request<LocalProxyProfile>('/api/local-proxy/profiles', {
      method: 'POST',
      body: JSON.stringify(emptyToNull(form))
    }),
  updateLocalProxyProfile: (id: string, form: LocalProxyProfileForm) =>
    request<LocalProxyProfile>(`/api/local-proxy/profiles/${id}`, {
      method: 'PUT',
      body: JSON.stringify(emptyToNull(form))
    }),
  deleteLocalProxyProfile: (id: string) =>
    request<void>(`/api/local-proxy/profiles/${id}`, {
      method: 'DELETE'
    }),
  localProxySession: () => request<LocalProxySession | null>('/api/local-proxy/session'),
  startLocalProxy: (profileId: string) =>
    request<LocalProxyResult>(`/api/local-proxy/profiles/${profileId}/start`, {
      method: 'POST'
    }),
  stopLocalProxy: () =>
    request<LocalProxyResult>('/api/local-proxy/stop', {
      method: 'POST'
    }),
  probeLocalProxy: () =>
    request<LocalProxyResult>('/api/local-proxy/probe', {
      method: 'POST'
    }),
  accounts: () => request<GitHubAccount[]>('/api/github/accounts'),
  createAccount: (form: GitHubAccountForm) =>
    request<GitHubAccount>('/api/github/accounts', {
      method: 'POST',
      body: JSON.stringify(emptyToNull(form))
    }),
  updateAccount: (id: string, form: GitHubAccountForm) =>
    request<GitHubAccount>(`/api/github/accounts/${id}`, {
      method: 'PUT',
      body: JSON.stringify(emptyToNull(form))
    }),
  deleteAccount: (id: string) =>
    request<void>(`/api/github/accounts/${id}`, {
      method: 'DELETE'
    }),
  validateAccount: (id: string) =>
    request<GitHubAccount>(`/api/github/accounts/${id}/validate`, {
      method: 'POST'
    }),
  syncAccount: (id: string) =>
    request<CodespaceSnapshot[]>(`/api/github/accounts/${id}/sync`, {
      method: 'POST'
    }),
  usage: (id: string) => request<GitHubUsage>(`/api/github/accounts/${id}/usage`),
  codespaces: (id: string) => request<CodespaceSnapshot[]>(`/api/github/accounts/${id}/codespaces`),
  codespace: (id: string, name: string) =>
    request<CodespaceSnapshot>(`/api/github/accounts/${id}/codespaces/${encodeURIComponent(name)}`),
  createCodespace: (id: string, form: CreateCodespaceForm) =>
    request<GitHubLifecycleResult>(`/api/github/accounts/${id}/codespaces`, {
      method: 'POST',
      body: JSON.stringify(emptyToNull(form))
    }),
  startCodespace: (accountId: string, name: string) =>
    request<GitHubLifecycleResult>(`/api/github/accounts/${accountId}/codespaces/${encodeURIComponent(name)}/start`, {
      method: 'POST'
    }),
  stopCodespace: (accountId: string, name: string) =>
    request<GitHubLifecycleResult>(`/api/github/accounts/${accountId}/codespaces/${encodeURIComponent(name)}/stop`, {
      method: 'POST'
    }),
  exportCodespace: (accountId: string, name: string) =>
    request<GitHubLifecycleResult>(`/api/github/accounts/${accountId}/codespaces/${encodeURIComponent(name)}/export`, {
      method: 'POST'
    }),
  deleteCodespace: (accountId: string, name: string) =>
    request<void>(`/api/github/accounts/${accountId}/codespaces/${encodeURIComponent(name)}`, {
      method: 'DELETE'
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

function emptyToNull<T extends object>(value: T) {
  return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, item === '' ? null : item]));
}

function formatError(text: string, status: number) {
  if (!text) {
    return `Request failed with ${status}`;
  }

  try {
    const body = JSON.parse(text) as { error?: string; correlationId?: string; errors?: Record<string, string[]> };
    if (body.error && body.correlationId) {
      return `${body.error} Correlation ${body.correlationId}`;
    }
    if (body.error) {
      return body.error;
    }
    if (body.errors) {
      return Object.entries(body.errors)
        .map(([key, messages]) => `${key}: ${messages.join(', ')}`)
        .join(' ');
    }
  } catch {
    return text;
  }

  return text;
}
