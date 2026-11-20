using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using BlueTusk.Data;

internal sealed class StreamsPrimaryWriter : IAsyncDisposable
{
    private readonly Process _process;

    internal StreamsPrimaryWriter(string assemblyPath)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add("--write");
        _process = Process.Start(start) ?? throw new InvalidOperationException("The fixed Streams writer did not start.");
    }

    internal ValueTask<string?> ReadAsync(CancellationToken cancellationToken) =>
        _process.StandardOutput.ReadLineAsync(cancellationToken);

    internal async ValueTask RequestCommitAsync(long key, CancellationToken cancellationToken)
    {
        var text = key.ToString(CultureInfo.InvariantCulture);
        await _process.StandardInput.WriteLineAsync(text.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        finally
        {
            _process.Dispose();
        }
    }

    internal static async Task RunAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var version = typeof(BlueTuskConnection).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        await Console.Out.WriteLineAsync(version);
        while (await Console.In.ReadLineAsync(cancellationToken) is { } request)
        {
            var key = long.Parse(request, CultureInfo.InvariantCulture);
            await using var command = (BlueTuskCommand)connection.CreateCommand();
            command.CommandText = "INSERT INTO public.streams_primary_source (id) VALUES ($1)";
            command.Parameters.Add(new BlueTuskParameter<long>(key));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException("The Streams source did not commit exactly one row.");
            }
            await Console.Out.WriteLineAsync(request);
        }
    }
}
