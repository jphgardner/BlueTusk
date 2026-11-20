namespace BlueTusk.Dashboard;

public sealed record BlueTuskDashboardOptions
{
    public string RoutePrefix { get; set; } = "/bluetusk";

    public string BrandLabel { get; set; } = "Control plane";

    public string? DataProvenanceNotice { get; set; }

    public string ReadAuthorizationPolicy { get; set; } = "BlueTusk.ControlPlane.Read";

    public string MutationAuthorizationPolicy { get; set; } = "BlueTusk.ControlPlane.Mutate";

    public string GraphExecutionAuthorizationPolicy { get; set; } =
        "BlueTusk.ControlPlane.GraphExecute";

    public string ViewerRole { get; set; } = "BlueTuskViewer";

    public string OperatorRole { get; set; } = "BlueTuskOperator";

    public string AdministratorRole { get; set; } = "BlueTuskAdministrator";

    public string GraphExecutorRole { get; set; } = "BlueTuskOperator";

    internal void Validate()
    {
        BlueTuskGuard.NotNullOrWhiteSpace(RoutePrefix);
        BlueTuskGuard.NotNullOrWhiteSpace(BrandLabel);
        BlueTuskGuard.NotNullOrWhiteSpace(ReadAuthorizationPolicy);
        BlueTuskGuard.NotNullOrWhiteSpace(MutationAuthorizationPolicy);
        BlueTuskGuard.NotNullOrWhiteSpace(GraphExecutionAuthorizationPolicy);
        BlueTuskGuard.NotNullOrWhiteSpace(ViewerRole);
        BlueTuskGuard.NotNullOrWhiteSpace(OperatorRole);
        BlueTuskGuard.NotNullOrWhiteSpace(AdministratorRole);
        BlueTuskGuard.NotNullOrWhiteSpace(GraphExecutorRole);
        if (!RoutePrefix.StartsWith('/') ||
            RoutePrefix.Length > 1 && RoutePrefix.EndsWith('/') ||
            RoutePrefix.Any(character =>
                !(BlueTuskRuntime.IsAsciiLetterOrDigit(character) ||
                  character is '/' or '-' or '_' or '.' or '~')))
        {
            throw new ArgumentException(
                "The dashboard route prefix must be an absolute path without a trailing slash, query, or fragment.",
                nameof(RoutePrefix));
        }
    }
}
