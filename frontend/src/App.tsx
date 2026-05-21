import { FormEvent, ReactNode, useCallback, useEffect, useMemo, useState } from 'react';
import {
  Activity,
  AlertTriangle,
  CheckCircle2,
  CircleStop,
  Cloud,
  Copy,
  Database,
  Download,
  ExternalLink,
  Filter,
  Github,
  Pencil,
  Play,
  Plus,
  RefreshCw,
  ShieldCheck,
  Square,
  Terminal,
  Trash2,
  Wifi,
  Wrench,
  XCircle
} from 'lucide-react';
import { api } from './api';
import type {
  ActivityFilters,
  ActivitySummary,
  CodespaceSnapshot,
  CreateCodespaceForm,
  GitHubAccount,
  GitHubAccountForm,
  GitHubLifecycleResult,
  GitHubUsage,
  LocalProxyAutomationStatus,
  LocalProxyProfile,
  LocalProxySettingsForm,
  LocalProxyResult,
  LocalProxySession,
  OperationalEvent,
  RuntimeDiagnostics
} from './types';

const emptyAccountForm: GitHubAccountForm = {
  displayName: '',
  username: '',
  personalAccessToken: '',
  plan: 'Unknown'
};

const emptyCodespaceForm: CreateCodespaceForm = {
  repositoryOwner: '',
  repositoryName: '',
  ref: '',
  geo: 'UsEast',
  machine: '',
  displayName: '',
  idleTimeoutMinutes: 30
};

const emptySettingsForm: LocalProxySettingsForm = {
  bindHost: '127.0.0.1',
  localPort: 8910,
  proxyUsername: '',
  proxyPassword: '',
  idleShutdownMinutes: 30
};

const defaultActivityFilters: ActivityFilters = {
  severity: '',
  eventType: '',
  correlationId: '',
  search: '',
  limit: 100
};

type AppTab = 'codespaces' | 'local-proxy' | 'activity';
type Notice = { kind: 'info' | 'error'; text: string };

