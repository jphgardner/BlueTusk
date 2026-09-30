using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Schema;

public enum SchemaDeploymentPhase
{
    VerifyBaseline, ApproveReview, DeployCompatibleConsumers, FenceDelivery, Expand, ApplyReviewedChanges,
    RebuildConsumers, VerifyTarget, Cutover, RetireOldVersions,
}

public sealed record SchemaContractDependency(string ContractKind, SchemaRelationIdentity Identity, string? Member = null);

/// <summary>Dependencies are explicitly supplied by the host; SQL bodies are not parsed to infer them.</summary>
public sealed class SchemaDeploymentConsumer
{
    public SchemaDeploymentConsumer(string name, IEnumerable<SchemaContractDependency> dependencies, bool rebuildOnChange = true)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        var budget = new SchemaModelBudget(SchemaSnapshotLimits.Default);
        budget.Text(name, required: true);
        if (Encoding.UTF8.GetByteCount(name) > 128) { throw new ArgumentException("Consumer names are bounded to 128 UTF-8 bytes.", nameof(name)); }
        var values = SchemaModelBudget.Materialize(dependencies, 10_000, value =>
        {
            CatalogValidation.Identity(budget, value.Identity); budget.Text(value.Member);
            if (value.ContractKind is not ("relation" or "type" or "routine" or "privilege" or "publication" or "extension"))
            { throw new ArgumentException("Unknown consumer dependency kind.", nameof(dependencies)); }
        });
        Name = name; RebuildOnChange = rebuildOnChange;
        Dependencies = Array.AsReadOnly(values.Distinct().OrderBy(value => value.ContractKind, StringComparer.Ordinal)
            .ThenBy(value => value.Identity.Schema, StringComparer.Ordinal).ThenBy(value => value.Identity.Name, StringComparer.Ordinal)
            .ThenBy(value => value.Member, StringComparer.Ordinal).ToArray());
    }
    public string Name { get; }
    public bool RebuildOnChange { get; }
    public ReadOnlyCollection<SchemaContractDependency> Dependencies { get; }
}

public sealed class SchemaDeploymentStep
{
    internal SchemaDeploymentStep(string id, SchemaDeploymentPhase phase, string? consumer, IEnumerable<string> dependencies)
    { Id = id; Phase = phase; Consumer = consumer; DependsOn = Array.AsReadOnly(dependencies.ToArray()); }
    public string Id { get; }
    public SchemaDeploymentPhase Phase { get; }
    public string? Consumer { get; }
    public ReadOnlyCollection<string> DependsOn { get; }
}

/// <summary>A deterministic deployment dependency graph. The host supplies reviewed SQL, fences, rebuilds and authorization.</summary>
public sealed class SchemaDeploymentPlan
{
    internal IReadOnlyDictionary<string, SchemaDeploymentStep> StepIndex { get; }
    private SchemaDeploymentPlan(string before, string after, bool requiresReview, IEnumerable<SchemaDeploymentStep> steps,
        IEnumerable<SchemaDeploymentConsumer> affected)
    {
        BeforeFingerprint = before; AfterFingerprint = after; RequiresReview = requiresReview;
        Steps = Array.AsReadOnly(steps.ToArray()); AffectedConsumers = Array.AsReadOnly(affected.ToArray());
        StepIndex = Steps.ToDictionary(value => value.Id, StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string? value)
        {
            Span<byte> length = stackalloc byte[4];
            if (value is null) { BinaryPrimitives.WriteInt32BigEndian(length, -1); hash.AppendData(length); return; }
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes);
        }
        Append("BlueTusk.Schema.Deployment:1"); Append(before); Append(after); Append(requiresReview ? "review" : "additive");
        Append(AffectedConsumers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var consumer in AffectedConsumers)
        {
            Append(consumer.Name); Append(consumer.RebuildOnChange ? "rebuild" : "deploy");
            Append(consumer.Dependencies.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var dependency in consumer.Dependencies)
            { Append(dependency.ContractKind); Append(dependency.Identity.Schema); Append(dependency.Identity.Name); Append(dependency.Member); }
        }
        Append(Steps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var step in Steps)
        {
            Append(step.Id); Append(step.Phase.ToString()); Append(step.Consumer);
            Append(step.DependsOn.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var dependency in step.DependsOn) { Append(dependency); }
        }
        Fingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public string BeforeFingerprint { get; }
    public string AfterFingerprint { get; }
    public string Fingerprint { get; }
    public bool RequiresReview { get; }
    public ReadOnlyCollection<SchemaDeploymentStep> Steps { get; }
    public ReadOnlyCollection<SchemaDeploymentConsumer> AffectedConsumers { get; }

