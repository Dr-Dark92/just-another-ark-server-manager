using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace JAASM.App.Services;

public sealed record BrowserModBridgeResult(
    bool Success,
    string Message,
    bool AlreadyExists = false);

public sealed class BrowserModBridgeService : IAsyncDisposable
{
    public const int DefaultPort = 8485;

    private readonly Func<string, Task<BrowserModBridgeResult>> _addMod;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public int Port { get; }
    public bool IsRunning => _listener is not null;

    public BrowserModBridgeService(
        Func<string, Task<BrowserModBridgeResult>> addMod,
        int port = DefaultPort)
    {
        _addMod = addMod;
        Port = port;
    }

    public Task StartAsync()
    {
        if (_listener is not null)
            return Task.CompletedTask;

        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (!ct.IsCancellationRequested)
                    await Task.Delay(250, ct);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;

            try
            {
                using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, ct);

            if (request is null)
                return;

            var origin = request.Headers.TryGetValue("Origin", out var originValue)
                ? originValue
                : string.Empty;

            if (request.Method == "OPTIONS")
            {
                await WriteResponseAsync(
                    stream,
                    204,
                    string.Empty,
                    origin,
                    ct);
                return;
            }

            if (request.Method == "GET" && request.Path == "/health")
            {
                await WriteJsonAsync(
                    stream,
                    200,
                    new { ok = true, service = "JAASM Browser Bridge", port = Port },
                    origin,
                    ct);
                return;
            }

            if (request.Method != "POST" || request.Path != "/mods/add")
            {
                await WriteJsonAsync(
                    stream,
                    404,
                    new { ok = false, message = "Not found." },
                    origin,
                    ct);
                return;
            }

            if (!IsExtensionOrigin(origin))
            {
                await WriteJsonAsync(
                    stream,
                    403,
                    new { ok = false, message = "Requests are accepted only from a browser extension." },
                    origin,
                    ct);
                return;
            }

            var bridgeHeader = request.Headers.TryGetValue("X-JAASM-Bridge", out var marker)
                ? marker
                : string.Empty;

            if (!string.Equals(bridgeHeader, "1", StringComparison.Ordinal))
            {
                await WriteJsonAsync(
                    stream,
                    403,
                    new { ok = false, message = "Missing JAASM bridge marker." },
                    origin,
                    ct);
                return;
            }

            using var doc = JsonDocument.Parse(request.Body);
            var modId = doc.RootElement.TryGetProperty("modId", out var idElement)
                ? idElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            if (modId.Length is < 4 or > 10 || !modId.All(char.IsDigit))
            {
                await WriteJsonAsync(
                    stream,
                    400,
                    new { ok = false, message = "Invalid mod ID." },
                    origin,
                    ct);
                return;
            }

            var result = await _addMod(modId);

            var statusCode = result.Success
                ? 200
                : result.AlreadyExists
                    ? 409
                    : 422;

            await WriteJsonAsync(
                stream,
                statusCode,
                new
                {
                    ok = result.Success,
                    alreadyExists = result.AlreadyExists,
                    message = result.Message,
                    modId
                },
                origin,
                ct);
            }
            catch
            {
                // Browser bridge must never be able to crash JAASM.
            }
        }
    }

    private static bool IsExtensionOrigin(string origin) =>
        origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase) ||
        origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
        origin.StartsWith("edge-extension://", StringComparison.OrdinalIgnoreCase);

    private static async Task<HttpRequest?> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        const int maxHeaderBytes = 32 * 1024;
        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        int headerEnd = -1;

        while (ms.Length < maxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read <= 0)
                return null;

            ms.Write(buffer, 0, read);

            var bytes = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            headerEnd = IndexOf(bytes, "\r\n\r\n"u8);
            if (headerEnd >= 0)
                break;
        }

        if (headerEnd < 0)
            return null;

        var allBytes = ms.ToArray();
        var headerText = Encoding.ASCII.GetString(allBytes, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);

        if (lines.Length == 0)
            return null;

        var first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length < 2)
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var contentLength = headers.TryGetValue("Content-Length", out var lengthText) &&
                            int.TryParse(lengthText, out var parsedLength)
            ? Math.Clamp(parsedLength, 0, 64 * 1024)
            : 0;

        var bodyOffset = headerEnd + 4;
        var body = new byte[contentLength];
        var available = Math.Max(0, allBytes.Length - bodyOffset);
        var copied = Math.Min(available, contentLength);

        if (copied > 0)
            Array.Copy(allBytes, bodyOffset, body, 0, copied);

        var position = copied;
        while (position < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(position, contentLength - position), ct);
            if (read <= 0)
                break;

            position += read;
        }

        return new HttpRequest(
            first[0].ToUpperInvariant(),
            first[1],
            headers,
            Encoding.UTF8.GetString(body, 0, position));
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        }

        return -1;
    }

    private static async Task WriteJsonAsync(
        NetworkStream stream,
        int status,
        object body,
        string origin,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        await WriteResponseAsync(stream, status, json, origin, ct, "application/json; charset=utf-8");
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        string body,
        string origin,
        CancellationToken ct,
        string contentType = "text/plain; charset=utf-8")
    {
        var reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            422 => "Unprocessable Entity",
            _ => "Error"
        };

        var payload = Encoding.UTF8.GetBytes(body);
        var allowOrigin = IsExtensionOrigin(origin) ? origin : "null";

        var headers =
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            $"Access-Control-Allow-Origin: {allowOrigin}\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type, X-JAASM-Bridge\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, ct);

        if (payload.Length > 0)
            await stream.WriteAsync(payload, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        _listener = null;

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch
            {
                // Ignore shutdown race.
            }
        }

        _cts.Dispose();
    }

    private sealed record HttpRequest(
        string Method,
        string Path,
        Dictionary<string, string> Headers,
        string Body);
}
