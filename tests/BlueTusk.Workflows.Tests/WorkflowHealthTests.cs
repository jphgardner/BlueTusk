using BlueTusk.Jobs;

namespace BlueTusk.Workflows.Tests;

public sealed class WorkflowHealthTests
{
    [Fact]
    public async Task ActiveInspectionIsBoundedAndScoped()
    {
        await using var database = await WorkflowDatabase.CreateAsync();
        await database.Store.RegisterDefinitionAsync(database.Scope, new WorkflowDefinition
        {
            Name = "test", Version = 1, Nodes = [new() { Id = "wait", Kind = WorkflowNodeKind.Signal, Signal = "resume" }],
        });
        for (int index = 0; index < 4; index++)
        {
            _ = await database.Store.StartAsync(database.Request());
        }

        var health = await database.Store.InspectAsync(database.Scope, maximumObserved: 2);
        Assert.Equal(2, health.RunningObserved);
        Assert.True(health.RunningCountCapped);
        Assert.Equal(0, health.CompensatingObserved);
        Assert.NotNull(health.OldestActiveAge);
        var other = await database.Store.InspectAsync(new JobScope("other", database.Scope.Queue));
        Assert.Equal(0, other.RunningObserved);
        Assert.Null(other.OldestActiveAge);
    }
}
