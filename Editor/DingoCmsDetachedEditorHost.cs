#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using DingoGameObjectsCMSEditorServer.Runtime;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;

namespace DingoGameObjectsCMSEditorServer.Editor
{
    public enum DingoCmsEditorHostMode
    {
        DetachedProcess = 0,
        UnityProcess = 1,
    }

    public enum DingoCmsDetachedProcessState
    {
        Stopped = 0,
        Running = 1,
        OwnershipVerificationPending = 2,
        BuildRequired = 3,
    }

    [InitializeOnLoad]
    public static class DingoCmsDetachedEditorHost
    {
        private const string LEGACY_PREFS_PREFIX =
            "DingoCMS.EditorServer.Detached.";
        private const double PROCESS_POLL_INTERVAL_SECONDS = 1d;
        private const double HEALTH_PROBE_INTERVAL_SECONDS = 1d;
        private const double STARTUP_HEALTH_TIMEOUT_SECONDS = 15d;
        private const double READY_UNHEALTHY_TIMEOUT_SECONDS = 15d;
        private const int READY_UNHEALTHY_FAILURE_COUNT = 3;
        private const double HEALTH_STABILITY_RESET_SECONDS = 30d;
        private const double BUILD_VALIDATION_RETRY_SECONDS = 2d;
        private static readonly double[] RESTART_DELAYS_SECONDS =
            { 0d, 1d, 3d, 5d, 10d, 30d };

        private static string _lastError;
        private static double _nextProcessPollTime;
        private static DingoCmsDetachedProcessState _lastObservedState;
        private static int _lastObservedProcessId;
        private static int _restartAttempt;
        private static long _nextRestartUtcTicks;
        private static bool _startInProgress;
        private static bool _buildInProgress;
        private static string _healthProbeKey;
        private static Task<DingoCmsEditorHealthProbeResult> _healthProbeTask;
        private static bool _hasHealthResult;
        private static bool _lastHealthSucceeded;
        private static bool _lastHealthIdentitySucceeded;
        private static bool _lastHealthBuildRequired;
        private static string _lastHealthReportedBuildFingerprint;
        private static int _consecutiveHealthFailures;
        private static double _unhealthySinceTime;
        private static long _healthySinceUtcTicks;
        private static double _nextHealthProbeTime;
        private static bool _buildValidationInitialized;
        private static string _currentBuildFingerprint;
        private static string _buildValidationError;
        private static bool _buildValidationRetryableFailure;
        private static double _nextBuildValidationRetryTime;
        private static string _buildValidationSceneConfiguration;

        public static event Action StateChanged;

        public static DingoCmsEditorHostMode Mode
        {
            get => DingoCmsEditorHostMode.DetachedProcess;
            set
            {
                if (value == DingoCmsEditorHostMode.DetachedProcess)
                    return;
                throw new NotSupportedException(
                    "DingoCMS no longer hosts its MCP listener inside Unity. "
                    + "The detached process is required for domain-reload "
                    + "stability.");
            }
        }

        public static bool KeepRunning
        {
            get => EditorPrefs.GetBool(KeepRunningPrefsKey, false);
            set
            {
                var wasEnabled = EditorPrefs.GetBool(
                    KeepRunningPrefsKey,
                    false);
                EditorPrefs.SetBool(KeepRunningPrefsKey, value);
                if (value && !wasEnabled)
                {
                    ResetRestartBackoff();
                }
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
                        "Stop the detached DingoCMS host before changing "
                        + "its executable.");
                }

