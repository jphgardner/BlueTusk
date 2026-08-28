using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace BlueTusk.EntityFrameworkCore.Graphs;

/// <summary>
/// Carries the immutable typed graph translation metadata to a coordinating compiler.
/// The capture is scoped to the short-lived registration context and never affects
/// ordinary query execution.
/// </summary>
internal static class BlueTuskGraphQueryCapture
{
    private static readonly ConditionalWeakTable<DbContext, Capture> Captures = new();

    public static void Record(DbContext context, BlueTuskGraphQueryImpactPlan plan)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        var capture = Captures.GetOrCreateValue(context);
        lock (capture)
        {
            capture.Plan = plan;
        }
    }

    public static BlueTuskGraphQueryImpactPlan? Consume(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Captures.TryGetValue(context, out var capture))
        {
            return null;
        }

        lock (capture)
        {
            var plan = capture.Plan;
            capture.Plan = null;
            return plan;
        }
    }

    private sealed class Capture
    {
        public BlueTuskGraphQueryImpactPlan? Plan { get; set; }
    }
}
