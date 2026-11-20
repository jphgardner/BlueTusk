export interface GuideHeading {
  id: string;
  text: string;
  level: number;
}

export interface GuideIndexEntry {
  category: string;
  categoryLabel: string;
  slug: string;
  title: string;
  summary: string;
  sourcePath: string;
  sourceUrl: string;
  headings: readonly GuideHeading[];
  keywords: readonly string[];
  order: number;
}

export interface GuideManifestEntry extends GuideIndexEntry {
  blocks: readonly GuideContentBlock[];
}

export type GuideContentBlock =
  | { kind: 'heading'; heading: GuideHeading }
  | { kind: 'html'; html: string }
  | { kind: 'code'; code: string; highlighted: string; language: string };

export interface SearchRecord {
  title: string;
  description: string;
  route: string;
  group: string;
  keywords: string;
}
