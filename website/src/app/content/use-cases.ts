export interface UseCase {
  readonly slug: string;
  readonly name: string;
  readonly headline: string;
  readonly summary: string;
  readonly icon: string;
  readonly image?: string;
  readonly source: string;
  readonly stages: readonly { title: string; detail: string; guide: string; icon: string }[];
  readonly checks: readonly string[];
}

export const USE_CASES: readonly UseCase[] = [
  {
    slug: 'order-fulfilment',
    name: 'Order fulfilment',
    headline: 'Follow an order from commit to delivery.',
    summary:
      'Give operators a current view of orders while preserving PostgreSQL as the authority for order state and business transactions.',
    icon: 'local_shipping',
    image: '/use-cases/order-fulfilment.webp',
    source: 'applications/src/OrderOperations',
    stages: [
      {
        title: 'Commit the order',
        detail:
          'Validate the command, store a stable idempotency key, and write the order in one PostgreSQL transaction.',
        guide: 'ef-core/overview',
        icon: 'shopping_cart',
      },
      {
        title: 'Capture the change',
        detail:
          'Streams delivers the complete committed transaction and its position after a consistent snapshot.',
        guide: 'real-time/snapshot-bootstrap',
        icon: 'sensors',
      },
      {
        title: 'Update the read model',
        detail: 'Use Sync to apply the change and preserve destination progress across retries.',
        guide: 'real-time/sync',
        icon: 'layers',
      },
      {
        title: 'Refresh the operator view',
        detail:
          'A bounded Live plan pushes authorized updates and gives clients a cursor for reconnect.',
        guide: 'real-time/live',
        icon: 'monitor_heart',
      },
    ],
    checks: [
      'Resubmit the same idempotency key and confirm the order was created once.',
      'Restart the destination worker before acknowledgement and check it resumes from durable progress.',
      'Reconnect the client and compare its result with a fresh authorized query.',
    ],
  },
  {
    slug: 'service-topology',
    name: 'Service topology',
    headline: 'See what a service change can affect.',
    summary:
      'Map services and dependencies in PostgreSQL. Find upstream dependencies, downstream consumers, and shortest paths from an anchor.',
    icon: 'account_tree',
    image: '/use-cases/service-topology.webp',
    source: 'applications/src/ServiceTopology',
    stages: [
      {
        title: 'Store the service map',
        detail:
          'Use services as vertices and dependency rows as directed edges. Index both endpoints for traversal in either direction.',
        guide: 'graph/property-graphs',
        icon: 'hub',
      },
      {
        title: 'Find the impact',
        detail:
          'Query reachable services and inspect their minimum hop counts. Apply limits on depth and work.',
        guide: 'graph/traversals',
        icon: 'alt_route',
      },
      {
        title: 'Watch dependencies',
        detail:
          'Maintain a keyed watch across commits. Insertion can add a result, and removal of the only path can remove it.',
        guide: 'real-time/continuous-graph',
        icon: 'sensors',
      },
      {
        title: 'Protect the topology',
        detail:
          'Apply PostgreSQL row-level security to intermediate services and edges as well as endpoints.',
        guide: 'operations/security',
        icon: 'shield',
      },
    ],
    checks: [
      'Reassign a dependency and compare the watch with a fresh query.',
      'Remove the only path between services and verify reachability changes.',
      'Reconnect a subscriber and verify replay preserves result identity.',
    ],
  },
  {
    slug: 'fraud-investigation',
    name: 'Fraud investigation',
    headline: 'Find the paths behind a suspicious transfer.',
    summary:
      'Explore related accounts, rank a bounded set of transfers, and retain investigation decisions separately from the changing query result.',
    icon: 'manage_search',
    image: '/use-cases/fraud-investigation.webp',
    source: 'applications/src/FraudInvestigation',
    stages: [
      {
        title: 'Store exact amounts',
        detail: 'Map PostgreSQL numeric values with precision and scale appropriate to transfers.',
        guide: 'provider/types-reference',
        icon: 'payments',
      },
      {
        title: 'Follow transfer edges',
        detail:
          'Use a typed pattern so parallel transfers remain separate matches even when they share an account.',
        guide: 'graph/property-graphs',
        icon: 'hub',
      },
      {
        title: 'Watch ranked results',
        detail:
          'Use a unique ranking tie-breaker. Requery the authorized result when a change could cross its limit.',
        guide: 'real-time/continuous-graph-reference',
        icon: 'sort',
      },
      {
        title: 'Record a decision',
        detail:
          'Persist the case decision and audit details through the application write model with explicit investigator permissions.',
        guide: 'operations/security',
        icon: 'fact_check',
      },
    ],
    checks: [
      'Create parallel transfers and verify both can appear.',
      'Change an amount across the top-N boundary and compare it with a fresh authorized query.',
      'Confirm investigators only see accounts and paths permitted by their role.',
    ],
  },
  {
    slug: 'offline-apps',
    name: 'Offline applications',
    headline: 'Keep working when the network drops.',
    summary:
      'Read a scoped local cache, queue writes with stable identities, and reconcile them against the authorized server on reconnect.',
    icon: 'cloud_sync',
    source: 'clients/edge',
    stages: [
      {
        title: 'Activate a scope',
        detail:
          'Derive the tenant and access scope on the server. Seed the cache with a consistent snapshot and checkpoint.',
        guide: 'services/edge',
        icon: 'verified_user',
      },
      {
        title: 'Queue a local write',
        detail: 'Store a stable mutation identity and expected revision in IndexedDB or SQLite.',
        guide: 'services/edge',
        icon: 'edit_note',
      },
      {
        title: 'Reconcile on reconnect',
        detail:
          'Send bounded mutations, resolve explicit conflicts, and pull changes after the confirmed checkpoint.',
        guide: 'services/edge',
        icon: 'sync',
      },
      {
        title: 'Rotate access',
        detail: 'Resolve pending writes and activate a new scope epoch when permissions change.',
        guide: 'operations/security',
        icon: 'lock_reset',
      },
    ],
    checks: [
      'Make an edit offline and confirm it remains visible locally.',
      'Reconnect after a concurrent server write and verify the app exposes the conflict.',
      'Revoke access and check cached data follows the application’s policy.',
    ],
  },
];
