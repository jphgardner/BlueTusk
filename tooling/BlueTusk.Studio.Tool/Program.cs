using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BlueTusk.Studio.Tool;

internal static class Program
{
    internal static Task<int> Main(string[] args) => StudioCli.RunAsync(args, Console.Out, Console.Error);
}

internal static class StudioCli
{
    private const int MaximumReplyBytes = 1024 * 1024;

    internal static async Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (arguments.Count == 0 || arguments is ["--help"] or ["-h"])
            {
                await output.WriteLineAsync("bluetusk-studio subscriptions|streams|events --endpoint URL [--limit 1..1000] [--after CURSOR] [--stream ALIAS]\nAuthenticate with BLUETUSK_STUDIO_TOKEN. HTTPS is required except for loopback fixtures.\nCommands inspect authorized operational metadata only. Exit: 0 success; 2 unauthorized; 1 invalid/failure.").ConfigureAwait(false);
                return 0;
            }
            var route = Parse(arguments);
            var token = Environment.GetEnvironmentVariable("BLUETUSK_STUDIO_TOKEN");
            if (string.IsNullOrWhiteSpace(token) || token.Length > 16 * 1024 || token.Any(char.IsControl))
            {
                throw new ArgumentException("Authentication is not configured.");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new("application/json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await error.WriteLineAsync("Studio inspection denied.").ConfigureAwait(false);
                return 2;
            }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumReplyBytes ||
                response.Content.Headers.ContentType?.MediaType != "application/json")
            {
                throw new InvalidOperationException("Studio inspection returned an invalid response.");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (bytes.Length + read > MaximumReplyBytes) { throw new InvalidOperationException("Studio inspection exceeds its byte limit."); }
                    bytes.Write(buffer, 0, read);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            using var json = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)), new() { MaxDepth = 8 });
            ValidateResponse(arguments[0], json.RootElement);
            // Canonical JSON escaping prevents arbitrary metadata from becoming terminal control sequences.
            await output.WriteLineAsync(JsonSerializer.Serialize(json.RootElement)).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Studio inspection cancelled or timed out.").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // HTTP errors, parsing exceptions and authentication inputs never become log text.
            await error.WriteLineAsync("Studio inspection failed or arguments were invalid.").ConfigureAwait(false);
            return 1;
        }
    }

    private static Uri Parse(IReadOnlyList<string> arguments)
    {
        var command = arguments[0];
        if (command is not ("subscriptions" or "streams" or "events") || arguments.Count > 11) { throw new ArgumentException("Invalid command."); }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count || arguments[index] is not ("--endpoint" or "--limit" or "--after" or "--stream") ||
                !options.TryAdd(arguments[index], arguments[index + 1])) { throw new ArgumentException("Invalid options."); }
        }
        if (!options.TryGetValue("--endpoint", out var endpoint) || endpoint.Length > 2048 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback))
        {
            throw new ArgumentException("Invalid Studio endpoint.");
        }
        if (options.TryGetValue("--limit", out var limit) && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1 || count > 1000))
        {
            throw new ArgumentException("Invalid page limit.");
        }
        var after = options.GetValueOrDefault("--after");
        var alias = options.GetValueOrDefault("--stream");
        if (command == "streams" && options.Count != 1 || command != "events" && alias is not null)
        {
            throw new ArgumentException("Invalid command options.");
        }
        if (command == "events" && (alias is not { Length: > 0 and <= 128 } || alias.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.'))))
        {
            throw new ArgumentException("Invalid stream alias.");
        }
        if (after is not null && (command == "subscriptions" ? !Fingerprint(after) :
            !long.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 0 || after != sequence.ToString(CultureInfo.InvariantCulture)))
        {
            throw new ArgumentException("Invalid page cursor.");
        }
        var query = new List<string>();
        if (alias is not null) { query.Add("stream=" + Uri.EscapeDataString(alias)); }
        if (limit is not null) { query.Add("limit=" + Uri.EscapeDataString(limit)); }
        if (after is not null) { query.Add("after=" + Uri.EscapeDataString(after)); }
        var path = command switch { "subscriptions" => "operations/live", "streams" => "events/streams", _ => "events/" };
        return new(new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"), path + (query.Count == 0 ? string.Empty : "?" + string.Join('&', query)));
    }

    private static bool Fingerprint(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateResponse(string command, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
        var name = command switch { "subscriptions" => "subscriptions", "streams" => "streams", _ => "events" };
        if (!root.TryGetProperty(name, out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 1000) { throw new JsonException(); }
        foreach (var field in root.EnumerateObject())
        {
            if (field.Name != name && (command switch
            {
                "subscriptions" => field.Name is not ("observedAt" or "next" or "replayTargets"),
                "events" => field.Name != "next",
                _ => true,
            })) { throw new JsonException(); }
        }
        foreach (var row in rows.EnumerateArray())
        {
            if (command == "streams")
            {
                if (row.ValueKind != JsonValueKind.String || row.GetString() is not { Length: > 0 and <= 128 }) { throw new JsonException(); }
            }
            else
            {
                if (row.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
                foreach (var field in row.EnumerateObject())
                {
                    if (field.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) { throw new JsonException(); }
                    if (command == "events" ? field.Name is not ("id" or "sequence" or "type" or "version" or "occurredAt" or "payloadBytes") :
                        field.Name is not ("fingerprint" or "started" or "subscribers" or "resultCount" or "persistedSequence" or "publishedEvents" or "replayedEvents" or
                            "resumeRejections" or "slowClientDisconnects" or "authoritativeQueries" or "coalescedInvalidations" or "invalidationLag"))
                    {
                        throw new JsonException();
                    }
                }
            }
        }
    }
}