export default function App() {
  const [activeTab, setActiveTab] = useState<AppTab>('codespaces');
  const [accounts, setAccounts] = useState<GitHubAccount[]>([]);
  const [selectedAccountId, setSelectedAccountId] = useState<string | null>(null);
  const [codespaces, setCodespaces] = useState<CodespaceSnapshot[]>([]);
  const [usage, setUsage] = useState<GitHubUsage | null>(null);
  const [profiles, setProfiles] = useState<LocalProxyProfile[]>([]);
  const [localSession, setLocalSession] = useState<LocalProxySession | null>(null);
  const [localStatus, setLocalStatus] = useState<LocalProxyAutomationStatus | null>(null);
  const [activityEvents, setActivityEvents] = useState<OperationalEvent[]>([]);
  const [activitySummary, setActivitySummary] = useState<ActivitySummary | null>(null);
  const [diagnostics, setDiagnostics] = useState<RuntimeDiagnostics | null>(null);
  const [activityFilters, setActivityFilters] = useState<ActivityFilters>(defaultActivityFilters);
  const [selectedEvent, setSelectedEvent] = useState<OperationalEvent | null>(null);
  const [accountForm, setAccountForm] = useState<GitHubAccountForm>(emptyAccountForm);
  const [codespaceForm, setCodespaceForm] = useState<CreateCodespaceForm>(emptyCodespaceForm);
  const [settingsForm, setSettingsForm] = useState<LocalProxySettingsForm>(emptySettingsForm);
  const [editingAccountId, setEditingAccountId] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<Notice>({ kind: 'info', text: 'Ready' });
  const [codespaceProgress, setCodespaceProgress] = useState<Record<string, string>>({});

  const selectedAccount = useMemo(
    () => accounts.find((account) => account.id === selectedAccountId) ?? null,
    [accounts, selectedAccountId]
  );

  const selectedProfile = profiles[0] ?? null;
  const codespaceProxyMissingTools = useMemo(() => {
    const requiredTools = new Set(['Xray', 'GitHub CLI', 'ssh']);
    return diagnostics?.tools.filter((tool) => requiredTools.has(tool.name) && !tool.available).map((tool) => tool.name) ?? [];
  }, [diagnostics]);

  const loadAccounts = useCallback(async () => {
    const nextAccounts = await api.accounts();
    setAccounts(nextAccounts);
    setSelectedAccountId((current) => (nextAccounts.some((account) => account.id === current) ? current : (nextAccounts[0]?.id ?? null)));
  }, []);

  const loadCodespaces = useCallback(async (accountId: string | null = selectedAccountId) => {
    if (!accountId) {
      setCodespaces([]);
      setUsage(null);
      return;
    }

    const [nextCodespaces, nextUsage] = await Promise.all([
      api.codespaces(accountId),
      api.usage(accountId).catch(() => null)
    ]);
    setCodespaces(nextCodespaces);
    setUsage(nextUsage);
  }, [selectedAccountId]);

  const loadLocalProxy = useCallback(async () => {
    const status = await api.localProxyStatus();
    const settings = status.settings;
    setLocalStatus(status);
    setProfiles([settings]);
    setSettingsForm((current) => ({
      bindHost: settings.bindHost,
      localPort: settings.localPort,
      proxyUsername: settings.proxyUsername ?? '',
      proxyPassword: current.proxyPassword,
      idleShutdownMinutes: settings.idleShutdownMinutes
    }));
    setLocalSession(status.session ?? null);
  }, []);

  const loadActivity = useCallback(async (filters = activityFilters) => {
    const [events, summary, runtimeDiagnostics] = await Promise.all([
      api.activity(filters),
      api.activitySummary(),
      api.runtimeDiagnostics()
    ]);
    setActivityEvents(events);
    setActivitySummary(summary);
    setDiagnostics(runtimeDiagnostics);
  }, [activityFilters]);

  const loadAll = useCallback(async () => {
    await loadAccounts();
    await Promise.all([loadLocalProxy(), loadActivity()]);
  }, [loadAccounts, loadActivity, loadLocalProxy]);

  useEffect(() => {
    loadAll().catch((error) => setNotice({ kind: 'error', text: errorMessage(error) }));
  }, [loadAll]);

  useEffect(() => {
    loadCodespaces(selectedAccountId).catch(() => undefined);
  }, [loadCodespaces, selectedAccountId]);

  useEffect(() => {
    const timer = window.setInterval(() => {
      loadAccounts().catch(() => undefined);
      loadCodespaces().catch(() => undefined);
      loadLocalProxy().catch(() => undefined);
      loadActivity().catch(() => undefined);
    }, 20000);
    return () => window.clearInterval(timer);
  }, [loadAccounts, loadActivity, loadCodespaces, loadLocalProxy]);

  async function runAction(label: string, action: () => Promise<unknown>) {
    setBusy(label);
    setNotice({ kind: 'info', text: label.replaceAll('-', ' ') });
    try {
      const result = await action();
      setNotice({ kind: 'info', text: formatResult(label, result) });
      await loadAccounts();
      await loadLocalProxy();
      const accountId = lifecycleAccountId(result) ?? selectedAccountId;
      await loadCodespaces(accountId);
      await loadActivity();
    } catch (error) {
      setNotice({ kind: 'error', text: errorMessage(error) });
      await loadActivity().catch(() => undefined);
    } finally {
      setBusy(null);
    }
  }

  async function saveAccount(event: FormEvent) {
    event.preventDefault();
    await runAction(editingAccountId ? 'update-account' : 'create-account', async () => {
      if (editingAccountId) {
        return api.updateAccount(editingAccountId, accountForm);
      }

      const created = await api.createAccount(accountForm);
      setSelectedAccountId(created.id);
      return created;
    });
    setAccountForm(emptyAccountForm);
    setEditingAccountId(null);
  }

  async function createCodespace(event: FormEvent) {
    event.preventDefault();
    if (!selectedAccountId) {
      setNotice({ kind: 'error', text: 'Select an account first.' });
      return;
    }

    await runAction('create-codespace', async () => {
      const result = await api.createCodespace(selectedAccountId, codespaceForm);
      if (result.codespace) {
        await pollCodespace(selectedAccountId, result.codespace.name, isAvailableState, 'Creating');
      }
      return result;
    });
    setCodespaceForm(emptyCodespaceForm);
  }

  async function startCodespace(accountId: string, name: string) {
    const key = progressKey(accountId, name);
    setCodespaceProgress((current) => ({ ...current, [key]: 'Starting proxy' }));
    try {
      await runAction('start-codespace-proxy', async () => {
        const result = await api.startCodespaceProxy(accountId, name, selectedProfile?.id ?? null);
        await loadLocalProxy();
        await loadCodespaces(accountId);
        return result;
      });
    } finally {
      setCodespaceProgress((current) => clearProgress(current, key));
    }
  }

  async function stopCodespace(accountId: string, name: string) {
    await runAction('stop-codespace', async () => {
      const result = await api.stopCodespace(accountId, name);
      await pollCodespace(accountId, name, isStoppedState, 'Stopping');
      return result;
    });
  }

  async function refreshCodespace(accountId: string, name: string) {
    const key = progressKey(accountId, name);
    setCodespaceProgress((current) => ({ ...current, [key]: 'Refreshing' }));
    try {
      await api.codespace(accountId, name);
      await loadCodespaces(accountId);
    } finally {
      setCodespaceProgress((current) => clearProgress(current, key));
    }
  }

  async function pollCodespace(accountId: string, name: string, done: (state: string) => boolean, label: string) {
    const key = progressKey(accountId, name);
    setCodespaceProgress((current) => ({ ...current, [key]: label }));
    for (let attempt = 0; attempt < 30; attempt += 1) {
      await delay(3000);
      const snapshot = await api.codespace(accountId, name);
      await loadCodespaces(accountId);
      setCodespaceProgress((current) => ({ ...current, [key]: `${label}: ${snapshot.state}` }));
      if (done(snapshot.state)) {
        setCodespaceProgress((current) => clearProgress(current, key));
        return;
      }
    }
    setCodespaceProgress((current) => ({ ...current, [key]: `${label}: still pending` }));
  }

  async function saveSettings(event: FormEvent) {
    event.preventDefault();
    await runAction('update-proxy-settings', async () => {
      const updated = await api.updateLocalProxySettings(settingsForm);
      setProfiles([updated]);
      setSettingsForm((current) => ({ ...current, proxyPassword: '' }));
      return updated;
    });
  }

  function editAccount(account: GitHubAccount) {
    setEditingAccountId(account.id);
    setAccountForm({
      displayName: account.displayName,
      username: account.username,
      personalAccessToken: '',
      plan: account.plan
    });
  }

  function updateAccountField<K extends keyof GitHubAccountForm>(field: K, value: GitHubAccountForm[K]) {
    setAccountForm((current) => ({ ...current, [field]: value }));
  }

  function updateCodespaceField<K extends keyof CreateCodespaceForm>(field: K, value: CreateCodespaceForm[K]) {
    setCodespaceForm((current) => ({ ...current, [field]: value }));
  }

  function updateSettingsField<K extends keyof LocalProxySettingsForm>(field: K, value: LocalProxySettingsForm[K]) {
    setSettingsForm((current) => ({ ...current, [field]: value }));
  }

  function updateActivityFilter<K extends keyof ActivityFilters>(field: K, value: ActivityFilters[K]) {
    setActivityFilters((current) => ({ ...current, [field]: value }));
  }

  async function applyActivityFilters(event: FormEvent) {
    event.preventDefault();
    await runAction('refresh-activity', () => loadActivity(activityFilters));
  }

  async function clearActivity() {
    if (!confirmAction('Delete all Activity log entries?')) {
      return;
    }

    setBusy('clear-activity');
    setNotice({ kind: 'info', text: 'clear activity' });
    try {
      const result = await api.clearActivity();
      setActivityEvents([]);
      setActivitySummary({
        recentCount: 0,
        errorCount: 0,
        warningCount: 0,
        commandFailureCount: 0,
        averageCommandDurationMs: null,
        lastError: null
      });
      setSelectedEvent(null);
      setNotice({ kind: 'info', text: `Deleted ${result.deletedCount} activity log entries and ${result.deletedFileCount} log files.` });
    } catch (error) {
      setNotice({ kind: 'error', text: errorMessage(error) });
    } finally {
      setBusy(null);
    }
  }

  return (
    <main className="shell">
      <header className="topbar">
        <div>
          <h1>GitHub Codespaces Manager</h1>
          <p>Run a GitHub Codespace-backed proxy with one HTTP/SOCKS port</p>
        </div>
        <button className="icon-button" onClick={() => runAction('refresh', loadAll)} disabled={busy !== null} title="Refresh">
          <RefreshCw size={18} />
        </button>
      </header>

      <section className="status-band">
        <StatusTile icon={<Github size={20} />} label="Accounts" value={String(accounts.length)} />
        <StatusTile icon={<Cloud size={20} />} label="Codespaces" value={String(codespaces.length)} />
        <StatusTile icon={<Wifi size={20} />} label="Local proxy" value={localSession ? localSession.proxyUrl : 'Stopped'} />
      </section>

      {notice && (
        <section className={notice.kind === 'error' ? 'notice error' : 'notice'}>
          {notice.kind === 'error' ? <XCircle size={18} /> : <CheckCircle2 size={18} />}
          <span>{notice.text}</span>
        </section>
      )}

      <section className="tabs">
        <button className={activeTab === 'codespaces' ? 'active-tab' : ''} onClick={() => setActiveTab('codespaces')}>
          <Cloud size={16} />
          Codespaces
        </button>
        <button className={activeTab === 'local-proxy' ? 'active-tab' : ''} onClick={() => setActiveTab('local-proxy')}>
          <Wifi size={16} />
          Codespace Proxy
        </button>
        <button className={activeTab === 'activity' ? 'active-tab' : ''} onClick={() => setActiveTab('activity')}>
          <Activity size={16} />
          Activity
        </button>
      </section>

      {activeTab === 'codespaces' && (
        <CodespacesPanel
          accountForm={accountForm}
          accounts={accounts}
          busy={busy}
          codespaceForm={codespaceForm}
          codespaceProgress={codespaceProgress}
          codespaces={codespaces}
          codespaceProxyMissingTools={codespaceProxyMissingTools}
          editingAccountId={editingAccountId}
          selectedAccount={selectedAccount}
          selectedAccountId={selectedAccountId}
          usage={usage}
          onCancelEditAccount={() => { setEditingAccountId(null); setAccountForm(emptyAccountForm); }}
          onCreateCodespace={createCodespace}
          onDeleteAccount={(id) => runAction('delete-account', () => api.deleteAccount(id))}
          onDeleteCodespace={(accountId, name) => runAction('delete-codespace', () => api.deleteCodespace(accountId, name))}
          onEditAccount={editAccount}
          onExportCodespace={(accountId, name) => runAction('export-codespace', () => api.exportCodespace(accountId, name))}
          onRefreshCodespace={refreshCodespace}
          onSaveAccount={saveAccount}
          onSelectAccount={setSelectedAccountId}
          onStartCodespace={startCodespace}
          onStopCodespace={stopCodespace}
          onSyncAccount={(id) => runAction('sync-account', () => api.syncAccount(id))}
          onUpdateAccountField={updateAccountField}
          onUpdateCodespaceField={updateCodespaceField}
          onValidateAccount={(id) => runAction('validate-account', () => api.validateAccount(id))}
        />
      )}

      {activeTab === 'local-proxy' && (
        <LocalProxyPanel
          busy={busy}
          form={settingsForm}
          profiles={profiles}
          selectedProfile={selectedProfile}
          session={localSession}
          status={localStatus}
          onProbe={() => runAction('probe-local-proxy', () => api.probeLocalProxy())}
          onSaveSettings={saveSettings}
          onStop={() => runAction('stop-local-proxy', () => api.stopLocalProxy())}
          onUpdateField={updateSettingsField}
        />
      )}

      {activeTab === 'activity' && (
        <ActivityPanel
          busy={busy}
          diagnostics={diagnostics}
          events={activityEvents}
          filters={activityFilters}
          selectedEvent={selectedEvent}
          summary={activitySummary}
          onApplyFilters={applyActivityFilters}
          onCloseEvent={() => setSelectedEvent(null)}
          onClear={clearActivity}
          onRefresh={() => runAction('refresh-activity', () => loadActivity(activityFilters))}
          onSelectEvent={setSelectedEvent}
          onUpdateFilter={updateActivityFilter}
        />
      )}
    </main>
  );
}

