import { FormEvent, ReactNode, useCallback, useEffect, useMemo, useState } from 'react';
import {
  Activity,
  AlertTriangle,
  BarChart3,
  CheckCircle2,
  CircleStop,
  Cloud,
  Copy,
  Database,
  Download,
  ExternalLink,
  Filter,
  Github,
  Monitor,
  Moon,
  Pencil,
  Play,
  Plus,
  RefreshCw,
  ShieldCheck,
  Square,
  Sun,
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
  LocalProxyStatistics,
  LocalProxyStatisticsPeriod,
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

const appTimeZone = 'Asia/Tehran';
const appTabs = ['codespaces', 'local-proxy', 'statistics', 'activity'] as const;
const statisticsPeriods: LocalProxyStatisticsPeriod[] = ['24h', '7d', '30d'];
const themePreferences = ['system', 'light', 'dark'] as const;

type AppTab = (typeof appTabs)[number];
type ThemePreference = (typeof themePreferences)[number];
type Notice = { kind: 'info' | 'error'; text: string };

export default function App() {
  const [activeTab, setActiveTab] = useState<AppTab>(() => readTabFromUrl());
  const [themePreference, setThemePreference] = useState<ThemePreference>(() => readThemePreference());
  const [accounts, setAccounts] = useState<GitHubAccount[]>([]);
  const [selectedAccountId, setSelectedAccountId] = useState<string | null>(null);
  const [codespaces, setCodespaces] = useState<CodespaceSnapshot[]>([]);
  const [usage, setUsage] = useState<GitHubUsage | null>(null);
  const [profiles, setProfiles] = useState<LocalProxyProfile[]>([]);
  const [localSession, setLocalSession] = useState<LocalProxySession | null>(null);
  const [localStatus, setLocalStatus] = useState<LocalProxyAutomationStatus | null>(null);
  const [statistics, setStatistics] = useState<LocalProxyStatistics | null>(null);
  const [statisticsPeriod, setStatisticsPeriod] = useState<LocalProxyStatisticsPeriod>('24h');
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

  useEffect(() => {
    window.localStorage.setItem('gh-proxy.theme', themePreference);
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const apply = () => applyThemePreference(themePreference);
    apply();
    if (themePreference !== 'system') {
      return undefined;
    }

    media.addEventListener('change', apply);
    return () => media.removeEventListener('change', apply);
  }, [themePreference]);

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

  const loadStatistics = useCallback(async (period = statisticsPeriod) => {
    const nextStatistics = await api.localProxyStatistics(period);
    setStatistics(nextStatistics);
  }, [statisticsPeriod]);

  const loadAll = useCallback(async () => {
    await loadAccounts();
    await Promise.all([loadLocalProxy(), loadStatistics(), loadActivity()]);
  }, [loadAccounts, loadActivity, loadLocalProxy, loadStatistics]);

  useEffect(() => {
    loadAll().catch((error) => setNotice({ kind: 'error', text: errorMessage(error) }));
  }, [loadAll]);

  useEffect(() => {
    const onPopState = () => setActiveTab(readTabFromUrl());
    window.addEventListener('popstate', onPopState);
    return () => window.removeEventListener('popstate', onPopState);
  }, []);

  useEffect(() => {
    loadCodespaces(selectedAccountId).catch(() => undefined);
  }, [loadCodespaces, selectedAccountId]);

  useEffect(() => {
    const timer = window.setInterval(() => {
      loadAccounts().catch(() => undefined);
      loadCodespaces().catch(() => undefined);
      loadLocalProxy().catch(() => undefined);
      loadStatistics().catch(() => undefined);
      loadActivity().catch(() => undefined);
    }, 20000);
    return () => window.clearInterval(timer);
  }, [loadAccounts, loadActivity, loadCodespaces, loadLocalProxy, loadStatistics]);

  async function runAction(label: string, action: () => Promise<unknown>) {
    setBusy(label);
    setNotice({ kind: 'info', text: label.replaceAll('-', ' ') });
    try {
      const result = await action();
      setNotice({ kind: actionSucceeded(result) ? 'info' : 'error', text: formatResult(label, result) });
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

  function selectTab(tab: AppTab) {
    setActiveTab(tab);
    writeTabToUrl(tab);
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

  async function changeStatisticsPeriod(period: LocalProxyStatisticsPeriod) {
    setStatisticsPeriod(period);
    await runAction('refresh-statistics', () => loadStatistics(period));
  }

  return (
    <main className="shell">
      <header className="topbar">
        <div>
          <h1>GitHub Codespaces Manager</h1>
          <p>Run a GitHub Codespace-backed proxy with one HTTP/SOCKS port</p>
        </div>
        <div className="topbar-actions">
          <div className="theme-switch" aria-label="Theme">
            <button type="button" className={themePreference === 'system' ? 'active-option' : ''} onClick={() => setThemePreference('system')} title="Use system theme">
              <Monitor size={16} />
              <span>System</span>
            </button>
            <button type="button" className={themePreference === 'light' ? 'active-option' : ''} onClick={() => setThemePreference('light')} title="Use light theme">
              <Sun size={16} />
              <span>Light</span>
            </button>
            <button type="button" className={themePreference === 'dark' ? 'active-option' : ''} onClick={() => setThemePreference('dark')} title="Use dark theme">
              <Moon size={16} />
              <span>Dark</span>
            </button>
          </div>
          <button type="button" className="icon-button" onClick={() => runAction('refresh', loadAll)} disabled={busy !== null} title="Refresh">
            <RefreshCw size={18} />
          </button>
        </div>
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
        <button className={activeTab === 'codespaces' ? 'active-tab' : ''} onClick={() => selectTab('codespaces')}>
          <Cloud size={16} />
          Codespaces
        </button>
        <button className={activeTab === 'local-proxy' ? 'active-tab' : ''} onClick={() => selectTab('local-proxy')}>
          <Wifi size={16} />
          Codespace Proxy
        </button>
        <button className={activeTab === 'statistics' ? 'active-tab' : ''} onClick={() => selectTab('statistics')}>
          <BarChart3 size={16} />
          Statistics
        </button>
        <button className={activeTab === 'activity' ? 'active-tab' : ''} onClick={() => selectTab('activity')}>
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
          localSession={localSession}
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
          onRetry={() => runAction('retry-local-proxy', () => api.retryLocalProxy())}
          onSaveSettings={saveSettings}
          onStop={() => runAction('stop-local-proxy', () => api.stopLocalProxy())}
          onUpdateField={updateSettingsField}
        />
      )}

      {activeTab === 'statistics' && (
        <StatisticsPanel
          busy={busy}
          period={statisticsPeriod}
          statistics={statistics}
          onChangePeriod={changeStatisticsPeriod}
          onRefresh={() => runAction('refresh-statistics', () => loadStatistics(statisticsPeriod))}
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
  localSession: LocalProxySession | null;
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
  localSession,
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
        {usage && <UsageQuotaSummary usage={usage} />}
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
          localSession={localSession}
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

function UsageQuotaSummary({ usage }: { usage: GitHubUsage }) {
  if (usage.quotas.length === 0) {
    return null;
  }

  return (
    <section className="quota-summary">
      {usage.quotas.map((quota) => {
        const percent = quota.percentUsed ?? null;
        return (
          <article className={`quota-card ${quotaClass(percent)}`} key={quota.name}>
            <div className="quota-card-title">
              {quota.name === 'Storage' ? <Database size={18} /> : <Cloud size={18} />}
              <strong>{quota.name}</strong>
              {percent !== null && <span>{formatPercent(percent)}</span>}
            </div>
            <div className="quota-values">
              <span>
                Used
                <strong>{formatQuotaValue(quota.used, quota.unit)}</strong>
              </span>
              <span>
                Remaining
                <strong>{quota.remaining !== null && quota.remaining !== undefined ? formatQuotaValue(quota.remaining, quota.unit) : 'Unknown'}</strong>
              </span>
              <span>
                Limit
                <strong>{quota.limit !== null && quota.limit !== undefined ? formatQuotaValue(quota.limit, quota.unit) : 'Unknown'}</strong>
              </span>
            </div>
            {percent !== null && (
              <div className="quota-bar" aria-hidden="true">
                <span style={{ width: `${Math.min(100, Math.max(0, percent))}%` }} />
              </div>
            )}
          </article>
        );
      })}
    </section>
  );
}

interface CodespaceTableProps {
  busy: string | null;
  codespaceProgress: Record<string, string>;
  codespaces: CodespaceSnapshot[];
  codespaceProxyReady: boolean;
  localSession: LocalProxySession | null;
  selectedAccountId: string | null;
  onDelete: (accountId: string, name: string) => void;
  onExport: (accountId: string, name: string) => void;
  onRefresh: (accountId: string, name: string) => void;
  onStart: (accountId: string, name: string) => void;
  onStop: (accountId: string, name: string) => void;
}

function CodespaceTable({ busy, codespaceProgress, codespaces, codespaceProxyReady, localSession, selectedAccountId, onDelete, onExport, onRefresh, onStart, onStop }: CodespaceTableProps) {
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
        <span>Geo</span>
        <span>Last used</span>
        <span>Actions</span>
      </div>
      {codespaces.map((codespace) => {
        const progress = codespaceProgress[progressKey(selectedAccountId, codespace.name)];
        const state = (progress ?? codespace.state).toLowerCase();
        const isActiveCodespace = ['available', 'running', 'starting', 'queued', 'provisioning'].includes(state);
        const isRunningCodespace = ['available', 'running'].includes(state);
        const isActiveProxy = localSession?.codespaceName === codespace.name && localSession.status === 'Running';
        return (
          <div className="codespace-row" key={codespace.id}>
            <span>
              <strong>{codespace.name}</strong>
              {codespace.webUrl && <a href={codespace.webUrl} target="_blank" rel="noreferrer" title="Open Codespace"><ExternalLink size={14} /></a>}
            </span>
            <span>{codespace.repositoryFullName ?? ''}</span>
            <span className={`badge ${badgeClass(progress ?? codespace.state)}`}>{progress ?? codespace.state}</span>
            <span>{codespace.machineDisplayName ?? ''}</span>
            <span>{codespace.location ?? ''}</span>
            <span>{codespace.lastUsedAt ? formatDate(codespace.lastUsedAt) : ''}</span>
            <span className="row-actions">
              <button title={codespaceProxyReady ? (isActiveCodespace ? 'Codespace is already started' : 'Start Codespace proxy') : 'Runtime tools are missing'} onClick={() => onStart(selectedAccountId, codespace.name)} disabled={busy !== null || !codespaceProxyReady || isActiveCodespace || isActiveProxy}>
                <Play size={16} />
              </button>
              <button title={isRunningCodespace ? 'Stop' : 'Codespace is not running'} onClick={() => onStop(selectedAccountId, codespace.name)} disabled={busy !== null || !isRunningCodespace}>
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
  onRetry: () => void;
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
  onRetry,
  onSaveSettings,
  onStop,
  onUpdateField
}: LocalProxyPanelProps) {
  const settings = selectedProfile;
  const statusAvailability = status?.availability ?? (session ? 'Up' : 'Idle');
  const statusSeverity = status?.severity ?? (session ? 'success' : 'muted');
  const statusMessage = status?.message ?? (session ? 'Proxy is up.' : 'Proxy is idle.');
  const retryText = status?.retryInSeconds !== null && status?.retryInSeconds !== undefined
    ? `Retry in ${status.retryInSeconds}s`
    : null;
  const idleWakeText = status?.idleWakePaused && status.idleWakeRequestThreshold > 1
    ? `Wake requests ${status.idleWakeRequestCount}/${status.idleWakeRequestThreshold}`
    : null;
  const lastRequestAt = status?.lastRequestAt ?? session?.lastRequestAt ?? null;
  const idleSummary = session ? buildIdleSummary(session) : null;
  const canRetry = statusAvailability !== 'Up' && statusAvailability !== 'Starting';
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
        <span className={`badge ${badgeClass(statusAvailability)}`}>{statusAvailability}</span>
        <strong>{session ? session.profileName : status?.publicPortOpen ? 'Codespace proxy wake gateway' : 'Codespace proxy is not listening'}</strong>
        {session && <span>{session.activeConnections} active / {session.totalRequests} requests</span>}
        {idleSummary && <span>Idle {idleSummary.idleFor} / stop in {idleSummary.stopIn}</span>}
        {!session && <span>{settings ? `${settings.bindHost}:${settings.localPort}` : 'Loading settings'}</span>}
        <span>{status?.publicPortOpen ? 'public port open' : 'public port closed'}</span>
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
            <article className={`node-card selected-row proxy-status-card status-${badgeClass(statusSeverity)}`}>
              <div className="node-main">
                <div>
                  <h3>Single Codespace Proxy</h3>
                  <p>{statusMessage}</p>
                </div>
                <span className={`badge ${badgeClass(statusAvailability)}`}>{statusAvailability}</span>
              </div>
              <div className="node-meta">
                <span>{settings.idleShutdownMinutes}m idle</span>
                <span>{settings.requiresAuthentication ? `auth ${settings.proxyUsername}` : 'no auth'}</span>
                <span>{status?.phase ?? 'WaitingForTraffic'}</span>
                {retryText && <span>{retryText}</span>}
                {idleWakeText && <span>{idleWakeText}</span>}
                {status?.idleWakePaused && status.idleWakeWindowExpiresAt && <span>Wake window until {formatDateTime(status.idleWakeWindowExpiresAt)}</span>}
                {idleSummary && <span>Idle for {idleSummary.idleFor}</span>}
                {idleSummary && <span>Stops Codespace in {idleSummary.stopIn}</span>}
                <span>Latest request: {lastRequestAt ? formatDateTime(lastRequestAt) : 'Never'}</span>
                {status?.selectedAccount && <span>{status.selectedAccount}</span>}
                {session?.codespaceName && <span>{session.codespaceName}</span>}
              </div>
              <div className="node-actions">
                <button title="Retry Codespace proxy startup" type="button" onClick={onRetry} disabled={busy !== null || !canRetry}>
                  <RefreshCw size={16} /> Retry
                </button>
              </div>
              {status?.warning && <p className="muted warning-text">{status.warning}</p>}
              {status?.lastError && <p className="muted warning-text">{status.lastError}</p>}
            </article>
          )}

          <article className="node-card proxy-requests-card">
            <div className="section-title">
              <Activity size={20} />
              <h2>Latest Requests</h2>
            </div>
            {!status || status.latestRequests.length === 0 ? (
              <div className="empty-state">No proxy requests recorded yet.</div>
            ) : (
              <div className="proxy-request-list">
                {status.latestRequests.map((request) => (
                  <div className="proxy-request-row" key={request.id} title={request.errorMessage ?? undefined}>
                    <span>{formatDateTime(request.observedAt)}</span>
                    <strong>{request.protocol}</strong>
                    <span>{requestTargetLabel(request.targetHost, request.targetPort)}</span>
                    <span className={`badge ${requestOutcomeBadge(request.outcome)}`}>{request.outcome}</span>
                    <span>{request.codespaceName ?? 'No Codespace'}</span>
                  </div>
                ))}
              </div>
            )}
          </article>
        </section>
      </section>
    </>
  );
}

interface StatisticsPanelProps {
  busy: string | null;
  period: LocalProxyStatisticsPeriod;
  statistics: LocalProxyStatistics | null;
  onChangePeriod: (period: LocalProxyStatisticsPeriod) => Promise<void>;
  onRefresh: () => void;
}

function StatisticsPanel({ busy, period, statistics, onChangePeriod, onRefresh }: StatisticsPanelProps) {
  const buckets = period === '24h' ? (statistics?.hourlyBuckets ?? []) : (statistics?.dailyBuckets ?? []);
  return (
    <section className="activity-panel">
      <div className="activity-summary stats-summary">
        <StatusTile icon={<Wifi size={20} />} label="Active" tone="success" value={statistics ? formatDurationSeconds(statistics.totals.activeSeconds) : 'Loading'} />
        <StatusTile icon={<CircleStop size={20} />} label="Off" tone="idle" value={statistics ? formatDurationSeconds(statistics.totals.offSeconds) : 'Loading'} />
        <StatusTile icon={<AlertTriangle size={20} />} label="Error" tone="error" value={statistics ? formatDurationSeconds(statistics.totals.errorSeconds) : 'Loading'} />
        <StatusTile icon={<BarChart3 size={20} />} label="Active percent" value={statistics ? `${statistics.totals.activePercent}%` : 'Loading'} />
        <StatusTile icon={<Activity size={20} />} label="Sessions" value={statistics ? String(statistics.totals.sessionCount) : 'Loading'} />
      </div>

      <section className="activity-filters compact">
        <div className="section-title">
          <BarChart3 size={20} />
          <h2>Codespace Proxy Statistics</h2>
        </div>
        <div className="segmented-control">
          {statisticsPeriods.map((item) => (
            <button
              className={period === item ? 'active-option' : ''}
              disabled={busy !== null}
              key={item}
              onClick={() => onChangePeriod(item)}
              type="button"
            >
              {periodLabel(item)}
            </button>
          ))}
        </div>
        <button type="button" className="secondary" onClick={onRefresh} disabled={busy !== null}>
          <RefreshCw size={16} />
          Refresh
        </button>
      </section>

      {statistics && (
        <section className="stats-range">
          <span>{formatDateTime(statistics.rangeStart)}</span>
          <span>{formatDateTime(statistics.rangeEnd)}</span>
          <span>{statistics.timeZone}</span>
          {period !== '24h' && <span>Average active {formatDurationSeconds(statistics.totals.averageActiveSecondsPerDay)} / day</span>}
          <span className="stats-legend-item">
            <i className="stats-swatch active" /> Active
          </span>
          <span className="stats-legend-item">
            <i className="stats-swatch error" /> Error
          </span>
          <span className="stats-legend-item">
            <i className="stats-swatch off" /> Off/idle
          </span>
          <span className="stats-legend-item">
            <i className="stats-swatch future" /> Future
          </span>
        </section>
      )}

      <section className="stats-chart">
        {statistics === null ? (
          <div className="empty-state">Loading statistics.</div>
        ) : buckets.length === 0 ? (
          <div className="empty-state">No statistics are available for this period.</div>
        ) : (
          buckets.map((bucket) => {
            return (
              <div className="stats-bar-row" key={`${bucket.start}-${bucket.end}`}>
                <span>{bucket.label}</span>
                <div
                  className="stats-bar"
                  title={`${formatDurationSeconds(bucket.activeSeconds)} active, ${formatDurationSeconds(bucket.errorSeconds)} error, ${formatDurationSeconds(bucket.offSeconds)} idle/off`}
                >
                  {bucket.segments.map((segment) => (
                    <div
                      className={`stats-bar-segment stats-bar-${segment.state}`}
                      key={`${segment.start}-${segment.end}-${segment.state}`}
                      style={{ width: `${Math.max(0, Math.min(100, segment.percent))}%` }}
                      title={`${segmentLabel(segment.state)}: ${formatDurationSeconds(segment.seconds)}`}
                    />
                  ))}
                </div>
                <strong>{formatDurationSeconds(bucket.activeSeconds)}</strong>
                <span>{formatDurationSeconds(bucket.errorSeconds)}</span>
                <span>{bucket.activePercent}% / {bucket.errorPercent}%</span>
              </div>
            );
          })
        )}
      </section>

      {statistics && statistics.mismatches.length > 0 && (
        <section className="notice warning">
          <AlertTriangle size={18} />
          <span>{statistics.mismatches.length} GitHub active sample(s) happened while the app proxy was not active.</span>
        </section>
      )}

      {statistics && (
        <section className="stats-grid">
          <article className="editor">
            <div className="section-title">
              <Monitor size={20} />
              <h2>Recent Sessions</h2>
            </div>
            {statistics.sessions.length === 0 ? (
              <div className="empty-state">No app-managed proxy sessions in this period.</div>
            ) : (
              <div className="stats-session-list">
                <div className="stats-session-row stats-session-header">
                  <span>Started</span>
                  <span>Codespace</span>
                  <strong>Duration</strong>
                  <span>Status</span>
                </div>
                {statistics.sessions.slice(0, 12).map((session) => (
                  <div className="stats-session-row" key={session.sessionId} title={session.lastError ?? undefined}>
                    <span>{formatDateTime(session.startedAt)}</span>
                    <span>{session.codespaceName ?? 'Codespace'}</span>
                    <strong>{formatDurationSeconds(session.activeSeconds)}</strong>
                    <span className={`badge ${badgeClass(session.status)}`}>{session.status}</span>
                  </div>
                ))}
              </div>
            )}
          </article>

          <article className="editor">
            <div className="section-title">
              <Github size={20} />
              <h2>GitHub Samples</h2>
            </div>
            {statistics.gitHubSamples.length === 0 ? (
              <div className="empty-state">No GitHub state samples in this period.</div>
            ) : (
              <div className="stats-session-list">
                {statistics.gitHubSamples.slice(0, 12).map((sample) => (
                  <div className="stats-session-row" key={`${sample.accountId}-${sample.codespaceName}-${sample.observedAt}`}>
                    <span>{formatDateTime(sample.observedAt)}</span>
                    <span>{sample.codespaceName}</span>
                    <strong>{sample.state}</strong>
                    <span>{sample.source}</span>
                  </div>
                ))}
              </div>
            )}
          </article>
        </section>
      )}
    </section>
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
              <div><dt>Timestamp</dt><dd>{formatDateTime(selectedEvent.timestamp)}</dd></div>
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

function StatusTile({ icon, label, tone, value }: { icon?: ReactNode; label: string; tone?: 'success' | 'idle' | 'error'; value: string }) {
  return (
    <div className={`status-tile${tone ? ` status-tile-${tone}` : ''}`}>
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

function readTabFromUrl(): AppTab {
  const params = new URLSearchParams(window.location.search);
  const tab = params.get('tab');
  return isAppTab(tab) ? tab : 'codespaces';
}

function writeTabToUrl(tab: AppTab) {
  const url = new URL(window.location.href);
  if (tab === 'codespaces') {
    url.searchParams.delete('tab');
  } else {
    url.searchParams.set('tab', tab);
  }

  const nextUrl = `${url.pathname}${url.search}${url.hash}`;
  const currentUrl = `${window.location.pathname}${window.location.search}${window.location.hash}`;
  if (nextUrl !== currentUrl) {
    window.history.pushState(null, '', nextUrl);
  }
}

function isAppTab(value: string | null): value is AppTab {
  return appTabs.some((tab) => tab === value);
}

function readThemePreference(): ThemePreference {
  const stored = window.localStorage.getItem('gh-proxy.theme');
  return isThemePreference(stored) ? stored : 'system';
}

function isThemePreference(value: string | null): value is ThemePreference {
  return themePreferences.some((theme) => theme === value);
}

function applyThemePreference(preference: ThemePreference) {
  const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
  document.documentElement.dataset.theme = preference === 'system'
    ? prefersDark ? 'dark' : 'light'
    : preference;
  document.documentElement.dataset.themePreference = preference;
}

function formatResult(label: string, result: unknown) {
  if (isGitHubLifecycleResult(result) || isLocalProxyResult(result)) {
    return result.message;
  }

  return label.replaceAll('-', ' ');
}

function actionSucceeded(result: unknown) {
  if (isGitHubLifecycleResult(result) || isLocalProxyResult(result)) {
    return result.succeeded;
  }

  return true;
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
    timeZone: appTimeZone,
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    timeZoneName: 'short'
  }).format(new Date(value));
}

function formatDateTime(value: string) {
  return new Intl.DateTimeFormat(undefined, {
    timeZone: appTimeZone,
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    timeZoneName: 'short'
  }).format(new Date(value));
}

function buildIdleSummary(session: LocalProxySession) {
  const now = Date.now();
  const lastActivityAt = Date.parse(session.lastActivityAt);
  const idleShutdownAt = Date.parse(session.idleShutdownAt);
  if (Number.isNaN(lastActivityAt) || Number.isNaN(idleShutdownAt)) {
    return null;
  }

  return {
    idleFor: formatDuration(Math.max(0, now - lastActivityAt)),
    stopIn: formatDuration(Math.max(0, idleShutdownAt - now))
  };
}

function formatDuration(milliseconds: number) {
  const totalSeconds = Math.max(0, Math.ceil(milliseconds / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes >= 60) {
    const hours = Math.floor(minutes / 60);
    const remainingMinutes = minutes % 60;
    return remainingMinutes === 0 ? `${hours}h` : `${hours}h ${remainingMinutes}m`;
  }

  if (minutes > 0) {
    return seconds === 0 ? `${minutes}m` : `${minutes}m ${seconds}s`;
  }

  return `${seconds}s`;
}

function formatDurationSeconds(seconds: number) {
  return formatDuration(seconds * 1000);
}

function periodLabel(period: LocalProxyStatisticsPeriod) {
  switch (period) {
    case '7d':
      return 'Last 7 days';
    case '30d':
      return 'Last 30 days';
    default:
      return 'Last 24 hours';
  }
}

function segmentLabel(state: string) {
  switch (state) {
    case 'up':
      return 'Up';
    case 'error':
      return 'Error';
    case 'off':
      return 'Off/idle';
    case 'future':
      return 'Future';
    default:
      return 'Unknown';
  }
}

function requestTargetLabel(host?: string | null, port?: number | null) {
  if (!host) {
    return 'Unknown host';
  }

  return port ? `${host}:${port}` : host;
}

function requestOutcomeBadge(outcome: string) {
  switch (outcome.toLowerCase()) {
    case 'forwarded':
      return 'success';
    case 'wakepending':
      return 'warning';
    case 'failed':
      return 'error';
    default:
      return badgeClass(outcome);
  }
}

function formatQuotaValue(value: number, unit: string) {
  return `${formatCompactNumber(value)} ${unit}`;
}

function formatCompactNumber(value: number) {
  return new Intl.NumberFormat(undefined, {
    maximumFractionDigits: Math.abs(value) < 10 ? 1 : 0
  }).format(value);
}

function formatPercent(value: number) {
  return `${formatCompactNumber(value)}%`;
}

function quotaClass(percent: number | null) {
  if (percent === null) {
    return '';
  }

  if (percent >= 100) {
    return 'quota-limited';
  }

  if (percent >= 90) {
    return 'quota-warning';
  }

  return 'quota-healthy';
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
