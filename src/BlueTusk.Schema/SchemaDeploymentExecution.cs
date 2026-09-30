using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Schema;

public enum SchemaDeploymentActionKind { External, TransactionalSql }

/// <summary>An exact, bounded action reviewed for one step. External actions are never run by Schema.</summary>
public sealed class SchemaDeploymentAction
{
    public SchemaDeploymentAction(string stepId, SchemaDeploymentActionKind kind, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (!Enum.IsDefined(kind) || Encoding.UTF8.GetByteCount(stepId) > 256 ||
            Encoding.UTF8.GetByteCount(content) > (kind == SchemaDeploymentActionKind.TransactionalSql ? 1024 * 1024 : 4096))
        { throw new ArgumentException("Invalid or oversized deployment action."); }
        if (kind == SchemaDeploymentActionKind.TransactionalSql && !IsDdl(content))
        { throw new ArgumentException("Transactional SQL must begin with a reviewed DDL command.", nameof(content)); }
        StepId = stepId; Kind = kind; Content = content;
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    public string StepId { get; }
    public SchemaDeploymentActionKind Kind { get; }
    public string Content { get; }
    public string Fingerprint { get; }

    private static bool IsDdl(string content)
    {
        var start = content.AsSpan().TrimStart();
        var end = 0;
        while (end < start.Length && start[end] is >= 'A' and <= 'Z' or >= 'a' and <= 'z') { end++; }
        var verb = start[..end];
        return verb.Equals("CREATE", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("ALTER", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("DROP", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("COMMENT", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("GRANT", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("REVOKE", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Binds every planner step to exact reviewed SQL or a named external operation.</summary>
public sealed class SchemaDeploymentDefinition
{
    internal IReadOnlyDictionary<string, SchemaDeploymentAction> ActionIndex { get; }

    public SchemaDeploymentDefinition(SchemaDeploymentPlan plan, IEnumerable<SchemaDeploymentAction> actions)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(actions);
        Plan = plan;
        var index = new Dictionary<string, SchemaDeploymentAction>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var action in actions)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (!plan.StepIndex.TryGetValue(action.StepId, out var step) || !index.TryAdd(action.StepId, action))
            { throw new ArgumentException("Actions must be unique and belong to the deployment plan.", nameof(actions)); }
            if (action.Kind == SchemaDeploymentActionKind.TransactionalSql &&
                step.Phase is not (SchemaDeploymentPhase.Expand or SchemaDeploymentPhase.ApplyReviewedChanges))
            { throw new ArgumentException("SQL execution is only admitted for DDL deployment steps.", nameof(actions)); }
            bytes += Encoding.UTF8.GetByteCount(action.Content);
            if (index.Count > plan.Steps.Count || bytes > 32 * 1024 * 1024)
            { throw new SchemaCaptureLimitException(); }
        }
        if (index.Count != plan.Steps.Count)
        { throw new ArgumentException("Every deployment step needs an action binding.", nameof(actions)); }
        ActionIndex = index;
        Actions = Array.AsReadOnly(plan.Steps.Select(step => index[step.Id]).ToArray());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            var data = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            hash.AppendData(length); hash.AppendData(data);
        }
        Add("BlueTusk.Schema.Execution:1"); Add(plan.Fingerprint);
        foreach (var action in Actions)
        { Add(action.StepId); Add(action.Kind.ToString()); Add(action.Fingerprint); }
        Fingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public SchemaDeploymentPlan Plan { get; }
    public ReadOnlyCollection<SchemaDeploymentAction> Actions { get; }
    public string Fingerprint { get; }
}

public sealed record SchemaDeploymentLease(string DeploymentId, string Owner, long FencingToken);

public enum SchemaDeploymentAttemptState { Pending, Completed, ReconciledNotApplied }

/// <summary>Only IsNew authorizes starting an external action. A pending attempt needs reconciliation.</summary>
public sealed record SchemaDeploymentStepAttempt(string StepId, int Attempt, SchemaDeploymentAttemptState State,
    bool IsNew, string ActionFingerprint, string? ObservedFingerprint, string? EvidenceSha256);
