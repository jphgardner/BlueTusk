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
