#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using DingoGameObjectsCMSEditorServer.Application;
using DingoGameObjectsCMSEditorServer.Authoring;
using DingoGameObjectsCMSEditorServer.Transport;
using DingoGameObjectsCMSEditorServer.Web;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DingoGameObjectsCMSEditorServer.Runtime
{
    public sealed class DingoCmsEditorServerRuntime : IDisposable
    {
        private readonly DingoCmsAuthoringApplication _authoring;
        private readonly DingoCmsEditorHttpServer _server;
        private bool _disposed;

        public DingoCmsEditorServerOptions Options { get; }
        public bool IsRunning => !_disposed && _server.IsRunning;
        public string AssetsRoot => _authoring.AssetsRoot;

        private DingoCmsEditorServerRuntime(
            DingoCmsEditorServerOptions options,
            DingoCmsAuthoringApplication authoring,
            DingoCmsEditorHttpServer server)
        {
            Options = options;
            _authoring = authoring;
            _server = server;
        }

        public static DingoCmsEditorServerRuntime Start(
            DingoCmsEditorServerOptions options,
            string assetsRoot,
            Func<JObject> runtimeStatus = null,
            Func<JObject> runtimeReload = null)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (!options.Enabled)
            {
                throw new InvalidOperationException(
                    "DingoCMS Editor Server cannot start from disabled options.");
            }

            var authoring = new DingoCmsAuthoringApplication(assetsRoot);
            DingoCmsEditorHttpServer server = null;
            try
            {
                var applicationRouter = new DingoCmsEditorRequestRouter(
                    authoring.Execute,
                    DingoCmsEditorWebUi.LoadHtml(),
                    runtimeStatus,
                    runtimeReload);
                server = new DingoCmsEditorHttpServer(
                    applicationRouter,
                    options.Port,
                    options.Token);
                server.Start();
                Debug.Log(
                    "DingoCMS Editor Server started explicitly.\n"
                    + $"MCP: {options.McpUrl}\n"
                    + $"Web (one-time): {server.CreateBrowserBootstrapUrl()}\n"
                    + (options.AuthoringOnly
                        ? "Authoring-only host: no gameplay content snapshot "
                          + "will be created."
                        : "Commits update AppData only; the active gameplay "
                          + "session is not reloaded automatically."));
                return new DingoCmsEditorServerRuntime(
                    options,
                    authoring,
                    server);
            }
            catch
            {
                try
                {
                    server?.Dispose();
                }
                finally
                {
                    authoring.Shutdown();
                }
                throw;
            }
        }

        public static DingoCmsEditorServerRuntime TryStartFromCommandLine(
            IReadOnlyList<string> arguments,
            string assetsRoot,
            Func<JObject> runtimeStatus = null,
            Func<JObject> runtimeReload = null)
        {
            var options = DingoCmsEditorServerOptions.Parse(arguments);
            return options.Enabled
                ? Start(options, assetsRoot, runtimeStatus, runtimeReload)
                : null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _server.Dispose();
            }
            finally
            {
                _authoring.Shutdown();
            }
        }

        public string CreateBrowserBootstrapUrl()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(DingoCmsEditorServerRuntime));
            }

            return _server.CreateBrowserBootstrapUrl();
        }
    }
}
#endif
