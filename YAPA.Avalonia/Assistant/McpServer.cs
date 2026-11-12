using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace YAPA.Avalonia.Assistant;

/// <summary>
/// YAPA's tools, served to Claude Code over MCP (Streamable HTTP, plain JSON replies).
///
/// In-process, so a tool call runs straight against the live app: no helper process and no
/// IPC of our own. It listens on loopback only, and only while a conversation is open, and
/// every request must carry the bearer token handed to Claude Code in its config, which
/// is enough to keep other local programs and web pages (DNS rebinding) out.
/// </summary>
public sealed class McpServer : IDisposable
{
    public const string ServerName = "yapa";
    private const string ProtocolFallback = "2025-06-18";
    private const int MaxBodyBytes = 1 << 20;

    private readonly AssistantToolbox _tools;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    private McpServer(AssistantToolbox tools, HttpListener listener, int port, string token)
    {
        _tools = tools;
        _listener = listener;
        Port = port;
        Token = token;
    }

    public int Port { get; }
    public string Token { get; }
    public string Url => $"http://127.0.0.1:{Port}/mcp";

    public static McpServer Start(AssistantToolbox tools)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        // HttpListener can't bind port 0, so borrow a free port from the OS first. Another
        // program could take it in between; then try again with a new one.
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
                continue;
            }

            var server = new McpServer(tools, listener, port, token);
            _ = server.AcceptLoop();
            return server;
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>The <c>--mcp-config</c> document that points Claude Code here.</summary>
    public string ClientConfig() => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            [ServerName] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = Url,
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + Token },
            },
        },
    }.ToJsonString();

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return; // stopped
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            if (!Authorized(request))
            {
                response.StatusCode = 401;
                return;
            }
            if (request.Url?.AbsolutePath != "/mcp")
            {
                response.StatusCode = 404;
                return;
            }
            if (request.HttpMethod != "POST")
            {
                // No server-initiated stream (GET) and no session to end (DELETE).
                response.StatusCode = 405;
                response.AddHeader("Allow", "POST");
                return;
            }

            var body = await ReadBodyAsync(request).ConfigureAwait(false);
            var reply = body == null
                ? Error(null, -32700, "The request body is too large or not UTF-8 JSON.")
                : await DispatchAsync(body).ConfigureAwait(false);

            if (reply == null)
            {
                response.StatusCode = 202; // a notification: accepted, nothing to say
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away mid-reply, or we're shutting down.
        }
        catch (Exception e)
        {
            Trace.WriteLine($"[assistant] MCP request failed: {e}");
            try { response.StatusCode = 500; } catch (InvalidOperationException) { }
        }
        finally
        {
            try { response.Close(); } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    private bool Authorized(HttpListenerRequest request)
    {
        // A browser always sends Origin; Claude Code doesn't. Refusing any Origin keeps pages
        // out even before the token is checked.
        if (request.Headers["Origin"] is { Length: > 0 }) return false;
        var header = request.Headers["Authorization"] ?? "";
        var expected = "Bearer " + Token;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes(expected));
    }

    private static async Task<JsonNode?> ReadBodyAsync(HttpListenerRequest request)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes) return null;
        }
        try
        {
            return JsonNode.Parse(buffer.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <returns>The JSON-RPC reply, or null for a notification.</returns>
    internal async Task<JsonNode?> DispatchAsync(JsonNode message)
    {
        if (message is not JsonObject call) return Error(null, -32600, "Expected a JSON-RPC request object.");

        var id = call["id"]?.DeepClone();
        var method = call["method"]?.GetValue<string>() ?? "";
        if (id == null) return null; // notifications/initialized, notifications/cancelled, …

        var parameters = call["params"] as JsonObject;
        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = parameters?["protocolVersion"]?.GetValue<string>() ?? ProtocolFallback,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "YAPA 2", ["version"] = Program.Version },
                });

            case "ping":
                return Result(id, new JsonObject());

            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = _tools.Describe() });

            case "tools/call":
                var name = parameters?["name"]?.GetValue<string>() ?? "";
                var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
                var outcome = await _tools.CallAsync(name, arguments).ConfigureAwait(false);
                return Result(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = outcome.Text }),
                    ["isError"] = outcome.IsError,
                });

            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static JsonObject Result(JsonNode id, JsonNode result)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string text)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = text } };

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Close(); } catch (ObjectDisposedException) { }
        _stop.Dispose();
    }
}
