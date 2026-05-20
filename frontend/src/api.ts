import type { ProxySession, RuntimeResult, VpsNode, VpsNodeForm } from './types';

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
    throw new Error(text || `Request failed with ${response.status}`);
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
    })
};

