using System.Diagnostics;

namespace BlueTusk.Diagnostics;

internal readonly struct BlueTuskConnectionInstrumentation
{
    private readonly Activity? _activity;
    private readonly string? _host;
    private readonly int _port;

    private BlueTuskConnectionInstrumentation(Activity? activity, string host, int port)
    {
        _activity = activity;
        _host = host;
        _port = port;
    }

    internal static BlueTuskConnectionInstrumentation Start(
        string database,
        string host,
        int port)
    {
        if (!BlueTuskDiagnostics.ActivitySource.HasListeners() &&
            !BlueTuskDiagnostics.ConnectionsFailed.Enabled)
        {
            return default;
        }

        var activity = BlueTuskDiagnostics.ActivitySource.StartActivity(
            $"CONNECT {host}:{port}",
            ActivityKind.Client);
        activity?.SetTag("db.system.name", "postgresql");
        activity?.SetTag("db.namespace", database);
        activity?.SetTag("server.address", host);
        activity?.SetTag("server.port", port);
        return new BlueTuskConnectionInstrumentation(activity, host, port);
    }

    internal void Complete(Exception? exception)
    {
        if (_host is null)
        {
            return;
        }

        if (exception is not null)
        {
            var errorType = exception.GetType().FullName;
            _activity?.SetTag("error.type", errorType);
            _activity?.SetStatus(ActivityStatusCode.Error);
            BlueTuskDiagnostics.ConnectionsFailed.Add(
                1,
                new KeyValuePair<string, object?>("server.address", _host),
                new KeyValuePair<string, object?>("server.port", _port),
                new KeyValuePair<string, object?>("error.type", errorType));
        }

        _activity?.Dispose();
    }
}
