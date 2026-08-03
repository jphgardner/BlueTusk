namespace BlueTusk.EntityFrameworkCore.Views;

/// <summary>The PostgreSQL relation kind represented by view migration metadata.</summary>
public enum BlueTuskViewKind
{
    /// <summary>A non-materialized PostgreSQL view.</summary>
    View,

    /// <summary>A PostgreSQL materialized view with stored contents.</summary>
    MaterializedView,
}

/// <summary>The check-option behavior of an automatically updatable PostgreSQL view.</summary>
public enum BlueTuskViewCheckOption
{
    /// <summary>Check only conditions defined directly by this view.</summary>
    Local,

    /// <summary>Check this view and applicable underlying views.</summary>
    Cascaded,
}

/// <summary>A provider-owned dependency on another view or materialized view.</summary>
public sealed record BlueTuskViewDependencyDefinition(string Name, string? Schema = null);

/// <summary>A PostgreSQL materialized-view storage parameter.</summary>
public sealed record BlueTuskMaterializedViewStorageParameterDefinition(string Name, string ValueSql);

/// <summary>A provider-owned ordinary PostgreSQL view. Query SQL is trusted model-time input.</summary>
public sealed record BlueTuskViewDefinition(
    string Name,
    string? Schema,
    string QuerySql,
    IReadOnlyList<string> Columns,
    IReadOnlyList<BlueTuskViewDependencyDefinition> Dependencies,
    bool SecurityBarrier = false,
    bool SecurityInvoker = false,
    BlueTuskViewCheckOption? CheckOption = null,
    bool IsRecursive = false);

/// <summary>A provider-owned PostgreSQL materialized view. Query SQL is trusted model-time input.</summary>
public sealed record BlueTuskMaterializedViewDefinition(
    string Name,
    string? Schema,
    string QuerySql,
    IReadOnlyList<string> Columns,
    IReadOnlyList<BlueTuskViewDependencyDefinition> Dependencies,
    string AccessMethod,
    IReadOnlyList<BlueTuskMaterializedViewStorageParameterDefinition> StorageParameters,
    string? Tablespace = null,
    bool IsPopulated = true);

/// <summary>Provider-owned ordinary and materialized PostgreSQL views.</summary>
public sealed record BlueTuskViewDefinitionSet(
    IReadOnlyList<BlueTuskViewDefinition> Views,
    IReadOnlyList<BlueTuskMaterializedViewDefinition> MaterializedViews)
{
    /// <summary>An empty view definition set.</summary>
    public static BlueTuskViewDefinitionSet Empty { get; } = new([], []);
}
