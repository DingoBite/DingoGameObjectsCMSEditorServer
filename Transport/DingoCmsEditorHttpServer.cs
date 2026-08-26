#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMSEditorServer.Transport
{
    public class DingoCmsEditorHttpServer : IDisposable
    {
        public const string PROTOCOL_VERSION = "2025-11-25";

        private const string SERVER_NAME = "DingoCMS Editor Server";
        private const string SERVER_VERSION = "0.1.0";
        private const string SESSION_COOKIE_NAME = "DingoCmsEditorSession";
        private const int MAX_REQUEST_BODY_CHARACTERS = 32 * 1024 * 1024;

        private readonly IDingoCmsEditorRequestRouter _router;
        private readonly string _bearerToken;
        private readonly HttpListener _listener;
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private readonly object _lifecycleGate = new();
        private readonly object _authenticationGate = new();

        private CancellationTokenSource _shutdown;
        private Task _listenTask;
        private string _browserSessionToken;
        private string _browserBootstrapToken;
        private bool _browserBootstrapAvailable = true;
        private bool _started;
        private bool _disposed;

        public int Port { get; }
        public string Origin { get; }
        public string BrowserBootstrapUrl
        {
            get
            {
                lock (_authenticationGate)
                {
                    return BuildBrowserBootstrapUrl();
                }
            }
        }
        public bool IsRunning => _started && !_disposed && _listener.IsListening;

        public DingoCmsEditorHttpServer(IDingoCmsEditorRequestRouter router, int port, string bearerToken)
        {
            _router = router ?? throw new ArgumentNullException(nameof(router));
            if (port is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port), port, "The loopback port must be between 1 and 65535.");
            }

            if (string.IsNullOrWhiteSpace(bearerToken))
            {
                throw new ArgumentException("A non-empty bearer token is required.", nameof(bearerToken));
            }

            Port = port;
            Origin = $"http://127.0.0.1:{port}";
            _bearerToken = bearerToken;
            _browserBootstrapToken = Guid.NewGuid().ToString("N")
                                     + Guid.NewGuid().ToString("N");
            _listener = new HttpListener
            {
                IgnoreWriteExceptions = true,
            };
            _listener.Prefixes.Add($"{Origin}/");
        }

        public string CreateBrowserBootstrapUrl()
        {
            lock (_authenticationGate)
            {
                ThrowIfDisposed();
                _browserBootstrapToken = Guid.NewGuid().ToString("N")
                                         + Guid.NewGuid().ToString("N");
                _browserBootstrapAvailable = true;
                return BuildBrowserBootstrapUrl();
            }
        }

        public void Start()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();
                if (_started)
                {
                    return;
                }

                _shutdown = new CancellationTokenSource();
                _listener.Start();
                _started = true;
                _listenTask = Task.Run(() => ListenAsync(_shutdown.Token));
            }
        }

        public void Dispose()
        {
            Task listenTask;
            CancellationTokenSource shutdown;
            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                shutdown = _shutdown;
                listenTask = _listenTask;
                shutdown?.Cancel();

                // Abort, not Stop/Close. Mono keeps its endpoint registrations
                // in a static inside System.dll, which Unity's domain reload
                // never unloads, and the graceful path can return with the
                // prefix still registered. That registration then holds the
                // port for the life of the editor process: every later start
                // fails with "Only one usage of each socket address", and no
                // code in the process can reclaim it. Abort tears the
                // registration down with the connections.
                try
                {
                    _listener.Abort();
                }
                catch (Exception exception) when (
                    exception is ObjectDisposedException
                    or InvalidOperationException
                    or HttpListenerException)
                {
                }

                try
                {
                    _listener.Close();
                }
                catch (Exception exception) when (
                    exception is ObjectDisposedException
                    or InvalidOperationException
                    or HttpListenerException)
                {
                }
            }

            if (listenTask != null && Task.CurrentId != listenTask.Id)
            {
                try
                {
                    listenTask.Wait(TimeSpan.FromSeconds(1));
                }
                catch (AggregateException)
                {
                }
            }

            // No new contexts can be accepted after the listener closes. By
            // taking the serialized request gate here, Dispose waits until a
            // publish already in progress has reached its transactional end;
            // queued requests observe the cancelled token and do not start.
            //
            // The wait is bounded because this runs on the editor main thread
            // from beforeAssemblyReload. Waiting indefinitely there hangs the
            // reload behind whatever request happens to be open, and a reload
            // forced through that hang leaves the listener's endpoint
            // registered for the rest of the process — after which no restart
            // of the server can take the port again until Unity is restarted.
            var drained = _requestGate.Wait(TimeSpan.FromSeconds(2));
            shutdown?.Dispose();
            if (drained)
            {
                _requestGate.Dispose();
            }
            // An undrained gate is deliberately leaked rather than disposed:
            // the request still holding it would fault on a disposed handle,
            // and one abandoned semaphore costs less than that.
        }

        private async Task ListenAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested || !_listener.IsListening)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || _disposed)
                {
                    break;
                }

                _ = Task.Run(() => HandleSafelyAsync(context, cancellationToken));
            }
        }

        private async Task HandleSafelyAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            try
            {
                await HandleAsync(context, cancellationToken);
            }
            catch (Exception exception)
            {
                await TryWriteUnexpectedErrorAsync(context.Response, exception);
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch
                {
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            var request = context.Request;
            var response = context.Response;
            var path = request.Url?.AbsolutePath ?? "/";

            if (request.HttpMethod == "OPTIONS")
            {
                await HandleOptionsAsync(request, response);
                return;
            }

            if (!IsAllowedOrigin(request))
            {
                await WriteHttpErrorAsync(response, HttpStatusCode.Forbidden, "forbidden_origin", "The request origin is not allowed.");
                return;
            }

            AddCorsHeaders(request, response);

            if (request.HttpMethod == "GET" && path == "/")
            {
                await HandleWebIndexAsync(request, response);
                return;
            }

            if (path == "/mcp")
            {
                if (!IsAuthenticated(request))
                {
                    await WriteUnauthorizedAsync(response);
                    return;
                }

                if (request.HttpMethod == "POST")
                {
                    await HandleMcpAsync(request, response, cancellationToken);
                    return;
                }

                response.Headers["Allow"] = "POST";
                await WriteHttpErrorAsync(
                    response,
                    HttpStatusCode.MethodNotAllowed,
                    "method_not_allowed",
                    "This server has no SSE stream; send MCP messages with POST.");
                return;
            }

            const string apiPrefix = "/api/";
            if (request.HttpMethod == "POST" && path.StartsWith(apiPrefix, StringComparison.Ordinal))
            {
                if (!IsAuthenticated(request))
                {
                    await WriteUnauthorizedAsync(response);
                    return;
                }

                var operation = Uri.UnescapeDataString(path.Substring(apiPrefix.Length));
                if (string.IsNullOrWhiteSpace(operation) || operation.Contains("/"))
                {
                    await WriteHttpErrorAsync(response, HttpStatusCode.BadRequest, "invalid_operation", "The API operation must be one non-empty path segment.");
                    return;
                }

                await HandleApiAsync(request, response, operation, cancellationToken);
                return;
            }

            await WriteHttpErrorAsync(response, HttpStatusCode.NotFound, "not_found", "The requested endpoint does not exist.");
        }

        private async Task HandleOptionsAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!HasExactOrigin(request))
            {
                await WriteHttpErrorAsync(response, HttpStatusCode.Forbidden, "forbidden_origin", "Preflight requests require the server's exact origin.");
                return;
            }

            AddCorsHeaders(request, response);
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type, MCP-Protocol-Version, MCP-Session-Id";
            response.StatusCode = (int)HttpStatusCode.NoContent;
        }

        private async Task HandleWebIndexAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            var bootstrapToken = request.QueryString["token"];
            if (!string.IsNullOrEmpty(bootstrapToken))
            {
                if (!TryBootstrapBrowserSession(bootstrapToken, out var browserSessionToken))
                {
                    await WriteHttpErrorAsync(response, HttpStatusCode.Unauthorized, "invalid_bootstrap_token", "The browser bootstrap token is invalid or has already been used.");
                    return;
                }

                response.Headers.Add(HttpResponseHeader.SetCookie, $"{SESSION_COOKIE_NAME}={browserSessionToken}; Path=/; HttpOnly; SameSite=Strict");
                response.StatusCode = (int)HttpStatusCode.SeeOther;
                response.RedirectLocation = "/";
                return;
            }

            var html = _router.WebIndexHtml ?? string.Empty;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Content-Security-Policy"] =
                "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            await WriteTextAsync(response, HttpStatusCode.OK, "text/html; charset=utf-8", html);
        }

        private async Task HandleMcpAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
        {
            JObject rpcRequest;
            try
            {
                var body = await ReadBodyAsync(request);
                rpcRequest = JObject.Parse(body);
            }
            catch (JsonException exception)
            {
                await WriteJsonAsync(response, HttpStatusCode.OK, JsonRpcError(null, -32700, "Parse error", exception.Message));
                return;
            }
            catch (InvalidDataException exception)
            {
                await WriteJsonAsync(response, HttpStatusCode.RequestEntityTooLarge, JsonRpcError(null, -32600, "Invalid Request", exception.Message));
                return;
            }

            var id = rpcRequest["id"]?.DeepClone();
            var isNotification = rpcRequest["id"] == null;
            if (rpcRequest.Value<string>("jsonrpc") != "2.0" || rpcRequest["method"]?.Type != JTokenType.String)
            {
                if (isNotification)
                {
                    response.StatusCode = (int)HttpStatusCode.Accepted;
                    return;
                }

                await WriteJsonAsync(response, HttpStatusCode.OK, JsonRpcError(id, -32600, "Invalid Request", "Expected a JSON-RPC 2.0 request with a string method."));
                return;
            }

            var method = rpcRequest.Value<string>("method");
            var protocolVersion = request.Headers["MCP-Protocol-Version"];
            if (!string.Equals(method, "initialize", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(protocolVersion)
                && !string.Equals(
                    protocolVersion,
                    PROTOCOL_VERSION,
                    StringComparison.Ordinal))
            {
                await WriteHttpErrorAsync(
                    response,
                    HttpStatusCode.BadRequest,
                    "unsupported_protocol_version",
                    $"MCP-Protocol-Version '{protocolVersion}' is unsupported; expected '{PROTOCOL_VERSION}'.");
                return;
            }
            if (isNotification)
            {
                response.StatusCode = (int)HttpStatusCode.Accepted;
                return;
            }

            JObject rpcResponse;
            switch (method)
            {
                case "initialize":
                    rpcResponse = JsonRpcResult(id, CreateInitializeResult());
                    break;
                case "ping":
                    rpcResponse = JsonRpcResult(id, new JObject());
                    break;
                case "tools/list":
                    rpcResponse = await CreateToolsListResponseAsync(id, cancellationToken);
                    break;
                case "tools/call":
                    rpcResponse = await CreateToolsCallResponseAsync(id, rpcRequest["params"] as JObject, cancellationToken);
                    break;
                default:
                    rpcResponse = JsonRpcError(id, -32601, "Method not found", $"MCP method '{method}' is not supported.");
                    break;
            }

            await WriteJsonAsync(response, HttpStatusCode.OK, rpcResponse);
        }

        private async Task HandleApiAsync(HttpListenerRequest request, HttpListenerResponse response, string operation, CancellationToken cancellationToken)
        {
            JObject arguments;
            try
            {
                var body = await ReadBodyAsync(request);
                arguments = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            }
            catch (JsonException exception)
            {
                await WriteHttpErrorAsync(response, HttpStatusCode.BadRequest, "invalid_json", exception.Message);
                return;
            }
            catch (InvalidDataException exception)
            {
                await WriteHttpErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, "request_too_large", exception.Message);
                return;
            }

            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                var result = _router.Execute(operation, arguments) ?? new JObject();
                await WriteJsonAsync(response, HttpStatusCode.OK, new JObject
                {
                    ["ok"] = true,
                    ["result"] = result,
                });
            }
            catch (Exception exception)
            {
                if (exception is DingoCmsEditorRequestException requestException)
                {
                    await WriteHttpErrorAsync(
                        response,
                        MapOperationStatus(requestException.Code),
                        requestException.Code,
                        requestException.Message,
                        requestException.Details);
                }
                else
                {
                    await WriteHttpErrorAsync(
                        response,
                        HttpStatusCode.InternalServerError,
                        "operation_failed",
                        exception.Message);
                }
            }
            finally
            {
                _requestGate.Release();
            }
        }

        private JObject CreateInitializeResult()
        {
            return new JObject
            {
                ["protocolVersion"] = PROTOCOL_VERSION,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject
                    {
                        ["listChanged"] = false,
                    },
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = SERVER_NAME,
                    ["version"] = SERVER_VERSION,
                },
                ["instructions"] = "Use schema and read operations before editing. Save a single document with asset_save and upload a single file with resource_put; apply wider writes through a changeset, validate it, then commit it explicitly.",
            };
        }

        private async Task<JObject> CreateToolsListResponseAsync(JToken id, CancellationToken cancellationToken)
        {
            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                var tools = _router.DescribeTools() ?? new JArray();
                return JsonRpcResult(id, new JObject
                {
                    ["tools"] = tools.DeepClone(),
                });
            }
            catch (Exception exception)
            {
                return JsonRpcError(id, -32603, "Internal error", exception.Message);
            }
            finally
            {
                _requestGate.Release();
            }
        }

        private async Task<JObject> CreateToolsCallResponseAsync(JToken id, JObject parameters, CancellationToken cancellationToken)
        {
            var operation = parameters?.Value<string>("name");
            if (string.IsNullOrWhiteSpace(operation))
            {
                return JsonRpcError(id, -32602, "Invalid params", "tools/call requires a non-empty params.name.");
            }

            if (parameters?["arguments"] != null && parameters["arguments"] is not JObject)
            {
                return JsonRpcError(id, -32602, "Invalid params", "tools/call params.arguments must be a JSON object.");
            }

            var arguments = parameters?["arguments"] as JObject ?? new JObject();
            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                var structuredContent = _router.Execute(operation, arguments) ?? new JObject();
                return JsonRpcResult(id, ToolResult(structuredContent, false));
            }
            catch (Exception exception)
            {
                var requestException = exception as DingoCmsEditorRequestException;
                var structuredContent = new JObject
                {
                    ["error"] = new JObject
                    {
                        ["code"] = requestException?.Code
                                   ?? "operation_failed",
                        ["message"] = exception.Message,
                    },
                };
                if (requestException?.Details != null)
                {
                    structuredContent["error"]["details"] =
                        requestException.Details;
                }
                return JsonRpcResult(id, ToolResult(structuredContent, true));
            }
            finally
            {
                _requestGate.Release();
            }
        }

        private static JObject ToolResult(JObject structuredContent, bool isError)
        {
            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = structuredContent.ToString(Formatting.None),
                    },
                },
                ["structuredContent"] = structuredContent,
                ["isError"] = isError,
            };
        }

        private bool IsAllowedOrigin(HttpListenerRequest request)
        {
            var origin = request.Headers["Origin"];
            return string.IsNullOrEmpty(origin) || string.Equals(origin, Origin, StringComparison.Ordinal);
        }

        private bool HasExactOrigin(HttpListenerRequest request)
        {
            return string.Equals(request.Headers["Origin"], Origin, StringComparison.Ordinal);
        }

        private void AddCorsHeaders(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!HasExactOrigin(request))
            {
                return;
            }

            response.Headers["Access-Control-Allow-Origin"] = Origin;
            response.Headers["Access-Control-Allow-Credentials"] = "true";
            response.Headers["Vary"] = "Origin";
        }

        private bool IsAuthenticated(HttpListenerRequest request)
        {
            var authorization = request.Headers["Authorization"];
            const string bearerPrefix = "Bearer ";
            if (!string.IsNullOrEmpty(authorization) && authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase) && TokensMatch(authorization.Substring(bearerPrefix.Length), _bearerToken))
            {
                return true;
            }

            var cookieToken = request.Cookies[SESSION_COOKIE_NAME]?.Value;
            lock (_authenticationGate)
            {
                return !string.IsNullOrEmpty(_browserSessionToken) && TokensMatch(cookieToken, _browserSessionToken);
            }
        }

        private bool TryBootstrapBrowserSession(string token, out string browserSessionToken)
        {
            lock (_authenticationGate)
            {
                if (!_browserBootstrapAvailable
                    || !TokensMatch(token, _browserBootstrapToken))
                {
                    browserSessionToken = null;
                    return false;
                }

                _browserBootstrapAvailable = false;
                _browserBootstrapToken = null;
                _browserSessionToken ??= Guid.NewGuid().ToString("N")
                                         + Guid.NewGuid().ToString("N");
                browserSessionToken = _browserSessionToken;
                return true;
            }
        }

        private string BuildBrowserBootstrapUrl()
        {
            return Origin + "/?token=" + Uri.EscapeDataString(
                _browserBootstrapToken ?? string.Empty);
        }

        private static bool TokensMatch(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            var difference = 0;
            for (var index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
        {
            if (request.ContentLength64 > MAX_REQUEST_BODY_CHARACTERS)
            {
                throw new InvalidDataException($"Request body exceeds the {MAX_REQUEST_BODY_CHARACTERS}-character limit.");
            }

            var encoding = request.ContentEncoding ?? Encoding.UTF8;
            using var reader = new StreamReader(request.InputStream, encoding, true, 4096, true);
            var body = new StringBuilder();
            var buffer = new char[4096];
            while (true)
            {
                var count = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    break;
                }

                body.Append(buffer, 0, count);
                if (body.Length > MAX_REQUEST_BODY_CHARACTERS)
                {
                    throw new InvalidDataException($"Request body exceeds the {MAX_REQUEST_BODY_CHARACTERS}-character limit.");
                }
            }

            return body.ToString();
        }

        private static JObject JsonRpcResult(JToken id, JObject result)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["result"] = result,
            };
        }

        private static JObject JsonRpcError(JToken id, int code, string message, string details)
        {
            var error = new JObject
            {
                ["code"] = code,
                ["message"] = message,
            };
            if (!string.IsNullOrWhiteSpace(details))
            {
                error["data"] = new JObject
                {
                    ["details"] = details,
                };
            }

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = error,
            };
        }

        private static async Task WriteUnauthorizedAsync(HttpListenerResponse response)
        {
            response.Headers["WWW-Authenticate"] = $"Bearer realm=\"{SERVER_NAME}\"";
            await WriteHttpErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized", "A valid bearer token or browser session is required.");
        }

        private static Task WriteHttpErrorAsync(
            HttpListenerResponse response,
            HttpStatusCode statusCode,
            string code,
            string message,
            JObject details = null)
        {
            var error = new JObject
            {
                ["code"] = code,
                ["message"] = message,
            };
            if (details != null)
            {
                error["details"] = details;
            }
            return WriteJsonAsync(response, statusCode, new JObject
            {
                ["ok"] = false,
                ["error"] = error,
            });
        }

        private static HttpStatusCode MapOperationStatus(string code)
        {
            return code switch
            {
                "not_found" => HttpStatusCode.NotFound,
                "content_conflict" or "asset_conflict" or
                    "path_conflict" or "module_casing_conflict" or
                    "changeset_not_active" or "recovery_pending" or
                    "authoring_locked" => HttpStatusCode.Conflict,
                "invalid_request" or "invalid_data" or
                    "protected_path" or "unknown_operation" =>
                    HttpStatusCode.BadRequest,
                _ => HttpStatusCode.InternalServerError,
            };
        }

        private static Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode statusCode, JObject body)
        {
            return WriteTextAsync(response, statusCode, "application/json; charset=utf-8", body.ToString(Formatting.None));
        }

        private static async Task WriteTextAsync(HttpListenerResponse response, HttpStatusCode statusCode, string contentType, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
            response.StatusCode = (int)statusCode;
            response.ContentType = contentType;
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = bytes.LongLength;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private static async Task TryWriteUnexpectedErrorAsync(HttpListenerResponse response, Exception exception)
        {
            if (response.OutputStream == null || !response.OutputStream.CanWrite)
            {
                return;
            }

            try
            {
                await WriteHttpErrorAsync(response, HttpStatusCode.InternalServerError, "internal_error", exception.Message);
            }
            catch
            {
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DingoCmsEditorHttpServer));
            }
        }
    }
}
#endif
