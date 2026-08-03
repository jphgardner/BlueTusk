namespace BlueTusk.EntityFrameworkCore.Views;

/// <summary>Builds a provider-owned ordinary PostgreSQL view.</summary>
public sealed class BlueTuskViewBuilder
{
    private readonly List<string> _columns = [];
    private readonly List<BlueTuskViewDependencyDefinition> _dependencies = [];

    internal BlueTuskViewBuilder(string name, string? schema, string querySql)
    {
        Name = name;
        Schema = schema;
        QuerySql = querySql;
    }

    private string Name { get; }

    private string? Schema { get; }

    private string QuerySql { get; }

    private bool SecurityBarrier { get; set; }

    private bool SecurityInvoker { get; set; }

    private BlueTuskViewCheckOption? CheckOption { get; set; }

    private bool IsRecursiveValue { get; set; }

    /// <summary>Adds an explicit output-column name in ordinal order.</summary>
    public BlueTuskViewBuilder HasColumn(string name)
    {
        _columns.Add(name);
        return this;
    }

    /// <summary>Adds explicit output-column names in ordinal order.</summary>
    public BlueTuskViewBuilder HasColumns(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _columns.AddRange(names);
        return this;
    }

    /// <summary>Declares a dependency used to order provider-owned view DDL.</summary>
    public BlueTuskViewBuilder DependsOnView(string name, string? schema = null)
    {
        _dependencies.Add(new BlueTuskViewDependencyDefinition(name, schema));
        return this;
    }

    /// <summary>Marks the view as a security barrier.</summary>
    public BlueTuskViewBuilder IsSecurityBarrier(bool securityBarrier = true)
    {
        SecurityBarrier = securityBarrier;
        return this;
    }

    /// <summary>Uses the invoking user's privileges for underlying relations.</summary>
    public BlueTuskViewBuilder IsSecurityInvoker(bool securityInvoker = true)
    {
        SecurityInvoker = securityInvoker;
        return this;
    }

    /// <summary>Configures the check option for an automatically updatable view.</summary>
    public BlueTuskViewBuilder HasCheckOption(BlueTuskViewCheckOption? checkOption)
    {
        CheckOption = checkOption;
        return this;
    }

    /// <summary>Uses PostgreSQL's <c>CREATE RECURSIVE VIEW</c> form.</summary>
    public BlueTuskViewBuilder IsRecursive(bool recursive = true)
    {
        IsRecursiveValue = recursive;
        return this;
    }

    internal BlueTuskViewDefinition Build() => new(
        Name,
        Schema,
        QuerySql,
        _columns.ToArray(),
        _dependencies.ToArray(),
        SecurityBarrier,
        SecurityInvoker,
        CheckOption,
        IsRecursiveValue);
}

/// <summary>Builds a provider-owned PostgreSQL materialized view.</summary>
public sealed class BlueTuskMaterializedViewBuilder
{
    private readonly List<string> _columns = [];
    private readonly List<BlueTuskViewDependencyDefinition> _dependencies = [];
    private readonly List<BlueTuskMaterializedViewStorageParameterDefinition> _storageParameters = [];

    internal BlueTuskMaterializedViewBuilder(string name, string? schema, string querySql)
    {
        Name = name;
        Schema = schema;
        QuerySql = querySql;
    }

    private string Name { get; }

    private string? Schema { get; }

    private string QuerySql { get; }

    private string AccessMethod { get; set; } = "heap";

    private string? Tablespace { get; set; }

    private bool IsPopulatedValue { get; set; } = true;

    /// <summary>Adds an explicit output-column name in ordinal order.</summary>
    public BlueTuskMaterializedViewBuilder HasColumn(string name)
    {
        _columns.Add(name);
        return this;
    }

    /// <summary>Adds explicit output-column names in ordinal order.</summary>
    public BlueTuskMaterializedViewBuilder HasColumns(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _columns.AddRange(names);
        return this;
    }

    /// <summary>Declares a dependency used to order provider-owned view DDL.</summary>
    public BlueTuskMaterializedViewBuilder DependsOnView(string name, string? schema = null)
    {
        _dependencies.Add(new BlueTuskViewDependencyDefinition(name, schema));
        return this;
    }

    /// <summary>Sets the table access method used for materialized contents.</summary>
    public BlueTuskMaterializedViewBuilder UseAccessMethod(string accessMethod)
    {
        AccessMethod = accessMethod;
        return this;
    }

    /// <summary>Adds a trusted materialized-view storage parameter value.</summary>
    public BlueTuskMaterializedViewBuilder HasStorageParameter(string name, string valueSql)
    {
        _storageParameters.Add(new BlueTuskMaterializedViewStorageParameterDefinition(name, valueSql));
        return this;
    }

    /// <summary>Sets the tablespace used for materialized contents.</summary>
    public BlueTuskMaterializedViewBuilder UseTablespace(string tablespace)
    {
        Tablespace = tablespace;
        return this;
    }

    /// <summary>Controls whether creation executes the defining query.</summary>
    public BlueTuskMaterializedViewBuilder IsPopulated(bool populated = true)
    {
        IsPopulatedValue = populated;
        return this;
    }

    internal BlueTuskMaterializedViewDefinition Build() => new(
        Name,
        Schema,
        QuerySql,
        _columns.ToArray(),
        _dependencies.ToArray(),
        AccessMethod,
        _storageParameters.ToArray(),
        Tablespace,
        IsPopulatedValue);
}
