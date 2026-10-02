#pragma warning disable EF1001 // Internal EF Core API usage.

using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

namespace BlueTusk.EntityFrameworkCore.Metadata.Internal;

// EF Core binds complex-type constructor parameters only to scalar properties (dotnet/efcore#31621), so a readonly
// struct whose constructor takes nested complex values cannot be mapped. A value type is always default-constructible:
// when every parameter of one of its constructors names a mapped member and at least one names a complex property,
// BlueTusk materializes it with default(T) followed by member assignment, which is how EF Core already materializes
// value types that declare no constructor. Every other binding outcome is EF Core's.
internal sealed class BlueTuskConstructorBindingFactory(
    IPropertyParameterBindingFactory propertyFactory,
    IParameterBindingFactories factories)
    : ConstructorBindingFactory(propertyFactory, factories)
{
    public override void GetBindings(
        IReadOnlyComplexType complexType,
        out InstantiationBinding constructorBinding,
        out InstantiationBinding? serviceOnlyBinding)
    {
        try
        {
            base.GetBindings(complexType, out constructorBinding, out serviceOnlyBinding);
        }
        catch (InvalidOperationException exception) when (IsConstructorNotFound(complexType, exception)
            && CanMaterializeByMemberAssignment(complexType, out var clrType))
        {
            constructorBinding = new DefaultValueBinding(clrType);
            serviceOnlyBinding = null;
        }
    }

    private static bool IsConstructorNotFound(IReadOnlyComplexType complexType, InvalidOperationException exception)
    {
        const string Marker = "\u0000";
        var template = CoreStrings.ConstructorNotFound(complexType.DisplayName(), Marker);
        var prefix = template[..template.IndexOf(Marker, StringComparison.Ordinal)];
        return exception.Message.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool CanMaterializeByMemberAssignment(IReadOnlyComplexType complexType, out Type clrType)
    {
        clrType = Nullable.GetUnderlyingType(complexType.ClrType) ?? complexType.ClrType;
        if (!clrType.IsValueType)
        {
            return false;
        }

        var scalarMembers = complexType.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var complexMembers = complexType.GetComplexProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return clrType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(constructor => constructor.GetParameters())
            .Any(parameters => parameters.Length > 0
                && parameters.All(p => p.Name is { } name && (scalarMembers.Contains(name) || complexMembers.Contains(name)))
                && parameters.Any(p => complexMembers.Contains(p.Name!)));
    }
}
