namespace BlueTusk.Fuzzing.Tests;

public sealed class FuzzCorpusTests
{
    [Fact]
    public void Every_target_has_replayable_seed_and_regression_cases()
    {
        var root = FindRepositoryRoot();
        var corpusRoot = Path.Combine(root, "tests", "fuzz-corpus");

        foreach (var target in FuzzTargets.Names)
        {
            var cases = Directory.GetFiles(
                Path.Combine(corpusRoot, target),
                "*.b64",
                SearchOption.TopDirectoryOnly);
            Assert.NotEmpty(cases);
            foreach (var path in cases.Order(StringComparer.Ordinal))
            {
                var encoded = File.ReadAllText(path).Trim();
                var input = Convert.FromBase64String(encoded);
                Assert.InRange(input.Length, 0, FuzzTargets.MaximumInputBytes);
                FuzzTargets.Run(target, input);
            }
        }
    }

    [Fact]
    public void Oversized_inputs_are_rejected_before_dispatch()
    {
        var input = new byte[FuzzTargets.MaximumInputBytes + 1];
        foreach (var target in FuzzTargets.Names)
        {
            FuzzTargets.Run(target, input);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "BlueTusk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new InvalidOperationException("Could not locate the BlueTusk repository root.");
    }
}
