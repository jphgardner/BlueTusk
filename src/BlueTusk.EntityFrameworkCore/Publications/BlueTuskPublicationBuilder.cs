namespace BlueTusk.EntityFrameworkCore.Publications;

public sealed class BlueTuskPublicationBuilder
{
    private readonly List<BlueTuskPublicationTableDefinition> _tables = [];
    private readonly List<string> _schemas = [];

    internal BlueTuskPublicationBuilder(string name)
    {
        Name = name;
    }

    private string Name { get; }
    private bool AllTablesValue { get; set; }
    private bool AllSequencesValue { get; set; }
    private BlueTuskPublicationOperations OperationsValue { get; set; } = BlueTuskPublicationOperations.All;
    private bool PublishViaPartitionRootValue { get; set; }
    private BlueTuskPublicationGeneratedColumns GeneratedColumnsValue { get; set; }

    public BlueTuskPublicationBuilder ForTable(
        string name,
        string? schema = null,
        Action<BlueTuskPublicationTableBuilder>? configure = null)
    {
        var builder = new BlueTuskPublicationTableBuilder(name, schema);
        configure?.Invoke(builder);
        _tables.Add(builder.Build());
        return this;
    }

    public BlueTuskPublicationBuilder ForTablesInSchema(string schema)
    {
        _schemas.Add(schema);
        return this;
    }

    public BlueTuskPublicationBuilder ForAllTables(bool enabled = true)
    {
        AllTablesValue = enabled;
        return this;
    }

    public BlueTuskPublicationBuilder ForAllSequences(bool enabled = true)
    {
        AllSequencesValue = enabled;
        return this;
    }

    public BlueTuskPublicationBuilder ExceptTable(
        string name,
        string? schema = null,
        bool includeDescendants = false)
    {
        _tables.Add(new BlueTuskPublicationTableDefinition(
            name,
            schema,
            includeDescendants,
            Columns: null,
            RowFilterSql: null,
            IsExcluded: true));
        return this;
    }

    public BlueTuskPublicationBuilder Publishes(BlueTuskPublicationOperations operations)
    {
        OperationsValue = operations;
        return this;
    }

    public BlueTuskPublicationBuilder PublishViaPartitionRoot(bool enabled = true)
    {
        PublishViaPartitionRootValue = enabled;
        return this;
    }

    public BlueTuskPublicationBuilder PublishGeneratedColumns(
        BlueTuskPublicationGeneratedColumns generatedColumns = BlueTuskPublicationGeneratedColumns.Stored)
    {
        GeneratedColumnsValue = generatedColumns;
        return this;
    }

    internal BlueTuskPublicationDefinition Build() => new(
        Name,
        _tables.ToArray(),
        _schemas.ToArray(),
        AllTablesValue,
        AllSequencesValue,
        OperationsValue,
        PublishViaPartitionRootValue,
        GeneratedColumnsValue);
}

public sealed class BlueTuskPublicationTableBuilder
{
    private IReadOnlyList<string>? _columns;
    private bool _includeDescendants;
    private string? _rowFilterSql;

    internal BlueTuskPublicationTableBuilder(string name, string? schema)
    {
        Name = name;
        Schema = schema;
    }

    private string Name { get; }
    private string? Schema { get; }

    public BlueTuskPublicationTableBuilder IncludeDescendants(bool enabled = true)
    {
        _includeDescendants = enabled;
        return this;
    }

    public BlueTuskPublicationTableBuilder HasColumns(params string[] columns)
    {
        _columns = columns;
        return this;
    }

    public BlueTuskPublicationTableBuilder HasRowFilter(string rowFilterSql)
    {
        _rowFilterSql = rowFilterSql;
        return this;
    }

    internal BlueTuskPublicationTableDefinition Build() => new(
        Name,
        Schema,
        _includeDescendants,
        _columns,
        _rowFilterSql);
}
