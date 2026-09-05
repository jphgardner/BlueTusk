using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BlueTusk.Benchmarks;

internal static class ProviderRequestCaptureSelfTests
{
    public static async Task RunAsync()
    {
        var efProbe = new ProviderRequestCapture.Options
        {
            Provider = "bluetusk", Feature = "ef-update", Concurrency = 1,
            WarmupSeconds = 1, MeasurementSeconds = 2, MaximumSamplesPerWorker = 1000,
            RequireTls = false, SourceCommit = new string('a', 40),
            PostgreSqlImage = "postgres:19beta3-alpine@sha256:" + new string('b', 64),
            OutputPath = "not-written.json", Diagnostic = true,
        };
        EfBatchCapture.Validate(efProbe, 100, null);
        EfBatchCapture.Validate(efProbe, 1000, 1000);
        Action[] invalidProbes =
        [
            () => EfBatchCapture.Validate(efProbe with { Diagnostic = false }, 100, 42),
            () => EfBatchCapture.Validate(efProbe with { Concurrency = 2 }, 100, 42),
            () => EfBatchCapture.Validate(efProbe with { Feature = "copy-import-1000" }, 100, 42),
            () => EfBatchCapture.Validate(efProbe, 2, 42),
            () => EfBatchCapture.Validate(efProbe, 100, 0),
            () => EfBatchCapture.Validate(efProbe, 100, 1001),
        ];
        foreach (var invalidProbe in invalidProbes)
        {
            await RejectAsync(() => { invalidProbe(); return Task.CompletedTask; }, typeof(ArgumentException), null);
        }
        var concurrent = 0;
        var peakConcurrent = 0;
        async Task CheckedOperation(CancellationToken token)
        {
            var active = Interlocked.Increment(ref concurrent);
            int observed;
            do
            {
                observed = Volatile.Read(ref peakConcurrent);
                if (observed >= active) break;
            }
            while (Interlocked.CompareExchange(ref peakConcurrent, active, observed) != observed);
            try { await Task.Delay(2, token); }
            finally { Interlocked.Decrement(ref concurrent); }
        }

        Func<CancellationToken, Task>[] operations = Enumerable.Repeat(
            (Func<CancellationToken, Task>)CheckedOperation, 4).ToArray();
        var window = await ProviderRequestCapture.MeasureWindowAsync(operations, 0.1, 1000, CancellationToken.None);
        Assert(peakConcurrent == 4, "All four workers must execute concurrently.");
        Assert(concurrent == 0, "No operation may remain active after capture.");
        Assert(window.CompletedOperations == window.Workers.Sum(worker => worker.Count), "Every request must have one raw sample.");
        Assert(window.Workers.All(worker => worker.Count > 0 &&
            worker.RequestTicks.Take(worker.Count).All(value => value > 0)), "No worker or sample may be missing.");
        Assert(window.ElapsedTicks > 0 && window.AllocatedBytes >= 0 && window.CpuMilliseconds >= 0 &&
            window.PeakWorkingSetBytes > 0 && window.GcCollections.Length == 3, "Process counters must be recorded.");

        var warmup = await ProviderRequestCapture.MeasureWindowAsync(operations, 0.05, 0, CancellationToken.None);
        Assert(warmup.CompletedOperations > 0 && warmup.Workers.All(worker => worker.RequestTicks.Length == 0),
            "Warmup must execute without retaining measured samples.");

        Func<CancellationToken, Task>[] synchronous = Enumerable.Repeat((Func<CancellationToken, Task>)(_ =>
        {
            Thread.SpinWait(100);
            return Task.CompletedTask;
        }), 64).ToArray();
        var synchronousWindow = await ProviderRequestCapture.MeasureWindowAsync(synchronous, 0.2, 100000, CancellationToken.None);
        Assert(synchronousWindow.Workers.All(worker => worker.Count > 0),
            "Synchronous operations must not starve later workers until the window has ended.");

        await RejectAsync(() => ProviderRequestCapture.MeasureWindowAsync(
            operations, 1, 1, CancellationToken.None), typeof(InvalidOperationException), "capacity exhausted");
        Assert(concurrent == 0, "Sample overflow must cancel and join all workers.");

        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
        {
            await RejectAsync(() => ProviderRequestCapture.MeasureWindowAsync(
                operations, 10, 1000, cancellation.Token), typeof(OperationCanceledException), null);
        }
        Assert(concurrent == 0, "Cancellation must drain all in-flight requests.");

        Func<CancellationToken, Task>[] failing = [
            _ => Task.FromException(new InvalidOperationException("deliberate worker failure")), CheckedOperation];
        await RejectAsync(() => ProviderRequestCapture.MeasureWindowAsync(
            failing, 1, 1000, CancellationToken.None), typeof(InvalidOperationException), "deliberate worker failure");
        Assert(concurrent == 0, "Failure must stop companion workers.");
        await RejectAsync(() => ProviderRequestCapture.MeasureWindowAsync(
            [], 1, 1, CancellationToken.None), typeof(ArgumentOutOfRangeException), null);
        await RejectAsync(() => ProviderRequestCapture.MeasureWindowAsync(
            operations, double.NaN, 1, CancellationToken.None), typeof(ArgumentOutOfRangeException), null);
        await RejectAsync(() => ProviderRequestCapture.MeasureWindowAsync(
            operations, 1, int.MaxValue, CancellationToken.None), typeof(ArgumentOutOfRangeException), null);
        Assert(ProviderRequestFixture.Features.Length == 16 &&
            ProviderRequestFixture.Features.Distinct(StringComparer.Ordinal).Count() == 16, "All 16 feature adapters must be named uniquely.");
        Assert(ProviderRequestFixture.CaptureFeatures.Length == 20 &&
            ProviderRequestFixture.CaptureFeatures.Distinct(StringComparer.Ordinal).Count() == 20,
            "The original 16 adapters plus four contention probes must remain distinct.");
        foreach (var feature in ProviderRequestFixture.ContentionFeatures)
        {
            foreach (var concurrency in new[] { 1, 64, 256 })
            {
                var options = efProbe with { Feature = feature, Concurrency = concurrency };
                options.Validate();
                Assert(ProviderRequestFixture.GetPoolSize(options) == 4,
                    "Increasing worker count must not increase the contention probe's physical pool.");
            }
        }
        Assert(ProviderRequestFixture.GetPoolSize(efProbe with { Concurrency = 64 }) == 130,
            "The original feature adapters' pool contract must remain unchanged.");
        TestPrivateCertificateValidation();
        await ProviderRequestAnalysisSelfTests.RunAsync();
        Console.WriteLine("Provider request-capture self-tests passed: concurrency, raw samples, counters, warmup exclusion, bounded capacity, cancellation, worker failure, option bounds, and private-CA validation.");
    }