interface CodespacesPanelProps {
  accountForm: GitHubAccountForm;
  accounts: GitHubAccount[];
  busy: string | null;
  codespaceForm: CreateCodespaceForm;
  codespaceProgress: Record<string, string>;
  codespaces: CodespaceSnapshot[];
  codespaceProxyMissingTools: string[];
  editingAccountId: string | null;
  selectedAccount: GitHubAccount | null;
  selectedAccountId: string | null;
  usage: GitHubUsage | null;
  onCancelEditAccount: () => void;
  onCreateCodespace: (event: FormEvent) => Promise<void>;
  onDeleteAccount: (id: string) => void;
  onDeleteCodespace: (accountId: string, name: string) => void;
  onEditAccount: (account: GitHubAccount) => void;
  onExportCodespace: (accountId: string, name: string) => void;
  onRefreshCodespace: (accountId: string, name: string) => void;
  onSaveAccount: (event: FormEvent) => Promise<void>;
  onSelectAccount: (id: string) => void;
  onStartCodespace: (accountId: string, name: string) => void;
  onStopCodespace: (accountId: string, name: string) => void;
  onSyncAccount: (id: string) => void;
  onUpdateAccountField: <K extends keyof GitHubAccountForm>(field: K, value: GitHubAccountForm[K]) => void;
  onUpdateCodespaceField: <K extends keyof CreateCodespaceForm>(field: K, value: CreateCodespaceForm[K]) => void;
  onValidateAccount: (id: string) => void;
}

