using Microsoft.EntityFrameworkCore.Query;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskParameterBasedSqlProcessorFactory(
    RelationalParameterBasedSqlProcessorDependencies dependencies)
    : IRelationalParameterBasedSqlProcessorFactory
{
    public RelationalParameterBasedSqlProcessor Create(
        RelationalParameterBasedSqlProcessorParameters parameters)
        => new BlueTuskParameterBasedSqlProcessor(dependencies, parameters);
}
