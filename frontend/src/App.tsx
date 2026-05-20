import { FormEvent, ReactNode, useCallback, useEffect, useMemo, useState } from 'react';
import {
  Activity,
  CheckCircle2,
  Clock3,
  Copy,
  Edit3,
  Play,
  RefreshCw,
  RotateCw,
  Shield,
  Square,
  Trash2,
  Wifi
} from 'lucide-react';
import { api } from './api';
import type {
  ActivityFilters,
  ActivitySummary,
  LocalProxyProfile,
  LocalProxyProfileForm,
  LocalProxySession,
  OperationalEvent
} from './types';

const emptyProfileForm: LocalProxyProfileForm = {
  name: '',
  bindHost: '127.0.0.1',
  localPort: 8901,
  proxyUsername: '',
  proxyPassword: '',
  idleShutdownMinutes: 30,
  notes: ''
};

const defaultFilters: ActivityFilters = {
  severity: '',
  eventType: '',
  correlationId: '',
  search: 'local_proxy',
  limit: 80
};

function App() {
  const [profiles, setProfiles] = useState<LocalProxyProfile[]>([]);
  const [session, setSession] = useState<LocalProxySession | null>(null);
  const [events, setEvents] = useState<OperationalEvent[]>([]);
  const [summary, setSummary] = useState<ActivitySummary | null>(null);
  const [filters, setFilters] = useState<ActivityFilters>(defaultFilters);
  const [form, setForm] = useState<LocalProxyProfileForm>(emptyProfileForm);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ kind: 'info' | 'error'; text: string } | null>(null);
  const [selectedEvent, setSelectedEvent] = useState<OperationalEvent | null>(null);

  const selectedProfile = useMemo(
    () => profiles.find((profile) => profile.id === editingId) ?? profiles[0] ?? null,
    [editingId, profiles]
  );

  const loadProfiles = useCallback(async () => {
    setProfiles(await api.localProxyProfiles());
  }, []);

  const loadSession = useCallback(async () => {
    setSession(await api.localProxySession());
  }, []);

  const loadActivity = useCallback(async () => {
    const [nextEvents, nextSummary] = await Promise.all([
      api.activity(filters),
      api.activitySummary()
    ]);
    setEvents(nextEvents);
    setSummary(nextSummary);
  }, [filters]);

  const loadAll = useCallback(async () => {
    await Promise.all([loadProfiles(), loadSession(), loadActivity()]);
  }, [loadActivity, loadProfiles, loadSession]);

  useEffect(() => {
    loadAll().catch((error) => setNotice({ kind: 'error', text: error.message }));
  }, [loadAll]);

  useEffect(() => {
    const handle = window.setInterval(() => {
      loadSession().catch(() => undefined);
      loadActivity().catch(() => undefined);
    }, 5000);
    return () => window.clearInterval(handle);
  }, [loadActivity, loadSession]);

  async function runAction(label: string, action: () => Promise<string>) {
    setBusy(label);
    setNotice(null);
    try {
      const message = await action();
      setNotice({ kind: 'info', text: message });
      await loadAll();
    } catch (error) {
      setNotice({ kind: 'error', text: error instanceof Error ? error.message : String(error) });
      await loadActivity().catch(() => undefined);
    } finally {
      setBusy(null);
    }
  }

  async function saveProfile(event: FormEvent) {
    event.preventDefault();
    await runAction(editingId ? 'update-profile' : 'create-profile', async () => {
      if (editingId) {
        await api.updateLocalProxyProfile(editingId, form);
        return 'Profile updated.';
      }

      await api.createLocalProxyProfile(form);
      setForm(emptyProfileForm);
      return 'Profile created.';
    });
  }

  function editProfile(profile: LocalProxyProfile) {
    setEditingId(profile.id);
    setForm({
      name: profile.name,
      bindHost: profile.bindHost,
      localPort: profile.localPort,
      proxyUsername: profile.proxyUsername ?? '',
      proxyPassword: '',
      idleShutdownMinutes: profile.idleShutdownMinutes,
      notes: profile.notes ?? ''
    });
  }

  function resetForm() {
    setEditingId(null);
    setForm(emptyProfileForm);
  }

  function updateForm<K extends keyof LocalProxyProfileForm>(field: K, value: LocalProxyProfileForm[K]) {
    setForm((current) => ({ ...current, [field]: value }));
  }

  const proxyExports = session
    ? [
        `export HTTP_PROXY=${session.proxyUrl}`,
        `export HTTPS_PROXY=${session.proxyUrl}`,
        `export http_proxy=${session.proxyUrl}`,
        `export https_proxy=${session.proxyUrl}`,
        'export NO_PROXY=localhost,127.0.0.1',
        'export no_proxy=localhost,127.0.0.1'
      ].join('\n')
    : '';

  return (
    <main className="shell">
      <header className="topbar">
        <div>
          <h1>Local Proxy Manager</h1>
          <p>Local listener, proxy health, activity, and idle shutdown</p>
        </div>
        <button className="icon-button" title="Refresh" onClick={() => runAction('refresh', async () => { await loadAll(); return 'Refreshed.'; })} disabled={busy !== null}>
          <RefreshCw size={18} />
        </button>
      </header>

      <section className="status-band">
        <StatusTile icon={<Wifi size={22} />} label="Status" value={session ? session.status : 'Stopped'} />
        <StatusTile icon={<Shield size={22} />} label="Endpoint" value={session?.proxyUrl ?? 'Not listening'} />
        <StatusTile icon={<Clock3 size={22} />} label="Idle stop" value={session ? formatDate(session.idleShutdownAt) : 'No active session'} />
      </section>

      {notice && <div className={`notice ${notice.kind === 'error' ? 'error' : ''}`}>{notice.text}</div>}

      <section className="active-strip">
        <span className={`badge ${badgeClass(session?.status ?? 'Stopped')}`}>{session?.status ?? 'Stopped'}</span>
        <strong>{session ? session.profileName : 'No active proxy session'}</strong>
        {session && <span>{session.activeConnections} active / {session.totalRequests} requests</span>}
        <div className="active-actions">
          <button title="Start selected profile" onClick={() => selectedProfile && runAction('start', async () => (await api.startLocalProxy(selectedProfile.id)).message)} disabled={busy !== null || !selectedProfile || session?.status === 'Running'}>
            <Play size={17} />
          </button>
          <button title="Stop active proxy" onClick={() => runAction('stop', async () => (await api.stopLocalProxy()).message)} disabled={busy !== null || !session}>
            <Square size={17} />
          </button>
          <button title="Probe active proxy" onClick={() => runAction('probe', async () => (await api.probeLocalProxy()).message)} disabled={busy !== null || !session}>
            <CheckCircle2 size={17} />
          </button>
        </div>
      </section>

      <section className="content-grid">
        <div className="panel-stack">
          <form className="editor" onSubmit={saveProfile}>
            <div className="section-title">
              <Wifi size={20} />
              <h2>{editingId ? 'Edit Profile' : 'Create Profile'}</h2>
            </div>
            <div className="form-grid">
              <label>
                Name
                <input value={form.name} onChange={(event) => updateForm('name', event.target.value)} required />
              </label>
              <label>
                Bind host
                <input value={form.bindHost} onChange={(event) => updateForm('bindHost', event.target.value)} required />
              </label>
              <label>
                Local port
                <input type="number" min="1" max="65535" value={form.localPort} onChange={(event) => updateForm('localPort', Number(event.target.value))} required />
              </label>
              <label>
                Idle minutes
                <input type="number" min="1" max="1440" value={form.idleShutdownMinutes} onChange={(event) => updateForm('idleShutdownMinutes', Number(event.target.value))} required />
              </label>
              <label>
                Username
                <input value={form.proxyUsername} onChange={(event) => updateForm('proxyUsername', event.target.value)} />
              </label>
              <label>
                Password
                <input type="password" value={form.proxyPassword} onChange={(event) => updateForm('proxyPassword', event.target.value)} placeholder={editingId ? 'Leave empty to keep current' : ''} />
              </label>
              <label className="wide">
                Notes
                <input value={form.notes} onChange={(event) => updateForm('notes', event.target.value)} />
              </label>
            </div>
            <div className="form-actions">
              <button type="submit" disabled={busy !== null}>{editingId ? 'Update' : 'Create'}</button>
              {editingId && <button type="button" onClick={resetForm} disabled={busy !== null}>Cancel</button>}
            </div>
          </form>

          {session && (
            <section className="editor">
              <div className="section-title">
                <Copy size={20} />
                <h2>Use Proxy</h2>
              </div>
              <div className="proxy-copy-row">
                <code>{session.proxyUrl}</code>
                <button title="Copy proxy URL" type="button" onClick={() => copyText(session.proxyUrl)}>
                  <Copy size={16} />
                </button>
              </div>
              <pre>{proxyExports}</pre>
              <button className="spaced" type="button" onClick={() => copyText(proxyExports)}>
                <Copy size={16} /> Copy exports
              </button>
            </section>
          )}
        </div>

        <div className="panel-stack">
          <section className="node-list">
            <div className="section-title">
              <Wifi size={20} />
              <h2>Profiles</h2>
            </div>
            {profiles.length === 0 && <div className="empty-state">No local proxy profiles yet.</div>}
            {profiles.map((profile) => (
              <article className={`node-card ${profile.id === selectedProfile?.id ? 'selected-row' : ''}`} key={profile.id}>
                <div className="node-main">
                  <div>
                    <h3>{profile.name}</h3>
                    <p>{profile.bindHost}:{profile.localPort}</p>
                  </div>
                  <span className={`badge ${badgeClass(profile.status)}`}>{profile.status}</span>
                </div>
                <div className="node-meta">
                  <span>{profile.idleShutdownMinutes}m idle</span>
                  <span>{profile.requiresAuthentication ? `auth ${profile.proxyUsername}` : 'no auth'}</span>
                  {profile.notes && <span>{profile.notes}</span>}
                </div>
                <div className="node-actions">
                  <button title="Start" onClick={() => runAction('start', async () => (await api.startLocalProxy(profile.id)).message)} disabled={busy !== null || session?.status === 'Running'}>
                    <Play size={16} />
                  </button>
                  <button title="Edit" onClick={() => editProfile(profile)} disabled={busy !== null}>
                    <Edit3 size={16} />
                  </button>
                  <button title="Delete" className="danger" onClick={() => confirmDelete(profile.name) && runAction('delete-profile', async () => { await api.deleteLocalProxyProfile(profile.id); return 'Profile deleted.'; })} disabled={busy !== null || session?.profileId === profile.id}>
                    <Trash2 size={16} />
                  </button>
                </div>
              </article>
            ))}
          </section>

          <section className="dashboard-list">
            <div className="section-title">
              <Activity size={20} />
              <h2>Activity</h2>
            </div>
            {summary && (
              <div className="activity-summary compact">
                <StatusTile label="Recent" value={String(summary.recentCount)} />
                <StatusTile label="Warnings" value={String(summary.warningCount)} />
                <StatusTile label="Errors" value={String(summary.errorCount)} />
              </div>
            )}
            <ActivityFilters filters={filters} onChange={setFilters} onRefresh={loadActivity} busy={busy !== null} />
            <EventTable events={events} onSelect={setSelectedEvent} />
          </section>
        </div>
      </section>

      {selectedEvent && <EventDetail event={selectedEvent} onClose={() => setSelectedEvent(null)} />}
    </main>
  );
}

