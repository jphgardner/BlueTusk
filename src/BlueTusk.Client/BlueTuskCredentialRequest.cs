namespace BlueTusk.Client;

/// <summary>Identifies the PostgreSQL endpoint requesting a connection credential.</summary>
public sealed record BlueTuskCredentialRequest(
    string Host,
    int Port,
    string Database,
    string Username);

/// <summary>Resolves a PostgreSQL password for a new physical connection.</summary>
public delegate string BlueTuskPasswordProvider(BlueTuskCredentialRequest request);

/// <summary>Asynchronously resolves a PostgreSQL password for a new physical connection.</summary>
public delegate ValueTask<string> BlueTuskPasswordProviderAsync(
    BlueTuskCredentialRequest request,
    CancellationToken cancellationToken);

/// <summary>Resolves an access token used as a PostgreSQL password for a new physical connection.</summary>
public delegate string BlueTuskAccessTokenProvider(BlueTuskCredentialRequest request);

/// <summary>Asynchronously resolves an access token used as a PostgreSQL password.</summary>
public delegate ValueTask<string> BlueTuskAccessTokenProviderAsync(
    BlueTuskCredentialRequest request,
    CancellationToken cancellationToken);
