using System.Text;

namespace BlueTusk.Live.Tests;

public sealed class LiveQueryContractsTests
{
    [Fact]
    public void Registered_parameters_are_exact_typed_and_stably_fingerprinted()
    {
        var plan = CreatePlan();
        var first = plan.Bind(new Dictionary<string, object?>
        {
            ["tenantId"] = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ["minimum"] = 5,
        });
        var second = plan.Bind(new Dictionary<string, object?>
        {
            ["minimum"] = 5,
            ["tenantId"] = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        });

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(5, first.Get<int>("minimum"));
        Assert.Throws<ArgumentException>(() => plan.Bind(new Dictionary<string, object?>
        {
            ["tenantId"] = Guid.Empty,
            ["minimum"] = "5",
        }));
        Assert.Throws<ArgumentException>(() => plan.Bind(new Dictionary<string, object?>
        {
            ["tenantId"] = Guid.Empty,
            ["minimum"] = 5,
            ["sql"] = "select *",
        }));
    }

    [Fact]
    public void Subscription_identity_partitions_security_policy_parameters_and_limit()
    {
        var plan = CreatePlan();
        var arguments = plan.Bind(new Dictionary<string, object?>
        {
            ["tenantId"] = Guid.Empty,
            ["minimum"] = 5,
        });
        var tenantA = LiveSubscriptionIdentity.Create(
            plan,
            arguments,
            new LiveSecurityScope("tenant:a:user:1", "policy:v1"),
            25);
        var tenantB = LiveSubscriptionIdentity.Create(
            plan,
            arguments,
            new LiveSecurityScope("tenant:b:user:1", "policy:v1"),
            25);
        var policyV2 = LiveSubscriptionIdentity.Create(
            plan,
            arguments,
            new LiveSecurityScope("tenant:a:user:1", "policy:v2"),
            25);

        Assert.NotEqual(tenantA.Fingerprint, tenantB.Fingerprint);
        Assert.NotEqual(tenantA.Fingerprint, policyV2.Fingerprint);
        Assert.Throws<ArgumentOutOfRangeException>(() => LiveSubscriptionIdentity.Create(
            plan,
            arguments,
            new LiveSecurityScope("tenant:a:user:1", "policy:v1"),
            101));
    }

    [Fact]
    public void Registry_rejects_duplicate_names_and_type_confusion()
    {
        var registry = new LiveQueryRegistry();
        var plan = CreatePlan();
        registry.Register(plan);

        Assert.Same(plan, registry.Get<Row, int>("orders"));
        Assert.Throws<InvalidOperationException>(() => registry.Register(plan));
        Assert.Throws<InvalidOperationException>(() => registry.Get<Row, Guid>("orders"));
        Assert.Throws<KeyNotFoundException>(() => registry.Get<Row, int>("missing"));
    }

    private static LiveQueryPlan<Row, int> CreatePlan() =>
        new(
            "orders",
            "db-primary",
            LiveQueryFingerprint.Create("orders", "v1", Encoding.UTF8.GetBytes("bounded-plan")),
            LiveQueryCapabilities.SingleTable |
                LiveQueryCapabilities.ParameterizedPredicate |
                LiveQueryCapabilities.TenantFilter |
                LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake,
            [new LiveTableDependency("sales", "orders")],
            [
                new LiveQueryParameter("tenantId", typeof(Guid)),
                new LiveQueryParameter("minimum", typeof(int)),
            ],
            100,
            static (_, _) => ValueTask.FromResult<IReadOnlyList<Row>>([]),
            static row => row.Id);

    private sealed record Row(int Id, string Value);
}