function StatusTile({ icon, label, value }: { icon?: ReactNode; label: string; value: string }) {
  return (
    <div className="status-tile">
      {icon}
      <div>
        <span>{label}</span>
        <strong>{value}</strong>
      </div>
    </div>
  );
}

function ActivityFilters({ filters, onChange, onRefresh, busy }: {
  filters: ActivityFilters;
  onChange: (filters: ActivityFilters) => void;
  onRefresh: () => Promise<void>;
  busy: boolean;
}) {
  return (
    <div className="activity-filters compact">
      <label>
        Severity
        <select value={filters.severity} onChange={(event) => onChange({ ...filters, severity: event.target.value })}>
          <option value="">All</option>
          <option value="Error">Error</option>
          <option value="Warning">Warning</option>
          <option value="Information">Information</option>
          <option value="Debug">Debug</option>
        </select>
      </label>
      <label>
        Event
        <input value={filters.eventType} onChange={(event) => onChange({ ...filters, eventType: event.target.value })} placeholder="local_proxy" />
      </label>
      <label>
        Search
        <input value={filters.search} onChange={(event) => onChange({ ...filters, search: event.target.value })} />
      </label>
      <label>
        Limit
        <input type="number" min="10" max="500" value={filters.limit} onChange={(event) => onChange({ ...filters, limit: Number(event.target.value) })} />
      </label>
      <button title="Refresh activity" type="button" onClick={() => onRefresh()} disabled={busy}>
        <RotateCw size={16} />
      </button>
    </div>
  );
}