function CodespacesPanel({
  accountForm,
  accounts,
  busy,
  codespaceForm,
  codespaceProgress,
  codespaces,
  codespaceProxyMissingTools,
  editingAccountId,
  selectedAccount,
  selectedAccountId,
  usage,
  onCancelEditAccount,
  onCreateCodespace,
  onDeleteAccount,
  onDeleteCodespace,
  onEditAccount,
  onExportCodespace,
  onRefreshCodespace,
  onSaveAccount,
  onSelectAccount,
  onStartCodespace,
  onStopCodespace,
  onSyncAccount,
  onUpdateAccountField,
  onUpdateCodespaceField,
  onValidateAccount
}: CodespacesPanelProps) {
  return (
    <section className="content-grid">
      <section className="panel-stack">
        <form className="editor" onSubmit={onSaveAccount}>
          <div className="section-title">
            <Github size={20} />
            <h2>{editingAccountId ? 'Edit GitHub Account' : 'Add GitHub Account'}</h2>
          </div>
          <div className="form-grid">
            <label>
              Display name
              <input value={accountForm.displayName} onChange={(event) => onUpdateAccountField('displayName', event.target.value)} required />
            </label>
            <label>
              Username
              <input value={accountForm.username} onChange={(event) => onUpdateAccountField('username', event.target.value)} required />
            </label>
            <label>
              Plan
              <select value={accountForm.plan} onChange={(event) => onUpdateAccountField('plan', event.target.value)}>
                <option value="Unknown">Unknown</option>
                <option value="Free">Free</option>
                <option value="Pro">Pro</option>
              </select>
            </label>
            <label>
              Personal access token
              <input type="password" value={accountForm.personalAccessToken} onChange={(event) => onUpdateAccountField('personalAccessToken', event.target.value)} required={!editingAccountId} placeholder={editingAccountId ? 'Leave unchanged' : ''} />
            </label>
          </div>
          <div className="form-actions">
            <button type="submit" disabled={busy !== null}>
              <Plus size={16} />
              {editingAccountId ? 'Save account' : 'Add account'}
            </button>
            {editingAccountId && <button type="button" className="secondary" onClick={onCancelEditAccount}>Cancel</button>}
          </div>
        </form>

        <form className="editor" onSubmit={onCreateCodespace}>
          <div className="section-title">
            <Cloud size={20} />
            <h2>Create Codespace</h2>
          </div>
          <div className="form-grid">
            <label>
              Repository owner
              <input value={codespaceForm.repositoryOwner} onChange={(event) => onUpdateCodespaceField('repositoryOwner', event.target.value)} required />
            </label>
            <label>
              Repository name
              <input value={codespaceForm.repositoryName} onChange={(event) => onUpdateCodespaceField('repositoryName', event.target.value)} required />
            </label>
            <label>
              Ref
              <input value={codespaceForm.ref} onChange={(event) => onUpdateCodespaceField('ref', event.target.value)} placeholder="Default branch" />
            </label>
            <label>
              Geo
              <select value={codespaceForm.geo} onChange={(event) => onUpdateCodespaceField('geo', event.target.value)}>
                <option value="UsEast">US East</option>
                <option value="UsWest">US West</option>
                <option value="EuropeWest">Europe West</option>
                <option value="SoutheastAsia">Southeast Asia</option>
              </select>
            </label>
            <label>
              Machine
              <input value={codespaceForm.machine} onChange={(event) => onUpdateCodespaceField('machine', event.target.value)} placeholder="Optional" />
            </label>
            <label>
              Idle timeout
              <input type="number" min="5" max="240" value={codespaceForm.idleTimeoutMinutes} onChange={(event) => onUpdateCodespaceField('idleTimeoutMinutes', Number(event.target.value))} />
            </label>
            <label className="wide">
              Display name
              <input value={codespaceForm.displayName} onChange={(event) => onUpdateCodespaceField('displayName', event.target.value)} />
            </label>
          </div>
          <div className="form-actions">
            <button type="submit" disabled={busy !== null || !selectedAccountId || selectedAccount?.quotaState === 'Limited'}>
              <Plus size={16} />
              Create
            </button>
          </div>
        </form>
      </section>

      <section className="dashboard-list">
        <div className="section-title">
          <Github size={20} />
          <h2>Accounts</h2>
        </div>
        <div className="account-grid">
          {accounts.length === 0 ? (
            <div className="empty-state">No GitHub accounts yet.</div>
          ) : (
            accounts.map((account) => (
              <article className={`account-row ${account.id === selectedAccountId ? 'selected-row' : ''}`} key={account.id} onClick={() => onSelectAccount(account.id)}>
                <span>
                  <strong>{account.displayName}</strong>
                  <small>@{account.username}</small>
                </span>
                <span className={`badge ${badgeClass(account.validationStatus)}`}>{account.validationStatus}</span>
                <span className={`badge ${badgeClass(account.quotaState)}`}>{account.quotaState}</span>
                <span className="row-actions">
                  <button title="Validate token" onClick={(event) => { event.stopPropagation(); onValidateAccount(account.id); }} disabled={busy !== null}>
                    <ShieldCheck size={16} />
                  </button>
                  <button title="Sync Codespaces" onClick={(event) => { event.stopPropagation(); onSyncAccount(account.id); }} disabled={busy !== null}>
                    <RefreshCw size={16} />
                  </button>
                  <button title="Edit" onClick={(event) => { event.stopPropagation(); onEditAccount(account); }} disabled={busy !== null}>
                    <Pencil size={16} />
                  </button>
                  <button title="Delete" className="danger" onClick={(event) => { event.stopPropagation(); if (confirmAction(`Delete GitHub account "${account.displayName}"?`)) onDeleteAccount(account.id); }} disabled={busy !== null}>
                    <Trash2 size={16} />
                  </button>
                </span>
              </article>
            ))
          )}
        </div>

        <div className="section-title spaced">
          <Cloud size={20} />
          <h2>{selectedAccount ? `${selectedAccount.displayName} Codespaces` : 'Codespaces'}</h2>
          {usage?.billingUrl && (
            <a className="text-link" href={usage.billingUrl} target="_blank" rel="noreferrer">
              <ExternalLink size={15} />
              Billing
            </a>
          )}
        </div>
        {usage && <section className={`notice ${usage.state === 'Unavailable' || usage.state === 'Limited' ? 'error' : ''}`}>{usage.message}</section>}
        {codespaceProxyMissingTools.length > 0 && (
          <section className="notice error">
            <Wrench size={18} />
            <span>Codespace proxy runtime is missing: {codespaceProxyMissingTools.join(', ')}</span>
          </section>
        )}
        <CodespaceTable
          busy={busy}
          codespaceProgress={codespaceProgress}
          codespaces={codespaces}
          codespaceProxyReady={codespaceProxyMissingTools.length === 0}
          selectedAccountId={selectedAccountId}
          onDelete={onDeleteCodespace}
          onExport={onExportCodespace}
          onRefresh={onRefreshCodespace}
          onStart={onStartCodespace}
          onStop={onStopCodespace}
        />
      </section>
    </section>
  );
}

