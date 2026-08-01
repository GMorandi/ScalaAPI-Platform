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