function EventTable({ events, onSelect }: { events: OperationalEvent[]; onSelect: (event: OperationalEvent) => void }) {
  if (events.length === 0) {
    return <div className="empty-state">No activity matches the current filters.</div>;
  }

  return (
    <div className="event-table">
      <div className="event-row event-header">
        <span>Severity</span>
        <span>Time</span>
        <span>Event</span>
        <span>Message</span>
        <span>Command</span>
        <span>Exit</span>
      </div>
      {events.map((event) => (
        <button className="event-row" key={event.id} onClick={() => onSelect(event)}>
          <span className={`badge ${badgeClass(event.severity)}`}>{event.severity}</span>
          <span>{formatDate(event.timestamp)}</span>
          <span>{event.eventType}</span>
          <span>{event.message}</span>
          <span>{event.commandKind ?? '-'}</span>
          <span>{event.exitCode ?? '-'}</span>
        </button>
      ))}
    </div>
  );
}

function EventDetail({ event, onClose }: { event: OperationalEvent; onClose: () => void }) {
  return (
    <div className="event-detail" role="dialog" aria-modal="true">
      <section className="event-detail-panel">
        <div className="node-main">
          <div>
            <h3>{event.eventType}</h3>
            <p>{event.message}</p>
          </div>
          <button onClick={onClose}>Close</button>
        </div>
        <dl className="event-fields">
          <Field label="Severity" value={event.severity} />
          <Field label="Timestamp" value={formatDate(event.timestamp)} />
          <Field label="Correlation" value={event.correlationId ?? '-'} />
          <Field label="Command" value={event.commandDisplay ?? event.commandKind ?? '-'} />
          <Field label="Duration" value={event.durationMs ? `${event.durationMs} ms` : '-'} />
          <Field label="Exit" value={event.exitCode?.toString() ?? '-'} />
        </dl>
        {event.standardOutputSnippet && <pre>{event.standardOutputSnippet}</pre>}
        {event.standardErrorSnippet && <pre className="stderr">{event.standardErrorSnippet}</pre>}
        {event.detailsJson && <pre>{event.detailsJson}</pre>}
      </section>
    </div>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  );
}

function badgeClass(value: string) {
  return value.toLowerCase();
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  }).format(new Date(value));
}

function copyText(value: string) {
  navigator.clipboard?.writeText(value).catch(() => undefined);
}

function confirmDelete(name: string) {
  return window.confirm(`Delete profile "${name}"?`);
}

export default App;
