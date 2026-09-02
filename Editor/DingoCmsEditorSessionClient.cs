#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DingoGameObjectsCMSEditorServer.Application;
using DingoGameObjectsCMSEditorServer.Authoring;
using DingoGameObjectsCMSEditorServer.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace DingoGameObjectsCMSEditorServer.Editor
{
    public enum DingoCmsEditorSessionState
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
    }

    public enum DingoCmsSharedServerProbeState
    {
        Unreachable = 0,
        Compatible = 1,
        Incompatible = 2,
    }

    [InitializeOnLoad]
    public static class DingoCmsEditorSessionClient
    {
        private const int BRIDGE_PROTOCOL_VERSION = 1;
        private const string INSTANCE_ARGUMENT = "dingo_cms_instance";
        private const string SESSION_DESIRED_KEY =
            "DingoCMS.EditorServer.SessionDesired";
        private static readonly TimeSpan REQUEST_TIMEOUT =
            TimeSpan.FromSeconds(5);
        private static readonly TimeSpan REGISTRATION_TIMEOUT =
            TimeSpan.FromSeconds(10);
        private static readonly TimeSpan KEEP_ALIVE_INTERVAL =
            TimeSpan.FromSeconds(15);
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly object Sync = new();
        private static readonly SemaphoreSlim SendLock = new(1, 1);
        private static readonly ConcurrentQueue<PendingEditorCommand>
            EditorCommands = new();

        private static CancellationTokenSource _lifecycleCancellation;
        private static ClientWebSocket _socket;
        private static Task _connectTask;
        private static Task _receiveTask;
        private static Task _keepAliveTask;
        private static TaskCompletionSource<bool> _registrationSignal;
        private static DingoCmsAuthoringApplication _authoring;
        private static DingoCmsEditorRequestRouter _localRouter;
        private static DingoCmsEditorSessionState _state;
        private static string _sessionId;
        private static string _instanceId;
        private static string _lastError;
        private static int _generation;
        private static int _stateChangedPending;
        private static double _nextReconnectTime;

        public static event Action StateChanged;

        public static DingoCmsEditorSessionState State
        {
            get
            {
                lock (Sync)
                    return _state;
            }
        }

        public static bool IsConnected =>
            State == DingoCmsEditorSessionState.Connected;
        public static bool IsConnecting =>
            State == DingoCmsEditorSessionState.Connecting;

        public static string SessionId
        {
            get
            {
                lock (Sync)
                    return _sessionId;
            }
        }

        public static string InstanceId
        {
            get
            {
                lock (Sync)
                    return _instanceId;
            }
        }

        public static string LastError
        {
            get
            {
                lock (Sync)
                    return _lastError;
            }
        }

        public static string ProjectName =>
            new DirectoryInfo(
                DingoCmsEditorServerSettings.ProjectRoot).Name;

        static DingoCmsEditorSessionClient()
        {
            AssemblyReloadEvents.beforeAssemblyReload +=
                () => ForceStop(preserveIntent: true);
            EditorApplication.quitting +=
                () => ForceStop(preserveIntent: false);
            EditorApplication.update += Pump;
            _nextReconnectTime = EditorApplication.timeSinceStartup;
        }

        public static Task ConnectAsync()
        {
            SessionState.SetBool(SESSION_DESIRED_KEY, true);
            return StartConnectionAsync();
        }

        public static Task DisconnectAsync()
        {
            SessionState.SetBool(SESSION_DESIRED_KEY, false);
            return StopConnectionAsync();
        }

        public static async Task OpenWebEditorAsync()
        {
            string instance;
            lock (Sync)
            {
                if (_state != DingoCmsEditorSessionState.Connected
                    || string.IsNullOrWhiteSpace(_instanceId))
                {
                    throw new InvalidOperationException(
                        "Connect this Unity project before opening the DingoCMS Web editor.");
                }
                instance = _instanceId;
            }

            var response = await PostWebBootstrapAsync(
                new JObject
                {
                    [INSTANCE_ARGUMENT] = instance,
                },
                CancellationToken.None);
            var url = response.Value<string>("url");
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new InvalidOperationException(
                    "The shared DingoCMS server did not return a Web editor URL.");
            }
            UnityEngine.Application.OpenURL(url);
        }

        internal static JObject BuildRegistrationPayload()
        {
            return new JObject
            {
                ["type"] = "register",
                ["project_name"] = ProjectName,
                ["project_hash"] = DingoCmsDetachedEditorHost.ProjectId,
                ["project_id"] = DingoCmsDetachedEditorHost.ProjectId,
                ["project_path"] =
                    DingoCmsEditorServerSettings.ProjectRoot,
                ["assets_root"] =
                    DingoCmsEditorServerSettings.AssetsRoot,
                ["unity_version"] = UnityEngine.Application.unityVersion,
            };
        }

        private static Task StartConnectionAsync()
        {
            lock (Sync)
            {
                if (_state == DingoCmsEditorSessionState.Connected)
                    return Task.CompletedTask;
                if (_connectTask != null && !_connectTask.IsCompleted)
                    return _connectTask;

                var generation = ++_generation;
                var cancellation = new CancellationTokenSource();
                _lifecycleCancellation = cancellation;
                _state = DingoCmsEditorSessionState.Connecting;
                _lastError = null;
                _nextReconnectTime = double.PositiveInfinity;
                _connectTask = ConnectCoreAsync(generation, cancellation);
                RequestStateChanged();
                return _connectTask;
            }
        }

        private static async Task ConnectCoreAsync(
            int generation,
            CancellationTokenSource cancellation)
        {
            DingoCmsAuthoringApplication authoring = null;
            ClientWebSocket socket = null;
            var adopted = false;
            try
            {
                var serverState = await ProbeServerAsync(cancellation.Token);
                if (serverState == DingoCmsSharedServerProbeState.Unreachable)
                {
                    throw new InvalidOperationException(
                        "No DingoCMS server is running at "
                        + DingoCmsEditorServerSettings.BaseUrl
                        + ". Press Start Server here, or start it from "
                        + "another Unity project.");
                }
                if (serverState == DingoCmsSharedServerProbeState.Incompatible)
                {
                    throw new InvalidOperationException(
                        "A process is listening at "
                        + DingoCmsEditorServerSettings.BaseUrl
                        + ", but it is not a compatible DingoCMS shared server.");
                }

                authoring = new DingoCmsAuthoringApplication(
                    DingoCmsEditorServerSettings.AssetsRoot);
                var router = new DingoCmsEditorRequestRouter(
                    authoring.Execute,
                    string.Empty);
                socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = KEEP_ALIVE_INTERVAL;
                socket.Options.SetRequestHeader(
                    "Authorization",
                    "Bearer " + DingoCmsEditorServerSettings.EnsureToken());
                await socket.ConnectAsync(
                    BuildWebSocketUri(),
                    cancellation.Token);

                var registrationSignal = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (Sync)
                {
                    if (generation != _generation)
                        throw new OperationCanceledException();
                    _authoring = authoring;
                    authoring = null;
                    _localRouter = router;
                    _socket = socket;
                    socket = null;
                    _registrationSignal = registrationSignal;
                    _receiveTask = Task.Run(() => ReceiveLoopAsync(
                        generation,
                        _socket,
                        cancellation.Token));
                    _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(
                        generation,
                        _socket,
                        cancellation.Token));
                    adopted = true;
                }

                await SendJsonAsync(
                    _socket,
                    BuildRegistrationPayload(),
                    cancellation.Token);
                var completed = await Task.WhenAny(
                    registrationSignal.Task,
                    Task.Delay(REGISTRATION_TIMEOUT, cancellation.Token));
                if (completed != registrationSignal.Task
                    || !await registrationSignal.Task)
                {
                    throw new TimeoutException(
                        "The DingoCMS server did not register this Unity project within 10 seconds.");
                }
            }
            catch (OperationCanceledException)
                when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                MarkSessionLost(generation, exception.Message);
            }
            finally
            {
                if (!adopted)
                {
                    socket?.Abort();
                    socket?.Dispose();
                    Shutdown(authoring);
                }
                lock (Sync)
                {
                    if (generation == _generation)
                        _connectTask = null;
                }
            }
        }

        private static async Task ReceiveLoopAsync(
            int generation,
            ClientWebSocket socket,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var message = await ReceiveMessageAsync(
                        socket,
                        cancellationToken);
                    if (message == null)
                    {
                        throw new WebSocketException(
                            "The DingoCMS server closed the project session.");
                    }
                    await HandleServerMessageAsync(
                        generation,
                        socket,
                        message,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                MarkSessionLost(generation, exception.Message);
            }
        }

        private static async Task HandleServerMessageAsync(
            int generation,
            ClientWebSocket socket,
            string message,
            CancellationToken cancellationToken)
        {
            JObject payload;
            try
            {
                payload = JObject.Parse(message);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException(
                    "The DingoCMS server sent invalid WebSocket JSON.");
            }

            switch (payload.Value<string>("type"))
            {
                case "welcome":
                    return;
                case "registered":
                {
                    var sessionId = payload.Value<string>("session_id");
                    var instanceId = payload.Value<string>("instance_id");
                    if (string.IsNullOrWhiteSpace(sessionId)
                        || string.IsNullOrWhiteSpace(instanceId))
                    {
                        throw new InvalidOperationException(
                            "The DingoCMS server returned an invalid project registration.");
                    }
                    DingoCmsEditorRequestRouter router;
                    lock (Sync)
                    {
                        if (generation != _generation)
                            return;
                        _sessionId = sessionId;
                        _instanceId = instanceId;
                        router = _localRouter;
                    }
                    await SendJsonAsync(
                        socket,
                        new JObject
                        {
                            ["type"] = "register_tools",
                            ["tools"] = router?.DescribeTools()
                                        ?? new JArray(),
                        },
                        cancellationToken);
                    lock (Sync)
                    {
                        if (generation != _generation)
                            return;
                        _state = DingoCmsEditorSessionState.Connected;
                        _lastError = null;
                        _registrationSignal?.TrySetResult(true);
                    }
                    RequestStateChanged();
                    return;
                }
                case "execute":
                    await HandleExecuteAsync(
                        generation,
                        socket,
                        payload,
                        cancellationToken);
                    return;
                case "ping":
                    await SendPongAsync(socket, cancellationToken);
                    return;
            }
        }

        private static async Task HandleExecuteAsync(
            int generation,
            ClientWebSocket socket,
            JObject payload,
            CancellationToken cancellationToken)
        {
            var commandId = payload.Value<string>("id");
            var operation = payload.Value<string>("name");
            if (string.IsNullOrWhiteSpace(commandId)
                || string.IsNullOrWhiteSpace(operation))
            {
                return;
            }

            var command = new PendingEditorCommand(
                generation,
                commandId,
                operation,
                payload["params"] as JObject ?? new JObject());
            EditorCommands.Enqueue(command);
            var timeoutSeconds = Math.Max(
                1,
                payload.Value<int?>("timeout") ?? 60);
            var completed = await Task.WhenAny(
                command.Completion.Task,
                Task.Delay(
                    TimeSpan.FromSeconds(timeoutSeconds),
                    cancellationToken));
            PendingEditorCommandResult result;
            if (completed == command.Completion.Task)
            {
                result = await command.Completion.Task;
            }
            else
            {
                result = PendingEditorCommandResult.Failure(
                    "instance_timeout",
                    $"Unity did not execute '{operation}' within {timeoutSeconds} seconds.");
            }

            var response = new JObject
            {
                ["type"] = "command_result",
                ["id"] = commandId,
            };
            if (result.Error == null)
                response["result"] = result.Result ?? new JObject();
            else
                response["error"] = result.Error;
            await SendJsonAsync(socket, response, cancellationToken);
        }

        private static async Task KeepAliveLoopAsync(
            int generation,
            ClientWebSocket socket,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(
                        KEEP_ALIVE_INTERVAL,
                        cancellationToken);
                    if (socket.State != WebSocketState.Open)
                        return;
                    await SendPongAsync(socket, cancellationToken);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                MarkSessionLost(generation, exception.Message);
            }
        }

        private static Task SendPongAsync(
            ClientWebSocket socket,
            CancellationToken cancellationToken)
        {
            string sessionId;
            lock (Sync)
                sessionId = _sessionId;
            return SendJsonAsync(
                socket,
                new JObject
                {
                    ["type"] = "pong",
                    ["session_id"] = sessionId,
                },
                cancellationToken);
        }

        private static async Task<string> ReceiveMessageAsync(
            ClientWebSocket socket,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            using var stream = new MemoryStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                if (result.Count > 0)
                    stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(stream.ToArray());
            }
            return null;
        }

        private static async Task SendJsonAsync(
            ClientWebSocket socket,
            JObject payload,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(
                payload.ToString(Formatting.None));
            await SendLock.WaitAsync(cancellationToken);
            try
            {
                if (socket == null || socket.State != WebSocketState.Open)
                {
                    throw new WebSocketException(
                        "The DingoCMS project WebSocket is not open.");
                }
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
            }
            finally
            {
                SendLock.Release();
            }
        }

        private static Uri BuildWebSocketUri()
        {
            var builder = new UriBuilder(
                DingoCmsEditorServerSettings.BaseUrl)
            {
                Scheme = "ws",
                Path = "/hub/plugin",
                Query = string.Empty,
            };
            return builder.Uri;
        }

        private static async Task StopConnectionAsync()
        {
            ClientWebSocket socket;
            CancellationTokenSource cancellation;
            DingoCmsAuthoringApplication authoring;
            lock (Sync)
            {
                _generation++;
                socket = _socket;
                cancellation = _lifecycleCancellation;
                authoring = _authoring;
                _socket = null;
                _lifecycleCancellation = null;
                _authoring = null;
                _localRouter = null;
                _registrationSignal?.TrySetCanceled();
                _registrationSignal = null;
                _sessionId = null;
                _instanceId = null;
                _lastError = null;
                _state = DingoCmsEditorSessionState.Disconnected;
                _nextReconnectTime = double.PositiveInfinity;
                _connectTask = null;
                _receiveTask = null;
                _keepAliveTask = null;
            }
            Cancel(cancellation);
            if (socket != null)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Disconnect",
                            CancellationToken.None);
                    }
                }
                catch
                {
                    socket.Abort();
                }
                socket.Dispose();
            }
            cancellation?.Dispose();
            Shutdown(authoring);
            FailPendingCommands(
                "session_disconnected",
                "The DingoCMS project session disconnected.");
            RequestStateChanged();
        }

        private static void MarkSessionLost(int generation, string message)
        {
            ClientWebSocket socket;
            CancellationTokenSource cancellation;
            DingoCmsAuthoringApplication authoring;
            lock (Sync)
            {
                if (generation != _generation)
                    return;
                _generation++;
                socket = _socket;
                cancellation = _lifecycleCancellation;
                authoring = _authoring;
                _socket = null;
                _lifecycleCancellation = null;
                _authoring = null;
                _localRouter = null;
                _registrationSignal?.TrySetResult(false);
                _registrationSignal = null;
                _sessionId = null;
                _instanceId = null;
                _state = DingoCmsEditorSessionState.Disconnected;
                _lastError = message;
                _nextReconnectTime = 0d;
                _connectTask = null;
                _receiveTask = null;
                _keepAliveTask = null;
            }
            Cancel(cancellation);
            try
            {
                socket?.Abort();
                socket?.Dispose();
            }
            catch
            {
            }
            cancellation?.Dispose();
            Shutdown(authoring);
            FailPendingCommands(
                "session_disconnected",
                message ?? "The DingoCMS project session disconnected.");
            RequestStateChanged();
        }

        public static async Task<DingoCmsSharedServerProbeState>
            ProbeServerAsync(CancellationToken cancellationToken)
        {
            try
            {
                var token = DingoCmsEditorServerSettings.EnsureToken();
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    DingoCmsEditorServerSettings.BaseUrl + "/server");
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
                using var response = await Http.SendAsync(
                    request,
                    cancellationToken);
                var responseText = await response.Content.ReadAsStringAsync();
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return DingoCmsSharedServerProbeState.Incompatible;
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        ReadServerError(responseText, response.StatusCode));
                }
                try
                {
                    var payload = string.IsNullOrWhiteSpace(responseText)
                        ? new JObject()
                        : JObject.Parse(responseText);
                    return payload.Value<int?>("bridgeProtocolVersion")
                           == BRIDGE_PROTOCOL_VERSION
                        ? DingoCmsSharedServerProbeState.Compatible
                        : DingoCmsSharedServerProbeState.Incompatible;
                }
                catch (JsonException)
                {
                    return DingoCmsSharedServerProbeState.Incompatible;
                }
            }
            catch (HttpRequestException)
            {
                return DingoCmsSharedServerProbeState.Unreachable;
            }
            catch (TaskCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                return DingoCmsSharedServerProbeState.Unreachable;
            }
        }

        public static async Task<DingoCmsSharedServerProbeState>
            WaitForServerAsync(
                TimeSpan timeout,
                CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + timeout;
            do
            {
                var state = await ProbeServerAsync(cancellationToken);
                if (state != DingoCmsSharedServerProbeState.Unreachable)
                    return state;
                await Task.Delay(250, cancellationToken);
            }
            while (DateTime.UtcNow < deadline);
            return DingoCmsSharedServerProbeState.Unreachable;
        }

        private static async Task<JObject> PostWebBootstrapAsync(
            JObject body,
            CancellationToken cancellationToken)
        {
            var token = DingoCmsEditorServerSettings.EnsureToken();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                DingoCmsEditorServerSettings.BaseUrl
                + "/web/bootstrap");
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(
                (body ?? new JObject()).ToString(Formatting.None),
                Encoding.UTF8,
                "application/json");
            using var response = await Http.SendAsync(
                request,
                cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    ReadServerError(responseText, response.StatusCode));
            }
            var payload = string.IsNullOrWhiteSpace(responseText)
                ? new JObject()
                : JObject.Parse(responseText);
            return payload["result"] as JObject ?? payload;
        }

        private static HttpClient CreateHttpClient()
        {
            return new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
            })
            {
                Timeout = REQUEST_TIMEOUT,
            };
        }

        private static void Pump()
        {
            ExecutePendingEditorCommands();
            if (Interlocked.Exchange(ref _stateChangedPending, 0) != 0)
                StateChanged?.Invoke();
            if (EditorApplication.isCompiling)
                return;

            var shouldReconnect = false;
            lock (Sync)
            {
                if (_state == DingoCmsEditorSessionState.Disconnected
                    && SessionState.GetBool(
                        SESSION_DESIRED_KEY,
                        defaultValue: false)
                    && EditorApplication.timeSinceStartup
                    >= _nextReconnectTime
                    && (_connectTask == null || _connectTask.IsCompleted))
                {
                    shouldReconnect = true;
                    _nextReconnectTime = double.PositiveInfinity;
                }
            }
            if (shouldReconnect)
                _ = StartConnectionAsync();
        }

        private static void ExecutePendingEditorCommands()
        {
            var processed = 0;
            while (processed++ < 8
                   && EditorCommands.TryDequeue(out var command))
            {
                DingoCmsEditorRequestRouter router;
                lock (Sync)
                {
                    router = command.Generation == _generation
                        ? _localRouter
                        : null;
                }
                if (router == null)
                {
                    command.Completion.TrySetResult(
                        PendingEditorCommandResult.Failure(
                            "session_disconnected",
                            "The DingoCMS project session is no longer active."));
                    continue;
                }
                try
                {
                    command.Completion.TrySetResult(
                        PendingEditorCommandResult.Success(
                            router.Execute(
                                command.Operation,
                                command.Arguments)));
                }
                catch (Exception exception)
                {
                    var requestException = exception
                        as DingoCmsEditorRequestException;
                    command.Completion.TrySetResult(
                        PendingEditorCommandResult.Failure(
                            requestException?.Code ?? "operation_failed",
                            exception.Message,
                            requestException?.Details));
                }
            }
        }

        private static void ForceStop(bool preserveIntent)
        {
            if (!preserveIntent)
                SessionState.SetBool(SESSION_DESIRED_KEY, false);
            ClientWebSocket socket;
            CancellationTokenSource cancellation;
            DingoCmsAuthoringApplication authoring;
            lock (Sync)
            {
                _generation++;
                socket = _socket;
                cancellation = _lifecycleCancellation;
                authoring = _authoring;
                _socket = null;
                _lifecycleCancellation = null;
                _authoring = null;
                _localRouter = null;
                _registrationSignal?.TrySetCanceled();
                _registrationSignal = null;
                _sessionId = null;
                _instanceId = null;
                _state = DingoCmsEditorSessionState.Disconnected;
                _lastError = null;
                _nextReconnectTime = preserveIntent
                    ? EditorApplication.timeSinceStartup
                    : double.PositiveInfinity;
                _connectTask = null;
                _receiveTask = null;
                _keepAliveTask = null;
            }
            Cancel(cancellation);
            try
            {
                socket?.Abort();
                socket?.Dispose();
            }
            catch
            {
            }
            cancellation?.Dispose();
            Shutdown(authoring);
            FailPendingCommands(
                "session_reloading",
                "The Unity domain is reloading.");
        }

        private static void FailPendingCommands(
            string code,
            string message)
        {
            while (EditorCommands.TryDequeue(out var command))
            {
                command.Completion.TrySetResult(
                    PendingEditorCommandResult.Failure(code, message));
            }
        }

        private static void Cancel(
            CancellationTokenSource cancellation)
        {
            if (cancellation == null)
                return;
            try
            {
                cancellation.Cancel();
            }
            catch
            {
            }
        }

        private static void Shutdown(
            DingoCmsAuthoringApplication authoring)
        {
            try
            {
                authoring?.Shutdown();
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogException(exception);
            }
        }

        private static string ReadServerError(
            string body,
            HttpStatusCode statusCode)
        {
            try
            {
                return JObject.Parse(body)["error"]?.Value<string>("message")
                       ?? $"DingoCMS server returned HTTP {(int)statusCode}.";
            }
            catch
            {
                return $"DingoCMS server returned HTTP {(int)statusCode}.";
            }
        }

        private static void RequestStateChanged()
        {
            Interlocked.Exchange(ref _stateChangedPending, 1);
        }

        private sealed class PendingEditorCommand
        {
            public readonly int Generation;
            public readonly string Operation;
            public readonly JObject Arguments;
            public readonly TaskCompletionSource<PendingEditorCommandResult>
                Completion = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            public PendingEditorCommand(
                int generation,
                string commandId,
                string operation,
                JObject arguments)
            {
                Generation = generation;
                Operation = operation;
                Arguments = arguments ?? new JObject();
            }
        }

        private sealed class PendingEditorCommandResult
        {
            public readonly JObject Result;
            public readonly JObject Error;

            private PendingEditorCommandResult(
                JObject result,
                JObject error)
            {
                Result = result;
                Error = error;
            }

            public static PendingEditorCommandResult Success(JObject result)
            {
                return new PendingEditorCommandResult(
                    result ?? new JObject(),
                    null);
            }

            public static PendingEditorCommandResult Failure(
                string code,
                string message,
                JObject details = null)
            {
                return new PendingEditorCommandResult(
                    null,
                    new JObject
                    {
                        ["code"] = string.IsNullOrWhiteSpace(code)
                            ? "operation_failed"
                            : code,
                        ["message"] = message
                                      ?? "The Unity project could not execute the DingoCMS request.",
                        ["details"] = details == null
                            ? JValue.CreateNull()
                            : details.DeepClone(),
                    });
            }
        }
    }
}
#endif
