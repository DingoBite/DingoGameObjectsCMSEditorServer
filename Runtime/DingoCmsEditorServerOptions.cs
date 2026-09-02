using System;
using System.Collections.Generic;
using System.Globalization;

namespace DingoGameObjectsCMSEditorServer.Runtime
{
    public class DingoCmsEditorServerOptions
    {
        public const int DEFAULT_PORT = 17844;
        public const string ENABLE_ARGUMENT = "--dingo-cms-editor-server";
        public const string AUTHORING_ONLY_ARGUMENT =
            "--dingo-cms-editor-authoring-only";
        public const string PORT_ARGUMENT = "--dingo-cms-editor-port";
        public const string TOKEN_ARGUMENT = "--dingo-cms-editor-token";
        public const string TOKEN_ENVIRONMENT_VARIABLE =
            "DINGO_CMS_EDITOR_TOKEN";
        public const string INSTANCE_TOKEN_ENVIRONMENT_VARIABLE =
            "DINGO_CMS_EDITOR_INSTANCE_TOKEN";
        public const string HOST_STATE_FILE_ENVIRONMENT_VARIABLE =
            "DINGO_CMS_EDITOR_HOST_STATE_FILE";
        public const string PROJECT_ID_ENVIRONMENT_VARIABLE =
            "DINGO_CMS_EDITOR_PROJECT_ID";
        public const string BUILD_FINGERPRINT_ENVIRONMENT_VARIABLE =
            "DINGO_CMS_EDITOR_BUILD_FINGERPRINT";

        public bool Enabled { get; private set; }
        public bool AuthoringOnly { get; private set; }
        public int Port { get; private set; } = DEFAULT_PORT;
        public string Token { get; private set; }
        public bool GeneratedToken { get; private set; }
        public string InstanceToken { get; private set; }
        public string HostStateFile { get; private set; }
        public string ProjectId { get; private set; }
        public string BuildFingerprint { get; private set; }

        public string BaseUrl => $"http://127.0.0.1:{Port}";
        public string McpUrl => BaseUrl + "/mcp";
        public bool StartsBeforeRuntimeCatalog => Enabled && AuthoringOnly;
        public bool StartsAfterRuntimeCatalog => Enabled && !AuthoringOnly;

        public static DingoCmsEditorServerOptions CreateEnabled(
            int port,
            string token,
            bool authoringOnly = true)
        {
            return new DingoCmsEditorServerOptions
            {
                Enabled = true,
                AuthoringOnly = authoringOnly,
                Port = RequirePort(port),
                Token = RequireToken(token),
            };
        }

        public static DingoCmsEditorServerOptions Parse(
            IReadOnlyList<string> arguments,
            Func<string, string> readEnvironmentVariable = null)
        {
            arguments ??= Array.Empty<string>();
            readEnvironmentVariable ??= Environment.GetEnvironmentVariable;

            var result = new DingoCmsEditorServerOptions();
            for (var index = 0; index < arguments.Count; index++)
            {
                if (string.Equals(
                        arguments[index],
                        ENABLE_ARGUMENT,
                        StringComparison.Ordinal))
                {
                    result.Enabled = true;
                    break;
                }
            }
            if (!result.Enabled)
                return result;

            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index];
                if (string.Equals(
                        argument,
                        ENABLE_ARGUMENT,
                        StringComparison.Ordinal))
                {
                    result.Enabled = true;
                    continue;
                }

                if (string.Equals(
                        argument,
                        AUTHORING_ONLY_ARGUMENT,
                        StringComparison.Ordinal))
                {
                    result.AuthoringOnly = true;
                    continue;
                }

                if (TryReadValue(
                        arguments,
                        ref index,
                        PORT_ARGUMENT,
                        out var portText))
                {
                    if (!int.TryParse(
                            portText,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var port))
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(arguments),
                            portText,
                            $"{PORT_ARGUMENT} must be between 1024 and 65535.");
                    }

                    result.Port = RequirePort(port);
                    continue;
                }

                if (TryReadValue(
                        arguments,
                        ref index,
                        TOKEN_ARGUMENT,
                        out var token))
                {
                    result.Token = RequireToken(token);
                }
            }

            if (string.IsNullOrWhiteSpace(result.Token))
            {
                var environmentToken = readEnvironmentVariable(
                    TOKEN_ENVIRONMENT_VARIABLE);
                if (!string.IsNullOrWhiteSpace(environmentToken))
                    result.Token = RequireToken(environmentToken);
            }

            if (string.IsNullOrWhiteSpace(result.Token))
            {
                result.Token = Guid.NewGuid().ToString("N");
                result.GeneratedToken = true;
            }

            result.InstanceToken = NormalizeOptional(
                readEnvironmentVariable(INSTANCE_TOKEN_ENVIRONMENT_VARIABLE));
            result.HostStateFile = NormalizeOptional(
                readEnvironmentVariable(HOST_STATE_FILE_ENVIRONMENT_VARIABLE));
            result.ProjectId = NormalizeOptional(
                readEnvironmentVariable(PROJECT_ID_ENVIRONMENT_VARIABLE));
            result.BuildFingerprint = NormalizeOptional(
                readEnvironmentVariable(
                    BUILD_FINGERPRINT_ENVIRONMENT_VARIABLE));
            var hasDetachedIdentity = result.InstanceToken != null
                                      || result.HostStateFile != null
                                      || result.ProjectId != null
                                      || result.BuildFingerprint != null;
            if (hasDetachedIdentity
                && (result.InstanceToken == null
                    || result.HostStateFile == null
                    || result.ProjectId == null
                    || result.BuildFingerprint == null))
            {
                throw new ArgumentException(
                    $"{INSTANCE_TOKEN_ENVIRONMENT_VARIABLE}, "
                    + $"{HOST_STATE_FILE_ENVIRONMENT_VARIABLE}, and "
                    + $"{PROJECT_ID_ENVIRONMENT_VARIABLE}, and "
                    + $"{BUILD_FINGERPRINT_ENVIRONMENT_VARIABLE} "
                    + "must be supplied "
                    + "together for a detached DingoCMS host.");
            }

            return result;
        }

        private static string NormalizeOptional(string value)
        {
            value = value?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static bool TryReadValue(
            IReadOnlyList<string> arguments,
            ref int index,
            string name,
            out string value)
        {
            var argument = arguments[index];
            var prefix = name + "=";
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                value = argument.Substring(prefix.Length);
                return true;
            }

            if (!string.Equals(argument, name, StringComparison.Ordinal))
            {
                value = null;
                return false;
            }

            if (index + 1 >= arguments.Count)
                throw new ArgumentException($"{name} requires a value.");

            index++;
            value = arguments[index];
            return true;
        }

        private static string RequireToken(string token)
        {
            token = token?.Trim();
            if (string.IsNullOrWhiteSpace(token) || token.Length < 24)
            {
                throw new ArgumentException(
                    $"{TOKEN_ARGUMENT} and {TOKEN_ENVIRONMENT_VARIABLE} "
                    + "must contain at least 24 non-whitespace characters.");
            }

            return token;
        }

        private static int RequirePort(int port)
        {
            if (port < 1024 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(port),
                    port,
                    $"{PORT_ARGUMENT} must be between 1024 and 65535.");
            }

            return port;
        }
    }
}
