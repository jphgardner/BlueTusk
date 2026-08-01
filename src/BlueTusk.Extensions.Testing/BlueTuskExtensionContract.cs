using BlueTusk.Data;
using BlueTusk.TypeSystem;

namespace BlueTusk.Extensions.Testing;

/// <summary>Describes the public registrations an extension must contribute.</summary>
public sealed record BlueTuskExtensionContract
{
    public required string FeatureName { get; init; }

    public required Type FeatureType { get; init; }

    public required BlueTuskTypeName PostgreSqlType { get; init; }

    public required Type ClrType { get; init; }

    public required Type CodecType { get; init; }
}

/// <summary>A successful live extension compatibility verification.</summary>
public sealed record BlueTuskExtensionCompatibilityReport(
    string FeatureName,
    Type FeatureType,
    BlueTuskTypeDescriptor PostgreSqlType,
    Type ClrType,
    Type CodecType);

/// <summary>Framework-neutral live compatibility checks for optional extensions.</summary>
public static class BlueTuskExtensionCompatibility
{
    /// <summary>
    /// Verifies feature retention and catalogue-to-codec binding through a built data source.
    /// </summary>
    /// <remarks>
    /// The caller owns <paramref name="dataSource"/>. The method briefly opens a normal data-source
    /// connection so runtime PostgreSQL catalogue identifiers are resolved, then returns it to the
    /// pool before completing.
    /// </remarks>
    public static async ValueTask<BlueTuskExtensionCompatibilityReport> VerifyAsync(
        BlueTuskDataSource dataSource,
        BlueTuskExtensionContract contract,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(contract);
        ValidateContract(contract);

        if (!dataSource.Features.TryGet(contract.FeatureName, out var feature) ||
            feature is null ||
            !contract.FeatureType.IsInstanceOfType(feature))
        {
            throw new InvalidOperationException(
                $"Feature '{contract.FeatureName}' was not retained as {contract.FeatureType.FullName}.");
        }

        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
        }

        if (!dataSource.TypeRegistry.TryGetType(
                contract.PostgreSqlType,
                out var descriptor,
                out var codec) ||
            descriptor is null ||
            codec is null)
        {
            throw new InvalidOperationException(
                $"PostgreSQL type '{contract.PostgreSqlType}' did not resolve to a runtime codec.");
        }

        if (codec.ClrType != contract.ClrType)
        {
            throw new InvalidOperationException(
                $"PostgreSQL type '{contract.PostgreSqlType}' resolved CLR type " +
                $"{codec.ClrType.FullName} instead of {contract.ClrType.FullName}.");
        }

        if (!contract.CodecType.IsInstanceOfType(codec))
        {
            throw new InvalidOperationException(
                $"PostgreSQL type '{contract.PostgreSqlType}' resolved codec " +
                $"{codec.GetType().FullName} instead of {contract.CodecType.FullName}.");
        }

        return new BlueTuskExtensionCompatibilityReport(
            contract.FeatureName,
            feature.GetType(),
            descriptor,
            codec.ClrType,
            codec.GetType());
    }

    private static void ValidateContract(BlueTuskExtensionContract contract)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contract.FeatureName);
        ArgumentNullException.ThrowIfNull(contract.FeatureType);
        ArgumentNullException.ThrowIfNull(contract.ClrType);
        ArgumentNullException.ThrowIfNull(contract.CodecType);
        if (!typeof(IBlueTuskCodec).IsAssignableFrom(contract.CodecType))
        {
            throw new ArgumentException(
                "The expected codec type must implement IBlueTuskCodec.",
                nameof(contract));
        }
    }
}
