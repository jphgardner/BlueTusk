using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Streams;

namespace BlueTusk.Projections.Orders.Live.Sample;

/// <summary>A real two-table join with customer dependency invalidation and exact order-total aggregation.</summary>
internal sealed class OrdersProjection(ProjectionIdentity identity) : IProjectionDefinition
{
    public ProjectionIdentity Identity { get; } = identity;

    public async ValueTask ApplySnapshotAsync(ChangeSnapshotBatch batch, ProjectionWriteContext context, CancellationToken cancellationToken)
    {
        await StageAsync(batch.Rows.Select(static value => value.Row), [], context, cancellationToken);
    }

    public async ValueTask ApplyTransactionAsync(ChangeTransaction transaction, ProjectionWriteContext context, CancellationToken cancellationToken)
    {
        var final = new Dictionary<(string Table, string Tenant, string Id), (ChangeRow Row, bool Deleted)>();
        void Stage(ChangeRow row, bool deleted) => final[(row.Table.Name, Text(row, "tenant"), Text(row, "id"))] = (row, deleted);
        await foreach (var change in transaction.Changes.WithCancellation(cancellationToken))
        {
            switch (change)
            {
                case InsertChange insert: Stage(insert.NewRow, false); break;
                case UpdateChange update: Stage(update.OldRow, true); Stage(update.NewRow, false); break;
                case DeleteChange delete: Stage(delete.OldRow, true); break;
                case LogicalMessageChange message when message.Prefix == "bluetusk.projections.barrier": break;
                default: throw new InvalidOperationException("This definition requires inserts, updates and deletes.");
            }
        }

        await StageAsync(final.Values.Where(static item => !item.Deleted).Select(static item => item.Row),
            final.Values.Where(static item => item.Deleted).Select(static item => item.Row), context, cancellationToken);
    }

    private async ValueTask StageAsync(IEnumerable<ChangeRow> upserts, IEnumerable<ChangeRow> deletes,
        ProjectionWriteContext context, CancellationToken cancellationToken)
    {
        var orders = new HashSet<(string Tenant, string Id)>();
        var customers = new HashSet<(string Tenant, string Id)>();
        var sourceDeletes = new List<ProjectionSourceDelete>();
        var sourceWrites = new List<ProjectionSourceWrite>();
        var aggregateDeltas = new Dictionary<string, decimal>(StringComparer.Ordinal);
        void AddDelta(string tenant, decimal amount) => aggregateDeltas[tenant] = aggregateDeltas.GetValueOrDefault(tenant) + amount;
        foreach (var row in deletes)
        {
            var tenant = Text(row, "tenant");
            var id = Text(row, "id");
            if (row.Table.Name == "orders")
            {
                var prior = await context.ReadSourceAsync(tenant, new("public.orders", id), cancellationToken);
                if (prior is { } payload)
                {
                    var old = JsonSerializer.Deserialize(payload.Span, ProjectionJson.Default.OrderState)!;
                    AddDelta(tenant, -old.Amount);
                }

                orders.Add((tenant, id));
                sourceDeletes.Add(new(tenant, new("public.orders", id)));
            }
            else
            {
                customers.Add((tenant, id));
                sourceDeletes.Add(new(tenant, new("public.customers", id)));
            }
        }

        foreach (var row in upserts)
        {
            var tenant = Text(row, "tenant");
            var id = Text(row, "id");
            if (row.Table.Name == "orders")
            {
                var value = new OrderState(id, tenant, Text(row, "customer"), decimal.Parse(Text(row, "amount"), CultureInfo.InvariantCulture));
                var prior = await context.ReadSourceAsync(tenant, new("public.orders", id), cancellationToken);
                if (prior is { } payload)
                {
                    var old = JsonSerializer.Deserialize(payload.Span, ProjectionJson.Default.OrderState)!;
                    AddDelta(tenant, -old.Amount);
                }

                sourceWrites.Add(new(tenant, new("public.orders", id), JsonSerializer.SerializeToUtf8Bytes(value, ProjectionJson.Default.OrderState)));
                AddDelta(tenant, value.Amount);
                orders.Add((tenant, id));
            }
            else
            {
                var value = new CustomerState(id, tenant, Text(row, "name"));
                sourceWrites.Add(new(tenant, new("public.customers", id), JsonSerializer.SerializeToUtf8Bytes(value, ProjectionJson.Default.CustomerState)));
                customers.Add((tenant, id));
            }
        }

        foreach (var batch in sourceDeletes.Chunk(1024)) { await context.DeleteSourcesAsync(batch, cancellationToken); }
        foreach (var batch in sourceWrites.Chunk(1024)) { await context.UpsertSourcesAsync(batch, cancellationToken); }
        foreach (var (tenant, delta) in aggregateDeltas) { await context.AddAggregateAsync(tenant, "all", "total", delta, cancellationToken); }

        foreach (var (tenant, id) in customers)
        {
            string? after = null;
            do
            {
                var page = await context.ReadDependentsAsync(tenant, new("public.customers", id), after, 1, cancellationToken);
                foreach (var order in page.DocumentKeys)
                {
                    orders.Add((tenant, order));
                }

                after = page.ContinueAfter;
            } while (after is not null);
        }

        var outputWrites = new List<ProjectionDocumentWrite>();
        foreach (var (tenant, id) in orders)
        {
            var source = await context.ReadSourceAsync(tenant, new("public.orders", id), cancellationToken);
            if (source is null)
            {
                await context.DeleteAsync(tenant, id, cancellationToken);
                continue;
            }

            var order = JsonSerializer.Deserialize(source.Value.Span, ProjectionJson.Default.OrderState)!;
            var customerSource = await context.ReadSourceAsync(tenant, new("public.customers", order.Customer), cancellationToken);
            var name = customerSource is { } customerPayload ? JsonSerializer.Deserialize(customerPayload.Span, ProjectionJson.Default.CustomerState)!.Name : null;
            outputWrites.Add(new ProjectionDocumentWrite(tenant, id,
                JsonSerializer.SerializeToUtf8Bytes(new OrderView(order.Id, name, order.Amount, Identity.Version), ProjectionJson.Default.OrderView),
                [new("public.orders", id), new("public.customers", order.Customer)]));
            if (outputWrites.Count == 1024)
            {
                await context.UpsertManyAsync(outputWrites, cancellationToken);
                outputWrites.Clear();
            }
        }

        if (outputWrites.Count != 0)
        {
            await context.UpsertManyAsync(outputWrites, cancellationToken);
        }
    }

    private static string Text(ChangeRow row, string column)
    {
        var value = row[column];
        if (value.State != ChangeColumnState.Value)
        {
            throw new InvalidOperationException("The example projection requires full published source values.");
        }

        return Encoding.UTF8.GetString(value.Data.Span);
    }
}

internal sealed record OrderState(string Id, string Tenant, string Customer, decimal Amount);
internal sealed record CustomerState(string Id, string Tenant, string Name);
internal sealed record OrderView(string Id, string? CustomerName, decimal Amount, int Version);

[JsonSerializable(typeof(OrderState))]
[JsonSerializable(typeof(CustomerState))]
[JsonSerializable(typeof(OrderView))]
internal sealed partial class ProjectionJson : JsonSerializerContext;
