# Explore complete examples

Use the examples to see how package ownership, database migrations, worker
lifetimes, and authorization fit into an application. Run them against
disposable databases and keep the example’s package versions aligned.

## Start with a single feature

| Goal | Example |
| --- | --- |
| Open a connection and run a bounded query | [ADO.NET sample](../../samples/BlueTusk.Samples.AdoNet/) |
| Consume a snapshot followed by committed transactions | [Streams sample](../streams/sample.md) |
| Query a graph with EF Core | [Property Graph sample](../../samples/BlueTusk.Samples.Graph/) |
| Maintain a bounded graph result | [Fraud and network samples](../continuous-graph/README.md#run-a-working-example) |

## Study an application end to end

| Application | Follow the flow |
| --- | --- |
| Order Fulfilment Operations | Write an order in PostgreSQL, project committed changes, and update the operator’s browser. |
| Service Topology Centre | Register services and dependencies, query impact, and refresh authorized graph results after updates. |
| Fraud Graph Investigator | Query transfers and accounts, rank bounded results, and preserve case decisions and audit history. |

The [application source](../../applications/src/) separates domain, application,
infrastructure, API, and worker code. Start with each infrastructure project
to see table definitions and query registrations. The worker owns change
consumption and checkpoints. The API and browser use authenticated transport
endpoints. Restore the exact package versions declared by the example from a
feed that contains them.

## Adapt a reference app

Provision a separate database and restricted roles. Configure the web host
with your own identity provider and secrets, apply the example’s migrations
through a deployment role, and test as an ordinary tenant. Check a committed
write, an authorized result, and a worker restart before adopting the pattern.
See the [production checklist](../operations/production-checklist.md).
