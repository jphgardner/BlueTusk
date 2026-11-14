using System.Reflection;
using BlueTusk.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Scaffolding;

namespace BlueTusk.EntityFrameworkCore.Design.Internal;

internal sealed class BlueTuskProviderCodeGenerator(ProviderCodeGeneratorDependencies dependencies)
    : ProviderCodeGenerator(dependencies)
{
#if BLUETUSK_EF6_OR_GREATER
    private static readonly MethodInfo UseBlueTuskMethod =
        typeof(BlueTuskDbContextOptionsBuilderExtensions).GetMethod(
            nameof(BlueTuskDbContextOptionsBuilderExtensions.UseBlueTusk),
            [
                typeof(DbContextOptionsBuilder),
                typeof(string),
                typeof(Action<BlueTuskDbContextOptionsBuilder>),
            ])!;
#endif

    public override MethodCallCodeFragment GenerateUseProvider(
        string connectionString,
        MethodCallCodeFragment? providerOptions)
#if BLUETUSK_EF6_OR_GREATER
        => new(UseBlueTuskMethod, connectionString, providerOptions);
#else
        => providerOptions is null
            ? new(nameof(BlueTuskDbContextOptionsBuilderExtensions.UseBlueTusk), connectionString)
            : new(nameof(BlueTuskDbContextOptionsBuilderExtensions.UseBlueTusk), connectionString,
                new NestedClosureCodeFragment("options", providerOptions));
#endif
}