                var normalized = string.IsNullOrWhiteSpace(value)
                    ? DefaultExecutablePath
                    : Path.GetFullPath(value.Trim());
                EditorPrefs.SetString(
                    ExecutablePrefsKey,
                    normalized);
                InvalidateBuildValidation();
                _lastError = null;
                NotifyStateChanged();
            }
        }

        public static string DefaultExecutablePath => Path.Combine(
            ProjectRoot,
            "Builds",
            "DingoCmsAuthoringHost",
            PlayerSettings.productName + ".exe");

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

        public static string BuildMetadataPath =>
            DingoCmsEditorHostBuildMetadataUtils.MetadataPath(
                ExecutablePath);

        public static string ProjectId => Hash128.Compute(
                ProjectRoot.Replace('\\', '/').ToLowerInvariant())
            .ToString();

        public static DingoCmsDetachedProcessState ProcessState
        {
            get
            {
                if (!TryReadIdentity(out var identity))
                {
                    // An atomic state-file replacement can still be briefly
                    // unreadable because of antivirus, indexing, or another
                    // Windows file handle. Treat an existing but unreadable
                    // identity as retained ownership. Starting a replacement
                    // here could orphan the live host or create a duplicate
                    // listener on the same port.
                    if (File.Exists(HostStatePath))
                    {
                        return DingoCmsDetachedProcessState
                            .OwnershipVerificationPending;
                    }
                    ResetHealthObservation();
                    ClearStableHealthyObservation();
                    return DingoCmsDetachedProcessState.Stopped;
                }
                if (TryGetTrackedProcess(
                        identity,
                        out var process,
                        clearStaleIdentity: true))
                {
                    using (process)
                    {
                        if (!TryGetProcessStartUtcTicks(
                                process,
                                out var processStartUtcTicks))
                        {
                            return DingoCmsDetachedProcessState
                                .OwnershipVerificationPending;
                        }
                        var buildIsCurrent =
                            TryGetCurrentBuildFingerprint(
                                refresh: false,
                                out var currentBuildFingerprint,
                                out var buildError);
                        var expectedHealthFingerprint = buildIsCurrent
                            ? currentBuildFingerprint
                            : identity.BuildFingerprint;
                        var healthSucceeded = ObserveHealth(
                            identity,
                            processStartUtcTicks,
                            expectedHealthFingerprint);
                        if ((identity.ProcessStartUtcTicks <= 0
                             || string.IsNullOrWhiteSpace(
                                 identity.BuildFingerprint))
                            && !TryUpgradeLegacyIdentityFromHealth(
                                identity,
                                processStartUtcTicks,
                                expectedHealthFingerprint,
                                out identity))
                        {
                            return DingoCmsDetachedProcessState
                                .OwnershipVerificationPending;
                        }
                        if (!buildIsCurrent
                            || !string.Equals(
                                identity.BuildFingerprint,
                                currentBuildFingerprint,
                                StringComparison.Ordinal))
                        {
                            _lastError = buildError
                                         ?? "The running detached DingoCMS "
                                         + "host was built from older source. "
                                         + "Stop it and rebuild the host.";
                            return DingoCmsDetachedProcessState.BuildRequired;
                        }
                        if (healthSucceeded)
                        {
                            TryMarkIdentityReady(
                                identity,
                                processStartUtcTicks);
                            return DingoCmsDetachedProcessState.Running;
                        }
                        if (_lastHealthBuildRequired)
                        {
                            _lastError = "The detached DingoCMS health "
                                         + "fingerprint does not match its "
                                         + "recorded build. Stop it and "
                                         + "rebuild the host.";
                            return DingoCmsDetachedProcessState.BuildRequired;
                        }
                        return DingoCmsDetachedProcessState
                            .OwnershipVerificationPending;
                    }
                }
                if (TryReadIdentity(out _) || File.Exists(HostStatePath))
                {
                    return DingoCmsDetachedProcessState
                        .OwnershipVerificationPending;
                }
                ResetHealthObservation();
                ClearStableHealthyObservation();
                return DingoCmsDetachedProcessState.Stopped;
            }
        }

        public static bool IsRunning =>
            ProcessState == DingoCmsDetachedProcessState.Running;

        public static bool HasRetainedIdentity =>
            RetainedProcessId > 0 || File.Exists(HostStatePath);

        public static int ProcessId
        {
            get
            {
                return RetainedProcessId;
            }
        }

        public static string LastError => _lastError;

        public static bool BuildIsCurrent => TryGetCurrentBuildFingerprint(
            refresh: false,
            out _,
            out _);

        public static string BuildValidationError
        {
            get
            {
                TryGetCurrentBuildFingerprint(
                    refresh: false,
                    out _,
                    out var error);
                return error;
            }
        }

        static DingoCmsDetachedEditorHost()
        {
            RestoreRestartBackoff();
            _lastObservedState = ProcessState;
            _lastObservedProcessId = ProcessId;
            CompilationPipeline.compilationFinished +=
                OnCompilationFinished;
            EditorApplication.projectChanged += OnProjectChanged;
            EditorBuildSettings.sceneListChanged += OnBuildSceneListChanged;
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
            var processState = ProcessState;
            if (processState == DingoCmsDetachedProcessState.Running)
            {
                return;
            }
            if (processState == DingoCmsDetachedProcessState.BuildRequired)
            {
                throw new InvalidOperationException(
                    _lastError
                    ?? "Stop and rebuild the detached DingoCMS host.");
            }
            var retainedProcessId = RetainedProcessId;
            if (processState == DingoCmsDetachedProcessState
                    .OwnershipVerificationPending
                || HasRetainedIdentity)
            {
                throw new InvalidOperationException(
                    "The detached DingoCMS process still has retained "
                    + (retainedProcessId > 0
                        ? $"identity (PID {retainedProcessId})"
                        : "an identity file")
                    + " but could not be "
                    + "verified during this poll. Wait for the next poll or "
                    + "stop that exact process before starting another host.");
            }
            if (DingoCmsEditorServerEditorHost.IsRunning)
            {
                throw new InvalidOperationException(
                    "Stop the Unity-process DingoCMS host before starting "
                    + "the detached host.");
            }

            var executablePath = ExecutablePath;
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException(
                    "Detached host player is missing. Build it from the "
                    + "DingoCMS window or select an existing player build.",
                    executablePath);
            }
            if (!TryGetCurrentBuildFingerprint(
                    refresh: false,
                    out var buildFingerprint,
                    out var buildError))
            {
                throw new InvalidOperationException(buildError);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            Directory.CreateDirectory(Path.GetDirectoryName(HostStatePath));
            var token = DingoCmsEditorServerEditorHost.EnsureToken();
            var instanceToken = Guid.NewGuid().ToString("N")
                                + Guid.NewGuid().ToString("N");
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath),
                Arguments = BuildArguments(
                    DingoCmsEditorServerEditorHost.Port,
                    LogPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.EnvironmentVariables[
                DingoCmsEditorServerOptions.TOKEN_ENVIRONMENT_VARIABLE] =
                token;
            startInfo.EnvironmentVariables[
                DingoCmsEditorServerOptions
                    .INSTANCE_TOKEN_ENVIRONMENT_VARIABLE] = instanceToken;
            startInfo.EnvironmentVariables[
                DingoCmsEditorServerOptions
                    .HOST_STATE_FILE_ENVIRONMENT_VARIABLE] = HostStatePath;
            startInfo.EnvironmentVariables[
                DingoCmsEditorServerOptions.PROJECT_ID_ENVIRONMENT_VARIABLE] =
                ProjectId;
            startInfo.EnvironmentVariables[
                DingoCmsEditorServerOptions
                    .BUILD_FINGERPRINT_ENVIRONMENT_VARIABLE] =
                buildFingerprint;

            _startInProgress = true;
            Process spawnedProcess = null;
            var spawnedProcessId = 0;
            try
            {
                spawnedProcess = Process.Start(startInfo)
                                 ?? throw new InvalidOperationException(
                                     "The detached DingoCMS process did "
                                     + "not start.");
                spawnedProcessId = spawnedProcess.Id;
                if (!TryGetProcessStartUtcTicks(
                        spawnedProcess,
                        out var spawnedProcessStartUtcTicks))
                {
                    throw new InvalidOperationException(
                        "The detached DingoCMS process started but its "
                        + "creation time could not be verified.");
                }
                DingoCmsEditorHostIdentity.WriteAtomic(
                    HostStatePath,
                    new DingoCmsEditorHostIdentity(
                        spawnedProcessId,
                        DingoCmsEditorServerEditorHost.Port,
                        ProjectId,
                        instanceToken,
                        executablePath,
                        processStartUtcTicks: spawnedProcessStartUtcTicks,
                        buildFingerprint: buildFingerprint));
                EditorPrefs.SetInt(
                    BootstrapOpenedProcessIdPrefsKey,
                    0);
                _lastObservedState = DingoCmsDetachedProcessState
                    .OwnershipVerificationPending;
                _lastObservedProcessId = spawnedProcessId;
                _lastError = null;
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                TryTerminateSpawnedProcess(spawnedProcess);
                DingoCmsEditorHostIdentity.DeleteIfOwned(
                    HostStatePath,
                    spawnedProcessId,
                    instanceToken);
                NotifyStateChanged();
                throw;
            }
            finally
            {
                spawnedProcess?.Dispose();
                _startInProgress = false;
            }

            NotifyStateChanged();
        }

        public static void Stop()
        {
            KeepRunning = false;
            using var process = TakeTrackedProcess(requireHealthy: false);
            var retainedProcessId = RetainedProcessId;
            if (!CanCompleteStop(
                    processVerified: process != null,
                    retainedIdentityExists: HasRetainedIdentity))
            {
                throw new InvalidOperationException(
                    "Detached DingoCMS ownership verification is pending for "
                    + (retainedProcessId > 0
                        ? $"PID {retainedProcessId}"
                        : "an unreadable identity file")
                    + ". Its identity was retained, "
                    + "so Stop cannot orphan it. Wait for verification or "
                    + "terminate that exact process outside Unity.");
            }
            if (process != null)
            {
                process.Kill();
                if (!process.WaitForExit(5000))
                {
                    throw new InvalidOperationException(
                        $"Detached DingoCMS process {process.Id} did not "
                        + "exit within five seconds.");
                }
            }

            if (TryReadIdentity(out var identity))
            {
                DingoCmsEditorHostIdentity.DeleteIfOwned(
                    HostStatePath,
                    identity.ProcessId,
                    identity.InstanceToken);
            }
            EditorPrefs.DeleteKey(
                BootstrapOpenedProcessIdPrefsKey);
            _lastObservedState = DingoCmsDetachedProcessState.Stopped;
            _lastObservedProcessId = 0;
            _lastError = null;
            ResetHealthObservation();
            ClearStableHealthyObservation();
            ResetRestartBackoff();
            NotifyStateChanged();
        }

        public static void OpenWebEditor()
        {
            using var process = TakeTrackedProcess(requireHealthy: true);
            if (process == null)
            {
                throw new InvalidOperationException(
                    "Start the detached DingoCMS host before opening its "
                    + "Web editor.");
            }

            var openedForProcess = EditorPrefs.GetInt(
                BootstrapOpenedProcessIdPrefsKey,
                0);
            if (openedForProcess == process.Id)
            {
                UnityEngine.Application.OpenURL(
                    $"http://127.0.0.1:"
                    + $"{DingoCmsEditorServerEditorHost.Port}/");
                return;
            }

            var bootstrapUrl = TryReadLatestBootstrapUrl(LogPath);
            if (string.IsNullOrWhiteSpace(bootstrapUrl))
            {
                throw new InvalidOperationException(
                    "The detached host is still starting. Wait until its "
                    + "log contains the Web bootstrap URL, then try again.");
            }

            EditorPrefs.SetInt(
                BootstrapOpenedProcessIdPrefsKey,
                process.Id);
            UnityEngine.Application.OpenURL(bootstrapUrl);
        }

        public static void BuildPlayer()
        {
            if (_buildInProgress)
                return;
            if (ProcessState != DingoCmsDetachedProcessState.Stopped)
            {
                throw new InvalidOperationException(
                    "Stop the detached DingoCMS host and clear its retained "
                    + "ownership before rebuilding it.");
            }

            var scenes = GetEnabledBuildScenePaths();
            if (scenes.Length == 0)
            {
                throw new InvalidOperationException(
                    "At least one enabled scene is required to build the "
                    + "detached authoring host.");
            }

            _buildInProgress = true;
            try
            {
                var outputPath = ExecutablePath;
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                if (File.Exists(BuildMetadataPath))
                {
                    File.Delete(BuildMetadataPath);
                }
                InvalidateBuildValidation();
                var report = BuildPipeline.BuildPlayer(
                    new BuildPlayerOptions
                    {
                        scenes = scenes,
                        locationPathName = outputPath,
                        target = BuildTarget.StandaloneWindows64,
                        options = BuildOptions.None,
                    });
                if (report.summary.result != BuildResult.Succeeded)
                {
                    throw new InvalidOperationException(
                        "Detached DingoCMS host build failed: "
                        + report.summary.result);
                }

                var sourceFingerprint =
                    DingoCmsEditorHostBuildMetadataUtils
                        .ComputeSourceFingerprint(
                            ProjectRoot,
                            scenes,
                            Array.Empty<string>(),
                            GetBuildFingerprintHashedInputs(scenes));
                DingoCmsEditorHostBuildMetadataUtils.WriteAtomic(
                    BuildMetadataPath,
                    new DingoCmsEditorHostBuildMetadata(
                        sourceFingerprint,
                        DateTime.UtcNow.Ticks));
                InvalidateBuildValidation();
                _lastError = null;
                NotifyStateChanged();
            }
            finally
            {
                _buildInProgress = false;
            }
        }

        internal static string BuildArguments(int port, string logPath)
        {
            return "-batchmode -nographics -logFile "
                   + QuoteArgument(logPath)
                   + " "
                   + DingoCmsEditorServerOptions.ENABLE_ARGUMENT
                   + " "
                   + DingoCmsEditorServerOptions.AUTHORING_ONLY_ARGUMENT
                   + " "
                   + DingoCmsEditorServerOptions.PORT_ARGUMENT
                   + "="
                   + port;
        }

        internal static string TryReadLatestBootstrapUrl(string logPath)
        {
            if (!File.Exists(logPath))
                return null;

            const string marker = "Web (one-time): ";
            string latest = null;
            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var markerIndex = line.IndexOf(
                    marker,
                    StringComparison.Ordinal);
                if (markerIndex >= 0)
                {
                    latest = line.Substring(markerIndex + marker.Length)
                        .Trim();
                }
            }
            return latest;
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

        internal static bool TryCreateHealthVerifiedIdentity(
            DingoCmsEditorHostIdentity identity,
            long observedProcessStartUtcTicks,
            bool identityMatches,
            string reportedBuildFingerprint,
            out DingoCmsEditorHostIdentity replacement)
        {
            replacement = null;
            if (identity == null
                || !identityMatches
                || observedProcessStartUtcTicks <= 0)
            {
                return false;
            }

            var normalizedReportedFingerprint =
                reportedBuildFingerprint?.Trim();
            replacement = new DingoCmsEditorHostIdentity(
                identity.ProcessId,
                identity.Port,
                identity.ProjectId,
                identity.InstanceToken,
                identity.ExecutablePath,
                observedProcessStartUtcTicks,
                ready: identity.Ready,
                buildFingerprint: string.IsNullOrWhiteSpace(
                    normalizedReportedFingerprint)
                    ? identity.BuildFingerprint
                    : normalizedReportedFingerprint);
            return true;
        }

        internal static bool CanControlProcessAtCreationTime(
            DingoCmsEditorHostIdentity identity,
            long observedProcessStartUtcTicks)
        {
            return identity != null
                   && identity.ProcessStartUtcTicks > 0
                   && observedProcessStartUtcTicks > 0
                   && identity.ProcessStartUtcTicks
                   == observedProcessStartUtcTicks;
        }

        internal static bool ShouldRefreshBuildValidation(
            bool forceRefresh,
            bool initialized,
            bool previousFailureIsRetryable,
            double now,
            double retryAt)
        {
            return forceRefresh
                   || !initialized
                   || (previousFailureIsRetryable && now >= retryAt);
        }

        internal static double RestartDelaySecondsForAttempt(int attempt)
        {
            var delayIndex = Math.Min(
                Math.Max(attempt, 0),
                RESTART_DELAYS_SECONDS.Length - 1);
            return RESTART_DELAYS_SECONDS[delayIndex];
        }

        internal static bool StartupHealthTimedOut(
            DateTime processStartUtc,
            DateTime utcNow)
        {
            return utcNow >= processStartUtc.AddSeconds(
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

        internal static bool ShouldHashCompiledReferenceContent(
            string referencePath,
            string assetsRoot,
            string unityContentsRoot)
        {
            var fullReferencePath = Path.GetFullPath(referencePath);
            return !IsPathUnderRoot(fullReferencePath, assetsRoot)
                   && !IsPathUnderRoot(
                       fullReferencePath,
                       unityContentsRoot);
        }

        private static bool IsPathUnderRoot(string path, string root)
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root).TrimEnd(
                               Path.DirectorySeparatorChar,
                               Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(
                fullRoot,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string ProjectRoot => Path.GetFullPath(
            Path.Combine(UnityEngine.Application.dataPath, ".."));

        private static string ProjectPrefsPrefix =>
            LEGACY_PREFS_PREFIX + ProjectId + ".";
        private static string ExecutablePrefsKey =>
            ProjectPrefsPrefix + "Executable";
        private static string KeepRunningPrefsKey =>
            ProjectPrefsPrefix + "KeepRunning";
        private static string BootstrapOpenedProcessIdPrefsKey =>
            ProjectPrefsPrefix + "BootstrapOpenedProcessId";
        private static string RestartAttemptSessionKey =>
            ProjectPrefsPrefix + "RestartAttempt";
        private static string NextRestartUtcTicksSessionKey =>
            ProjectPrefsPrefix + "NextRestartUtcTicks";
        private static string HealthyProbeSessionKey =>
            ProjectPrefsPrefix + "HealthyProbeKey";
        private static string HealthySinceUtcTicksSessionKey =>
            ProjectPrefsPrefix + "HealthySinceUtcTicks";

        private static int RetainedProcessId => TryReadIdentity(
            out var identity)
            ? identity.ProcessId
            : 0;

        private static bool TryGetCurrentBuildFingerprint(
            bool refresh,
            out string fingerprint,
            out string error)
        {
            var now = EditorApplication.timeSinceStartup;
            var enabledScenePaths = GetEnabledBuildScenePaths();
            var sceneConfiguration = string.Join("\n", enabledScenePaths);
            var sceneConfigurationChanged = !string.Equals(
                _buildValidationSceneConfiguration,
                sceneConfiguration,
                StringComparison.Ordinal);
            if (ShouldRefreshBuildValidation(
                    refresh || sceneConfigurationChanged,
                    _buildValidationInitialized,
                    _buildValidationRetryableFailure,
                    now,
                    _nextBuildValidationRetryTime))
            {
                _buildValidationInitialized = true;
                _currentBuildFingerprint = null;
                _buildValidationError = null;
                _buildValidationRetryableFailure = false;
                _buildValidationSceneConfiguration = sceneConfiguration;
                try
                {
                    var executablePath = ExecutablePath;
                    if (!File.Exists(executablePath))
                    {
                        _buildValidationError =
                            "Detached host player is missing. Build it before "
                            + "starting DingoCMS.";
                        _buildValidationRetryableFailure = true;
                    }
                    else if (!DingoCmsEditorHostBuildMetadataUtils.TryRead(
                                 DingoCmsEditorHostBuildMetadataUtils
                                     .MetadataPath(executablePath),
                                 out var metadata))
                    {
                        _buildValidationError =
                            "Detached host build metadata is missing or "
                            + "invalid. Rebuild the host once.";
                        _buildValidationRetryableFailure = true;
                    }
                    else
                    {
                        var sourceFingerprint =
                            DingoCmsEditorHostBuildMetadataUtils
                                .ComputeSourceFingerprint(
                                    ProjectRoot,
                                    enabledScenePaths,
                                    Array.Empty<string>(),
                                    GetBuildFingerprintHashedInputs(
                                        enabledScenePaths));
                        if (!string.Equals(
                                sourceFingerprint,
                                metadata.SourceFingerprint,
                                StringComparison.Ordinal))
                        {
                            _buildValidationError =
                                "DingoCMS player source changed after the "
                                + "detached host was built. Stop the host and "
                                + "build it again.";
                        }
                        else
                        {
                            _currentBuildFingerprint = sourceFingerprint;
                        }
                    }
                }
                catch (Exception exception)
                {
                    _buildValidationError =
                        "Detached host build could not be verified: "
                        + exception.Message;
                    _buildValidationRetryableFailure = true;
                }
                _nextBuildValidationRetryTime =
                    _buildValidationRetryableFailure
                        ? now + BUILD_VALIDATION_RETRY_SECONDS
                        : double.PositiveInfinity;
            }

            fingerprint = _currentBuildFingerprint;
            error = _buildValidationError;
            return !string.IsNullOrWhiteSpace(fingerprint);
        }

        private static void InvalidateBuildValidation()
        {
            _buildValidationInitialized = false;
            _currentBuildFingerprint = null;
            _buildValidationError = null;
            _buildValidationRetryableFailure = false;
            _nextBuildValidationRetryTime = 0d;
            _buildValidationSceneConfiguration = null;
        }

        private static string[] GetEnabledBuildScenePaths()
        {
            return EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();
        }

        private static DingoCmsEditorHostFingerprintInput[]
            GetBuildFingerprintHashedInputs(
            string[] enabledScenePaths)
        {
            var inputs = new List<DingoCmsEditorHostFingerprintInput>();
            var assetPaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var compiledReferencePaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var playerAssemblies = CompilationPipeline.GetAssemblies(
                AssembliesType.Player);
            foreach (var assembly in playerAssemblies)
            {
                var topology = new StringBuilder();
                topology.AppendLine(assembly.name ?? string.Empty);
                topology.AppendLine(assembly.rootNamespace ?? string.Empty);
                topology.AppendLine(assembly.flags.ToString());
                AppendSorted(topology, "define:", assembly.defines);
                AppendSorted(topology, "source:", assembly.sourceFiles);
                AppendSorted(
                    topology,
                    "assembly-reference:",
                    assembly.assemblyReferences?.Select(
                        reference => reference.name));
                AppendSorted(
                    topology,
                    "compiled-reference:",
                    assembly.compiledAssemblyReferences);
                inputs.Add(new DingoCmsEditorHostFingerprintInput(
                    "assembly-topology/" + assembly.name,
                    Hash128.Compute(topology.ToString()).ToString()));
                foreach (var sourceFile in assembly.sourceFiles
                             ?? Array.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(sourceFile))
                        assetPaths.Add(sourceFile);
                }
                foreach (var referencePath in
                         assembly.compiledAssemblyReferences
                         ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(referencePath))
                        continue;
                    var fullReferencePath = Path.GetFullPath(referencePath);
                    if (ShouldHashCompiledReferenceContent(
                            fullReferencePath,
                            UnityEngine.Application.dataPath,
                            EditorApplication.applicationContentsPath))
                    {
                        compiledReferencePaths.Add(fullReferencePath);
                    }
                }
            }
            foreach (var scenePath in enabledScenePaths
                         ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(scenePath))
                    assetPaths.Add(scenePath);
            }
            AddFingerprintAssetsUnderRoots(
                assetPaths,
                new[]
                {
                    "Assets/AppSDK/DingoGameObjectsCMS",
                    "Assets/AppSDK/DingoGameObjectsCMSEditorServer",
                },
                dllOnly: false);
            AddFingerprintAssetsUnderRoots(
                assetPaths,
                new[] { "Assets" },
                dllOnly: true);
            foreach (var referencePath in compiledReferencePaths.OrderBy(
                         path => path,
                         StringComparer.OrdinalIgnoreCase))
            {
                inputs.Add(new DingoCmsEditorHostFingerprintInput(
                    "compiled-reference-content/"
                    + referencePath.Replace('\\', '/'),
                    DingoCmsEditorHostBuildMetadataUtils
                        .ComputeFileContentHash(referencePath)));
            }
            foreach (var assetPath in assetPaths.OrderBy(
                         path => path,
                         StringComparer.OrdinalIgnoreCase))
            {
                inputs.Add(new DingoCmsEditorHostFingerprintInput(
                    assetPath,
                    AssetDatabase.GetAssetDependencyHash(assetPath)
                        .ToString()));
            }
            return inputs.ToArray();
        }

        private static void AddFingerprintAssetsUnderRoots(
            ISet<string> assetPaths,
            string[] searchRoots,
            bool dllOnly)
        {
            foreach (var guid in AssetDatabase.FindAssets(
                         string.Empty,
                         searchRoots))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(assetPath)
                    || (dllOnly && !string.Equals(
                        Path.GetExtension(assetPath),
                        ".dll",
                        StringComparison.OrdinalIgnoreCase))
                    || !DingoCmsEditorHostBuildMetadataUtils
                        .MayAffectBuildFingerprint(assetPath))
                {
                    continue;
                }
                assetPaths.Add(assetPath);
            }
        }

        private static void AppendSorted(
            StringBuilder target,
            string prefix,
            IEnumerable<string> values)
        {
            foreach (var value in (values ?? Array.Empty<string>())
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .OrderBy(
                             value => value,
                             StringComparer.OrdinalIgnoreCase))
            {
                target.Append(prefix);
                target.AppendLine(value.Replace('\\', '/'));
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"")
                + "\"";
        }

        private static Process TakeTrackedProcess(bool requireHealthy)
        {
            if (!TryReadIdentity(out var identity)
                || !TryGetTrackedProcess(
                    identity,
                    out var process,
                    clearStaleIdentity: true))
            {
                return null;
            }

            if (!TryGetProcessStartUtcTicks(
                    process,
                    out var processStartUtcTicks))
            {
                process.Dispose();
                return null;
            }

            TryGetCurrentBuildFingerprint(
                refresh: false,
                out var currentBuildFingerprint,
                out _);
            var expectedHealthFingerprint =
                string.IsNullOrWhiteSpace(currentBuildFingerprint)
                    ? identity.BuildFingerprint
                    : currentBuildFingerprint;
            if (identity.ProcessStartUtcTicks <= 0)
            {
                ObserveHealth(
                    identity,
                    processStartUtcTicks,
                    expectedHealthFingerprint);
                if (!TryUpgradeLegacyIdentityFromHealth(
                        identity,
                        processStartUtcTicks,
                        expectedHealthFingerprint,
                        out identity))
                {
                    process.Dispose();
                    return null;
                }
            }
            else if (!CanControlProcessAtCreationTime(
                         identity,
                         processStartUtcTicks))
            {
                process.Dispose();
                return null;
            }

            if (!requireHealthy
                || ObserveHealth(
                    identity,
                    processStartUtcTicks,
                    expectedHealthFingerprint))
            {
                return process;
            }
            process.Dispose();
            return null;
        }

        private static bool TryGetTrackedProcess(
            DingoCmsEditorHostIdentity identity,
            out Process process,
            bool clearStaleIdentity)
        {
            process = null;
            var processId = identity?.ProcessId ?? 0;
            if (processId <= 0)
                return false;

            Process candidate;
            try
            {
                candidate = Process.GetProcessById(processId);
            }
            catch (ArgumentException)
            {
                if (clearStaleIdentity)
                {
                    DingoCmsEditorHostIdentity.DeleteIfOwned(
                        HostStatePath,
                        processId,
                        identity.InstanceToken);
                }
                return false;
            }
            catch
            {
                // A transient OS/process-table read failure is not proof that
                // the detached host died. Keep its identity so Start cannot
                // create an untracked duplicate on the same port.
                return false;
            }

            try
            {
                if (candidate.HasExited)
                {
                    candidate.Dispose();
                    if (clearStaleIdentity)
                    {
                        DingoCmsEditorHostIdentity.DeleteIfOwned(
                            HostStatePath,
                            processId,
                            identity.InstanceToken);
                    }
                    return false;
                }

                string executable;
                try
                {
                    executable = candidate.MainModule?.FileName;
                }
                catch
                {
                    candidate.Dispose();
                    // MainModule can be temporarily unavailable during a
                    // domain reload or process startup. Preserve the PID and
                    // retry on the next poll instead of orphaning the host.
                    return false;
                }

                if (string.IsNullOrWhiteSpace(executable))
                {
                    candidate.Dispose();
                    // A missing module path is not a confirmed mismatch.
                    // Keep ownership until Windows returns an identity that
                    // can be compared safely.
                    return false;
                }

                if (!TryGetProcessStartUtcTicks(
                        candidate,
                        out var processStartUtcTicks))
                {
                    candidate.Dispose();
                    // Creation time is part of durable ownership. Preserve
                    // identity and retry instead of making a destructive
                    // decision without it.
                    return false;
                }

                if (!PathsEqual(executable, identity.ExecutablePath)
                    || !string.Equals(
                        identity.ProjectId,
                        ProjectId,
                        StringComparison.Ordinal)
                    || identity.Port != DingoCmsEditorServerEditorHost.Port
                    || (identity.ProcessStartUtcTicks > 0
                        && identity.ProcessStartUtcTicks
                        != processStartUtcTicks))
                {
                    candidate.Dispose();
                    if (clearStaleIdentity)
                    {
                        DingoCmsEditorHostIdentity.DeleteIfOwned(
                            HostStatePath,
                            processId,
                            identity.InstanceToken);
                    }
                    return false;
                }

                process = candidate;
                return true;
            }
            catch
            {
                candidate.Dispose();
                // Preserve identity on any unconfirmed read failure. Only a
                // missing PID, confirmed exit, or executable mismatch clears
                // it.
                return false;
            }
        }

        private static bool TryReadIdentity(
            out DingoCmsEditorHostIdentity identity)
        {
            return DingoCmsEditorHostIdentity.TryRead(
                HostStatePath,
                out identity);
        }

        private static bool ObserveHealth(
            DingoCmsEditorHostIdentity identity,
            long processStartUtcTicks,
            string expectedBuildFingerprint)
        {
            var key = BuildHealthProbeKey(
                identity,
                processStartUtcTicks,
                expectedBuildFingerprint);
            var now = EditorApplication.timeSinceStartup;
            if (!string.Equals(
                    _healthProbeKey,
                    key,
                    StringComparison.Ordinal))
            {
                ResetHealthObservation(key);
            }

            if (_healthProbeTask != null && _healthProbeTask.IsCompleted)
            {
                DingoCmsEditorHealthProbeResult result;
                try
                {
                    result = _healthProbeTask.GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    result = new DingoCmsEditorHealthProbeResult(
                        false,
                        exception.Message);
                }

                _healthProbeTask = null;
                _hasHealthResult = true;
                _lastHealthSucceeded = result.Healthy;
                _lastHealthIdentitySucceeded = result.IdentityMatches;
                _lastHealthBuildRequired = result.BuildRequired;
                _lastHealthReportedBuildFingerprint =
                    result.ReportedBuildFingerprint;
                _nextHealthProbeTime = now + HEALTH_PROBE_INTERVAL_SECONDS;
                if (result.Healthy)
                {
                    _consecutiveHealthFailures = 0;
                    _unhealthySinceTime = 0d;
                    MarkHealthyObservation(key);
                }
                else
                {
                    ClearStableHealthyObservation();
                    _consecutiveHealthFailures++;
                    if (_unhealthySinceTime <= 0d)
                        _unhealthySinceTime = now;
                }
            }

            if (_healthProbeTask == null && now >= _nextHealthProbeTime)
            {
                var port = identity.Port;
                var processId = identity.ProcessId;
                var instanceToken = identity.InstanceToken;
                var bearerToken = DingoCmsEditorServerEditorHost.Token;
                _healthProbeTask = Task.Run(() => ProbeHealth(
                    port,
                    processId,
                    instanceToken,
                    expectedBuildFingerprint,
                    bearerToken));
            }

            return _hasHealthResult && _lastHealthSucceeded;
        }

        private static DingoCmsEditorHealthProbeResult ProbeHealth(
            int port,
            int processId,
            string instanceToken,
            string buildFingerprint,
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
                request.Headers["X-DingoCMS-Instance-Token"] =
                    instanceToken;
                using var response = (HttpWebResponse)request.GetResponse();
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return new DingoCmsEditorHealthProbeResult(
                        false,
                        $"HTTP {(int)response.StatusCode}");
                }
                using var reader = new StreamReader(response.GetResponseStream());
                var body = JObject.Parse(reader.ReadToEnd());
                var identityMatches = body.Value<bool?>("ok") == true
                                      && body.Value<int?>("processId")
                                      == processId
                                      && body.Value<int?>("port") == port
                                      && string.Equals(
                                          body.Value<string>("instanceToken"),
                                          instanceToken,
                                          StringComparison.Ordinal);
                var buildMatches = identityMatches
                                   && !string.IsNullOrWhiteSpace(
                                       buildFingerprint)
                                   && string.Equals(
                                       body.Value<string>(
                                           "buildFingerprint"),
                                       buildFingerprint,
                                       StringComparison.Ordinal);
                var healthy = identityMatches && buildMatches;
                return new DingoCmsEditorHealthProbeResult(
                    healthy,
                    healthy
                        ? null
                        : identityMatches
                            ? "Health build fingerprint mismatch."
                            : "Health identity mismatch.",
                    identityMatches: identityMatches,
                    buildRequired: identityMatches && !buildMatches,
                    reportedBuildFingerprint: body.Value<string>(
                        "buildFingerprint"));
            }
            catch (Exception exception)
            {
                return new DingoCmsEditorHealthProbeResult(
                    false,
                    exception.Message);
            }
        }

        private static string BuildHealthProbeKey(
            DingoCmsEditorHostIdentity identity,
            long processStartUtcTicks,
            string expectedBuildFingerprint)
        {
            return identity.ProcessId
                   + ":" + processStartUtcTicks
                   + ":" + identity.InstanceToken
                   + ":" + expectedBuildFingerprint;
        }

        private static void ResetHealthObservation(string key = null)
        {
            _healthProbeKey = key;
            _healthProbeTask = null;
            _hasHealthResult = false;
            _lastHealthSucceeded = false;
            _lastHealthIdentitySucceeded = false;
            _lastHealthBuildRequired = false;
            _lastHealthReportedBuildFingerprint = null;
            _consecutiveHealthFailures = 0;
            _unhealthySinceTime = 0d;
            _healthySinceUtcTicks = 0;
            _nextHealthProbeTime = 0d;
        }

        private static bool TryGetProcessStartUtcTicks(
            Process process,
            out long processStartUtcTicks)
        {
            processStartUtcTicks = 0;
            if (process == null)
                return false;
            try
            {
                processStartUtcTicks = process.StartTime
                    .ToUniversalTime()
                    .Ticks;
                return processStartUtcTicks > 0;
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
                if (process.HasExited)
                    return;
                process.Kill();
                process.WaitForExit(5000);
            }
            catch
            {
                // This is best-effort cleanup for the exact Process handle
                // returned by Process.Start. The original startup exception
                // remains the actionable error.
            }
        }

        private static bool TryAbortUnhealthyOwnedProcess(out string error)
        {
            error = null;
            if (!TryReadIdentity(out var identity)
                || !TryGetTrackedProcess(
                    identity,
                    out var process,
                    clearStaleIdentity: true))
            {
                return false;
            }

            using (process)
            {
                if (!TryGetProcessStartUtcTicks(
                        process,
                        out var processStartUtcTicks)
                    || !CanControlProcessAtCreationTime(
                        identity,
                        processStartUtcTicks)
                    || _healthProbeTask != null)
                {
                    return false;
                }

                var probeKey = BuildHealthProbeKey(
                    identity,
                    processStartUtcTicks,
                    string.IsNullOrWhiteSpace(_currentBuildFingerprint)
                        ? identity.BuildFingerprint
                        : _currentBuildFingerprint);
                if (!string.Equals(
                        _healthProbeKey,
                        probeKey,
                        StringComparison.Ordinal)
                    || !_hasHealthResult
                    || _lastHealthSucceeded
                    || _consecutiveHealthFailures <= 0)
                {
                    return false;
                }

                var unhealthySeconds = _unhealthySinceTime > 0d
                    ? Math.Max(
                        0d,
                        EditorApplication.timeSinceStartup
                        - _unhealthySinceTime)
                    : 0d;
                var startupTimedOut = !identity.Ready
                                      && StartupHealthTimedOut(
                                          new DateTime(
                                              processStartUtcTicks,
                                              DateTimeKind.Utc),
                                          DateTime.UtcNow);
                var readyHostUnhealthy = identity.Ready
                                         && ShouldRecoverReadyHost(
                                             _consecutiveHealthFailures,
                                             unhealthySeconds);
                if (!startupTimedOut && !readyHostUnhealthy)
                {
                    return false;
                }

                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        if (!process.WaitForExit(5000))
                        {
                            error = $"Detached DingoCMS process "
                                    + $"{identity.ProcessId} did not become "
                                    + "healthy and could not be stopped "
                                    + "within five seconds.";
                            return false;
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the identity check and Kill.
                }
                catch (Exception exception)
                {
                    error = $"Detached DingoCMS process "
                            + $"{identity.ProcessId} did not become healthy: "
                            + exception.Message;
                    return false;
                }

                DingoCmsEditorHostIdentity.DeleteIfOwned(
                    HostStatePath,
                    identity.ProcessId,
                    identity.InstanceToken);
                ResetHealthObservation();
                ClearStableHealthyObservation();
                error = identity.Ready
                    ? $"Detached DingoCMS process {identity.ProcessId} "
                      + "failed authenticated health verification for "
                      + $"{unhealthySeconds:0} seconds and was restarted."
                    : $"Detached DingoCMS process {identity.ProcessId} "
                      + "did not pass authenticated health verification "
                      + $"within {STARTUP_HEALTH_TIMEOUT_SECONDS:0} seconds "
                      + "and was stopped.";
                return true;
            }
        }

        private static bool TryUpgradeLegacyIdentityFromHealth(
            DingoCmsEditorHostIdentity identity,
            long processStartUtcTicks,
            string expectedBuildFingerprint,
            out DingoCmsEditorHostIdentity upgradedIdentity)
        {
            upgradedIdentity = identity;
            if (identity == null
                || _healthProbeTask != null
                || !_hasHealthResult
                || !_lastHealthIdentitySucceeded
                || !string.Equals(
                    _healthProbeKey,
                    BuildHealthProbeKey(
                        identity,
                        processStartUtcTicks,
                        expectedBuildFingerprint),
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryCreateHealthVerifiedIdentity(
                    identity,
                    processStartUtcTicks,
                    _lastHealthIdentitySucceeded,
                    _lastHealthReportedBuildFingerprint,
                    out var replacement))
            {
                return false;
            }
            try
            {
                DingoCmsEditorHostIdentity.WriteAtomic(
                    HostStatePath,
                    replacement);
                upgradedIdentity = replacement;
                return true;
            }
            catch
            {
                // Authenticated health proved which process owns the identity,
                // but a transient file lock can still delay the durable
                // migration. Retain the legacy identity and retry next poll.
                return false;
            }
        }

        private static void TryMarkIdentityReady(
            DingoCmsEditorHostIdentity identity,
            long processStartUtcTicks)
        {
            if (identity == null
                || (identity.Ready
                    && identity.ProcessStartUtcTicks
                    == processStartUtcTicks))
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
                        processStartUtcTicks,
                        ready: true,
                        buildFingerprint: identity.BuildFingerprint));
            }
            catch
            {
                // Health already proved ownership. A later poll can retry
                // the durable readiness upgrade without degrading service.
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
        }

        private static void RestoreRestartBackoff()
        {
            _restartAttempt = Math.Max(
                0,
                SessionState.GetInt(RestartAttemptSessionKey, 0));
            _nextRestartUtcTicks = ReadSessionUtcTicks(
                NextRestartUtcTicksSessionKey);
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
            SessionState.EraseInt(RestartAttemptSessionKey);
            SessionState.EraseString(NextRestartUtcTicksSessionKey);
        }

        private static bool IsRestartDue()
        {
            return _nextRestartUtcTicks <= 0
                   || DateTime.UtcNow.Ticks >= _nextRestartUtcTicks;
        }

        private static void MarkHealthyObservation(string probeKey)
        {
            if (_healthySinceUtcTicks > 0)
                return;

            var nowTicks = DateTime.UtcNow.Ticks;
            var retainedProbeKey = SessionState.GetString(
                HealthyProbeSessionKey,
                string.Empty);
            var retainedHealthyTicks = ReadSessionUtcTicks(
                HealthySinceUtcTicksSessionKey);
            _healthySinceUtcTicks = string.Equals(
                                        retainedProbeKey,
                                        probeKey,
                                        StringComparison.Ordinal)
                                    && retainedHealthyTicks > 0
                                    && retainedHealthyTicks <= nowTicks
                ? retainedHealthyTicks
                : nowTicks;
            SessionState.SetString(HealthyProbeSessionKey, probeKey);
            SessionState.SetString(
                HealthySinceUtcTicksSessionKey,
                _healthySinceUtcTicks.ToString(
                    CultureInfo.InvariantCulture));
        }

        private static void ClearStableHealthyObservation()
        {
            _healthySinceUtcTicks = 0;
            SessionState.EraseString(HealthyProbeSessionKey);
            SessionState.EraseString(HealthySinceUtcTicksSessionKey);
        }

        private static long ReadSessionUtcTicks(string key)
        {
            return long.TryParse(
                SessionState.GetString(key, "0"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var ticks)
                && ticks > 0
                ? ticks
                : 0;
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left)
                || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasStableHealthyObservation(int processId)
        {
            if (!_hasHealthResult
                || !_lastHealthSucceeded
                || _healthySinceUtcTicks <= 0
                || !TryReadIdentity(out var identity)
                || identity.ProcessId != processId)
            {
                return false;
            }

            var healthySeconds = Math.Max(
                0d,
                new TimeSpan(
                    Math.Max(
                        0,
                        DateTime.UtcNow.Ticks - _healthySinceUtcTicks))
                    .TotalSeconds);
            return ShouldResetRestartBackoff(healthySeconds);
        }

        private static void PollProcess()
        {
            if (EditorApplication.timeSinceStartup < _nextProcessPollTime)
                return;
            _nextProcessPollTime = EditorApplication.timeSinceStartup
                                   + PROCESS_POLL_INTERVAL_SECONDS;

            var previousError = _lastError;
            var processState = ProcessState;
            var processId = ProcessId;
            var failureRegistered = false;
            if (processState == DingoCmsDetachedProcessState.Running)
            {
                _lastError = null;
                if (HasStableHealthyObservation(processId))
                {
                    ResetRestartBackoff();
                }
            }
            else if (processState == DingoCmsDetachedProcessState
                         .OwnershipVerificationPending
                     && !EditorApplication.isCompiling)
            {
                if (TryAbortUnhealthyOwnedProcess(out var healthError))
                {
                    RegisterRestartFailure(healthError);
                    failureRegistered = true;
                    processState = DingoCmsDetachedProcessState.Stopped;
                    processId = 0;
                }
                else if (!string.IsNullOrWhiteSpace(healthError))
                {
                    _lastError = healthError;
                }
            }

            if (processState == DingoCmsDetachedProcessState.Stopped
                && !failureRegistered
                && (_lastObservedState == DingoCmsDetachedProcessState
                        .OwnershipVerificationPending
                    || _lastObservedState
                    == DingoCmsDetachedProcessState.Running)
                && _lastObservedProcessId > 0)
            {
                RegisterRestartFailure(
                    $"Detached DingoCMS process {_lastObservedProcessId} "
                    + (_lastObservedState
                       == DingoCmsDetachedProcessState.Running
                        ? "exited unexpectedly."
                        : "exited before authenticated health verification."));
                failureRegistered = true;
            }

            if (processState == DingoCmsDetachedProcessState.Stopped
                && KeepRunning
                && !_startInProgress
                && !_buildInProgress
                && !EditorApplication.isCompiling
                && IsRestartDue())
            {
                try
                {
                    StartCore();
                }
                catch (Exception exception)
                {
                    RegisterRestartFailure(exception.Message);
                }

                processState = ProcessState;
                processId = ProcessId;
            }
            if (processState == _lastObservedState
                && processId == _lastObservedProcessId
                && string.Equals(
                    previousError,
                    _lastError,
                    StringComparison.Ordinal))
            {
                return;
            }

            _lastObservedState = processState;
            _lastObservedProcessId = processId;
            NotifyStateChanged();
        }

        private static void NotifyStateChanged()
        {
            StateChanged?.Invoke();
        }

        private static void OnCompilationFinished(object context)
        {
            InvalidateBuildValidation();
            NotifyStateChanged();
        }

        private static void OnProjectChanged()
        {
            InvalidateBuildValidation();
            NotifyStateChanged();
        }

        private static void OnBuildSceneListChanged()
        {
            InvalidateBuildValidation();
            NotifyStateChanged();
        }

        internal static void NotifyBuildInputsChanged()
        {
            InvalidateBuildValidation();
            NotifyStateChanged();
        }

    }

    class DingoCmsEditorHealthProbeResult
    {
        public readonly bool Healthy;
        public readonly string Error;
        public readonly bool IdentityMatches;
        public readonly bool BuildRequired;
        public readonly string ReportedBuildFingerprint;

        public DingoCmsEditorHealthProbeResult(
            bool healthy,
            string error,
            bool identityMatches = false,
            bool buildRequired = false,
            string reportedBuildFingerprint = null)
        {
            Healthy = healthy;
            Error = error;
            IdentityMatches = identityMatches;
            BuildRequired = buildRequired;
            ReportedBuildFingerprint = reportedBuildFingerprint;
        }
    }

    class DingoCmsEditorHostBuildInputPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            var changedAssets = (importedAssets ?? Array.Empty<string>())
                .Concat(deletedAssets ?? Array.Empty<string>())
                .Concat(movedAssets ?? Array.Empty<string>())
                .Concat(movedFromAssetPaths ?? Array.Empty<string>());
            if (changedAssets.Any(
                    DingoCmsEditorHostBuildMetadataUtils
                        .MayAffectBuildFingerprint))
            {
                DingoCmsDetachedEditorHost.NotifyBuildInputsChanged();
            }
        }
    }
}
#endif
