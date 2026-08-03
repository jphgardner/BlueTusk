namespace BlueTusk.Dashboard;

public sealed record BlueTuskDashboardOptions
{
    public string RoutePrefix { get; set; } = "/bluetusk";

    public string ReadAuthorizationPolicy { get; set; } = "BlueTusk.ControlPlane.Read";

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RoutePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(ReadAuthorizationPolicy);
        if (!RoutePrefix.StartsWith('/') ||
            RoutePrefix.Length > 1 && RoutePrefix.EndsWith('/') ||
            RoutePrefix.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '/' or '-' or '_' or '.' or '~')))
        {
            throw new ArgumentException(
                "The dashboard route prefix must be an absolute path without a trailing slash, query, or fragment.",
                nameof(RoutePrefix));
        }
    }
}
