using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BenchmarkDotNet.Attributes;
using BlueTusk.Protocol;
using BlueTusk.Transport;

namespace BlueTusk.Benchmarks;

public enum TransportLoopbackMode
{
    PlainTcp,
    Tls,
}

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class TransportPipelineSocketBenchmarks : IDisposable
{
    private const int MessageCount = 16;
    private byte[] _batch = null!;
    private LoopbackPair _currentPair = null!;
    private LoopbackPair _prototypePair = null!;
    private BenchmarkStreamTransport _prototypeTransport = null!;
    private BlueTuskProtocolConnection _current = null!;
    private TransportPipelinePrototype _prototype = null!;

    [ParamsAllValues]
    public TransportLoopbackMode Mode { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var frame = TransportPipelineBenchmarks.CreateFrame('D', new byte[256]);
        _batch = new byte[frame.Length * MessageCount];
        for (var index = 0; index < MessageCount; index++)
        {
            frame.CopyTo(_batch, index * frame.Length);
        }

        using var certificate = Mode == TransportLoopbackMode.Tls
            ? CreateCertificate()
            : null;
        _currentPair = LoopbackPair.Create(certificate);
        _prototypePair = LoopbackPair.Create(certificate);
        _current = new BlueTuskProtocolConnection(
            new BenchmarkStreamTransport(_currentPair.Client));
        _prototypeTransport = new BenchmarkStreamTransport(_prototypePair.Client);
        _prototype = new TransportPipelinePrototype();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = MessageCount)]
    public long CurrentArrayPoolSocketSync()
    {
        _currentPair.Server.Write(_batch);
        _currentPair.Server.Flush();
        var checksum = 0L;
        for (var index = 0; index < MessageCount; index++)
        {
            checksum += TransportPipelinePrototype.Consume(_current.ReadMessage());
        }

        return checksum;
    }

    [Benchmark(OperationsPerInvoke = MessageCount)]
    public long PipelinesPrototypeSocketBlockingSync()
    {
        _prototypePair.Server.Write(_batch);
        _prototypePair.Server.Flush();
        return _prototype.ReadBatch(
            _prototypeTransport,
            _batch.Length,
            MessageCount);
    }

    [Benchmark(OperationsPerInvoke = MessageCount)]
    public async ValueTask<long> CurrentArrayPoolSocketAsync()
    {
        await _currentPair.Server.WriteAsync(
            _batch,
            CancellationToken.None).ConfigureAwait(false);
        var checksum = 0L;
        for (var index = 0; index < MessageCount; index++)
        {
            checksum += TransportPipelinePrototype.Consume(
                await _current.ReadMessageAsync(CancellationToken.None).ConfigureAwait(false));
        }

        return checksum;
    }

    [Benchmark(OperationsPerInvoke = MessageCount)]
    public async ValueTask<long> PipelinesPrototypeSocketAsync()
    {
        await _prototypePair.Server.WriteAsync(
            _batch,
            CancellationToken.None).ConfigureAwait(false);
        return await _prototype.ReadBatchAsync(
            _prototypeTransport,
            _batch.Length,
            MessageCount).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _current?.Dispose();
        _prototype?.Dispose();
        _currentPair?.Dispose();
        _prototypePair?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")],
                critical: true));
        var subjectAlternativeName = new SubjectAlternativeNameBuilder();
        subjectAlternativeName.AddDnsName("localhost");
        request.CertificateExtensions.Add(subjectAlternativeName.Build());
        using var generatedCertificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(
            generatedCertificate.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable);
    }

    private sealed class BenchmarkStreamTransport(Stream stream) : IBlueTuskTransport
    {
        public EndPoint? RemoteEndPoint => null;

        public int Read(Span<byte> buffer) => stream.Read(buffer);

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            stream.ReadAsync(buffer, cancellationToken);

        public void Connect(BlueTuskEndpoint endpoint, BlueTuskTransportOptions options)
        {
        }

        public ValueTask ConnectAsync(
            BlueTuskEndpoint endpoint,
            BlueTuskTransportOptions options,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Write(ReadOnlySpan<byte> buffer) => stream.Write(buffer);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            stream.WriteAsync(buffer, cancellationToken);

        public void Flush() => stream.Flush();

        public ValueTask FlushAsync(CancellationToken cancellationToken) =>
            new(stream.FlushAsync(cancellationToken));

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LoopbackPair : IDisposable
    {
        private readonly TcpClient _clientSocket;
        private readonly TcpClient _serverSocket;

        private LoopbackPair(
            TcpClient clientSocket,
            TcpClient serverSocket,
            Stream client,
            Stream server)
        {
            _clientSocket = clientSocket;
            _serverSocket = serverSocket;
            Client = client;
            Server = server;
        }

        public Stream Client { get; }

        public Stream Server { get; }

        public static LoopbackPair Create(X509Certificate2? certificate)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var acceptTask = listener.AcceptTcpClientAsync(CancellationToken.None).AsTask();
            var clientSocket = new TcpClient(AddressFamily.InterNetwork);
            clientSocket.Connect((IPEndPoint)listener.LocalEndpoint);
            var serverSocket = acceptTask.GetAwaiter().GetResult();
            clientSocket.ReceiveTimeout = 5_000;
            clientSocket.SendTimeout = 5_000;
            serverSocket.ReceiveTimeout = 5_000;
            serverSocket.SendTimeout = 5_000;
            if (certificate is null)
            {
                return new LoopbackPair(
                    clientSocket,
                    serverSocket,
                    clientSocket.GetStream(),
                    serverSocket.GetStream());
            }

            var client = new SslStream(clientSocket.GetStream(), leaveInnerStreamOpen: false);
            var server = new SslStream(serverSocket.GetStream(), leaveInnerStreamOpen: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var serverAuthentication = server.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
                timeout.Token);
            var clientAuthentication = client.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, remoteCertificate, _, _) =>
                        remoteCertificate is not null &&
                        certificate.RawData.AsSpan().SequenceEqual(
                            remoteCertificate.GetRawCertData()),
                },
                timeout.Token);
            Task.WhenAll(serverAuthentication, clientAuthentication).GetAwaiter().GetResult();
            return new LoopbackPair(clientSocket, serverSocket, client, server);
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
            _clientSocket.Dispose();
            _serverSocket.Dispose();
        }
    }
}
