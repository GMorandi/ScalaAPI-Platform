export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  size: number;
}

export interface Account {
  id: number;
  name: string;
  platform: string;
  priority: number;
  concurrency: number;
  currentLoad: number;
  schedulable: boolean;
  rateMultiplier: number;
  loadFactor: number;
  status: string;
  supportedModels: string[];
  credentialExpiresAt: number | null;
  credentialStatus: string;
  credentialVersion: number;
  credentialRefreshError: string | null;
}

export interface Group {
  id: number;
  platform: string;
  rateMultiplier: number;
  modelRoutingEnabled: boolean;
  claudeCodeOnly: boolean;
  fallbackGroupId: number | null;
  rpmLimit: number;
  peakMultiplier: number | null;
  peakStartHour: number | null;
  peakEndHour: number | null;
}

export interface User {
  id: number;
  status: string;
  role: string;
  balance: number;
  concurrency: number;
  allowedGroups: number[];
  rpmLimit: number;
}

export interface ApiKeyEntry {
  hash: string;
  version: number;
}

export interface DashboardStats {
  totalAccounts: number;
  totalGroups: number;
  totalUsers: number;
  totalApiKeys: number;
}

export interface ReconciliationIncident {
  id: number;
  incident_key: string;
  kind: string;
  severity: string;
  user_id: number | null;
  lease_token: string | null;
  status: string;
  expected: unknown;
  actual: unknown;
  occurrences: number;
  first_seen_at: string;
  last_seen_at: string;
  resolved_at: string | null;
  last_run_id: number | null;
}

export interface ReconciliationRunResult {
  started: boolean;
  runId?: number;
  status?: string;
  openIncidents?: number;
  resolvedIncidents?: number;
}

export interface ReconciliationResolutionResult {
  status: string;
  error_code?: string;
  resolution_id?: number;
  lease_token?: string;
  action?: string;
  cost_usd?: number;
}

export interface ContentPolicyRule {
  id: number;
  pattern: string;
  actionType: "log" | "block";
  scope: string | null;
  status: "active" | "disabled";
  stage: "request" | "response" | "both";
  evaluatorVersion: string;
  classifier: "local" | "external" | "openai";
  redactContent: boolean;
  createdAt: string;
}

export interface ContentPolicyRuleRequest {
  pattern: string;
  actionType: "log" | "block";
  scope: string | null;
  status: "active" | "disabled";
  stage: "request" | "response" | "both";
  evaluatorVersion: string;
  classifier: "local" | "external" | "openai";
  redactContent: boolean;
}

export interface ContentPolicyChange {
  id: number;
  revision: number;
  action: string;
  ruleId: number | null;
  actorId: number | null;
  ipAddress: string | null;
  details: string;
  createdAt: string;
  propagatedAt: string | null;
  attempts: number;
  lastError: string | null;
}

export interface ContentPolicyAlert {
  id: number;
  eventKey: string;
  kind: string;
  severity: "info" | "warning" | "critical" | string;
  ruleId: number | null;
  userId: number | null;
  requestId: string | null;
  stage: string;
  code: string;
  policyRevision: number;
  details: string;
  createdAt: string;
}
