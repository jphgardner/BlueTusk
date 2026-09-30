using System.Text.Json.Serialization;
using BlueTusk.Data;

namespace BlueTusk.Jobs.Tests;

public sealed class JobContractTests
{
    [Theory]
    [InlineData("")]
    [InlineData("bad\0name")]
    public void ScopeRejectsInvalidIdentity(string identity)
    {
        Assert.Throws<ArgumentException>(() => new JobScope(identity, "queue"));
        Assert.Throws<ArgumentException>(() => new JobScope("tenant", identity));
    }

    [Theory]
    [InlineData("public; DROP SCHEMA public")]
    [InlineData("schema.with.dot")]
    [InlineData("1schema")]
    [InlineData("échema")]
    public void SchemaMustBeAnAsciiIdentifier(string schema)
    {
        using var source = BlueTuskDataSource.Create("Host=127.0.0.1;Port=1;Username=postgres;Database=test;SSL Mode=Disable");
        Assert.Throws<ArgumentException>(() => new PostgreSqlJobStore(source, new JobStoreOptions { Schema = schema }));
    }

    [Fact]
    public async Task OversizePayloadIsRejectedBeforeConnecting()
    {
        await using var source = BlueTuskDataSource.Create("Host=127.0.0.1;Port=1;Username=postgres;Database=test;SSL Mode=Disable");
        var store = new PostgreSqlJobStore(source, new JobStoreOptions { MaximumPayloadBytes = 4 });
        var request = new JobRequest { Scope = new JobScope("tenant", "queue"), JobType = "type", Payload = new byte[5] };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.EnqueueAsync(request).AsTask());
    }

    [Fact]
    public void ExponentialRetryIsBoundedAndDeterministicWithoutJitter()
    {
        var policy = new JobRetryPolicy { InitialDelay = TimeSpan.FromMilliseconds(10), MaximumDelay = TimeSpan.FromMilliseconds(25), JitterFraction = 0 };
        Assert.Equal(TimeSpan.FromMilliseconds(10), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(20), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromMilliseconds(25), policy.GetDelay(3));
        Assert.Equal(TimeSpan.FromMilliseconds(25), policy.GetDelay(int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.GetDelay(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => (policy with { JitterFraction = double.NaN }).GetDelay(1));
    }

    [Fact]
    public void FailureCodesCannotContainExceptionText()
    {
        var failure = new JobHandlerException("downstream_busy", retryable: false);
        Assert.Equal("downstream_busy", failure.FailureCode);
        Assert.False(failure.Retryable);
        Assert.Throws<ArgumentException>(() => new JobHandlerException("password=secret failed"));
    }

    [Fact]
    public void TypedRequestUsesGeneratedJsonContract()
    {
        var request = JobRequest.FromJson(new JobScope("tenant", "queue"), "test.v1", new TestJob(42), JobJsonContext.Default.TestJob);
        Assert.Equal("{\"Value\":42}", System.Text.Encoding.UTF8.GetString(request.Payload.Span));
    }
}

public sealed record TestJob(int Value);

[JsonSerializable(typeof(TestJob))]
internal sealed partial class JobJsonContext : JsonSerializerContext;
