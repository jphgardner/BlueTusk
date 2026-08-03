using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

/// <summary>PostgreSQL system columns that can be explicitly mapped for typed queries.</summary>
public enum BlueTuskSystemColumn
{
    TableOid,
    Xmin,
    Cmin,
    Xmax,
    Cmax,
    Ctid,
}

/// <summary>Stable shadow-property names used by BlueTusk's system-column mappings.</summary>
public static class BlueTuskSystemColumns
{
    public const string TableOid = "tableoid";

    public const string Xmin = "xmin";

    public const string Cmin = "cmin";

    public const string Xmax = "xmax";

    public const string Cmax = "cmax";

    public const string Ctid = "ctid";
}

/// <summary>Explicit model configuration for PostgreSQL-owned system columns.</summary>
public static class BlueTuskSystemColumnExtensions
{
    public static EntityTypeBuilder UseBlueTuskSystemColumn(
        this EntityTypeBuilder entityTypeBuilder,
        BlueTuskSystemColumn column)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        if (!Enum.IsDefined(column))
        {
            throw new ArgumentOutOfRangeException(nameof(column), column, null);
        }

        var (name, clrType, storeType) = column switch
        {
            BlueTuskSystemColumn.TableOid =>
                (BlueTuskSystemColumns.TableOid, typeof(uint), "oid"),
            BlueTuskSystemColumn.Xmin =>
                (BlueTuskSystemColumns.Xmin, typeof(BlueTuskTransactionId), "xid"),
            BlueTuskSystemColumn.Cmin =>
                (BlueTuskSystemColumns.Cmin, typeof(BlueTuskCommandId), "cid"),
            BlueTuskSystemColumn.Xmax =>
                (BlueTuskSystemColumns.Xmax, typeof(BlueTuskTransactionId), "xid"),
            BlueTuskSystemColumn.Cmax =>
                (BlueTuskSystemColumns.Cmax, typeof(BlueTuskCommandId), "cid"),
            BlueTuskSystemColumn.Ctid =>
                (BlueTuskSystemColumns.Ctid, typeof(BlueTuskTupleId), "tid"),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, null),
        };
        var property = entityTypeBuilder.Property(clrType, name)
            .HasColumnName(name)
            .HasColumnType(storeType)
            .ValueGeneratedOnAddOrUpdate()
            .IsRequired();
        property.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        property.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        property.Metadata.SetAnnotation(
            BlueTuskSystemColumnAnnotations.SystemColumn,
            (int)column);
        return entityTypeBuilder;
    }

    public static EntityTypeBuilder<TEntity> UseBlueTuskSystemColumn<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        BlueTuskSystemColumn column)
        where TEntity : class
    {
        UseBlueTuskSystemColumn((EntityTypeBuilder)entityTypeBuilder, column);
        return entityTypeBuilder;
    }

    public static EntityTypeBuilder UseBlueTuskSystemColumns(
        this EntityTypeBuilder entityTypeBuilder)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        foreach (var column in Enum.GetValues<BlueTuskSystemColumn>())
        {
            entityTypeBuilder.UseBlueTuskSystemColumn(column);
        }

        return entityTypeBuilder;
    }

    public static EntityTypeBuilder<TEntity> UseBlueTuskSystemColumns<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder)
        where TEntity : class
    {
        UseBlueTuskSystemColumns((EntityTypeBuilder)entityTypeBuilder);
        return entityTypeBuilder;
    }

    public static EntityTypeBuilder UseBlueTuskXminConcurrencyToken(
        this EntityTypeBuilder entityTypeBuilder)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        entityTypeBuilder.UseBlueTuskSystemColumn(BlueTuskSystemColumn.Xmin);
        entityTypeBuilder.Property<BlueTuskTransactionId>(BlueTuskSystemColumns.Xmin)
            .IsConcurrencyToken();
        return entityTypeBuilder;
    }

    public static EntityTypeBuilder<TEntity> UseBlueTuskXminConcurrencyToken<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder)
        where TEntity : class
    {
        UseBlueTuskXminConcurrencyToken((EntityTypeBuilder)entityTypeBuilder);
        return entityTypeBuilder;
    }
}
