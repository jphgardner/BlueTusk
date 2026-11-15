import type { Resource } from './resources';

export const REAL_TIME_RESOURCES: readonly Resource[] = [
  {
    path: 'real-time/streams',
    title: 'Stream committed PostgreSQL changes',
    description:
      'Consume complete transactions from PostgreSQL logical replication. Start with a consistent snapshot, then advance from a durable checkpoint.',
    action: { label: 'Run a Streams consumer', href: '/documentation/real-time/streams-sample' },
    sections: [
      {
        title: 'Set up the source',
        description:
          'Create the publication and slot with a dedicated role. Exclude relay and checkpoint tables from source publications.',
        links: [
          { label: 'Provision a Streams source', href: '/documentation/real-time/streams-cli' },
        ],
      },
      {
        title: 'Bootstrap with a consistent snapshot',
        description:
          'Read rows against the exported snapshot and start applying changes from its matching WAL position. Keep the export session alive until workers import the snapshot.',
        links: [
          { label: 'Snapshot and stream', href: '/documentation/real-time/snapshot-bootstrap' },
        ],
      },
      {
        title: 'Persist progress',
        description:
          'Commit the downstream effect before acknowledgement. Keep a durable checkpoint, retry by stable delivery identity, and monitor replication lag.',
        links: [
          { label: 'Choose a state store', href: '/documentation/real-time/state-stores' },
          { label: 'Configure durable relay', href: '/documentation/real-time/durable-relay' },
        ],
      },
    ],
  },
  {
    path: 'real-time/sync',
    title: 'Keep other systems current',
    description:
      'Apply complete source transactions to a destination that matches your application’s query, indexing, or delivery needs.',
    action: { label: 'Set up a Sync pipeline', href: '/documentation/real-time/sync' },
    sections: [
      {
        title: 'Choose a destination',
        description:
          'Materialize views in PostgreSQL, Redis, or OpenSearch. Send committed changes through NATS, Kafka, S3, or a signed webhook with its documented delivery contract.',
        links: [
          { label: 'Destination configurations', href: '/documentation/real-time/sync-reference' },
          { label: 'Browse Sync adapters', href: '/packages?family=Sync' },
        ],
      },
      {
        title: 'Control retries and replay',
        description:
          'Use a stable source position and idempotency identity. Bound retries and explicitly reconcile state when recovering after a partial destination failure.',
        links: [{ label: 'Delivery guarantees', href: '/documentation/real-time/contracts' }],
      },
      {
        title: 'Rebuild a destination',
        description:
          'Bootstrap a new destination generation, catch up from its snapshot boundary, reconcile, then switch consumers when the destination is ready.',
        links: [{ label: 'Recover a Sync pipeline', href: '/documentation/real-time/operations' }],
      },
    ],
  },
  {
    path: 'real-time/live',
    title: 'Keep authorized clients up to date',
    description:
      'Register a bounded server query. Send the first snapshot, keyed updates, and reconnect information over a supported Live transport.',
    action: { label: 'Register a Live query', href: '/documentation/real-time/live' },
    sections: [
      {
        title: 'Authorize the plan and its parameters',
        description:
          'Map input to declared arguments and derive the subscriber’s scope from authenticated identity. Keep the database query authoritative.',
        links: [
          {
            label: 'Live security and query registration',
            href: '/documentation/real-time/live-reference',
          },
          { label: 'Tenant isolation', href: '/documentation/operations/security' },
        ],
      },
      {
        title: 'Choose a client and transport',
        description:
          'Connect an Angular, React, Vue, Svelte, or framework-neutral client. Configure server-sent events, SignalR, or gRPC according to the host and client.',
        links: [
          { label: 'Browser client packages', href: '/packages?family=Live' },
          {
            label: 'Transport configuration',
            href: '/documentation/real-time/live-reference#aspnet-transports',
          },
        ],
      },
      {
        title: 'Reconnect and reset safely',
        description:
          'Resume from a retained cursor only inside the same scope. When replay is no longer available, refresh from a new authorized snapshot.',
        links: [{ label: 'Replay and recovery', href: '/documentation/real-time/operations' }],
      },
    ],
  },
];
