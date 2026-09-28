namespace BlueTusk.Schema.Tests;

public sealed class SchemaDeploymentPlanTests
{
    [Fact]
    public void Breaking_deployment_requires_matching_review_fences_rebuild_verification_and_atomic_cutover_order()
    {
        var before = Catalog("int4"); var after = Catalog("int8");
        var consumers = new[]
        {
            new SchemaDeploymentConsumer("projection", [new("relation", new("app", "items"))]),
            new SchemaDeploymentConsumer("query", [new("relation", new("app", "items"), "id")], rebuildOnChange: false),
            new SchemaDeploymentConsumer("unrelated", [new("type", new("app", "other"))]),
        };
        var plan = SchemaDeploymentPlan.Create(before, after, consumers);
        Assert.True(plan.RequiresReview); Assert.Equal(2, plan.AffectedConsumers.Count);
        Assert.Equal(plan.Fingerprint, SchemaDeploymentPlan.Create(before, after, consumers.Reverse()).Fingerprint);
        var initial = plan.Begin(); Assert.Single(initial.ReadySteps);
        Assert.Throws<InvalidOperationException>(() => initial.CompleteStep("cutover-0"));
        Assert.Throws<InvalidOperationException>(() => initial.CompleteStep("verify-baseline", after.Fingerprint));
        var current = initial.CompleteStep("verify-baseline", before.Fingerprint);
        Assert.Same(current, current.CompleteStep("verify-baseline", before.Fingerprint));
        Assert.Throws<InvalidOperationException>(() => current.CompleteStep("approve-review", before.Fingerprint));
        current = current.CompleteStep("approve-review", plan.Fingerprint);
        Assert.Equal(2, current.ReadySteps.Count);
        current = current.CompleteStep("deploy-0");
        Assert.Throws<InvalidOperationException>(() => current.CompleteStep("fence-delivery"));
        current = current.CompleteStep("deploy-1").CompleteStep("fence-delivery").CompleteStep("apply-reviewed");
        Assert.Single(current.ReadySteps); Assert.Equal(SchemaDeploymentPhase.RebuildConsumers, current.ReadySteps[0].Phase);
        current = current.CompleteStep("rebuild-0");
        Assert.Throws<InvalidOperationException>(() => current.CompleteStep("verify-target", before.Fingerprint));
        current = current.CompleteStep("verify-target", after.Fingerprint);
        Assert.Equal(2, current.ReadySteps.Count); Assert.False(current.IsComplete);
        current = current.CompleteStep("cutover-1").CompleteStep("cutover-0").CompleteStep("retire-old-versions");
        Assert.True(current.IsComplete); Assert.Empty(current.ReadySteps); Assert.Empty(initial.CompletedSteps);
        Assert.Throws<ArgumentException>(() => current.CompleteStep("unknown"));
    }

    [Fact]
    public void Additive_and_no_change_plans_avoid_breaking_actions_and_filter_column_dependencies()
    {
        var before = Catalog("int4");
        var after = new SchemaCatalogSnapshot(new([new(new("app", "items"), "r", false, false, "d",
            [new("id", 1, "int4", false), new("label", 2, "text", true)])]));
        var consumers = new[]
        {
            new SchemaDeploymentConsumer("whole-row", [new("relation", new("app", "items"))]),
            new SchemaDeploymentConsumer("id-only", [new("relation", new("app", "items"), "id")]),
        };
        var plan = SchemaDeploymentPlan.Create(before, after, consumers);
        Assert.False(plan.RequiresReview); Assert.Equal("whole-row", Assert.Single(plan.AffectedConsumers).Name);
        Assert.Contains(plan.Steps, value => value.Phase == SchemaDeploymentPhase.Expand);
        Assert.Equal("fence-delivery", Assert.Single(Assert.Single(plan.Steps, value => value.Phase == SchemaDeploymentPhase.Expand).DependsOn));
        Assert.DoesNotContain(plan.Steps, value => value.Phase is SchemaDeploymentPhase.ApplyReviewedChanges or SchemaDeploymentPhase.ApproveReview);
        var unchanged = SchemaDeploymentPlan.Create(before, before, consumers);
        Assert.Empty(unchanged.AffectedConsumers); Assert.Equal(2, unchanged.Steps.Count);
        Assert.True(unchanged.Begin().CompleteStep("verify-baseline", before.Fingerprint).CompleteStep("verify-target", before.Fingerprint).IsComplete);
    }

