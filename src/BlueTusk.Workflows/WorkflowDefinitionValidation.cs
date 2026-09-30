using System.Security.Cryptography;
using System.Text.Json;

namespace BlueTusk.Workflows;

internal static class WorkflowDefinitionValidation
{
    internal static (WorkflowDefinition Definition, byte[] Bytes, byte[] Fingerprint) Canonicalize(WorkflowDefinition definition, WorkflowOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkflowOptions.Name(definition.Name, nameof(definition.Name));
        WorkflowOptions.Range(definition.Version, 1, int.MaxValue, nameof(definition.Version));
        ArgumentNullException.ThrowIfNull(definition.Nodes);
        WorkflowOptions.Range(definition.Nodes.Count, 1, options.MaximumNodes, nameof(definition.Nodes));
        var nodes = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var node in definition.Nodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            WorkflowOptions.Name(node.Id, nameof(node.Id), 100);
            ArgumentNullException.ThrowIfNull(node.DependsOn);
            WorkflowOptions.Range(node.DependsOn.Count, 0, options.MaximumDependenciesPerNode, nameof(node.DependsOn));
            WorkflowOptions.Range(node.MaximumAttempts, 1, 100, nameof(node.MaximumAttempts));
            if (!Enum.IsDefined(node.Kind) || node.DependsOn.Distinct(StringComparer.Ordinal).Count() != node.DependsOn.Count)
            {
                throw new ArgumentException("Workflow kinds must be valid and dependency identities must be unique.", nameof(definition));
            }

            if (node.Kind == WorkflowNodeKind.Activity)
            {
                WorkflowOptions.Name(node.Activity!, nameof(node.Activity));
                if (node.Compensation is not null)
                {
                    WorkflowOptions.Name(node.Compensation, nameof(node.Compensation));
                }
            }
            else if (node.Activity is not null || node.Compensation is not null)
            {
                throw new ArgumentException("Only activity nodes may have activity or compensation handlers.", nameof(definition));
            }

            if (node.Kind == WorkflowNodeKind.Timer)
            {
                if (node.Delay < TimeSpan.FromMilliseconds(1) || node.Delay > TimeSpan.FromDays(365))
                {
                    throw new ArgumentOutOfRangeException(nameof(definition), "Timer delay exceeds the supported duration.");
                }
            }
            else if (node.Delay != TimeSpan.Zero)
            {
                throw new ArgumentException("Only timer nodes may set a delay.", nameof(definition));
            }

            if (node.Kind == WorkflowNodeKind.Signal)
            {
                WorkflowOptions.Name(node.Signal!, nameof(node.Signal), 100);
            }
            else if (node.Signal is not null)
            {
                throw new ArgumentException("Only signal nodes may specify a signal identity.", nameof(definition));
            }

            if (node.Kind == WorkflowNodeKind.Join && node.DependsOn.Count == 0)
            {
                throw new ArgumentException("A join requires dependencies.", nameof(definition));
            }

            nodes.Add(node.Id, node with { DependsOn = node.DependsOn.Order(StringComparer.Ordinal).ToArray() });
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in nodes.Keys)
        {
            Visit(id);
        }

        void Visit(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }

            if (!nodes.TryGetValue(id, out var node) || !visiting.Add(id))
            {
                throw new ArgumentException("Workflow dependencies contain a missing node or a cycle.", nameof(definition));
            }

            foreach (string dependency in node.DependsOn)
            {
                Visit(dependency);
            }

            visiting.Remove(id);
            visited.Add(id);
        }

        var canonical = definition with { Nodes = nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray() };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, WorkflowJsonContext.Default.WorkflowDefinition);
        if (bytes.Length > options.MaximumDefinitionBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "Workflow definition exceeds the byte limit.");
        }

        return (canonical, bytes, SHA256.HashData(bytes));
    }
}
