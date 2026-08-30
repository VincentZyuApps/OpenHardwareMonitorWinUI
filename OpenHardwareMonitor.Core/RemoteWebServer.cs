using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenHardwareMonitor.Core;

public sealed class RemoteWebServer : IAsyncDisposable
{
    private readonly HardwareMonitorService _hardware;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptLoop;
    private WebServerSettings? _settings;

    public RemoteWebServer(HardwareMonitorService hardware) => _hardware = hardware;

    public bool IsRunning => _listener?.IsListening == true;
    public string Address => IsRunning && _settings is not null ? $"http://{_settings.Host}:{_settings.Port}/" : string.Empty;

    public Task StartAsync(WebServerSettings settings, CancellationToken cancellationToken = default)
    {
        if (IsRunning) return Task.CompletedTask;
        var host = NormalizeHost(settings.Host);
        var listener = new HttpListener { IgnoreWriteExceptions = true };
        listener.Prefixes.Add($"http://{host}:{settings.Port}/");
        listener.Start();
        _settings = settings;
        _settings.Host = host;
        _listener = listener;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(listener, _cancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        _listener = null;
        _cancellation?.Cancel();
        if (listener is not null)
        {
            try { listener.Stop(); }
            catch (HttpListenerException) { }
            listener.Close();
        }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; }
            catch (OperationCanceledException) { }
        }
        _cancellation?.Dispose();
        _cancellation = null;
        _acceptLoop = null;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested || !listener.IsListening) { break; }
            catch (ObjectDisposedException) { break; }
            _ = HandleAsync(context, cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsAuthorized(context.Request))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"OpenHardwareMonitor\"");
                return;
            }

            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            if (context.Request.HttpMethod == "GET" && (path == string.Empty || path == "/"))
            {
                await WriteHtmlAsync(context.Response, cancellationToken);
            }
            else if (context.Request.HttpMethod == "GET" && path == "/api/health")
            {
                await WriteJsonAsync(context.Response, new { status = "ok", timestamp = _hardware.Snapshot.Timestamp }, cancellationToken);
            }
            else if (context.Request.HttpMethod == "GET" && path == "/api/snapshot")
            {
                await WriteJsonAsync(context.Response, _hardware.Snapshot, cancellationToken);
            }
            else if (context.Request.HttpMethod == "GET" && path == "/api/sensors")
            {
                await WriteJsonAsync(context.Response, _hardware.Snapshot.Sensors, cancellationToken);
            }
            else if (context.Request.HttpMethod == "POST" && path.StartsWith("/api/sensors/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/control", StringComparison.OrdinalIgnoreCase))
            {
                var id = Uri.UnescapeDataString(path[13..^8]);
                var request = await JsonSerializer.DeserializeAsync<ControlRequest>(context.Request.InputStream, _jsonOptions, cancellationToken);
                await _hardware.SetControlAsync(id, request?.Value, cancellationToken);
                await WriteJsonAsync(context.Response, new { status = "ok" }, cancellationToken);
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteJsonAsync(context.Response, new { error = "Not found" }, cancellationToken);
            }
        }
        catch (ArgumentException exception)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(context.Response, new { error = exception.Message }, cancellationToken);
        }
        catch (Exception exception)
        {
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await WriteJsonAsync(context.Response, new { error = exception.Message }, cancellationToken);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (_settings is not { RequireAuthentication: true }) return true;
        var header = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
            var separator = credentials.IndexOf(':');
            if (separator < 0) return false;
            var username = credentials[..separator];
            var passwordHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials[(separator + 1)..])));
            return string.Equals(username, _settings.UserName, StringComparison.Ordinal) &&
                   string.Equals(passwordHash, _settings.PasswordSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task WriteJsonAsync(HttpListenerResponse response, object value, CancellationToken cancellationToken)
    {
        response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(response.OutputStream, value, _jsonOptions, cancellationToken);
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        const string page = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Open Hardware Monitor</title><style>body{font-family:Segoe UI,Arial;margin:32px;background:#111;color:#eee}pre{background:#202020;padding:16px;border-radius:6px;overflow:auto}</style></head><body><h1>Open Hardware Monitor</h1><p>JSON API: <code>/api/snapshot</code> and <code>/api/sensors</code></p><pre id=\"data\">Loading...</pre><script>async function refresh(){let r=await fetch('/api/snapshot');document.getElementById('data').textContent=JSON.stringify(await r.json(),null,2)}refresh();setInterval(refresh,1000)</script></body></html>";
        var bytes = Encoding.UTF8.GetBytes(page);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
    }

    private static string NormalizeHost(string host) => string.IsNullOrWhiteSpace(host) || host is "?" or "+" or "*" ? "127.0.0.1" : host.Trim();

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed record ControlRequest(double? Value);
}
