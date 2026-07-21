namespace BlueTusk.Transport;

/// <summary>Identifies a TCP or Unix-domain PostgreSQL endpoint.</summary>
public abstract record BlueTuskEndpoint
{
    private BlueTuskEndpoint()
    {
    }

    public sealed record Tcp(string Host, int Port = 5432) : BlueTuskEndpoint
    {
        public string Host { get; } = ValidateHost(Host);

        public int Port { get; } = ValidatePort(Port);

        private static string ValidateHost(string host) =>
            !string.IsNullOrWhiteSpace(host)
                ? host
                : throw new ArgumentException("A host is required.", nameof(host));

        private static int ValidatePort(int port) =>
            port is > 0 and <= 65_535
                ? port
                : throw new ArgumentOutOfRangeException(nameof(port), port, "The port must be between 1 and 65535.");
    }

    public sealed record UnixSocket(string Path) : BlueTuskEndpoint
    {
        public string Path { get; } = !string.IsNullOrWhiteSpace(Path)
            ? System.IO.Path.GetFullPath(Path)
            : throw new ArgumentException("A socket path is required.", nameof(Path));
    }
}

