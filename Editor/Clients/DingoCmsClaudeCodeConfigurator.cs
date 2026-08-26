#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMSEditorServer.Editor.Clients
{
    public class DingoCmsClaudeCodeConfigurator :
        IDingoCmsMcpClientConfigurator
    {
        public const string SERVER_ID = "dingo-cms";

        public string Id => "claude-code";
        public string DisplayName => "Claude Code";
        public bool IsInstalled => Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude"));

        public DingoCmsMcpClientState Inspect(
            DingoCmsMcpClientContext context)
        {
            var path = GetConfigPath(context);
            if (!File.Exists(path))
            {
                return new DingoCmsMcpClientState(
                    IsInstalled
                        ? DingoCmsMcpClientStatus.NotConfigured
                        : DingoCmsMcpClientStatus.NotInstalled,
                    path,
                    IsInstalled
                        ? "No project MCP configuration."
                        : "Claude Code was not detected; the project config can still be prepared.");
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                var entry = root["mcpServers"]?[SERVER_ID] as JObject;
                if (entry == null)
                {
                    return new DingoCmsMcpClientState(
                        DingoCmsMcpClientStatus.NotConfigured,
                        path,
                        "No dingo-cms server entry in .mcp.json.");
                }

                var expected = BuildEntry(context);
                var configured = JToken.DeepEquals(entry, expected);
                return new DingoCmsMcpClientState(
                    configured
                        ? DingoCmsMcpClientStatus.Configured
                        : DingoCmsMcpClientStatus.NeedsUpdate,
                    path,
                    configured
                        ? "Project MCP entry matches this server. Approve it in Claude Code when prompted."
                        : "The project MCP entry uses another endpoint or token source.");
            }
            catch (Exception exception)
            {
                return new DingoCmsMcpClientState(
                    DingoCmsMcpClientStatus.Error,
                    path,
                    exception.Message);
            }
        }

        public void Configure(DingoCmsMcpClientContext context)
        {
            var path = GetConfigPath(context);
            var root = File.Exists(path)
                ? JObject.Parse(File.ReadAllText(path))
                : new JObject();
            if (root["mcpServers"] is not JObject servers)
            {
                servers = new JObject();
                root["mcpServers"] = servers;
            }
            servers[SERVER_ID] = BuildEntry(context);
            DingoCmsMcpClientConfigUtils.WriteAtomically(
                path,
                root.ToString(Formatting.Indented) + Environment.NewLine,
                context.BackupRoot,
                "claude-mcp.json.bak");
        }

        public void Remove(DingoCmsMcpClientContext context)
        {
            var path = GetConfigPath(context);
            if (!File.Exists(path))
            {
                return;
            }
            var root = JObject.Parse(File.ReadAllText(path));
            if (root["mcpServers"] is not JObject servers
                || servers.Property(SERVER_ID) == null)
            {
                return;
            }
            servers.Property(SERVER_ID)?.Remove();
            DingoCmsMcpClientConfigUtils.WriteAtomically(
                path,
                root.ToString(Formatting.Indented) + Environment.NewLine,
                context.BackupRoot,
                "claude-mcp.json.bak");
        }

        public string GetManualConfiguration(
            DingoCmsMcpClientContext context)
        {
            return new JObject
            {
                ["mcpServers"] = new JObject
                {
                    [SERVER_ID] = BuildEntry(context),
                },
            }.ToString(Formatting.Indented);
        }

        public static string GetConfigPath(
            DingoCmsMcpClientContext context)
        {
            return Path.Combine(context.ProjectRoot, ".mcp.json");
        }

        public static JObject BuildEntry(
            DingoCmsMcpClientContext context)
        {
            return new JObject
            {
                ["type"] = "http",
                ["url"] = context.McpUrl,
                ["headers"] = new JObject
                {
                    ["Authorization"] =
                        $"Bearer ${{{context.TokenEnvironmentVariable}}}",
                },
            };
        }
    }
}
#endif
