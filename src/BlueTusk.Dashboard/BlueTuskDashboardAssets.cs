using System.Text;

namespace BlueTusk.Dashboard;

public static partial class BlueTuskDashboardEndpointRouteBuilderExtensions
{
    private static readonly string DashboardScript = ReadAsset("dashboard.js");
    private static readonly string DashboardStyle = ReadAsset("brand.css") + "\n" + ReadAsset("dashboard.css");

    private static string ReadAsset(string name)
    {
        using var stream = OpenAsset(name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Stream OpenAsset(string name) =>
        typeof(BlueTuskDashboardEndpointRouteBuilderExtensions).Assembly
            .GetManifestResourceStream("BlueTusk.Dashboard." + name) ??
        throw new InvalidOperationException("The dashboard presentation asset is missing.");
}
