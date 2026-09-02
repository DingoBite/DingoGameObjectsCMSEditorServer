#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using DingoGameObjectsCMSEditorServer.Runtime;
using DingoGameObjectsCMSEditorServer.Transport;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DingoGameObjectsCMSEditorServer.Tests.Editor
{
    public class DingoCmsEditorHttpServerTests
    {
        private const string TOKEN = "test-token-long-enough-for-local-server";

        [Test]
        public async Task McpRequiresAuthenticationAndRejectsForeignOrigin()
        {
            var router = new DingoCmsEditorTestRouter();
            using var server = new DingoCmsEditorHttpServer(router, ReserveLoopbackPort(), TOKEN);
            server.Start();
            using var client = CreateClient();

            using var unauthenticated = await SendMcpAsync(client, server, InitializeRequest(), null, null);
            Assert.That(unauthenticated.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

            using var foreignOrigin = await SendMcpAsync(client, server, InitializeRequest(), TOKEN, "http://127.0.0.1:1");
            Assert.That(foreignOrigin.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        [Test]
        public async Task McpInitializesListsAndExecutesTools()
        {
            var router = new DingoCmsEditorTestRouter();
            using var server = new DingoCmsEditorHttpServer(router, ReserveLoopbackPort(), TOKEN);
            server.Start();
            using var client = CreateClient();

            using var initializeResponse = await SendMcpAsync(client, server, InitializeRequest(), TOKEN, null);
            Assert.That(initializeResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var initialize = JObject.Parse(await initializeResponse.Content.ReadAsStringAsync());
            Assert.That(initialize.SelectToken("result.protocolVersion")?.Value<string>(), Is.EqualTo(DingoCmsEditorHttpServer.PROTOCOL_VERSION));
            Assert.That(initialize.SelectToken("result.capabilities.tools"), Is.Not.Null);

            var listRequest = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/list",
                ["params"] = new JObject(),
            };
            using var listResponse = await SendMcpAsync(client, server, listRequest, TOKEN, null);
            var list = JObject.Parse(await listResponse.Content.ReadAsStringAsync());
            Assert.That(list.SelectToken("result.tools[0].name")?.Value<string>(), Is.EqualTo("echo"));

            var callRequest = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "call-1",
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "echo",
                    ["arguments"] = new JObject
                    {
                        ["value"] = 42,
                    },
                },
            };
            using var callResponse = await SendMcpAsync(client, server, callRequest, TOKEN, null);
            var call = JObject.Parse(await callResponse.Content.ReadAsStringAsync());
            Assert.That(call.SelectToken("result.structuredContent.operation")?.Value<string>(), Is.EqualTo("echo"));
            Assert.That(call.SelectToken("result.structuredContent.arguments.value")?.Value<int>(), Is.EqualTo(42));
            Assert.That(call.SelectToken("result.isError")?.Value<bool>(), Is.False);
            Assert.That(router.ExecutionCount, Is.EqualTo(1));
        }

        [Test]
        public async Task McpNotificationReturnsAcceptedWithoutJsonRpcBody()
        {
            var router = new DingoCmsEditorTestRouter();
            using var server = new DingoCmsEditorHttpServer(router, ReserveLoopbackPort(), TOKEN);
            server.Start();
            using var client = CreateClient();
            var notification = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
            };

            using var response = await SendMcpAsync(client, server, notification, TOKEN, null);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty);
        }

        [Test]
        public async Task McpGetReturnsMethodNotAllowedWhenSseIsUnavailable()
        {
            var router = new DingoCmsEditorTestRouter();
            using var server = new DingoCmsEditorHttpServer(router, ReserveLoopbackPort(), TOKEN);
            server.Start();
            using var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, server.Origin + "/mcp");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + TOKEN);

            using var response = await client.SendAsync(request);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
            Assert.That(response.Content.Headers.Allow, Does.Contain("POST"));
        }

        [Test]
        public async Task McpRejectsUnsupportedProtocolHeaderAfterInitialize()
        {
            var router = new DingoCmsEditorTestRouter();
            using var server = new DingoCmsEditorHttpServer(router, ReserveLoopbackPort(), TOKEN);
            server.Start();
            using var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Origin + "/mcp")
            {
                Content = JsonContent(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 8,
                    ["method"] = "ping",
                }),
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + TOKEN);
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "1900-01-01");

            using var response = await client.SendAsync(request);
            var body = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(body.SelectToken("error.code")?.Value<string>(), Is.EqualTo("unsupported_protocol_version"));
        }

        [Test]
        public async Task DisposeReleasesPortWithKeepAliveClient()
        {
            var port = ReserveLoopbackPort();
            using var first = new DingoCmsEditorHttpServer(
                new DingoCmsEditorTestRouter(),
                port,
                TOKEN);
            first.Start();
            using var client = CreateClient();
            using var initialize = await SendMcpAsync(
                client,
                first,
                InitializeRequest(),
                TOKEN,
                null);
            Assert.That(initialize.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            first.Dispose();

            using var restarted = new DingoCmsEditorHttpServer(
                new DingoCmsEditorTestRouter(),
                port,
                TOKEN);
            Assert.DoesNotThrow(restarted.Start);
            Assert.That(restarted.IsRunning, Is.True);
        }

        [Test]
        public async Task RepeatedStartDisposeRebindsSamePortAndAnswersPing()
        {
            var port = ReserveLoopbackPort();

            for (var cycle = 0; cycle < 8; cycle++)
            {
                var server = new DingoCmsEditorHttpServer(
                    new DingoCmsEditorTestRouter(),
                    port,
                    TOKEN);
                try
                {
                    server.Start();
                    server.Start();
                    Assert.That(server.IsRunning, Is.True, $"cycle {cycle}");

                    using var client = CreateClient();
                    using var response = await SendMcpAsync(
                        client,
                        server,
                        PingRequest(cycle),
                        TOKEN,
                        null);
                    Assert.That(
                        response.StatusCode,
                        Is.EqualTo(HttpStatusCode.OK),
                        $"cycle {cycle}");
                    var body = JObject.Parse(
                        await response.Content.ReadAsStringAsync());
                    Assert.That(
                        body["result"],
                        Is.TypeOf<JObject>(),
                        $"cycle {cycle}");
                }
                finally
                {
                    server.Dispose();
                    server.Dispose();
                }

                Assert.That(server.IsRunning, Is.False, $"cycle {cycle}");
            }
        }

        [Test]
        public async Task DisposeClosesPartialRequestAndImmediatelyRebindsPort()
        {
            var port = ReserveLoopbackPort();
            using var first = new DingoCmsEditorHttpServer(
                new DingoCmsEditorTestRouter(),
                port,
                TOKEN);
            first.Start();

            using var partialClient = new TcpClient();
            await partialClient.ConnectAsync(IPAddress.Loopback, port);
            using var partialStream = partialClient.GetStream();
            var partialRequest = Encoding.ASCII.GetBytes(
                "POST /mcp HTTP/1.1\r\n"
                + $"Host: 127.0.0.1:{port}\r\n"
                + $"Authorization: Bearer {TOKEN}\r\n"
                + "Content-Type: application/json\r\n"
                + "Content-Length: 1024\r\n"
                + "Connection: keep-alive\r\n\r\n"
                + "{");
            await partialStream.WriteAsync(
                partialRequest,
                0,
                partialRequest.Length);
            await partialStream.FlushAsync();

            // Give HttpListener time to publish the accepted context while
            // deliberately leaving the declared request body incomplete.
            await Task.Delay(50);
            first.Dispose();
            Assert.That(first.IsRunning, Is.False);

            using var restarted = new DingoCmsEditorHttpServer(
                new DingoCmsEditorTestRouter(),
                port,
                TOKEN);
            Assert.DoesNotThrow(restarted.Start);
            using var client = CreateClient();
            using var response = await SendMcpAsync(
                client,
                restarted,
                PingRequest(1),
                TOKEN,
                null);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public void StartAndDisposeAreIdempotentAndStartAfterDisposeThrows()
        {
            using var server = new DingoCmsEditorHttpServer(
                new DingoCmsEditorTestRouter(),
                ReserveLoopbackPort(),
                TOKEN);

            Assert.DoesNotThrow(server.Start);
            Assert.DoesNotThrow(server.Start);
            Assert.That(server.IsRunning, Is.True);

            Assert.DoesNotThrow(server.Dispose);
            Assert.DoesNotThrow(server.Dispose);
            Assert.That(server.IsRunning, Is.False);
            Assert.Throws<ObjectDisposedException>(server.Start);
        }

        [Test]
        public async Task HealthRequiresBearerAndExactInstanceToken()
        {
            const string instanceToken = "test-instance-token";
            const string buildFingerprint = "test-build-fingerprint";
            using var server = new DingoCmsEditorHttpServer(
                new DingoCmsEditorTestRouter(),
                ReserveLoopbackPort(),
                TOKEN,
                instanceToken,
                buildFingerprint);
            server.Start();
            using var client = CreateClient();

            using var unauthenticated = await client.GetAsync(
                server.Origin + "/health");
            Assert.That(
                unauthenticated.StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));

            using var bearerOnlyRequest = new HttpRequestMessage(
                HttpMethod.Get,
                server.Origin + "/health");
            bearerOnlyRequest.Headers.TryAddWithoutValidation(
                "Authorization",
                "Bearer " + TOKEN);
            using var bearerOnly = await client.SendAsync(bearerOnlyRequest);
            Assert.That(
                bearerOnly.StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));

            using var wrongInstanceRequest = new HttpRequestMessage(
                HttpMethod.Get,
                server.Origin + "/health");
            wrongInstanceRequest.Headers.TryAddWithoutValidation(
                "Authorization",
                "Bearer " + TOKEN);
            wrongInstanceRequest.Headers.TryAddWithoutValidation(
                "X-DingoCMS-Instance-Token",
                "wrong-instance");
            using var wrongInstance = await client.SendAsync(
                wrongInstanceRequest);
            Assert.That(
                wrongInstance.StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));

            using var healthRequest = new HttpRequestMessage(
                HttpMethod.Get,
                server.Origin + "/health");
            healthRequest.Headers.TryAddWithoutValidation(
                "Authorization",
                "Bearer " + TOKEN);
            healthRequest.Headers.TryAddWithoutValidation(
                "X-DingoCMS-Instance-Token",
                instanceToken);
            using var healthResponse = await client.SendAsync(healthRequest);
            var health = JObject.Parse(
                await healthResponse.Content.ReadAsStringAsync());

            Assert.That(healthResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(health.Value<bool>("ok"), Is.True);
            Assert.That(
                health.Value<int>("processId"),
                Is.EqualTo(System.Diagnostics.Process.GetCurrentProcess().Id));
            Assert.That(health.Value<int>("port"), Is.EqualTo(server.Port));
            Assert.That(
                health.Value<string>("instanceToken"),
                Is.EqualTo(instanceToken));
            Assert.That(health.Value<string>("serverVersion"), Is.Not.Empty);
            Assert.That(
                health.Value<string>("buildFingerprint"),
                Is.EqualTo(buildFingerprint));
        }

        [Test]
        public void FailedRuntimeStartReleasesAuthoringWorkspaceLease()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "DingoCmsRuntimeTests",
                Guid.NewGuid().ToString("N"));
            var assetsRoot = Path.Combine(root, "assets");
            Directory.CreateDirectory(assetsRoot);
            var blockedPort = ReserveLoopbackPort();
            var blocker = new DingoCmsEditorHttpServer(
                new DingoCmsEditorTestRouter(),
                blockedPort,
                TOKEN);
            DingoCmsEditorServerRuntime recovered = null;

            try
            {
                blocker.Start();
                Assert.Catch<Exception>(() =>
                    DingoCmsEditorServerRuntime.Start(
                        DingoCmsEditorServerOptions.CreateEnabled(
                            blockedPort,
                            TOKEN,
                            authoringOnly: true),
                        assetsRoot));
                blocker.Dispose();

                Assert.DoesNotThrow(() =>
                    recovered = DingoCmsEditorServerRuntime.Start(
                        DingoCmsEditorServerOptions.CreateEnabled(
                            ReserveLoopbackPort(),
                            TOKEN,
                            authoringOnly: true),
                        assetsRoot));
                Assert.That(recovered.IsRunning, Is.True);
            }
            finally
            {
                recovered?.Dispose();
                blocker.Dispose();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Test]
        public void FailedRuntimeStartPreservesDetachedOwnershipIdentity()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "DingoCmsRuntimeIdentityTests",
                Guid.NewGuid().ToString("N"));
            var assetsRoot = Path.Combine(root, "assets");
            var statePath = Path.Combine(root, "detached-host.json");
            Directory.CreateDirectory(assetsRoot);
            var blockedPort = ReserveLoopbackPort();
            var instanceToken = Guid.NewGuid().ToString("N");
            const string projectId = "runtime-start-failure-test";
            const string buildFingerprint = "runtime-build-fingerprint";
            using var currentProcess = System.Diagnostics.Process
                .GetCurrentProcess();
            var executablePath = currentProcess.MainModule?.FileName
                                 ?? Environment.GetCommandLineArgs()[0];
            var identity = new DingoCmsEditorHostIdentity(
                currentProcess.Id,
                blockedPort,
                projectId,
                instanceToken,
                executablePath,
                currentProcess.StartTime.ToUniversalTime().Ticks,
                buildFingerprint: buildFingerprint);
            DingoCmsEditorHostIdentity.WriteAtomic(statePath, identity);
            var environment = new System.Collections.Generic
                .Dictionary<string, string>
                {
                    [DingoCmsEditorServerOptions
                        .INSTANCE_TOKEN_ENVIRONMENT_VARIABLE] = instanceToken,
                    [DingoCmsEditorServerOptions
                        .HOST_STATE_FILE_ENVIRONMENT_VARIABLE] = statePath,
                    [DingoCmsEditorServerOptions
                        .PROJECT_ID_ENVIRONMENT_VARIABLE] = projectId,
                    [DingoCmsEditorServerOptions
                        .BUILD_FINGERPRINT_ENVIRONMENT_VARIABLE] =
                        buildFingerprint,
                };
            var options = DingoCmsEditorServerOptions.Parse(
                new[]
                {
                    DingoCmsEditorServerOptions.ENABLE_ARGUMENT,
                    DingoCmsEditorServerOptions.AUTHORING_ONLY_ARGUMENT,
                    DingoCmsEditorServerOptions.PORT_ARGUMENT
                    + "=" + blockedPort,
                    DingoCmsEditorServerOptions.TOKEN_ARGUMENT,
                    TOKEN,
                },
                name => environment.TryGetValue(name, out var value)
                    ? value
                    : null);
            using var blocker = new DingoCmsEditorHttpServer(
                new DingoCmsEditorTestRouter(),
                blockedPort,
                TOKEN);

            try
            {
                blocker.Start();
                Assert.Catch<Exception>(() =>
                    DingoCmsEditorServerRuntime.Start(
                        options,
                        assetsRoot));

                Assert.That(
                    DingoCmsEditorHostIdentity.TryRead(
                        statePath,
                        out var retained),
                    Is.True);
                Assert.That(retained.ProcessId, Is.EqualTo(identity.ProcessId));
                Assert.That(
                    retained.InstanceToken,
                    Is.EqualTo(identity.InstanceToken));
            }
            finally
            {
                blocker.Dispose();
                DingoCmsEditorHostIdentity.DeleteIfOwned(
                    statePath,
                    identity.ProcessId,
                    identity.InstanceToken);
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public async Task DomainErrorsPreserveCodesForMcpAndHttpClients()
        {
            var router = new DingoCmsEditorTestRouter();
            using var server = new DingoCmsEditorHttpServer(router, ReserveLoopbackPort(), TOKEN);
            server.Start();
            using var client = CreateClient();
            var callRequest = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 7,
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "fail",
                    ["arguments"] = new JObject(),
                },
            };

            using var mcpResponse = await SendMcpAsync(client, server, callRequest, TOKEN, null);
            var mcp = JObject.Parse(await mcpResponse.Content.ReadAsStringAsync());
            Assert.That(mcp.SelectToken("result.isError")?.Value<bool>(), Is.True);
            Assert.That(
                mcp.SelectToken("result.structuredContent.error.code")?.Value<string>(),
                Is.EqualTo("content_conflict"));

            using var apiRequest = new HttpRequestMessage(HttpMethod.Post, server.Origin + "/api/fail")
            {
                Content = JsonContent(new JObject()),
            };
            apiRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + TOKEN);
            using var apiResponse = await client.SendAsync(apiRequest);
            var api = JObject.Parse(await apiResponse.Content.ReadAsStringAsync());
            Assert.That(apiResponse.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(api.SelectToken("error.code")?.Value<string>(), Is.EqualTo("content_conflict"));
        }

        [Test]
        public async Task BrowserBootstrapCreatesOneTimeAuthenticatedSession()
        {
            var router = new DingoCmsEditorTestRouter();
            using var server = new DingoCmsEditorHttpServer(router, ReserveLoopbackPort(), TOKEN);
            server.Start();
            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CookieContainer = cookies,
                UseCookies = true,
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(5),
            };

            var bootstrapUrl = server.BrowserBootstrapUrl;
            using var bootstrap = await client.GetAsync(bootstrapUrl);
            Assert.That(bootstrap.StatusCode, Is.EqualTo(HttpStatusCode.SeeOther));
            Assert.That(cookies.GetCookies(new Uri(server.Origin))["DingoCmsEditorSession"]?.Value, Is.Not.Empty);

            using var apiRequest = new HttpRequestMessage(HttpMethod.Post, server.Origin + "/api/echo")
            {
                Content = JsonContent(new JObject
                {
                    ["value"] = "browser",
                }),
            };
            apiRequest.Headers.TryAddWithoutValidation("Origin", server.Origin);
            using var apiResponse = await client.SendAsync(apiRequest);
            Assert.That(apiResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var api = JObject.Parse(await apiResponse.Content.ReadAsStringAsync());
            Assert.That(api.SelectToken("result.arguments.value")?.Value<string>(), Is.EqualTo("browser"));

            using var repeatedBootstrap = await client.GetAsync(bootstrapUrl);
            Assert.That(repeatedBootstrap.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

            var refreshedBootstrapUrl = server.CreateBrowserBootstrapUrl();
            Assert.That(refreshedBootstrapUrl, Is.Not.EqualTo(bootstrapUrl));
            using var refreshedBootstrap = await client.GetAsync(refreshedBootstrapUrl);
            Assert.That(refreshedBootstrap.StatusCode, Is.EqualTo(HttpStatusCode.SeeOther));
            using var repeatedRefreshedBootstrap = await client.GetAsync(refreshedBootstrapUrl);
            Assert.That(repeatedRefreshedBootstrap.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }

        private static JObject InitializeRequest()
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JObject
                {
                    ["protocolVersion"] = DingoCmsEditorHttpServer.PROTOCOL_VERSION,
                    ["capabilities"] = new JObject(),
                    ["clientInfo"] = new JObject
                    {
                        ["name"] = "test-client",
                        ["version"] = "1.0.0",
                    },
                },
            };
        }

        private static JObject PingRequest(int id)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = "ping",
            };
        }

        private static async Task<HttpResponseMessage> SendMcpAsync(HttpClient client, DingoCmsEditorHttpServer server, JObject body, string token, string origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, server.Origin + "/mcp")
            {
                Content = JsonContent(body),
            };
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            }

            if (!string.IsNullOrEmpty(origin))
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }

            return await client.SendAsync(request);
        }

        private static StringContent JsonContent(JObject body)
        {
            return new StringContent(body.ToString(), Encoding.UTF8, "application/json");
        }

        private static HttpClient CreateClient()
        {
            return new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
        }

        private static int ReserveLoopbackPort()
        {
            SocketException lastError = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                try
                {
                    listener.Start();
                    return ((IPEndPoint)listener.LocalEndpoint).Port;
                }
                catch (SocketException exception)
                {
                    lastError = exception;
                }
                finally
                {
                    listener.Stop();
                }
            }
            throw new InvalidOperationException(
                "Windows did not provide a free loopback test port after "
                + "ten attempts.",
                lastError);
        }
    }

    public class DingoCmsEditorTestRouter : IDingoCmsEditorRequestRouter
    {
        public int ExecutionCount { get; private set; }
        public string WebIndexHtml => "<!doctype html><title>DingoCMS test</title>";

        public JArray DescribeTools()
        {
            return new JArray
            {
                new JObject
                {
                    ["name"] = "echo",
                    ["description"] = "Echoes its arguments.",
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true,
                    },
                },
            };
        }

        public JObject Execute(string operation, JObject arguments)
        {
            ExecutionCount++;
            if (operation == "fail")
            {
                throw new DingoCmsEditorRequestException(
                    "content_conflict",
                    "The module changed.");
            }
            return new JObject
            {
                ["operation"] = operation,
                ["arguments"] = arguments.DeepClone(),
            };
        }
    }
}
#endif
