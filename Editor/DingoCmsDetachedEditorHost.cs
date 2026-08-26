#if NEWTONSOFT_EXISTS
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DingoGameObjectsCMSEditorServer.Runtime;
using UnityEditor;
using UnityEditor.Build.Reporting;
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
    }

    [InitializeOnLoad]
    public static class DingoCmsDetachedEditorHost
    {
        private const string PREFS_PREFIX =
            "DingoCMS.EditorServer.Detached.";
        private const string MODE_PREFS_KEY = PREFS_PREFIX + "Mode";
        private const string EXECUTABLE_PREFS_KEY =
            PREFS_PREFIX + "Executable";
        private const string PROCESS_ID_PREFS_KEY =
            PREFS_PREFIX + "ProcessId";
        private const string BOOTSTRAP_OPENED_PROCESS_ID_PREFS_KEY =
            PREFS_PREFIX + "BootstrapOpenedProcessId";
        private const double PROCESS_POLL_INTERVAL_SECONDS = 1d;

        private static string _lastError;
        private static double _nextProcessPollTime;
        private static DingoCmsDetachedProcessState _lastObservedState;
        private static int _lastObservedProcessId;

        public static event Action StateChanged;

        public static DingoCmsEditorHostMode Mode
        {
            get => (DingoCmsEditorHostMode)EditorPrefs.GetInt(
                MODE_PREFS_KEY,
                (int)DingoCmsEditorHostMode.DetachedProcess);
            set
            {
                if (value == Mode)
                    return;
                if (ProcessState != DingoCmsDetachedProcessState.Stopped
                    || DingoCmsEditorServerEditorHost.IsRunning)
                {
                    throw new InvalidOperationException(
                        "Stop the active DingoCMS host before changing its "
                        + "process mode.");
                }

                EditorPrefs.SetInt(MODE_PREFS_KEY, (int)value);
                _lastError = null;
                NotifyStateChanged();
            }
        }

        public static string ExecutablePath
        {
            get
            {
                var configured = EditorPrefs.GetString(
                    EXECUTABLE_PREFS_KEY,
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
                    EXECUTABLE_PREFS_KEY,
                    normalized);
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

        public static DingoCmsDetachedProcessState ProcessState
        {
            get
            {
                if (TryGetTrackedProcess(
                        out var process,
                        clearStaleIdentity: true))
                {
                    process.Dispose();
                    return DingoCmsDetachedProcessState.Running;
                }
                return ResolveProcessState(
                    processVerified: false,
                    retainedProcessId: RetainedProcessId);
            }
        }

        public static bool IsRunning =>
            ProcessState == DingoCmsDetachedProcessState.Running;

        public static bool HasRetainedIdentity => RetainedProcessId > 0;

        public static int ProcessId
        {
            get
            {
                return RetainedProcessId;
            }
        }

        public static string LastError => _lastError;

        static DingoCmsDetachedEditorHost()
        {
            _lastObservedState = ProcessState;
            _lastObservedProcessId = ProcessId;
            EditorApplication.update += PollProcess;
        }

        public static void Start()
        {
            var processState = ProcessState;
            if (processState == DingoCmsDetachedProcessState.Running)
                return;
            var retainedProcessId = RetainedProcessId;
            if (retainedProcessId > 0)
            {
                throw new InvalidOperationException(
                    "The detached DingoCMS process still has a retained "
                    + $"identity (PID {retainedProcessId}) but could not be "
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

            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            var token = DingoCmsEditorServerEditorHost.EnsureToken();
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

            try
            {
                using var process = Process.Start(startInfo)
                                    ?? throw new InvalidOperationException(
                                        "The detached DingoCMS process did "
                                        + "not start.");
                EditorPrefs.SetInt(PROCESS_ID_PREFS_KEY, process.Id);
                EditorPrefs.SetInt(
                    BOOTSTRAP_OPENED_PROCESS_ID_PREFS_KEY,
                    0);
                _lastObservedState = DingoCmsDetachedProcessState.Running;
                _lastObservedProcessId = process.Id;
                _lastError = null;
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                EditorPrefs.DeleteKey(PROCESS_ID_PREFS_KEY);
                NotifyStateChanged();
                throw;
            }

            NotifyStateChanged();
        }

        public static void Stop()
        {
            using var process = TakeTrackedProcess();
            if (!CanCompleteStop(
                    processVerified: process != null,
                    retainedProcessId: RetainedProcessId))
            {
                throw new InvalidOperationException(
                    "Detached DingoCMS ownership verification is pending for "
                    + $"PID {RetainedProcessId}. Its identity was retained, "
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

            EditorPrefs.DeleteKey(PROCESS_ID_PREFS_KEY);
            EditorPrefs.DeleteKey(
                BOOTSTRAP_OPENED_PROCESS_ID_PREFS_KEY);
            _lastObservedState = DingoCmsDetachedProcessState.Stopped;
            _lastObservedProcessId = 0;
            _lastError = null;
            NotifyStateChanged();
        }

        public static void OpenWebEditor()
        {
            using var process = TakeTrackedProcess();
            if (process == null)
            {
                throw new InvalidOperationException(
                    "Start the detached DingoCMS host before opening its "
                    + "Web editor.");
            }

            var openedForProcess = EditorPrefs.GetInt(
                BOOTSTRAP_OPENED_PROCESS_ID_PREFS_KEY,
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
                BOOTSTRAP_OPENED_PROCESS_ID_PREFS_KEY,
                process.Id);
            UnityEngine.Application.OpenURL(bootstrapUrl);
        }

        public static void BuildPlayer()
        {
            if (ProcessState != DingoCmsDetachedProcessState.Stopped)
            {
                throw new InvalidOperationException(
                    "Stop the detached DingoCMS host and clear its retained "
                    + "ownership before rebuilding it.");
            }

            var scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                throw new InvalidOperationException(
                    "At least one enabled scene is required to build the "
                    + "detached authoring host.");
            }

            var outputPath = ExecutablePath;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
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

            _lastError = null;
            NotifyStateChanged();
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
            int retainedProcessId)
        {
            return processVerified || retainedProcessId <= 0;
        }

        private static string ProjectRoot => Path.GetFullPath(
            Path.Combine(UnityEngine.Application.dataPath, ".."));

        private static int RetainedProcessId => EditorPrefs.GetInt(
            PROCESS_ID_PREFS_KEY,
            0);

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"")
                + "\"";
        }

        private static Process TakeTrackedProcess()
        {
            return TryGetTrackedProcess(
                out var process,
                clearStaleIdentity: true)
                ? process
                : null;
        }

        private static bool TryGetTrackedProcess(
            out Process process,
            bool clearStaleIdentity)
        {
            process = null;
            var processId = EditorPrefs.GetInt(
                PROCESS_ID_PREFS_KEY,
                0);
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
                    EditorPrefs.DeleteKey(PROCESS_ID_PREFS_KEY);
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
                        EditorPrefs.DeleteKey(PROCESS_ID_PREFS_KEY);
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

                if (!PathsEqual(executable, ExecutablePath))
                {
                    candidate.Dispose();
                    if (clearStaleIdentity)
                        EditorPrefs.DeleteKey(PROCESS_ID_PREFS_KEY);
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

        private static void PollProcess()
        {
            if (EditorApplication.timeSinceStartup < _nextProcessPollTime)
                return;
            _nextProcessPollTime = EditorApplication.timeSinceStartup
                                   + PROCESS_POLL_INTERVAL_SECONDS;

            var processState = ProcessState;
            var processId = ProcessId;
            if (processState == _lastObservedState
                && processId == _lastObservedProcessId)
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
    }
}
#endif