interface CodespaceTableProps {
  busy: string | null;
  codespaceProgress: Record<string, string>;
  codespaces: CodespaceSnapshot[];
  codespaceProxyReady: boolean;
  selectedAccountId: string | null;
  onDelete: (accountId: string, name: string) => void;
  onExport: (accountId: string, name: string) => void;
  onRefresh: (accountId: string, name: string) => void;
  onStart: (accountId: string, name: string) => void;
  onStop: (accountId: string, name: string) => void;
}

function CodespaceTable({ busy, codespaceProgress, codespaces, codespaceProxyReady, selectedAccountId, onDelete, onExport, onRefresh, onStart, onStop }: CodespaceTableProps) {
  if (!selectedAccountId) {
    return <div className="empty-state">Select or create a GitHub account.</div>;
  }

  if (codespaces.length === 0) {
    return <div className="empty-state">No Codespaces synced for this account.</div>;
  }

  return (
    <section className="codespace-table">
      <div className="codespace-row codespace-header">
        <span>Name</span>
        <span>Repository</span>
        <span>State</span>
        <span>Machine</span>
        <span>Last used</span>
        <span>Actions</span>
      </div>
      {codespaces.map((codespace) => {
        const progress = codespaceProgress[progressKey(selectedAccountId, codespace.name)];
        return (
          <div className="codespace-row" key={codespace.id}>
            <span>
              <strong>{codespace.name}</strong>
              {codespace.webUrl && <a href={codespace.webUrl} target="_blank" rel="noreferrer" title="Open Codespace"><ExternalLink size={14} /></a>}
            </span>
            <span>{codespace.repositoryFullName ?? ''}</span>
            <span className={`badge ${badgeClass(progress ?? codespace.state)}`}>{progress ?? codespace.state}</span>
            <span>{codespace.machineDisplayName ?? codespace.location ?? ''}</span>
            <span>{codespace.lastUsedAt ? formatDate(codespace.lastUsedAt) : ''}</span>
            <span className="row-actions">
              <button title={codespaceProxyReady ? 'Run Codespace proxy' : 'Runtime tools are missing'} onClick={() => onStart(selectedAccountId, codespace.name)} disabled={busy !== null || !codespaceProxyReady}>
                <Play size={16} />
              </button>
              <button title="Stop" onClick={() => onStop(selectedAccountId, codespace.name)} disabled={busy !== null}>
                <CircleStop size={16} />
              </button>
              <button title="Refresh" onClick={() => onRefresh(selectedAccountId, codespace.name)} disabled={busy !== null}>
                <RefreshCw size={16} />
              </button>
              <button title="Copy gh ssh" onClick={() => copyText(`gh codespace ssh -c ${codespace.name}`)} disabled={busy !== null}>
                <Terminal size={16} />
              </button>
              <button title="Export" onClick={() => onExport(selectedAccountId, codespace.name)} disabled={busy !== null}>
                <Download size={16} />
              </button>
              <button title="Delete" className="danger" onClick={() => { if (confirmAction(`Delete Codespace "${codespace.name}"?`)) onDelete(selectedAccountId, codespace.name); }} disabled={busy !== null}>
                <Trash2 size={16} />
              </button>
            </span>
          </div>
        );
      })}
    </section>
  );
}

