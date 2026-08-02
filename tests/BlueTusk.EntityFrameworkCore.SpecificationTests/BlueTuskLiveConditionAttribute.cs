using BlueTusk.Data;
using Microsoft.EntityFrameworkCore.TestUtilities.Xunit;

namespace Microsoft.EntityFrameworkCore.TestUtilities;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class BlueTuskLiveConditionAttribute : Attribute, ITestCondition
{
    public ValueTask<bool> IsMetAsync()
        => ValueTask.FromResult(BlueTuskTestStore.IsConfigured);

    public string SkipReason
        => $"{BlueTuskTestStore.ConnectionStringEnvironmentVariable} is not configured.";
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class BlueTuskServerVersionConditionAttribute(
    int minimumVersion,
    string feature) : Attribute, ITestCondition
{
    public async ValueTask<bool> IsMetAsync()
    {
        var configured = Environment.GetEnvironmentVariable(
            BlueTuskTestStore.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return true;
        }

        var settings = new BlueTuskConnectionStringBuilder(configured)
        {
            Database = "postgres",
            Pooling = false,
        };
        await using var dataSource = BlueTuskDataSource.Create(settings.ConnectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT current_setting('server_version_num')::int4");
        return await command.ExecuteScalarAsync<int>(CancellationToken.None) >= minimumVersion;
    }

    public string SkipReason
        => $"{feature} require PostgreSQL {minimumVersion / 10000} or later.";
}
