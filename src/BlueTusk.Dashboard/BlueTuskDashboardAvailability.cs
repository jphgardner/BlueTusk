using System.Text;
using BlueTusk.ControlPlane;

namespace BlueTusk.Dashboard;

public static partial class BlueTuskDashboardEndpointRouteBuilderExtensions
{
    private static string RenderInstanceAvailability(ControlPlaneOverview overview)
    {
        var failed = overview.Instances.Where(instance =>
            instance.Availability != ControlPlaneInstanceAvailability.Current).ToArray();
        if (failed.Length == 0)
        {
            return string.Empty;
        }
        var body = new StringBuilder("<section class=\"panel\" role=\"status\"><h2>Instance availability</h2><ul>");
        foreach (var instance in failed)
        {
            body.Append("<li>").Append(E(instance.InstanceName)).Append(": ")
                .Append(StatusBadge(instance.Availability.ToString(),
                    instance.Availability == ControlPlaneInstanceAvailability.Stale ? "warn" : "critical"))
                .Append(instance.LastSuccessfulObservationAt is null
                    ? " No successful observation."
                    : " Last successful observation: " + Date(instance.LastSuccessfulObservationAt) + ".")
                .Append("</li>");
        }
        return body.Append("</ul></section>").ToString();
    }
}
