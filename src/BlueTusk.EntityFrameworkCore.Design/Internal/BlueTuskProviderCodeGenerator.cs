using System.Reflection;
using BlueTusk.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Scaffolding;

namespace BlueTusk.EntityFrameworkCore.Design.Internal;

internal sealed class BlueTuskProviderCodeGenerator(ProviderCodeGeneratorDependencies dependencies)
    : ProviderCodeGenerator(dependencies)
{
    private static readonly MethodInfo UseBlueTuskMethod =
        typeof(BlueTuskDbContextOptionsBuilderExtensions).GetMethod(
            nameof(BlueTuskDbContextOptionsBuilderExtensions.UseBlueTusk),
            [
                typeof(DbContextOptionsBuilder),
                typeof(string),
                typeof(Action<BlueTuskDbContextOptionsBuilder>),
            ])!;

    public override MethodCallCodeFragment GenerateUseProvider(
        string connectionString,
        MethodCallCodeFragment? providerOptions)
        => new(UseBlueTuskMethod, connectionString, providerOptions);
}
