#if NEWTONSOFT_EXISTS
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DingoGameObjectsCMSEditorServer.Runtime;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMSEditorServer.Editor
{
    public enum DingoCmsDetachedProcessState
    {
        Stopped = 0,
        Running = 1,
        OwnershipVerificationPending = 2,
        RestartRequired = 3,
    }

    [InitializeOnLoad]
    public static class DingoCmsDetachedEditorHost
    {
        private const string PREFS_PREFIX =
            "DingoCMS.EditorServer.Detached.";
        private const double PROCESS_POLL_INTERVAL_SECONDS = 1d;
        private const double HEALTH_PROBE_INTERVAL_SECONDS = 1d;
        private const double STARTUP_HEALTH_TIMEOUT_SECONDS = 15d;
        private const double READY_UNHEALTHY_TIMEOUT_SECONDS = 15d;
        private const int READY_UNHEALTHY_FAILURE_COUNT = 3;
        private const double HEALTH_STABILITY_RESET_SECONDS = 30d;
        private static readonly double[] RESTART_DELAYS_SECONDS =
            { 0d, 1d, 3d, 5d, 10d, 30d };

        private static string _lastError;
        private static double _nextProcessPollTime;
        private static DingoCmsDetachedProcessState _lastObservedState;
        private static int _lastObservedProcessId;
        private static int _restartAttempt;
        private static long _nextRestartUtcTicks;
        private static bool _startInProgress;
        private static string _brokerFingerprint;
        private static string _brokerFingerprintSignature;
        private static string _healthProbeKey;
        private static Task<DingoCmsEditorHealthProbeResult> _healthProbeTask;
        private static DingoCmsEditorHealthProbeResult _lastHealthResult;
        private static double _nextHealthProbeTime;
        private static int _consecutiveHealthFailures;
        private static double _unhealthySinceTime;
        private static double _healthySinceTime;

        public static event Action StateChanged;

        public static bool KeepRunning
        {
            get => EditorPrefs.GetBool(KeepRunningPrefsKey, false);
            set
            {
                EditorPrefs.SetBool(KeepRunningPrefsKey, value);
                if (value)
                    ResetRestartBackoff();
                NotifyStateChanged();
            }
        }

        public static string ExecutablePath
        {
            get
            {
                var configured = EditorPrefs.GetString(
                    ExecutablePrefsKey,
                    string.Empty);
                return string.IsNullOrWhiteSpace(configured)
                    ? DefaultExecutablePath
                    : Path.GetFullPath(configured);
            }
            set
            {
                if (ProcessState != DingoCmsDetachedProcessState.Stopped)
                {
                    throw new InvalidOperationException(
                        "Stop the standalone DingoCMS server before changing its Python runtime.");
                }
                var normalized = string.IsNullOrWhiteSpace(value)
                    ? DefaultExecutablePath
                    : Path.GetFullPath(value.Trim());
                EditorPrefs.SetString(ExecutablePrefsKey, normalized);
                _lastError = null;
                NotifyStateChanged();
            }
        }

        public static string DefaultExecutablePath =>
            ResolveDefaultBrokerRuntimePath();

        public static string BrokerScriptPath => Path.Combine(
            ProjectRoot,
            "Assets",
            "AppSDK",
            "DingoGameObjectsCMSEditorServer",
            "Broker",
            "dingo_cms_broker.py");

        public static string WebIndexPath => Path.Combine(
            ProjectRoot,
            "Assets",
            "AppSDK",
            "DingoGameObjectsCMSEditorServer",
            "Resources",
            "DingoCmsEditorServer",
            "index.html");

        public static string LogPath => Path.Combine(
            ProjectRoot,
            "Library",
            "DingoCmsEditorServer",
            "detached-host.log");

        public static string HostStatePath => Path.Combine(
            ProjectRoot,
            "Library",
            "DingoCmsEditorServer",
            "detached-host.json");

        public static string ProjectId => Hash128.Compute(
                ProjectRoot.Replace('\\', '/').ToLowerInvariant())
            .ToString();

        public static DingoCmsDetachedProcessState ProcessState
        {
            get
            {
                if (!DingoCmsEditorHostIdentity.TryRead(
                        HostStatePath,
                        out var identity))
                {
                    ResetHealthObservation();
                    return File.Exists(HostStatePath)
                        ? DingoCmsDetachedProcessState
                            .OwnershipVerificationPending
                        : DingoCmsDetachedProcessState.Stopped;
                }

                if (!TryGetTrackedProcess(
                        identity,
                        out var process,
                        clearStaleIdentity: true))
                {
                    return File.Exists(HostStatePath)
                        ? DingoCmsDetachedProcessState
                            .OwnershipVerificationPending
                        : DingoCmsDetachedProcessState.Stopped;
                }

                using (process)
                {
                    if (!TryGetBrokerFingerprint(
                            out var currentFingerprint,
                            out var fingerprintError))
                    {
                        _lastError = fingerprintError;
                        return DingoCmsDetachedProcessState.RestartRequired;
                    }
                    if (!string.Equals(
                            identity.BrokerFingerprint,
                            currentFingerprint,
                            StringComparison.Ordinal))
                    {
                        _lastError = "The standalone DingoCMS broker files "
                                     + "changed. Stop and start the server once.";
                        return DingoCmsDetachedProcessState.RestartRequired;
                    }

                    var health = ObserveHealth(identity, currentFingerprint);
                    if (health?.Healthy != true)
                        return DingoCmsDetachedProcessState
                            .OwnershipVerificationPending;

                    TryMarkIdentityReady(identity);
                    _lastError = null;
                    return DingoCmsDetachedProcessState.Running;
                }
            }
        }

        public static bool IsRunning =>
            ProcessState == DingoCmsDetachedProcessState.Running;

        public static bool HasRetainedIdentity =>
            File.Exists(HostStatePath);

        public static int ProcessId =>
            DingoCmsEditorHostIdentity.TryRead(
                HostStatePath,
                out var identity)
                ? identity.ProcessId
                : 0;

        public static string LastError => _lastError;

        static DingoCmsDetachedEditorHost()
        {
            RestoreRestartBackoff();
            _lastObservedState = ProcessState;
            _lastObservedProcessId = ProcessId;
            EditorApplication.projectChanged += InvalidateBrokerFingerprint;
            EditorApplication.update += PollProcess;
        }

        public static void Start()
        {
            KeepRunning = true;
            ResetRestartBackoff();
            try
            {
                StartCore();
            }
            catch (Exception exception)
            {
                RegisterRestartFailure(exception.Message);
                throw;
            }
        }

        private static void StartCore()
        {
            if (_startInProgress)
                return;

            var state = ProcessState;
            if (state == DingoCmsDetachedProcessState.Running)
                return;
            if (state != DingoCmsDetachedProcessState.Stopped
                || File.Exists(HostStatePath))
            {
                throw new InvalidOperationException(
                    "A retained DingoCMS host identity is still being verified. Stop that exact server before starting another one.");
            }
            var executablePath = ExecutablePath;
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException(
                    "Python 3 is required. Select python.exe under Manual Server Launch or set DINGO_CMS_BROKER_PYTHON.",
                    executablePath);
            }
            if (!TryGetBrokerFingerprint(
                    out var brokerFingerprint,
                    out var fingerprintError))
            {
                throw new InvalidOperationException(fingerprintError);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            Directory.CreateDirectory(Path.GetDirectoryName(HostStatePath));
            var token = DingoCmsEditorServerSettings.EnsureToken();
            var instanceToken = Guid.NewGuid().ToString("N")
                                + Guid.NewGuid().ToString("N");
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(BrokerScriptPath),
                Arguments = CreateLaunchArguments(
                    BrokerScriptPath,
                    DingoCmsEditorServerSettings.Port,
                    LogPath,
                    WebIndexPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.EnvironmentVariables[
                DingoCmsEditorBrokerContract.TokenEnvironmentVariable] = token;
            startInfo.EnvironmentVariables[
                DingoCmsEditorBrokerContract
                    .InstanceTokenEnvironmentVariable] = instanceToken;
            startInfo.EnvironmentVariables[
                DingoCmsEditorBrokerContract
                    .BrokerFingerprintEnvironmentVariable] =
                brokerFingerprint;

            _startInProgress = true;
            Process spawnedProcess = null;
            try
            {
                spawnedProcess = Process.Start(startInfo)
                                 ?? throw new InvalidOperationException(
                                     "The standalone DingoCMS process did not start.");
                if (!TryGetProcessStartUtcTicks(
                        spawnedProcess,
                        out var processStartUtcTicks))
                {
                    throw new InvalidOperationException(
                        "The standalone DingoCMS process started, but its creation time could not be verified.");
                }

                DingoCmsEditorHostIdentity.WriteAtomic(
                    HostStatePath,
                    new DingoCmsEditorHostIdentity(
                        spawnedProcess.Id,
                        DingoCmsEditorServerSettings.Port,
                        ProjectId,
                        instanceToken,
                        executablePath,
                        processStartUtcTicks,
                        ready: false,
                        brokerFingerprint: brokerFingerprint));
                ResetHealthObservation();
                _lastError = null;
            }
            catch
            {
                TryTerminateSpawnedProcess(spawnedProcess);
                if (spawnedProcess != null)
                {
                    DingoCmsEditorHostIdentity.DeleteIfOwned(
                        HostStatePath,
                        spawnedProcess.Id,
                        instanceToken);
                }
                throw;
            }
            finally
            {
                spawnedProcess?.Dispose();
                _startInProgress = false;
                NotifyStateChanged();
            }
        }

        public static void Stop()
        {
            KeepRunning = false;
            ResetRestartBackoff();
            if (!DingoCmsEditorHostIdentity.TryRead(
                    HostStatePath,
                    out var identity))
            {
                if (File.Exists(HostStatePath))
                {
                    throw new InvalidOperationException(
                        "The DingoCMS host identity is unreadable. Refusing to guess which process to terminate.");
                }
                ResetHealthObservation();
                _lastError = null;
                NotifyStateChanged();
                return;
            }

            if (TryGetTrackedProcess(
                    identity,
                    out var process,
                    clearStaleIdentity: true))
            {
                using (process)
                {
                    process.Kill();
                    if (!process.WaitForExit(5000))
                    {
                        throw new InvalidOperationException(
                            $"Standalone DingoCMS process {process.Id} did not exit within five seconds.");
                    }
                }
            }
            else if (File.Exists(HostStatePath))
            {
                throw new InvalidOperationException(
                    "The retained DingoCMS process could not be verified. Refusing to terminate an unrelated PID.");
            }

            DingoCmsEditorHostIdentity.DeleteIfOwned(
                HostStatePath,
                identity.ProcessId,
                identity.InstanceToken);
            ResetHealthObservation();
            _lastError = null;
            NotifyStateChanged();
        }

        public static void OpenWebEditor()
        {
            _ = DingoCmsEditorSessionClient.OpenWebEditorAsync();
        }

        internal static string CreateLaunchArguments(
            string brokerScriptPath,
            int port,
            string logPath,
            string webIndexPath)
        {
            return "-u " + QuoteArgument(brokerScriptPath)
                   + " --port "
                   + port.ToString(CultureInfo.InvariantCulture)
                   + " --web-index " + QuoteArgument(webIndexPath)
                   + " --log-file " + QuoteArgument(logPath);
        }

        internal static DingoCmsDetachedProcessState ResolveProcessState(
            bool processVerified,
            int retainedProcessId)
        {
            if (processVerified)
                return DingoCmsDetachedProcessState.Running;
            return retainedProcessId > 0
                ? DingoCmsDetachedProcessState.OwnershipVerificationPending
                : DingoCmsDetachedProcessState.Stopped;
        }

        internal static bool CanCompleteStop(
            bool processVerified,
            bool retainedIdentityExists)
        {
            return processVerified || !retainedIdentityExists;
        }

        internal static double RestartDelaySecondsForAttempt(int attempt)
        {
            var index = Math.Max(
                0,
                Math.Min(attempt, RESTART_DELAYS_SECONDS.Length - 1));
            return RESTART_DELAYS_SECONDS[index];
        }

        internal static bool StartupHealthTimedOut(
            DateTime processStartedUtc,
            DateTime nowUtc)
        {
            return nowUtc - processStartedUtc
                   >= TimeSpan.FromSeconds(
                       STARTUP_HEALTH_TIMEOUT_SECONDS);
        }

        internal static bool ShouldRecoverReadyHost(
            int consecutiveFailures,
            double unhealthySeconds)
        {
            return consecutiveFailures >= READY_UNHEALTHY_FAILURE_COUNT
                   && unhealthySeconds >= READY_UNHEALTHY_TIMEOUT_SECONDS;
        }

        internal static bool ShouldResetRestartBackoff(
            double healthySeconds)
        {
            return healthySeconds >= HEALTH_STABILITY_RESET_SECONDS;
        }

        private static string ProjectRoot => Path.GetFullPath(
            Path.Combine(UnityEngine.Application.dataPath, ".."));

        private static string ProjectPrefsPrefix =>
            PREFS_PREFIX + ProjectId + ".";

        private static string ExecutablePrefsKey =>
            ProjectPrefsPrefix + "BrokerRuntime";

        private static string KeepRunningPrefsKey =>
            ProjectPrefsPrefix + "KeepRunning";

        private static string RestartAttemptSessionKey =>
            ProjectPrefsPrefix + "RestartAttempt";

        private static string NextRestartUtcTicksSessionKey =>
            ProjectPrefsPrefix + "NextRestartUtcTicks";

        private static bool TryGetBrokerFingerprint(
            out string fingerprint,
            out string error)
        {
            fingerprint = null;
            error = null;
            try
            {
                var inputs = new[] { BrokerScriptPath, WebIndexPath };
                foreach (var input in inputs)
                {
                    if (!File.Exists(input))
                    {
                        error = "Standalone DingoCMS broker input is missing: "
                                + input;
                        return false;
                    }
                }

                var signature = string.Join(
                    "|",
                    inputs.Select(path =>
                    {
                        var info = new FileInfo(path);
                        return Path.GetFullPath(path) + ":" + info.Length + ":"
                               + info.LastWriteTimeUtc.Ticks;
                    }));
                if (!string.Equals(
                        signature,
                        _brokerFingerprintSignature,
                        StringComparison.Ordinal))
                {
                    _brokerFingerprint = ComputeBrokerFingerprint(inputs);
                    _brokerFingerprintSignature = signature;
                }

                fingerprint = _brokerFingerprint;
                return !string.IsNullOrWhiteSpace(fingerprint);
            }
            catch (Exception exception)
            {
                error = "Standalone DingoCMS broker validation failed: "
                        + exception.Message;
                return false;
            }
        }

        private static string ComputeBrokerFingerprint(string[] inputPaths)
        {
            using var hash = SHA256.Create();
            foreach (var path in inputPaths)
            {
                var name = Encoding.UTF8.GetBytes(
                    Path.GetFileName(path).ToLowerInvariant() + "\0");
                hash.TransformBlock(name, 0, name.Length, null, 0);
                var contents = File.ReadAllBytes(path);
                hash.TransformBlock(
                    contents,
                    0,
                    contents.Length,
                    null,
                    0);
            }
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(hash.Hash)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
        }

        private static void InvalidateBrokerFingerprint()
        {
            _brokerFingerprint = null;
            _brokerFingerprintSignature = null;
            NotifyStateChanged();
        }

        private static string ResolveDefaultBrokerRuntimePath()
        {
            var configured = Environment.GetEnvironmentVariable(
                "DINGO_CMS_BROKER_PYTHON");
            if (!string.IsNullOrWhiteSpace(configured)
                && File.Exists(configured.Trim()))
            {
                return Path.GetFullPath(configured.Trim());
            }

            var path = Environment.GetEnvironmentVariable("PATH")
                       ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                    continue;
                try
                {
                    var candidate = Path.Combine(directory.Trim(), "python.exe");
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
                catch
                {
                }
            }

            var roots = new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "Python"),
                Path.GetPathRoot(Environment.SystemDirectory),
            };
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)
                    || !Directory.Exists(root))
                    continue;
                try
                {
                    foreach (var directory in Directory.GetDirectories(
                                 root,
                                 "Python*",
                                 SearchOption.TopDirectoryOnly)
                             .OrderByDescending(
                                 value => value,
                                 StringComparer.OrdinalIgnoreCase))
                    {
                        var candidate = Path.Combine(directory, "python.exe");
                        if (File.Exists(candidate))
                            return Path.GetFullPath(candidate);
                    }
                }
                catch
                {
                }
            }
            return "python.exe";
        }

        private static bool TryGetTrackedProcess(
            DingoCmsEditorHostIdentity identity,
            out Process process,
            bool clearStaleIdentity)
        {
            process = null;
            if (identity == null)
                return false;
            try
            {
                var candidate = Process.GetProcessById(identity.ProcessId);
                if (candidate.HasExited)
                {
                    candidate.Dispose();
                    if (clearStaleIdentity)
                    {
                        DingoCmsEditorHostIdentity.DeleteIfOwned(
                            HostStatePath,
                            identity.ProcessId,
                            identity.InstanceToken);
                    }
                    return false;
                }

                var executablePath = candidate.MainModule?.FileName;
                if (!PathsEqual(executablePath, identity.ExecutablePath)
                    || !TryGetProcessStartUtcTicks(
                        candidate,
                        out var processStartUtcTicks)
                    || processStartUtcTicks != identity.ProcessStartUtcTicks)
                {
                    candidate.Dispose();
                    return false;
                }

                process = candidate;
                return true;
            }
            catch (ArgumentException)
            {
                if (clearStaleIdentity)
                {
                    DingoCmsEditorHostIdentity.DeleteIfOwned(
                        HostStatePath,
                        identity.ProcessId,
                        identity.InstanceToken);
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static DingoCmsEditorHealthProbeResult ObserveHealth(
            DingoCmsEditorHostIdentity identity,
            string expectedBrokerFingerprint)
        {
            var key = identity.ProcessId + ":" + identity.ProcessStartUtcTicks
                      + ":" + identity.Port + ":" + identity.InstanceToken
                      + ":" + expectedBrokerFingerprint;
            var now = EditorApplication.timeSinceStartup;
            if (!string.Equals(key, _healthProbeKey, StringComparison.Ordinal))
                ResetHealthObservation(key);

            if (_healthProbeTask != null && _healthProbeTask.IsCompleted)
            {
                try
                {
                    _lastHealthResult =
                        _healthProbeTask.GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    _lastHealthResult = new DingoCmsEditorHealthProbeResult(
                        false,
                        exception.Message);
                }
                _healthProbeTask = null;
                _nextHealthProbeTime = now + HEALTH_PROBE_INTERVAL_SECONDS;
                if (_lastHealthResult.Healthy)
                {
                    _consecutiveHealthFailures = 0;
                    _unhealthySinceTime = 0d;
                    if (_healthySinceTime <= 0d)
                        _healthySinceTime = now;
                }
                else
                {
                    _healthySinceTime = 0d;
                    _consecutiveHealthFailures++;
                    if (_unhealthySinceTime <= 0d)
                        _unhealthySinceTime = now;
                    _lastError = _lastHealthResult.Error;
                }
            }

            if (_healthProbeTask == null && now >= _nextHealthProbeTime)
            {
                var port = identity.Port;
                var processId = identity.ProcessId;
                var instanceToken = identity.InstanceToken;
                var bearerToken = DingoCmsEditorServerSettings.Token;
                _healthProbeTask = Task.Run(() => ProbeHealth(
                    port,
                    processId,
                    instanceToken,
                    expectedBrokerFingerprint,
                    bearerToken));
            }
            return _lastHealthResult;
        }

        private static DingoCmsEditorHealthProbeResult ProbeHealth(
            int port,
            int processId,
            string instanceToken,
            string brokerFingerprint,
            string bearerToken)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(
                    $"http://127.0.0.1:{port}/health");
                request.Method = "GET";
                request.Proxy = null;
                request.KeepAlive = false;
                request.Timeout = 500;
                request.ReadWriteTimeout = 500;
                request.Headers[HttpRequestHeader.Authorization] =
                    "Bearer " + bearerToken;
                request.Headers["X-DingoCMS-Instance-Token"] = instanceToken;
                using var response = (HttpWebResponse)request.GetResponse();
                using var reader = new StreamReader(
                    response.GetResponseStream());
                var body = JObject.Parse(reader.ReadToEnd());
                var healthy = response.StatusCode == HttpStatusCode.OK
                              && body.Value<bool?>("ok") == true
                              && body.Value<int?>("processId") == processId
                              && body.Value<int?>("port") == port
                              && string.Equals(
                                  body.Value<string>("instanceToken"),
                                  instanceToken,
                                  StringComparison.Ordinal)
                              && string.Equals(
                                  body.Value<string>("brokerFingerprint"),
                                  brokerFingerprint,
                                  StringComparison.Ordinal);
                return new DingoCmsEditorHealthProbeResult(
                    healthy,
                    healthy ? null : "Standalone broker health identity mismatch.");
            }
            catch (Exception exception)
            {
                return new DingoCmsEditorHealthProbeResult(
                    false,
                    exception.Message);
            }
        }

        private static void TryMarkIdentityReady(
            DingoCmsEditorHostIdentity identity)
        {
            if (identity.Ready)
                return;
            try
            {
                DingoCmsEditorHostIdentity.WriteAtomic(
                    HostStatePath,
                    new DingoCmsEditorHostIdentity(
                        identity.ProcessId,
                        identity.Port,
                        identity.ProjectId,
                        identity.InstanceToken,
                        identity.ExecutablePath,
                        identity.ProcessStartUtcTicks,
                        ready: true,
                        brokerFingerprint: identity.BrokerFingerprint));
            }
            catch
            {
            }
        }

        private static void ResetHealthObservation(string key = null)
        {
            _healthProbeKey = key;
            _healthProbeTask = null;
            _lastHealthResult = null;
            _nextHealthProbeTime = 0d;
            _consecutiveHealthFailures = 0;
            _unhealthySinceTime = 0d;
            _healthySinceTime = 0d;
        }

        private static bool TryGetProcessStartUtcTicks(
            Process process,
            out long ticks)
        {
            ticks = 0;
            try
            {
                ticks = process.StartTime.ToUniversalTime().Ticks;
                return ticks > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void TryTerminateSpawnedProcess(Process process)
        {
            if (process == null)
                return;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch
            {
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left)
                || string.IsNullOrWhiteSpace(right))
                return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(left),
                    Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"")
                   + "\"";
        }

        private static void PollProcess()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now < _nextProcessPollTime)
                return;
            _nextProcessPollTime = now + PROCESS_POLL_INTERVAL_SECONDS;

            var state = ProcessState;
            var processId = ProcessId;
            if (state == DingoCmsDetachedProcessState.Running)
            {
                if (_healthySinceTime > 0d
                    && ShouldResetRestartBackoff(now - _healthySinceTime))
                {
                    ResetRestartBackoff();
                }
            }
            else if (state ==
                     DingoCmsDetachedProcessState.OwnershipVerificationPending
                     && DingoCmsEditorHostIdentity.TryRead(
                         HostStatePath,
                         out var identity)
                     && identity.Ready
                     && _unhealthySinceTime > 0d
                     && ShouldRecoverReadyHost(
                         _consecutiveHealthFailures,
                         now - _unhealthySinceTime))
            {
                if (TryGetTrackedProcess(
                        identity,
                        out var process,
                        clearStaleIdentity: false))
                {
                    using (process)
                    {
                        try
                        {
                            process.Kill();
                            process.WaitForExit(5000);
                        }
                        catch
                        {
                        }
                    }
                    DingoCmsEditorHostIdentity.DeleteIfOwned(
                        HostStatePath,
                        identity.ProcessId,
                        identity.InstanceToken);
                    ResetHealthObservation();
                    state = DingoCmsDetachedProcessState.Stopped;
                    processId = 0;
                    RegisterRestartFailure(
                        "The standalone DingoCMS server stopped responding and was restarted.");
                }
            }

            if (state == DingoCmsDetachedProcessState.Stopped
                && KeepRunning
                && IsRestartDue())
            {
                try
                {
                    StartCore();
                    state = ProcessState;
                    processId = ProcessId;
                }
                catch (Exception exception)
                {
                    RegisterRestartFailure(exception.Message);
                }
            }

            if (state != _lastObservedState
                || processId != _lastObservedProcessId)
            {
                _lastObservedState = state;
                _lastObservedProcessId = processId;
                NotifyStateChanged();
            }
        }

        private static void RegisterRestartFailure(string message)
        {
            _lastError = message;
            _nextRestartUtcTicks = DateTime.UtcNow.AddSeconds(
                    RestartDelaySecondsForAttempt(_restartAttempt))
                .Ticks;
            _restartAttempt++;
            PersistRestartBackoff();
            NotifyStateChanged();
        }

        private static bool IsRestartDue()
        {
            return _nextRestartUtcTicks <= 0
                   || DateTime.UtcNow.Ticks >= _nextRestartUtcTicks;
        }

        private static void RestoreRestartBackoff()
        {
            _restartAttempt = SessionState.GetInt(
                RestartAttemptSessionKey,
                0);
            var value = SessionState.GetString(
                NextRestartUtcTicksSessionKey,
                "0");
            long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _nextRestartUtcTicks);
        }

        private static void PersistRestartBackoff()
        {
            SessionState.SetInt(RestartAttemptSessionKey, _restartAttempt);
            SessionState.SetString(
                NextRestartUtcTicksSessionKey,
                _nextRestartUtcTicks.ToString(CultureInfo.InvariantCulture));
        }

        private static void ResetRestartBackoff()
        {
            _restartAttempt = 0;
            _nextRestartUtcTicks = 0;
            PersistRestartBackoff();
        }

        private static void NotifyStateChanged()
        {
            StateChanged?.Invoke();
        }
    }

    internal sealed class DingoCmsEditorHealthProbeResult
    {
        public readonly bool Healthy;
        public readonly string Error;

        public DingoCmsEditorHealthProbeResult(bool healthy, string error)
        {
            Healthy = healthy;
            Error = error;
        }
    }
}
#endif
