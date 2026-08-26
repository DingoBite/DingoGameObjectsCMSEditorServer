#if NEWTONSOFT_EXISTS
using System;
using System.IO;

namespace DingoGameObjectsCMSEditorServer.Editor.Clients
{
    public class DingoCmsCodexConfigurator :
        IDingoCmsMcpClientConfigurator
    {
        public const string BEGIN_MARKER =
            "# >>> DingoCMS Editor Server (managed by Unity)";
        public const string END_MARKER =
            "# <<< DingoCMS Editor Server (managed by Unity)";

        private const string TABLE_HEADER =
            "[mcp_servers.dingo_cms]";

        public string Id => "codex";
        public string DisplayName => "Codex";
        public bool IsInstalled => Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex"));

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
                        : "Codex was not detected; the project config can still be prepared.");
            }

            try
            {
                var section = FindOwnedSection(File.ReadAllText(path));
                if (section == null)
                {
                    return new DingoCmsMcpClientState(
                        DingoCmsMcpClientStatus.NotConfigured,
                        path,
                        "No dingo_cms server entry in the project config.");
                }

                var expectedUrl =
                    $"url = \"{DingoCmsMcpClientConfigUtils.EscapeTomlString(context.McpUrl)}\"";
                var expectedToken =
                    $"bearer_token_env_var = \"{DingoCmsMcpClientConfigUtils.EscapeTomlString(context.TokenEnvironmentVariable)}\"";
                var configured = section.Contains(
                                     expectedUrl,
                                     StringComparison.Ordinal)
                                 && section.Contains(
                                     expectedToken,
                                     StringComparison.Ordinal);
                return new DingoCmsMcpClientState(
                    configured
                        ? DingoCmsMcpClientStatus.Configured
                        : DingoCmsMcpClientStatus.NeedsUpdate,
                    path,
                    configured
                        ? "Project MCP entry matches this server."
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
            var existing = File.Exists(path)
                ? File.ReadAllText(path)
                : string.Empty;
            var withoutOwnedSection = RemoveOwnedSection(existing);
            var block = GetManualConfiguration(context);
            var next = string.IsNullOrWhiteSpace(withoutOwnedSection)
                ? block + Environment.NewLine
                : withoutOwnedSection.TrimEnd()
                  + Environment.NewLine
                  + Environment.NewLine
                  + block
                  + Environment.NewLine;
            DingoCmsMcpClientConfigUtils.WriteAtomically(
                path,
                next,
                context.BackupRoot,
                "codex-config.toml.bak");
        }

        public void Remove(DingoCmsMcpClientContext context)
        {
            var path = GetConfigPath(context);
            if (!File.Exists(path))
            {
                return;
            }

            var existing = File.ReadAllText(path);
            var next = RemoveOwnedSection(existing);
            if (string.Equals(existing, next, StringComparison.Ordinal))
            {
                return;
            }

            DingoCmsMcpClientConfigUtils.WriteAtomically(
                path,
                next.TrimEnd() + Environment.NewLine,
                context.BackupRoot,
                "codex-config.toml.bak");
        }

        public string GetManualConfiguration(
            DingoCmsMcpClientContext context)
        {
            return string.Join(
                Environment.NewLine,
                BEGIN_MARKER,
                TABLE_HEADER,
                $"url = \"{DingoCmsMcpClientConfigUtils.EscapeTomlString(context.McpUrl)}\"",
                $"bearer_token_env_var = \"{DingoCmsMcpClientConfigUtils.EscapeTomlString(context.TokenEnvironmentVariable)}\"",
                END_MARKER);
        }

        public static string GetConfigPath(
            DingoCmsMcpClientContext context)
        {
            return Path.Combine(
                context.ProjectRoot,
                ".codex",
                "config.toml");
        }

        public static string RemoveOwnedSection(string value)
        {
            var normalized = DingoCmsMcpClientConfigUtils
                .NormalizeLineEndings(value);
            var lines = normalized.Split('\n');
            var begin = FindLine(lines, BEGIN_MARKER);
            var end = FindLine(lines, END_MARKER);
            if ((begin >= 0) != (end >= 0) || end >= 0 && end < begin)
            {
                throw new InvalidDataException(
                    "The managed DingoCMS Codex block is incomplete. Repair its markers before configuring it again.");
            }

            if (begin >= 0)
            {
                return JoinWithoutRange(lines, begin, end);
            }

            begin = FindLine(lines, TABLE_HEADER);
            if (begin < 0)
            {
                return DingoCmsMcpClientConfigUtils
                    .ToPlatformLineEndings(normalized);
            }

            end = lines.Length - 1;
            for (var index = begin + 1; index < lines.Length; index++)
            {
                var trimmed = lines[index].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal)
                    && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    end = index - 1;
                    break;
                }
            }
            return JoinWithoutRange(lines, begin, end);
        }

        private static string FindOwnedSection(string value)
        {
            var normalized = DingoCmsMcpClientConfigUtils
                .NormalizeLineEndings(value);
            var lines = normalized.Split('\n');
            var begin = FindLine(lines, BEGIN_MARKER);
            var end = FindLine(lines, END_MARKER);
            if ((begin >= 0) != (end >= 0) || end >= 0 && end < begin)
            {
                throw new InvalidDataException(
                    "The managed DingoCMS Codex block is incomplete.");
            }
            if (begin >= 0)
            {
                return string.Join("\n", lines, begin, end - begin + 1);
            }

            begin = FindLine(lines, TABLE_HEADER);
            if (begin < 0)
            {
                return null;
            }
            end = lines.Length - 1;
            for (var index = begin + 1; index < lines.Length; index++)
            {
                var trimmed = lines[index].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal)
                    && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    end = index - 1;
                    break;
                }
            }
            return string.Join("\n", lines, begin, end - begin + 1);
        }

        private static int FindLine(string[] lines, string value)
        {
            for (var index = 0; index < lines.Length; index++)
            {
                if (string.Equals(
                        lines[index].Trim(),
                        value,
                        StringComparison.Ordinal))
                {
                    return index;
                }
            }
            return -1;
        }

        private static string JoinWithoutRange(
            string[] lines,
            int begin,
            int end)
        {
            var result = new System.Collections.Generic.List<string>(
                lines.Length - (end - begin + 1));
            for (var index = 0; index < lines.Length; index++)
            {
                if (index < begin || index > end)
                {
                    result.Add(lines[index]);
                }
            }
            return DingoCmsMcpClientConfigUtils.ToPlatformLineEndings(
                string.Join("\n", result).TrimEnd());
        }
    }
}
#endif
