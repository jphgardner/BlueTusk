using Microsoft.EntityFrameworkCore.Query;
#if !BLUETUSK_EF9_OR_GREATER
using RelationalParameterBasedSqlProcessorParameters = System.Boolean;
#endif

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskParameterBasedSqlProcessorFactory(
    RelationalParameterBasedSqlProcessorDependencies dependencies)
    : IRelationalParameterBasedSqlProcessorFactory
{
    public RelationalParameterBasedSqlProcessor Create(
        RelationalParameterBasedSqlProcessorParameters parameters)
        => new BlueTuskParameterBasedSqlProcessor(dependencies, parameters);
}
