using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;

namespace BlueTusk.Sync.S3;

internal sealed record S3SyncConfiguration(
    string PipelineId,
    string SourceFingerprint,
    string TransformFingerprint);

internal interface IS3SyncObjectStore
{
    ValueTask<S3SyncConfiguration?> LoadConfigurationAsync(CancellationToken cancellationToken);

    ValueTask WriteConfigurationAsync(
        S3SyncConfiguration configuration,
        CancellationToken cancellationToken);

    ValueTask<bool> CommitExistsAsync(string manifestKey, CancellationToken cancellationToken);

    ValueTask CommitAsync(
        string? dataKey,
        byte[]? parquet,
        string manifestKey,
        byte[] manifest,
        CancellationToken cancellationToken);
}

internal sealed class AwsS3SyncObjectStore(S3SyncOptions options) : IS3SyncObjectStore
{
    private readonly string _configurationKey = options.ObjectPrefix + "/_bluetusk/configuration.json";

    public async ValueTask<S3SyncConfiguration?> LoadConfigurationAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await options.Client.GetObjectAsync(
                options.BucketName,
                _configurationKey,
                cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<S3SyncConfiguration>(
                response.ResponseStream,
                cancellationToken: cancellationToken).ConfigureAwait(false) ??
                throw new S3SyncConfigurationException(
                    "The S3 BlueTusk configuration object is empty.");
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (JsonException exception)
        {
            throw new S3SyncConfigurationException(
                $"The S3 BlueTusk configuration object is invalid: {exception.Message}");
        }
    }

    public async ValueTask WriteConfigurationAsync(
        S3SyncConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(configuration);
        await PutImmutableAsync(
            _configurationKey,
            bytes,
            "application/json",
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> CommitExistsAsync(
        string manifestKey,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await options.Client.GetObjectMetadataAsync(
                options.BucketName,
                manifestKey,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async ValueTask CommitAsync(
        string? dataKey,
        byte[]? parquet,
        string manifestKey,
        byte[] manifest,
        CancellationToken cancellationToken)
    {
        if (dataKey is not null && parquet is not null)
        {
            await PutImmutableAsync(
                dataKey,
                parquet,
                "application/vnd.apache.parquet",
                cancellationToken).ConfigureAwait(false);
        }

        // The manifest is the commit marker and is intentionally written last.
        await PutImmutableAsync(
            manifestKey,
            manifest,
            "application/json",
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PutImmutableAsync(
        string key,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        await using var stream = new MemoryStream(content, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = options.BucketName,
            Key = key,
            InputStream = stream,
            AutoCloseStream = false,
            ContentType = contentType,
            IfNoneMatch = "*",
            ServerSideEncryptionMethod = options.ServerSideEncryption,
            ServerSideEncryptionKeyManagementServiceKeyId = options.KmsKeyId,
        };
        request.Metadata["bluetusk-sha256"] = hash;
        try
        {
            _ = await options.Client.PutObjectAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            var metadata = await options.Client.GetObjectMetadataAsync(
                options.BucketName,
                key,
                cancellationToken).ConfigureAwait(false);
            var existingHash = metadata.Metadata["x-amz-meta-bluetusk-sha256"];
            if (!string.Equals(existingHash, hash, StringComparison.Ordinal))
            {
                throw new S3SyncObjectConflictException(
                    $"Immutable S3 object '{key}' already exists with different content.");
            }
        }
        catch (AmazonS3Exception exception)
        {
            throw new S3SyncDeliveryException(
                $"S3 did not confirm immutable object '{key}'; the Sync checkpoint was not advanced.",
                exception);
        }
    }
}