    public static SchemaDeploymentPlan Create(SchemaCatalogSnapshot before, SchemaCatalogSnapshot after,
        IEnumerable<SchemaDeploymentConsumer> consumers)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after); ArgumentNullException.ThrowIfNull(consumers);
        long dependencies = 0;
        var budget = new SchemaModelBudget(SchemaSnapshotLimits.Default);
        var values = SchemaModelBudget.Materialize(consumers, 10_000, value =>
        {
            dependencies += value.Dependencies.Count; if (dependencies > 100_000) { throw new SchemaCaptureLimitException(); }
            budget.Add(64); budget.Text(value.Name, required: true);
            foreach (var dependency in value.Dependencies)
            {
                budget.Add(64); budget.Text(dependency.ContractKind, required: true);
                CatalogValidation.Identity(budget, dependency.Identity); budget.Text(dependency.Member);
            }
        });
        if (values.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != values.Count)
        { throw new ArgumentException("Deployment consumer names must be unique.", nameof(consumers)); }
        var comparison = SchemaCatalogCompatibility.Compare(before, after);
        var changedMembers = new Dictionary<(string Kind, SchemaRelationIdentity Identity), HashSet<string?>>();
        void Change(string kind, SchemaRelationIdentity identity, string? member)
        {
            if (!changedMembers.TryGetValue((kind, identity), out var members))
            { members = new(StringComparer.Ordinal); changedMembers.Add((kind, identity), members); }
            members.Add(member);
        }
        foreach (var change in comparison.Relations.Changes)
        {
            var columnChange = change.Kind is SchemaChangeKind.ColumnAdded or SchemaChangeKind.ColumnRemoved or SchemaChangeKind.ColumnTypeChanged or
                SchemaChangeKind.ColumnNullabilityChanged or SchemaChangeKind.ColumnOrdinalChanged or SchemaChangeKind.ColumnWriteBehaviorChanged or SchemaChangeKind.ColumnCollationChanged;
            Change("relation", change.Relation, columnChange ? change.Member : null);
        }
        var changedSchemas = new HashSet<string>(StringComparer.Ordinal);
        var publicationControls = before.Publications.Concat(after.Publications)
            .ToLookup(value => new SchemaRelationIdentity(value.Relation.Schema, value.Publication));
        foreach (var change in comparison.Changes)
        {
            Change(change.ContractKind, change.Identity, change.Member);
            if (change.ContractKind != "privilege" || change.Member is null) { continue; }
            if (change.Member.StartsWith("schema:", StringComparison.Ordinal)) { changedSchemas.Add(change.Identity.Schema); }
            else if (change.Member.StartsWith("relation:", StringComparison.Ordinal) || change.Member.StartsWith("column:", StringComparison.Ordinal))
            { Change("relation", change.Identity, null); }
            else if (change.Member.StartsWith("routine:", StringComparison.Ordinal)) { Change("routine", change.Identity, null); }
            else if (change.Member.StartsWith("type:", StringComparison.Ordinal)) { Change("type", change.Identity, null); }
            else if (change.Member.StartsWith("publication:", StringComparison.Ordinal))
            {
                foreach (var publication in publicationControls[change.Identity])
                { Change("publication", publication.Relation, publication.Publication); }
            }
        }
        bool Affected(SchemaDeploymentConsumer consumer) => consumer.Dependencies.Any(dependency =>
            changedSchemas.Contains(dependency.Identity.Schema) ||
            changedMembers.TryGetValue((dependency.ContractKind, dependency.Identity), out var members) &&
                (dependency.Member is null || members.Contains(null) || members.Contains(dependency.Member)));
        var affected = values.Where(Affected).OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
        var steps = new List<SchemaDeploymentStep>();
        string Add(string id, SchemaDeploymentPhase phase, string? consumer, params string[] prerequisites)
        { steps.Add(new(id, phase, consumer, prerequisites)); return id; }
        var baseline = Add("verify-baseline", SchemaDeploymentPhase.VerifyBaseline, null);
        var latest = baseline;
        var indexAdds = comparison.Relations.Changes.Any(value => value.Kind == SchemaChangeKind.IndexAdded);
        var requiresReview = comparison.RequiresReview || indexAdds;
        var additive = indexAdds || comparison.Relations.Changes.Any(value => value.Impact == SchemaChangeImpact.Additive) ||
            comparison.Changes.Any(value => value.Impact == SchemaChangeImpact.Additive);
        if (requiresReview) { latest = Add("approve-review", SchemaDeploymentPhase.ApproveReview, null, latest); }
        var deploys = new List<string>();
        for (var index = 0; index < affected.Length; index++)
        {
            var id = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            deploys.Add(Add("deploy-" + id, SchemaDeploymentPhase.DeployCompatibleConsumers, affected[index].Name, latest));
        }
        if (deploys.Count != 0)
        { latest = Add("fence-delivery", SchemaDeploymentPhase.FenceDelivery, null, deploys.ToArray()); }
        if (additive) { latest = Add("expand", SchemaDeploymentPhase.Expand, null, latest); }
        if (comparison.RequiresReview) { latest = Add("apply-reviewed", SchemaDeploymentPhase.ApplyReviewedChanges, null, latest); }
        var rebuilds = new List<string>();
        for (var index = 0; index < affected.Length; index++)
        {
            if (affected[index].RebuildOnChange)
            { rebuilds.Add(Add("rebuild-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), SchemaDeploymentPhase.RebuildConsumers, affected[index].Name, latest)); }
        }
        latest = Add("verify-target", SchemaDeploymentPhase.VerifyTarget, null, rebuilds.Count == 0 ? [latest] : rebuilds.ToArray());
        var cutovers = new List<string>();
        for (var index = 0; index < affected.Length; index++)
        { cutovers.Add(Add("cutover-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), SchemaDeploymentPhase.Cutover, affected[index].Name, latest)); }
        if (before.Fingerprint != after.Fingerprint)
        { Add("retire-old-versions", SchemaDeploymentPhase.RetireOldVersions, null, cutovers.Count == 0 ? [latest] : cutovers.ToArray()); }
        return new(before.Fingerprint, after.Fingerprint, requiresReview, steps, affected);
    }
    public SchemaDeploymentProgress Begin() => new(this, ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal));
}

/// <summary>Immutable sequencing state. Completing a step attests a host operation; it does not execute or authorize that operation.</summary>
public sealed class SchemaDeploymentProgress
{
    private readonly ImmutableHashSet<string> _completed;
    internal SchemaDeploymentProgress(SchemaDeploymentPlan plan, ImmutableHashSet<string> completed)
    { Plan = plan; _completed = completed; }
    public SchemaDeploymentPlan Plan { get; }
    public ReadOnlyCollection<string> CompletedSteps => Array.AsReadOnly(_completed.Order(StringComparer.Ordinal).ToArray());
    public bool IsComplete => _completed.Count == Plan.Steps.Count;
    public ReadOnlyCollection<SchemaDeploymentStep> ReadySteps => Array.AsReadOnly(Plan.Steps.Where(value =>
        !_completed.Contains(value.Id) && value.DependsOn.All(_completed.Contains)).ToArray());

    public SchemaDeploymentProgress CompleteStep(string stepId, string? observedFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(stepId);
        if (!Plan.StepIndex.TryGetValue(stepId, out var step)) { throw new ArgumentException("Unknown deployment step.", nameof(stepId)); }
        var required = step.Phase switch
        {
            SchemaDeploymentPhase.VerifyBaseline => Plan.BeforeFingerprint,
            SchemaDeploymentPhase.VerifyTarget => Plan.AfterFingerprint,
            SchemaDeploymentPhase.ApproveReview => Plan.Fingerprint,
            _ => null,
        };
        if (required is not null && observedFingerprint != required) { throw new InvalidOperationException("The deployment attestation fingerprint does not match."); }
        if (_completed.Contains(stepId)) { return this; }
        if (!step.DependsOn.All(_completed.Contains)) { throw new InvalidOperationException("Deployment prerequisites are incomplete."); }
        return new(Plan, _completed.Add(stepId));
    }
}
