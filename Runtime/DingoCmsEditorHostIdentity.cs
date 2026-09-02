#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMSEditorServer.Runtime
{
    public class DingoCmsEditorHostIdentity
    {
        public readonly int ProcessId;
        public readonly int Port;
        public readonly string ProjectId;
        public readonly string InstanceToken;
        public readonly string ExecutablePath;
        public readonly long ProcessStartUtcTicks;
        public readonly bool Ready;
        public readonly string BrokerFingerprint;

        public DingoCmsEditorHostIdentity(
            int processId,
            int port,
            string projectId,
            string instanceToken,
            string executablePath,
            long processStartUtcTicks = 0,
            bool ready = false,
            string brokerFingerprint = null)
        {
            if (processId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(processId),
                    processId,
                    "Process id must be positive.");
            }
            if (port <= 0 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(port),
                    port,
                    "Port must be between 1 and 65535.");
            }
            if (processStartUtcTicks < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(processStartUtcTicks),
                    processStartUtcTicks,
                    "Process start ticks cannot be negative.");
            }

            ProcessId = processId;
            Port = port;
            ProjectId = RequireText(projectId, nameof(projectId));
            InstanceToken = RequireText(instanceToken, nameof(instanceToken));
            ExecutablePath = RequireText(executablePath, nameof(executablePath));
            ProcessStartUtcTicks = processStartUtcTicks;
            Ready = ready;
            BrokerFingerprint = NormalizeOptional(brokerFingerprint);
        }

        public static void WriteAtomic(
            string path,
            DingoCmsEditorHostIdentity identity)
        {
            if (identity == null)
                throw new ArgumentNullException(nameof(identity));

            var fullPath = RequirePath(path);
            var directory = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);

            var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var document = new JObject
                {
                    ["processId"] = identity.ProcessId,
                    ["port"] = identity.Port,
                    ["projectId"] = identity.ProjectId,
                    ["instanceToken"] = identity.InstanceToken,
                    ["executablePath"] = identity.ExecutablePath,
                    ["processStartUtcTicks"] = identity.ProcessStartUtcTicks,
                    ["ready"] = identity.Ready,
                    ["brokerFingerprint"] = identity.BrokerFingerprint,
                };
                File.WriteAllText(
                    temporaryPath,
                    document.ToString(Formatting.None),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                if (File.Exists(fullPath))
                    File.Replace(temporaryPath, fullPath, null);
                else
                    File.Move(temporaryPath, fullPath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        public static bool TryRead(
            string path,
            out DingoCmsEditorHostIdentity identity)
        {
            identity = null;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                    return false;

                var document = JObject.Parse(File.ReadAllText(fullPath));
                if (!TryReadInt(document, "processId", 1, int.MaxValue, out var processId)
                    || !TryReadInt(document, "port", 1, 65535, out var port)
                    || !TryReadText(document, "projectId", out var projectId)
                    || !TryReadText(document, "instanceToken", out var instanceToken)
                    || !TryReadText(document, "executablePath", out var executablePath))
                {
                    return false;
                }

                var processStartUtcTicks =
                    ReadOptionalProcessStartTicks(document);
                var brokerFingerprint =
                    ReadOptionalText(document, "brokerFingerprint");
                if (processStartUtcTicks <= 0
                    || string.IsNullOrWhiteSpace(brokerFingerprint))
                {
                    return false;
                }

                identity = new DingoCmsEditorHostIdentity(
                    processId,
                    port,
                    projectId,
                    instanceToken,
                    executablePath,
                    processStartUtcTicks,
                    ReadOptionalBool(document, "ready"),
                    brokerFingerprint);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static bool DeleteIfOwned(
            string path,
            int processId,
            string instanceToken)
        {
            if (processId <= 0 || string.IsNullOrWhiteSpace(instanceToken))
                return false;
            if (!TryRead(path, out var identity))
                return false;
            if (identity.ProcessId != processId
                || !string.Equals(
                    identity.InstanceToken,
                    instanceToken.Trim(),
                    StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                File.Delete(Path.GetFullPath(path));
                return !File.Exists(Path.GetFullPath(path));
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string RequirePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("State file path is required.", nameof(path));

            return Path.GetFullPath(path);
        }

        private static string RequireText(string value, string parameterName)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(
                    "A non-empty value is required.",
                    parameterName);
            }

            return value;
        }

        private static bool TryReadInt(
            JObject document,
            string name,
            int minimum,
            int maximum,
            out int value)
        {
            value = 0;
            if (!document.TryGetValue(name, out var token)
                || token.Type != JTokenType.Integer)
            {
                return false;
            }

            var numericValue = token.Value<long>();
            if (numericValue < minimum || numericValue > maximum)
                return false;

            value = (int)numericValue;
            return true;
        }

        private static bool TryReadText(
            JObject document,
            string name,
            out string value)
        {
            value = null;
            if (!document.TryGetValue(name, out var token)
                || token.Type != JTokenType.String)
            {
                return false;
            }

            value = token.Value<string>()?.Trim();
            return !string.IsNullOrEmpty(value);
        }

        private static bool ReadOptionalBool(JObject document, string name)
        {
            return document.TryGetValue(name, out var token)
                   && token.Type == JTokenType.Boolean
                   && token.Value<bool>();
        }

        private static string ReadOptionalText(JObject document, string name)
        {
            if (!document.TryGetValue(name, out var token)
                || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type != JTokenType.String)
            {
                throw new JsonException($"{name} must be a string.");
            }
            return NormalizeOptional(token.Value<string>());
        }

        private static string NormalizeOptional(string value)
        {
            value = value?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static long ReadOptionalProcessStartTicks(JObject document)
        {
            if (!document.TryGetValue("processStartUtcTicks", out var token))
                return 0;
            if (token.Type != JTokenType.Integer)
            {
                throw new JsonException(
                    "processStartUtcTicks must be an integer.");
            }

            var value = token.Value<long>();
            if (value < 0)
            {
                throw new JsonException(
                    "processStartUtcTicks cannot be negative.");
            }
            return value;
        }
    }
}
#endif