    private static void TestPrivateCertificateValidation()
    {
        using var rootKey = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        using var leafKey = RSA.Create(2048);
        using var root = CreateRoot(rootKey, "capture-test-root");
        using var otherRoot = CreateRoot(otherKey, "unrelated-root");
        using var leaf = CreateLeaf(leafKey, root, "1.3.6.1.5.5.7.3.1", expired: false);
        using var expired = CreateLeaf(leafKey, root, "1.3.6.1.5.5.7.3.1", expired: true);
        using var wrongPurpose = CreateLeaf(leafKey, root, "1.3.6.1.5.5.7.3.2", expired: false);
        Assert(ProviderRequestFixture.ValidatePrivateCertificate(leaf, null,
            SslPolicyErrors.RemoteCertificateChainErrors, root, X509RevocationMode.NoCheck),
            "The explicit private CA must be accepted after independent chain validation.");
        Assert(!ProviderRequestFixture.ValidatePrivateCertificate(leaf, null,
            SslPolicyErrors.None, otherRoot, X509RevocationMode.NoCheck), "An unrelated root must be rejected.");
        Assert(!ProviderRequestFixture.ValidatePrivateCertificate(leaf, null,
            SslPolicyErrors.RemoteCertificateNameMismatch, root, X509RevocationMode.NoCheck),
            "Hostname mismatch must never be overridden by a private root.");
        Assert(!ProviderRequestFixture.ValidatePrivateCertificate(expired, null,
            SslPolicyErrors.None, root, X509RevocationMode.NoCheck), "Expired certificates must be rejected.");
        Assert(!ProviderRequestFixture.ValidatePrivateCertificate(wrongPurpose, null,
            SslPolicyErrors.None, root, X509RevocationMode.NoCheck), "A client-only certificate must be rejected.");
        Assert(!ProviderRequestFixture.ValidatePrivateCertificate(null, null,
            SslPolicyErrors.RemoteCertificateNotAvailable, root, X509RevocationMode.NoCheck),
            "Missing certificates must be rejected.");
    }

    private static X509Certificate2 CreateRoot(RSA key, string name)
    {
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
    }

    private static X509Certificate2 CreateLeaf(RSA key, X509Certificate2 issuer, string purpose, bool expired)
    {
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var purposes = new OidCollection { new(purpose) };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(purposes, true));
        return request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1), RandomNumberGenerator.GetBytes(16));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task RejectAsync(Func<Task> action, Type exceptionType, string? message)
    {
        try { await action(); }
        catch (Exception error) when (exceptionType.IsInstanceOfType(error) &&
            (message is null || error.Message.Contains(message, StringComparison.Ordinal)))
        {
            return;
        }
        throw new InvalidOperationException($"Expected {exceptionType.Name}: {message}");
    }
}