    [Fact]
    public void Large_consumer_sets_remain_bounded_and_duplicate_or_invalid_dependencies_fail_closed()
    {
        var before = Catalog("int4"); var after = Catalog("int8");
        var consumers = Enumerable.Range(0, 10_000).Select(index => new SchemaDeploymentConsumer("consumer-" + index,
            [new("relation", new("app", "items"))])).ToArray();
        var plan = SchemaDeploymentPlan.Create(before, after, consumers);
        Assert.Equal(10_000, plan.AffectedConsumers.Count); Assert.Equal(30_006, plan.Steps.Count);
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaDeploymentPlan.Create(before, after, consumers.Append(consumers[0])));
        Assert.Throws<ArgumentException>(() => SchemaDeploymentPlan.Create(before, after, [consumers[0], consumers[0]]));
        Assert.Throws<ArgumentException>(() => new SchemaDeploymentConsumer("bad", [new("unknown", new("app", "items"))]));
        Assert.Throws<ArgumentException>(() => new SchemaDeploymentConsumer(new string('x', 129), []));
    }

    [Fact]
    public void Policy_acl_schema_authority_and_extension_relocation_affect_named_column_consumers()
    {
        var identity = new SchemaRelationIdentity("app", "items");
        var before = Catalog("int4");
        var policy = new SchemaCatalogSnapshot(new([new(identity, "r", false, false, "d", [new("id", 1, "int4", false)],
            policies: [new("tenant_policy", "r", true, "PUBLIC", "tenant = 'a'", null)])]));
        var consumer = new SchemaDeploymentConsumer("id-only", [new("relation", identity, "id")]);
        Assert.Single(SchemaDeploymentPlan.Create(before, policy, [consumer]).AffectedConsumers);
        var acl = new SchemaCatalogSnapshot(before.Relations,
            privileges: [new("relation", identity, null, null, "owner", "reader", "SELECT", false)]);
        Assert.Single(SchemaDeploymentPlan.Create(before, acl, [consumer]).AffectedConsumers);
        var schemaAcl = new SchemaCatalogSnapshot(before.Relations,
            privileges: [new("schema", new("app", "app"), null, null, "owner", "reader", "USAGE", false)]);
        Assert.Single(SchemaDeploymentPlan.Create(before, schemaAcl, [consumer]).AffectedConsumers);
        var oldExtension = new SchemaCatalogSnapshot(new([]), extensions: [new("vector", "old", "1", true)]);
        var newExtension = new SchemaCatalogSnapshot(new([]), extensions: [new("vector", "new", "1", true)]);
        Assert.Equal(2, SchemaCatalogCompatibility.Compare(oldExtension, newExtension).Changes.Count);
        Assert.Single(SchemaDeploymentPlan.Create(oldExtension, newExtension,
            [new("extension", [new("extension", new("old", "vector"))])]).AffectedConsumers);
    }

    private static SchemaCatalogSnapshot Catalog(string type) => new(new([new(new("app", "items"), "r", false, false, "d", [new("id", 1, type, false)])]));

    [Fact]
    public void Plan_fingerprint_distinguishes_wildcard_from_zero_argument_routine_and_bounds_aggregate_metadata()
    {
        var before = new SchemaCatalogSnapshot(new([]), routines: [new(new("app", "run"), "", "f", "integer", "sql", false, false, "v", "u", "SELECT 1")]);
        var after = new SchemaCatalogSnapshot(new([]), routines: [new(new("app", "run"), "", "f", "integer", "sql", false, false, "v", "u", "SELECT 2")]);
        var wildcard = SchemaDeploymentPlan.Create(before, after, [new("routine", [new("routine", new("app", "run"))])]);
        var exact = SchemaDeploymentPlan.Create(before, after, [new("routine", [new("routine", new("app", "run"), "")])]);
        Assert.NotEqual(wildcard.Fingerprint, exact.Fingerprint);
        var member = new string('x', 4 * 1024 * 1024);
        var consumers = Enumerable.Range(0, 17).Select(index => new SchemaDeploymentConsumer("large-" + index,
            [new("routine", new("app", "run"), member)]));
        Assert.Throws<SchemaCaptureLimitException>(() => SchemaDeploymentPlan.Create(before, after, consumers));
    }
}
