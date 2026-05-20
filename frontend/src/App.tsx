import { FormEvent, useEffect, useMemo, useState } from 'react';
import {
  Activity,
  Cable,
  CheckCircle2,
  CircleStop,
  Pencil,
  Play,
  Plus,
  RefreshCw,
  Server,
  Trash2,
  UploadCloud,
  XCircle
} from 'lucide-react';
import { api } from './api';
import type { ProxySession, RuntimeResult, VpsNode, VpsNodeForm } from './types';

const emptyForm: VpsNodeForm = {
  name: '',
  host: '',
  sshPort: 22,
  sshUsername: 'root',
  sshKeyPath: '~/.ssh/id_rsa',
  region: '',
  notes: '',
  localPort: 8901,
  remoteHttpPort: 3128,
  remoteSocksPort: 1080,
  proxyUsername: 'proxy',
  proxyPassword: ''
};

const idleMinutes = 30;

export default function App() {
  const [nodes, setNodes] = useState<VpsNode[]>([]);
  const [activeSession, setActiveSession] = useState<ProxySession | null>(null);
  const [form, setForm] = useState<VpsNodeForm>(emptyForm);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<string>('Ready');
  const [error, setError] = useState<string | null>(null);

  async function load() {
    const [nodeList, session] = await Promise.all([api.nodes(), api.activeSession()]);
    setNodes(nodeList);
    setActiveSession(session);
  }

  useEffect(() => {
    load().catch((err: unknown) => setError(errorMessage(err)));
    const timer = window.setInterval(() => {
      load().catch(() => undefined);
    }, 15000);
    return () => window.clearInterval(timer);
  }, []);

  const selectedNode = useMemo(
    () => nodes.find((node) => node.id === activeSession?.nodeId) ?? null,
    [activeSession, nodes]
  );

  async function runAction(label: string, action: () => Promise<unknown>) {
    setBusy(label);
    setError(null);
    try {
      const result = await action();
      setMessage(formatResult(label, result));
      await load();
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(null);
    }
  }

  async function saveNode(event: FormEvent) {
    event.preventDefault();
    await runAction(editingId ? 'update-node' : 'create-node', async () => {
      if (editingId) {
        return api.updateNode(editingId, form);
      }

      return api.createNode(form);
    });
    setForm(emptyForm);
    setEditingId(null);
  }

  function editNode(node: VpsNode) {
    setEditingId(node.id);
    setForm({
      name: node.name,
      host: node.host,
      sshPort: node.sshPort,
      sshUsername: node.sshUsername,
      sshKeyPath: node.sshKeyPath,
      region: node.region ?? '',
      notes: node.notes ?? '',
      localPort: node.localPort,
      remoteHttpPort: node.remoteHttpPort,
      remoteSocksPort: node.remoteSocksPort,
      proxyUsername: node.proxyUsername,
      proxyPassword: ''
    });
  }

  function updateField<K extends keyof VpsNodeForm>(field: K, value: VpsNodeForm[K]) {
    setForm((current) => ({ ...current, [field]: value }));
  }

  return (
    <main className="shell">
      <header className="topbar">
        <div>
          <h1>GH Proxy</h1>
          <p>Local control plane for VPS proxy nodes</p>
        </div>
        <button className="icon-button" onClick={() => runAction('refresh', load)} disabled={busy !== null} title="Refresh">
          <RefreshCw size={18} />
        </button>
      </header>

      <section className="status-band">
        <div className="status-tile">
          <Cable size={20} />
          <div>
            <span>Local endpoint</span>
            <strong>{activeSession ? `127.0.0.1:${activeSession.localPort}` : 'Off'}</strong>
          </div>
        </div>
        <div className="status-tile">
          <Activity size={20} />
          <div>
            <span>Session</span>
            <strong>{activeSession ? `${activeSession.status} on ${activeSession.nodeName}` : 'No active tunnel'}</strong>
          </div>
        </div>
        <div className="status-tile">
          <CircleStop size={20} />
          <div>
            <span>Idle shutdown</span>
            <strong>{activeSession ? idleText(activeSession.lastActivityAt) : `${idleMinutes}m after activity`}</strong>
          </div>
        </div>
      </section>

      {(error || message) && (
        <section className={error ? 'notice error' : 'notice'}>
          {error ? <XCircle size={18} /> : <CheckCircle2 size={18} />}
          <span>{error ?? message}</span>
        </section>
      )}

      <section className="content-grid">
        <form className="editor" onSubmit={saveNode}>
          <div className="section-title">
            <Server size={20} />
            <h2>{editingId ? 'Edit VPS Node' : 'Add VPS Node'}</h2>
          </div>
          <div className="form-grid">
            <label>
              Name
              <input value={form.name} onChange={(e) => updateField('name', e.target.value)} required />
            </label>
            <label>
              Host
              <input value={form.host} onChange={(e) => updateField('host', e.target.value)} required />
            </label>
            <label>
              SSH user
              <input value={form.sshUsername} onChange={(e) => updateField('sshUsername', e.target.value)} required />
            </label>
            <label>
              SSH port
              <input type="number" min="1" max="65535" value={form.sshPort} onChange={(e) => updateField('sshPort', Number(e.target.value))} required />
            </label>
            <label className="wide">
              SSH key path
              <input value={form.sshKeyPath} onChange={(e) => updateField('sshKeyPath', e.target.value)} required />
            </label>
            <label>
              Region
              <input value={form.region} onChange={(e) => updateField('region', e.target.value)} />
            </label>
            <label>
              Local port
              <input type="number" min="1" max="65535" value={form.localPort} onChange={(e) => updateField('localPort', Number(e.target.value))} required />
            </label>
            <label>
              HTTP port
              <input type="number" min="1" max="65535" value={form.remoteHttpPort} onChange={(e) => updateField('remoteHttpPort', Number(e.target.value))} required />
            </label>
            <label>
              SOCKS port
              <input type="number" min="1" max="65535" value={form.remoteSocksPort} onChange={(e) => updateField('remoteSocksPort', Number(e.target.value))} required />
            </label>
            <label>
              Proxy user
              <input value={form.proxyUsername} onChange={(e) => updateField('proxyUsername', e.target.value)} required />
            </label>
            <label>
              Proxy password
              <input type="password" value={form.proxyPassword} onChange={(e) => updateField('proxyPassword', e.target.value)} required={!editingId} placeholder={editingId ? 'Leave unchanged' : ''} />
            </label>
            <label className="wide">
              Notes
              <input value={form.notes} onChange={(e) => updateField('notes', e.target.value)} />
            </label>
          </div>
          <div className="form-actions">
            <button type="submit" disabled={busy !== null}>
              <Plus size={16} />
              {editingId ? 'Save node' : 'Add node'}
            </button>
            {editingId && (
              <button type="button" className="secondary" onClick={() => { setEditingId(null); setForm(emptyForm); }}>
                Cancel
              </button>
            )}
          </div>
        </form>

        <section className="node-list">
          <div className="section-title">
            <Server size={20} />
            <h2>Nodes</h2>
          </div>
          {nodes.length === 0 ? (
            <div className="empty-state">No VPS nodes yet.</div>
          ) : (
            nodes.map((node) => (
              <article className="node-card" key={node.id}>
                <div className="node-main">
                  <div>
                    <h3>{node.name}</h3>
                    <p>{node.sshUsername}@{node.host}:{node.sshPort}</p>
                  </div>
                  <span className={`badge ${node.status.toLowerCase()}`}>{node.status}</span>
                </div>
                <div className="node-meta">
                  <span>Local {node.localPort}</span>
                  <span>HTTP {node.remoteHttpPort}</span>
                  <span>SOCKS {node.remoteSocksPort}</span>
                  {node.region && <span>{node.region}</span>}
                </div>
                <div className="node-actions">
                  <button title="Bootstrap" onClick={() => runAction('bootstrap-node', () => api.bootstrapNode(node.id))} disabled={busy !== null}>
                    <UploadCloud size={16} />
                  </button>
                  <button title="Probe status" onClick={() => runAction('probe-node', () => api.probeNode(node.id))} disabled={busy !== null}>
                    <RefreshCw size={16} />
                  </button>
                  <button title="Start proxy" onClick={() => runAction('start-proxy', () => api.startProxy(node.id))} disabled={busy !== null || activeSession !== null}>
                    <Play size={16} />
                  </button>
                  <button title="Edit" onClick={() => editNode(node)} disabled={busy !== null}>
                    <Pencil size={16} />
                  </button>
                  <button title="Delete" className="danger" onClick={() => runAction('delete-node', () => api.deleteNode(node.id))} disabled={busy !== null || selectedNode?.id === node.id}>
                    <Trash2 size={16} />
                  </button>
                </div>
              </article>
            ))
          )}
        </section>
      </section>

      {activeSession && (
        <section className="active-strip">
          <span>PID {activeSession.tunnelProcessId ?? 'unknown'}</span>
          <span>Started {formatDate(activeSession.startedAt)}</span>
          <span>Last activity {formatDate(activeSession.lastActivityAt)}</span>
          <button className="danger filled" onClick={() => runAction('stop-proxy', api.stopProxy)} disabled={busy !== null}>
            <CircleStop size={16} />
            Stop proxy
          </button>
        </section>
      )}
    </main>
  );
}

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : 'Unexpected error';
}

function formatResult(label: string, result: unknown) {
  if (isRuntimeResult(result)) {
    return result.message;
  }

  return label.replaceAll('-', ' ');
}

function isRuntimeResult(result: unknown): result is RuntimeResult {
  return typeof result === 'object' && result !== null && 'message' in result && 'succeeded' in result;
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  }).format(new Date(value));
}

function idleText(lastActivityAt: string) {
  const lastActivity = new Date(lastActivityAt).getTime();
  const shutdownAt = lastActivity + idleMinutes * 60 * 1000;
  const remainingMs = Math.max(0, shutdownAt - Date.now());
  const remainingMinutes = Math.ceil(remainingMs / 60000);
  return remainingMinutes <= 0 ? 'Stopping soon' : `${remainingMinutes}m remaining`;
}
