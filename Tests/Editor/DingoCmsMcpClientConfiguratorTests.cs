#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using System.Linq;
using DingoGameObjectsCMSEditorServer.Editor.Clients;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DingoGameObjectsCMSEditorServer.Tests.Editor
{
    public class DingoCmsMcpClientConfiguratorTests
    {
        private string _projectRoot;
        private DingoCmsMcpClientContext _context;

        [SetUp]
        public void SetUp()
        {
            _projectRoot = Path.Combine(
                Path.GetTempPath(),
                "DingoCmsMcpClientConfiguratorTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_projectRoot);
            _context = new DingoCmsMcpClientContext(
                _projectRoot,
                "http://127.0.0.1:18993/mcp",
                "DINGO_CMS_EDITOR_TOKEN");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_projectRoot))
            {
                Directory.Delete(_projectRoot, recursive: true);
            }
        }

        [Test]
        public void CodexConfigurationIsIdempotentAndPreservesOtherTables()
        {
            var configurator = new DingoCmsCodexConfigurator();
            var path = DingoCmsCodexConfigurator.GetConfigPath(_context);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(
                path,
                "model = \"gpt-5\"\n\n[mcp_servers.other]\nurl = \"http://example.test/mcp\"\n");

            configurator.Configure(_context);
            configurator.Configure(_context);

            var configured = File.ReadAllText(path);
            Assert.That(configured, Does.Contain("model = \"gpt-5\""));
            Assert.That(configured, Does.Contain("[mcp_servers.other]"));
            Assert.That(
                CountOccurrences(
                    configured,
                    DingoCmsCodexConfigurator.BEGIN_MARKER),
                Is.EqualTo(1));
            Assert.That(
                configurator.Inspect(_context).Status,
                Is.EqualTo(DingoCmsMcpClientStatus.Configured));

            configurator.Remove(_context);
            var removed = File.ReadAllText(path);
            Assert.That(removed, Does.Contain("[mcp_servers.other]"));
            Assert.That(
                removed,
                Does.Not.Contain(DingoCmsCodexConfigurator.BEGIN_MARKER));
        }

        [Test]
        public void ClaudeConfigurationIsIdempotentAndPreservesOtherServers()
        {
            var configurator = new DingoCmsClaudeCodeConfigurator();
            var path = DingoCmsClaudeCodeConfigurator.GetConfigPath(_context);
            File.WriteAllText(
                path,
                new JObject
                {
                    ["projectSetting"] = true,
                    ["mcpServers"] = new JObject
                    {
                        ["other"] = new JObject
                        {
                            ["type"] = "http",
                            ["url"] = "http://example.test/mcp",
                        },
                    },
                }.ToString());

            configurator.Configure(_context);
            configurator.Configure(_context);

            var root = JObject.Parse(File.ReadAllText(path));
            Assert.That(root["projectSetting"]?.Value<bool>(), Is.True);
            Assert.That(root["mcpServers"]?["other"], Is.Not.Null);
            Assert.That(
                root["mcpServers"]?[DingoCmsClaudeCodeConfigurator.SERVER_ID]?
                    ["headers"]?["Authorization"]?.Value<string>(),
                Is.EqualTo("Bearer ${DINGO_CMS_EDITOR_TOKEN}"));
            Assert.That(
                configurator.Inspect(_context).Status,
                Is.EqualTo(DingoCmsMcpClientStatus.Configured));

            configurator.Remove(_context);
            root = JObject.Parse(File.ReadAllText(path));
            Assert.That(root["mcpServers"]?["other"], Is.Not.Null);
            Assert.That(
                root["mcpServers"]?[DingoCmsClaudeCodeConfigurator.SERVER_ID],
                Is.Null);
        }

        [Test]
        public void RegistryDiscoversClientConfiguratorsWithoutStaticList()
        {
            DingoCmsMcpClientRegistry.Invalidate();
            var ids = DingoCmsMcpClientRegistry.Configurators
                .Select(configurator => configurator.Id)
                .ToArray();

            Assert.That(ids, Does.Contain("codex"));
            Assert.That(ids, Does.Contain("claude-code"));
        }

        private static int CountOccurrences(string value, string search)
        {
            return value.Split(
                       new[] { search },
                       StringSplitOptions.None)
                   .Length - 1;
        }
    }
}
#endif
