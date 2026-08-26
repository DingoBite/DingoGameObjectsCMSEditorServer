using System;
using System.Collections.Generic;
using System.Globalization;

namespace DingoGameObjectsCMSEditorServer.Runtime
{
    public sealed class DingoCmsEditorServerOptions
    {
        public const int DEFAULT_PORT = 17844;
        public const string ENABLE_ARGUMENT = "--dingo-cms-editor-server";
        public const string AUTHORING_ONLY_ARGUMENT =
            "--dingo-cms-editor-authoring-only";
        public const string PORT_ARGUMENT = "--dingo-cms-editor-port";
        public const string TOKEN_ARGUMENT = "--dingo-cms-editor-token";
        public const string TOKEN_ENVIRONMENT_VARIABLE =
            "DINGO_CMS_EDITOR_TOKEN";

        public bool Enabled { get; private set; }
        public bool AuthoringOnly { get; private set; }
        public int Port { get; private set; } = DEFAULT_PORT;
        public string Token { get; private set; }
        public bool GeneratedToken { get; private set; }

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

            return result;
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
