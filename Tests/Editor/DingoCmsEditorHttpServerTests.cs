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
            var first = new DingoCmsEditorHttpServer(
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
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
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