interface LocalProxyPanelProps {
  busy: string | null;
  form: LocalProxySettingsForm;
  profiles: LocalProxyProfile[];
  selectedProfile: LocalProxyProfile | null;
  session: LocalProxySession | null;
  status: LocalProxyAutomationStatus | null;
  onProbe: () => void;
  onSaveSettings: (event: FormEvent) => Promise<void>;
  onStop: () => void;
  onUpdateField: <K extends keyof LocalProxySettingsForm>(field: K, value: LocalProxySettingsForm[K]) => void;
}

function LocalProxyPanel({
  busy,
  form,
  profiles,
  selectedProfile,
  session,
  status,
  onProbe,
  onSaveSettings,
  onStop,
  onUpdateField
}: LocalProxyPanelProps) {
  const settings = selectedProfile;
  const proxyExports = session
    ? [
        `export HTTP_PROXY=${session.httpProxyUrl}`,
        `export HTTPS_PROXY=${session.httpProxyUrl}`,
        `export http_proxy=${session.httpProxyUrl}`,
        `export https_proxy=${session.httpProxyUrl}`,
        `export ALL_PROXY=${session.socksProxyUrl}`,
        `export all_proxy=${session.socksProxyUrl}`,
        'export NO_PROXY=localhost,127.0.0.1',
        'export no_proxy=localhost,127.0.0.1'
      ].join('\n')
    : '';

  return (
    <>
      <section className="active-strip">
        <span className={`badge ${badgeClass(session?.status ?? 'Stopped')}`}>{session?.status ?? 'Stopped'}</span>
        <strong>{session ? session.profileName : 'Gateway waiting for proxy traffic'}</strong>
        {session && <span>{session.activeConnections} active / {session.totalRequests} requests</span>}
        {!session && <span>{settings ? `${settings.bindHost}:${settings.localPort}` : 'Loading settings'}</span>}
        <div className="active-actions">
          <button title="Stop active proxy" onClick={onStop} disabled={busy !== null || !session}>
            <Square size={17} />
          </button>
          <button title="Probe active proxy" onClick={onProbe} disabled={busy !== null || !session}>
            <CheckCircle2 size={17} />
          </button>
        </div>
      </section>

      <section className="content-grid">
        <div className="panel-stack">
          <form className="editor" onSubmit={onSaveSettings}>
            <div className="section-title">
              <Wifi size={20} />
              <h2>Codespace Proxy Settings</h2>
            </div>
            <div className="form-grid">
              <label>
                Bind host
                <input value={form.bindHost} onChange={(event) => onUpdateField('bindHost', event.target.value)} required />
              </label>
              <label>
                Proxy port
                <input type="number" min="1" max="65535" value={form.localPort} onChange={(event) => onUpdateField('localPort', Number(event.target.value))} required />
              </label>
              <label>
                Idle minutes
                <input type="number" min="1" max="1440" value={form.idleShutdownMinutes} onChange={(event) => onUpdateField('idleShutdownMinutes', Number(event.target.value))} required />
              </label>
              <label>
                Username
                <input value={form.proxyUsername} onChange={(event) => onUpdateField('proxyUsername', event.target.value)} />
              </label>
              <label>
                Password
                <input type="password" value={form.proxyPassword} onChange={(event) => onUpdateField('proxyPassword', event.target.value)} placeholder={settings?.requiresAuthentication ? 'Leave empty to keep current' : ''} />
              </label>
            </div>
            <div className="form-actions">
              <button type="submit" disabled={busy !== null}>Save settings</button>
            </div>
          </form>

          {session && (
            <section className="editor">
              <div className="section-title">
                <Copy size={20} />
                <h2>Use Codespace Proxy</h2>
              </div>
              <div className="proxy-copy-row">
                <code>{session.httpProxyUrl}</code>
                <button title="Copy HTTP proxy URL" type="button" onClick={() => copyText(session.httpProxyUrl)}>
                  <Copy size={16} />
                </button>
              </div>
              <div className="proxy-copy-row">
                <code>{session.socksProxyUrl}</code>
                <button title="Copy SOCKS proxy URL" type="button" onClick={() => copyText(session.socksProxyUrl)}>
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

        <section className="node-list">
          <div className="section-title">
            <Wifi size={20} />
            <h2>Automation Status</h2>
          </div>
          {profiles.length === 0 && <div className="empty-state">Loading Codespace proxy settings.</div>}
          {settings && (
            <article className="node-card selected-row">
              <div className="node-main">
                <div>
                  <h3>Single Codespace Proxy</h3>
                  <p>HTTP and SOCKS {settings.bindHost}:{settings.localPort}</p>
                </div>
                <span className={`badge ${badgeClass(session?.status ?? settings.status)}`}>{session?.status ?? settings.status}</span>
              </div>
              <div className="node-meta">
                <span>{settings.idleShutdownMinutes}m idle</span>
                <span>{settings.requiresAuthentication ? `auth ${settings.proxyUsername}` : 'no auth'}</span>
                <span>{status?.phase ?? 'WaitingForTraffic'}</span>
                {status?.selectedAccount && <span>{status.selectedAccount}</span>}
                {session?.codespaceName && <span>{session.codespaceName}</span>}
              </div>
              {status?.warning && <p className="muted warning-text">{status.warning}</p>}
              {status?.lastError && <p className="muted warning-text">{status.lastError}</p>}
            </article>
          )}
        </section>
      </section>
    </>
  );
}

interface ActivityPanelProps {
  busy: string | null;
  diagnostics: RuntimeDiagnostics | null;
  events: OperationalEvent[];
  filters: ActivityFilters;
  selectedEvent: OperationalEvent | null;
  summary: ActivitySummary | null;
  onApplyFilters: (event: FormEvent) => Promise<void>;
  onCloseEvent: () => void;
  onClear: () => void;
  onRefresh: () => void;
  onSelectEvent: (event: OperationalEvent) => void;
  onUpdateFilter: <K extends keyof ActivityFilters>(field: K, value: ActivityFilters[K]) => void;
}

function ActivityPanel({
  busy,
  diagnostics,
  events,
  filters,
  selectedEvent,
  summary,
  onApplyFilters,
  onCloseEvent,
  onClear,
  onRefresh,
  onSelectEvent,
  onUpdateFilter
}: ActivityPanelProps) {
  return (
    <section className="activity-panel">
      <div className="activity-summary">
        <StatusTile icon={<AlertTriangle size={20} />} label="Last 24h" value={summary ? `${summary.errorCount} errors, ${summary.warningCount} warnings` : 'Loading'} />
        <StatusTile icon={<Terminal size={20} />} label="Command failures" value={summary ? String(summary.commandFailureCount) : 'Loading'} />
        <StatusTile icon={<Activity size={20} />} label="Average time" value={summary?.averageCommandDurationMs ? `${Math.round(summary.averageCommandDurationMs)} ms` : 'No samples'} />
      </div>

      <section className="diagnostic-strip">
        <div className="diagnostic-item">
          <Database size={18} />
          <span>SQLite</span>
          <strong>{diagnostics?.databaseAvailable ? 'Ready' : 'Unavailable'}</strong>
        </div>
        {diagnostics?.tools.map((tool) => (
          <div className="diagnostic-item" key={tool.name} title={tool.message}>
            {tool.available ? <ShieldCheck size={18} /> : <Wrench size={18} />}
            <span>{tool.name}</span>
            <strong>{tool.available ? 'Found' : 'Missing'}</strong>
          </div>
        ))}
      </section>

      {summary?.lastError && (
        <section className="notice error">
          <XCircle size={18} />
          <span>{summary.lastError.eventType}: {summary.lastError.message}</span>
        </section>
      )}

      <form className="activity-filters" onSubmit={onApplyFilters}>
        <div className="section-title">
          <Filter size={20} />
          <h2>Activity</h2>
        </div>
        <label>
          Severity
          <select value={filters.severity} onChange={(event) => onUpdateFilter('severity', event.target.value)}>
            <option value="">Any</option>
            <option value="Error">Error</option>
            <option value="Warning">Warning</option>
            <option value="Information">Information</option>
            <option value="Debug">Debug</option>
          </select>
        </label>
        <label>
          Event type
          <input value={filters.eventType} onChange={(event) => onUpdateFilter('eventType', event.target.value)} />
        </label>
        <label>
          Correlation ID
          <input value={filters.correlationId} onChange={(event) => onUpdateFilter('correlationId', event.target.value)} />
        </label>
        <label>
          Search
          <input value={filters.search} onChange={(event) => onUpdateFilter('search', event.target.value)} />
        </label>
        <label>
          Limit
          <input type="number" min="1" max="500" value={filters.limit} onChange={(event) => onUpdateFilter('limit', Number(event.target.value))} />
        </label>
        <button type="submit" disabled={busy !== null}>
          <RefreshCw size={16} />
          Apply
        </button>
        <button type="button" className="secondary" onClick={onRefresh} disabled={busy !== null}>
          <RefreshCw size={16} />
          Refresh
        </button>
        <button type="button" className="danger" onClick={onClear} disabled={busy !== null || events.length === 0}>
          <Trash2 size={16} />
          Clear
        </button>
      </form>

      <section className="event-table">
        <div className="event-row event-header">
          <span>Time</span>
          <span>Severity</span>
          <span>Event</span>
          <span>Message</span>
          <span>Duration</span>
          <span>Exit</span>
        </div>
        {events.length === 0 ? (
          <div className="empty-state">No activity events found.</div>
        ) : (
          events.map((event) => (
            <button className="event-row" key={event.id} type="button" onClick={() => onSelectEvent(event)}>
              <span>{formatDate(event.timestamp)}</span>
              <span className={`badge ${badgeClass(event.severity)}`}>{event.severity}</span>
              <span>{event.eventType}</span>
              <span>{event.message}</span>
              <span>{event.durationMs ? `${event.durationMs} ms` : ''}</span>
              <span>{event.exitCode ?? ''}</span>
            </button>
          ))
        )}
      </section>

      {selectedEvent && (
        <div className="event-detail" role="dialog" aria-modal="true">
          <div className="event-detail-panel">
            <div className="node-main">
              <div>
                <h3>{selectedEvent.eventType}</h3>
                <p>{selectedEvent.message}</p>
              </div>
              <button className="icon-button" title="Close" onClick={onCloseEvent}>
                <XCircle size={18} />
              </button>
            </div>
            <dl className="event-fields">
              <div><dt>Timestamp</dt><dd>{new Date(selectedEvent.timestamp).toLocaleString()}</dd></div>
              <div><dt>Severity</dt><dd>{selectedEvent.severity}</dd></div>
              <div><dt>Correlation</dt><dd>{selectedEvent.correlationId ?? ''}</dd></div>
              <div><dt>Command</dt><dd>{selectedEvent.commandDisplay ?? ''}</dd></div>
              <div><dt>Duration</dt><dd>{selectedEvent.durationMs ? `${selectedEvent.durationMs} ms` : ''}</dd></div>
              <div><dt>Exit code</dt><dd>{selectedEvent.exitCode ?? ''}</dd></div>
            </dl>
            {selectedEvent.correlationId && (
              <button className="secondary" type="button" onClick={() => copyText(selectedEvent.correlationId ?? '')}>
                <Copy size={16} />
                Copy correlation ID
              </button>
            )}
            {selectedEvent.standardOutputSnippet && <pre>{selectedEvent.standardOutputSnippet}</pre>}
            {selectedEvent.standardErrorSnippet && <pre className="stderr">{selectedEvent.standardErrorSnippet}</pre>}
            {selectedEvent.detailsJson && <pre>{selectedEvent.detailsJson}</pre>}
          </div>
        </div>
      )}
    </section>
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

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : 'Unexpected error';
}

function formatResult(label: string, result: unknown) {
  if (isGitHubLifecycleResult(result) || isLocalProxyResult(result)) {
    return result.message;
  }

  return label.replaceAll('-', ' ');
}

function isGitHubLifecycleResult(result: unknown): result is GitHubLifecycleResult {
  return typeof result === 'object' && result !== null && 'message' in result && 'succeeded' in result && 'codespace' in result;
}

function isLocalProxyResult(result: unknown): result is LocalProxyResult {
  return typeof result === 'object' && result !== null && 'message' in result && 'succeeded' in result && 'session' in result;
}

function lifecycleAccountId(result: unknown) {
  return isGitHubLifecycleResult(result) ? result.codespace?.accountId ?? null : null;
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  }).format(new Date(value));
}

function badgeClass(value: string | number | null | undefined) {
  return String(value ?? 'unknown').toLowerCase().replaceAll(' ', '-').replaceAll(':', '');
}

function progressKey(accountId: string, name: string) {
  return `${accountId}:${name}`;
}

function clearProgress(current: Record<string, string>, key: string) {
  const next = { ...current };
  delete next[key];
  return next;
}

function isAvailableState(state: string) {
  return ['available', 'running'].includes(state.toLowerCase());
}

function isStoppedState(state: string) {
  return ['shutdown', 'stopped', 'unavailable'].includes(state.toLowerCase());
}

function delay(milliseconds: number) {
  return new Promise((resolve) => window.setTimeout(resolve, milliseconds));
}

function copyText(value: string) {
  navigator.clipboard?.writeText(value).catch(() => undefined);
}

function confirmAction(message: string) {
  return window.confirm(message);
}
