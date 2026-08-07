namespace BlueTusk.ConformanceTests;

public sealed class PublicApiNamingTests
{
    private static readonly string[] ApiRoots = ["src", "extensions", "identity"];

    private static readonly HashSet<string> BrandedFrameworkBoundaryMembers =
        new(StringComparer.Ordinal)
        {
            "AddBlueTuskLiveAspNetCore",
            "AddBlueTuskStreams",
            "AddBlueTuskSync",
            "AddEntityFrameworkBlueTusk",
            "MapBlueTuskDashboard",
            "MapBlueTuskLiveGrpc",
            "MapBlueTuskLiveHub",
            "MapBlueTuskLiveServerSentEvents",
            "ToBlueTuskGeography",
            "ToBlueTuskGeometry",
            "UseBlueTusk",
            "WithBlueTuskLive",
            "WithBlueTuskStreams",
            "WithBlueTuskStreamsDirect",
            "WithBlueTuskSync",
            "WithBlueTuskSyncDirect",
        };

    [Fact]
    public void Domain_methods_do_not_repeat_the_product_brand()
    {
        var repositoryRoot = FindRepositoryRoot();
        var violations = ApiRoots
            .SelectMany(root => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, root),
                "PublicAPI.*.txt",
                SearchOption.AllDirectories))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => new
                {
                    Line = line,
                    LineNumber = index + 1,
                    Path = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
                }))
            .Select(entry => new
            {
                Entry = entry,
                Member = GetMember(entry.Line),
            })
            .Where(item =>
                item.Member is not null &&
                !item.Member.IsConstructor &&
                item.Member.Name.Contains("BlueTusk", StringComparison.Ordinal) &&
                !BrandedFrameworkBoundaryMembers.Contains(item.Member.Name))
            .Select(item => $"{item.Entry.Path}:{item.Entry.LineNumber} {item.Member!.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Redundant BlueTusk branding was found in domain API members:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    private static ApiMember? GetMember(string apiLine)
    {
        var parameterStart = apiLine.IndexOf('(', StringComparison.Ordinal);
        if (parameterStart < 0)
        {
            return null;
        }

        var declaration = apiLine[..parameterStart];
        if (declaration.Contains(" operator ", StringComparison.Ordinal))
        {
            return new ApiMember("operator", false);
        }

        var separator = declaration.LastIndexOf('.');
        var member = StripGenericArity(declaration[(separator + 1)..].Trim());
        var declaringTypeEnd = separator;
        var declaringTypeStart = declaration.LastIndexOf('.', declaringTypeEnd - 1);
        var declaringType = StripGenericArity(
            declaration[(declaringTypeStart + 1)..declaringTypeEnd].Trim());
        return new ApiMember(member, string.Equals(member, declaringType, StringComparison.Ordinal));
    }

    private static string StripGenericArity(string name)
    {
        var genericStart = name.IndexOf('<', StringComparison.Ordinal);
        return genericStart < 0 ? name : name[..genericStart];
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlueTusk.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the BlueTusk repository root.");
    }

    private sealed record ApiMember(string Name, bool IsConstructor);
}
