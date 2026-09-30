using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueTusk.Streams;

namespace BlueTusk.Projections.LoadHarness;

/// <summary>A real two-table join with customer dependency invalidation and exact order-total aggregation.</summary>
internal sealed class OrdersProjection(ProjectionIdentity identity) : IProjectionDefinition
{
    public ProjectionIdentity Identity { get; } = identity;
    public int DependencyPageSize { get; set; } = 1;

    public async ValueTask ApplySnapshotAsync(ChangeSnapshotBatch batch, ProjectionWriteContext context, CancellationToken cancellationToken)
    {
        await StageAsync(batch.Rows.Select(static value => value.Row).Where(static row => row.Table.Name is "orders" or "customers"), [], context, cancellationToken);
    }

    public async ValueTask ApplyTransactionAsync(ChangeTransaction transaction, ProjectionWriteContext context, CancellationToken cancellationToken)
    {
        var final = new Dictionary<(string Table, string Tenant, string Id), (ChangeRow Row, bool Deleted)>();
        void Stage(ChangeRow row, bool deleted) => final[(row.Table.Name, Text(row, "tenant"), Text(row, "id"))] = (row, deleted);
        await foreach (var change in transaction.Changes.WithCancellation(cancellationToken))
        {
            switch (change)
            {
                case InsertChange insert when insert.NewRow.Table.Name is "orders" or "customers": Stage(insert.NewRow, false); break;
                case UpdateChange update when update.NewRow.Table.Name is "orders" or "customers":
                    Stage(update.OldRow, true);
                    // FULL old tuples carry historical TOAST bytes; never query the current source
                    // to fill an unchanged new-tuple payload from a later committed transaction.
                    var values = update.NewRow.Values.ToArray();
                    for (var ordinal = 0; ordinal < values.Length; ordinal++)
                    {
                        if (values[ordinal].State != ChangeColumnState.UnchangedToast) { continue; }
                        if (update.OldRow[ordinal].State != ChangeColumnState.Value) { throw new InvalidOperationException("Historical FULL TOAST value unavailable."); }
                        values[ordinal] = update.OldRow[ordinal];
                    }
                    Stage(new(update.NewRow.Table, values), false);
                    break;
                case DeleteChange delete when delete.OldRow.Table.Name is "orders" or "customers": Stage(delete.OldRow, true); break;
                case LogicalMessageChange message when message.Prefix == "bluetusk.projections.barrier": break;
                case InsertChange insert when insert.NewRow.Table.Name == "outbox": break;
                default: throw new InvalidOperationException("Unexpected published source change.");
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
                    var old = JsonSerializer.Deserialize(payload.Span, ReportJson.Default.OrderState)!;
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
                var value = new OrderState(id, tenant, Text(row, "customer"), decimal.Parse(Text(row, "amount"), CultureInfo.InvariantCulture), Text(row, "payload"));
                var prior = await context.ReadSourceAsync(tenant, new("public.orders", id), cancellationToken);
                if (prior is { } payload)
                {
                    var old = JsonSerializer.Deserialize(payload.Span, ReportJson.Default.OrderState)!;
                    AddDelta(tenant, -old.Amount);
                }

                sourceWrites.Add(new(tenant, new("public.orders", id), JsonSerializer.SerializeToUtf8Bytes(value, ReportJson.Default.OrderState)));
                AddDelta(tenant, value.Amount);
                orders.Add((tenant, id));
            }
            else
            {
                var value = new CustomerState(id, tenant, Text(row, "name"));
                sourceWrites.Add(new(tenant, new("public.customers", id), JsonSerializer.SerializeToUtf8Bytes(value, ReportJson.Default.CustomerState)));
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
                var page = await context.ReadDependentsAsync(tenant, new("public.customers", id), after, DependencyPageSize, cancellationToken);
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

            var order = JsonSerializer.Deserialize(source.Value.Span, ReportJson.Default.OrderState)!;
            var customerSource = await context.ReadSourceAsync(tenant, new("public.customers", order.Customer), cancellationToken);
            var name = customerSource is { } customerPayload ? JsonSerializer.Deserialize(customerPayload.Span, ReportJson.Default.CustomerState)!.Name : null;
            outputWrites.Add(new ProjectionDocumentWrite(tenant, id,
                JsonSerializer.SerializeToUtf8Bytes(new OrderView(order.Id, order.Tenant, name, order.Amount, Identity.Version, order.Payload), ReportJson.Default.OrderView),
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

internal sealed record OrderState(string Id, string Tenant, string Customer, decimal Amount, string Payload);
internal sealed record CustomerState(string Id, string Tenant, string Name);
internal sealed record OrderView(string Id, string Tenant, string? CustomerName, decimal Amount, int Version, string Payload = "");
