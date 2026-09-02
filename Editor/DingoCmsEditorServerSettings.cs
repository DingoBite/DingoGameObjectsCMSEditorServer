#if NEWTONSOFT_EXISTS
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DingoGameObjectsCMS.AssetLibrary.AssetsEdit;
using DingoGameObjectsCMSEditorServer.Editor.Clients;
using DingoGameObjectsCMSEditorServer.Runtime;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMSEditorServer.Editor
{
    public static class DingoCmsEditorServerSettings
    {
        private const string PrefsPrefix = "DingoCMS.EditorServer.";
        private const string SharedPortPrefsKey = PrefsPrefix + "SharedPort";
        private const uint WmSettingChange = 0x001A;
        private const uint SmtoAbortIfHung = 0x0002;

        public static event Action StateChanged;

        public static string AssetsRoot =>
            GameAssetModPathPolicy.GetAssetsRootPath();

        public static string ProjectRoot =>
            DingoCmsMcpClientConfigUtils.GetProjectRoot();

        public static string BaseUrl => $"http://127.0.0.1:{Port}";

        public static string McpUrl => BaseUrl + "/mcp";

        public static bool HasToken =>
            !string.IsNullOrWhiteSpace(ReadExistingToken());

        public static string Token => ReadExistingToken();

        public static bool TokenIsUserPersistent =>
            UnityEngine.Application.platform == RuntimePlatform.WindowsEditor
            && IsValidToken(ReadUserEnvironmentToken());

        public static int Port
        {
            get => EditorPrefs.GetInt(
                SharedPortPrefsKey,
                DingoCmsEditorBrokerContract.DefaultPort);
            set
            {
                DingoCmsEditorBrokerContract.RequirePort(value);
                if (value == Port)
                    return;
                if (DingoCmsEditorSessionClient.IsConnected
                    || DingoCmsEditorSessionClient.IsConnecting)
                {
                    throw new InvalidOperationException(
                        "Disconnect the DingoCMS project session before changing the shared port.");
                }

                RequireOwnedBrokerReleased("changing the DingoCMS port");
                EditorPrefs.SetInt(SharedPortPrefsKey, value);
                StateChanged?.Invoke();
            }
        }

        public static string EnsureToken()
        {
            var token = ReadExistingToken();
            if (IsValidToken(token))
            {
                SetProcessToken(token);
                return token;
            }

            return RegenerateToken();
        }

        public static string RegenerateToken()
        {
            if (DingoCmsEditorSessionClient.IsConnected
                || DingoCmsEditorSessionClient.IsConnecting)
            {
                throw new InvalidOperationException(
                    "Disconnect the DingoCMS project session before rotating its token.");
            }

            RequireOwnedBrokerReleased("rotating the DingoCMS token");

            var bytes = new byte[32];
            using (var generator = RandomNumberGenerator.Create())
                generator.GetBytes(bytes);

            var token = BitConverter.ToString(bytes)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
            SetProcessToken(token);
            PersistUserToken(token);
            StateChanged?.Invoke();
            return token;
        }

        public static DingoCmsMcpClientContext CreateClientContext()
        {
            return new DingoCmsMcpClientContext(
                ProjectRoot,
                McpUrl,
                DingoCmsEditorBrokerContract.TokenEnvironmentVariable);
        }

        private static void RequireOwnedBrokerReleased(string operation)
        {
            var state = DingoCmsDetachedEditorHost.ProcessState;
            if (state == DingoCmsDetachedProcessState.Stopped)
                return;

            throw new InvalidOperationException(
                $"Stop the owned DingoCMS broker before {operation}. "
                + $"PID {DingoCmsDetachedEditorHost.ProcessId} remains "
                + (state == DingoCmsDetachedProcessState.Running
                    ? "verified and running."
                    : state == DingoCmsDetachedProcessState.RestartRequired
                        ? "verified but requires a restart."
                        : "retained while ownership verification is pending."));
        }

        private static string ReadExistingToken()
        {
            var processToken = Environment.GetEnvironmentVariable(
                DingoCmsEditorBrokerContract.TokenEnvironmentVariable,
                EnvironmentVariableTarget.Process);
            if (IsValidToken(processToken))
                return processToken;

            var userToken = ReadUserEnvironmentToken();
            return IsValidToken(userToken) ? userToken : null;
        }

        private static string ReadUserEnvironmentToken()
        {
            if (UnityEngine.Application.platform != RuntimePlatform.WindowsEditor)
                return null;

            try
            {
                return Environment.GetEnvironmentVariable(
                    DingoCmsEditorBrokerContract.TokenEnvironmentVariable,
                    EnvironmentVariableTarget.User);
            }
            catch
            {
                return null;
            }
        }

        private static void SetProcessToken(string token)
        {
            Environment.SetEnvironmentVariable(
                DingoCmsEditorBrokerContract.TokenEnvironmentVariable,
                token,
                EnvironmentVariableTarget.Process);
        }

        private static void PersistUserToken(string token)
        {
            if (UnityEngine.Application.platform != RuntimePlatform.WindowsEditor)
                return;

            Environment.SetEnvironmentVariable(
                DingoCmsEditorBrokerContract.TokenEnvironmentVariable,
                token,
                EnvironmentVariableTarget.User);
            SendMessageTimeout(
                new IntPtr(0xffff),
                WmSettingChange,
                UIntPtr.Zero,
                "Environment",
                SmtoAbortIfHung,
                1000,
                out _);
        }

        private static bool IsValidToken(string token)
        {
            return !string.IsNullOrWhiteSpace(token)
                   && token.Trim().Length >= 24;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr window,
            uint message,
            UIntPtr wParam,
            string lParam,
            uint flags,
            uint timeout,
            out UIntPtr result);
    }
}
#endif
