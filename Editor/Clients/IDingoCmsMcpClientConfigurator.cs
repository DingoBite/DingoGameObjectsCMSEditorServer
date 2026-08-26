#if NEWTONSOFT_EXISTS
using System;

namespace DingoGameObjectsCMSEditorServer.Editor.Clients
{
    public enum DingoCmsMcpClientStatus
    {
        NotInstalled,
        NotConfigured,
        Configured,
        NeedsUpdate,
        Error,
    }

    public readonly struct DingoCmsMcpClientContext
    {
        public readonly string ProjectRoot;
        public readonly string McpUrl;
        public readonly string TokenEnvironmentVariable;
        public readonly string BackupRoot;

        public DingoCmsMcpClientContext(
            string projectRoot,
            string mcpUrl,
            string tokenEnvironmentVariable)
        {
            ProjectRoot = projectRoot
                          ?? throw new ArgumentNullException(
                              nameof(projectRoot));
            McpUrl = mcpUrl
                     ?? throw new ArgumentNullException(nameof(mcpUrl));
            TokenEnvironmentVariable = tokenEnvironmentVariable
                                       ?? throw new ArgumentNullException(
                                           nameof(tokenEnvironmentVariable));
            BackupRoot = System.IO.Path.Combine(
                ProjectRoot,
                "Library",
                "DingoCmsEditorServer",
                "ConfigBackups");
        }
    }

    public readonly struct DingoCmsMcpClientState
    {
        public readonly DingoCmsMcpClientStatus Status;
        public readonly string ConfigPath;
        public readonly string Message;

        public DingoCmsMcpClientState(
            DingoCmsMcpClientStatus status,
            string configPath,
            string message)
        {
            Status = status;
            ConfigPath = configPath;
            Message = message;
        }
    }

    public interface IDingoCmsMcpClientConfigurator
    {
        string Id { get; }
        string DisplayName { get; }
        bool IsInstalled { get; }

        DingoCmsMcpClientState Inspect(
            DingoCmsMcpClientContext context);
        void Configure(DingoCmsMcpClientContext context);
        void Remove(DingoCmsMcpClientContext context);
        string GetManualConfiguration(
            DingoCmsMcpClientContext context);
    }
}
#endif
