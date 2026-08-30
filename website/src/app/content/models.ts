export type MaturityStage = 'preview' | 'gate-passed' | 'pending';
export type EvidenceStatus = 'passed' | 'pending' | 'guarded';

export interface ProductStatus {
  id: string;
  name: string;
  version: string;
  stage: MaturityStage;
  packageState: string;
  gateState: string;
  evidence: readonly string[];
  limitations: readonly string[];
}

export interface EvidenceRecord {
  id: string;
  subsystem: string;
  kind: 'compatibility' | 'tests' | 'security' | 'performance' | 'release';
  status: EvidenceStatus;
  value: string;
  label: string;
  detail: string;
  asOf: string;
  sourcePath: string;
  anchor?: string;
}

export interface GuideHeading {
  id: string;
  text: string;
  level: number;
}

export interface GuideManifestEntry {
  category: string;
  categoryLabel: string;
  listed: boolean;
  slug: string;
  title: string;
  summary: string;
  sourcePath: string;
  sourceUrl: string;
  headings: readonly GuideHeading[];
  keywords: readonly string[];
  order: number;
  wordCount: number;
  readMinutes: number;
  searchText: string;
  blocks: readonly GuideContentBlock[];
}

export type GuideContentBlock =
  | { kind: 'html'; html: string }
  | { kind: 'code'; code: string; highlighted: string; language: string };

export interface CapabilityRecord {
  surface: string;
  feature: string;
  workload: string;
  postgres: string;
  state: 'supported' | 'preview' | 'guarded';
  notes: string;
  sourcePath: string;
}

export interface SearchRecord {
  title: string;
  description: string;
  route: string;
  group: string;
  keywords: string;
}
