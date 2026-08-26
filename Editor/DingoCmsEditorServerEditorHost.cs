#if NEWTONSOFT_EXISTS
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DingoGameObjectsCMS.AssetLibrary.AssetsEdit;
using DingoGameObjectsCMSEditorServer.Editor.Clients;
using DingoGameObjectsCMSEditorServer.Runtime;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace DingoGameObjectsCMSEditorServer.Editor
{
    [InitializeOnLoad]
    public static class DingoCmsEditorServerEditorHost
    {
        private const string PREFS_PREFIX =
            "DingoCMS.EditorServer.";
        private const string PORT_PREFS_KEY =
            PREFS_PREFIX + "Port";
        private const string AUTO_START_PREFS_KEY =
            PREFS_PREFIX + "AutoStart";
        private const string SESSION_INITIALIZED_KEY =
            PREFS_PREFIX + "SessionInitialized";
        private const string SESSION_DESIRED_RUNNING_KEY =
            PREFS_PREFIX + "DesiredRunning";
        private const uint WM_SETTING_CHANGE = 0x001A;
        private const uint SMTO_ABORT_IF_HUNG = 0x0002;
        private const double POST_COMPILATION_RESUME_DELAY_SECONDS = 3d;

        private static readonly double[] RESUME_RETRY_DELAYS_SECONDS =
            { 0d, 1d, 3d, 5d, 10d };

        private static DingoCmsEditorServerRuntime _runtime;
        private static string _lastError;
        private static double _nextResumeTime;
        private static int _resumeAttempt;
        private static bool _resumePumpRegistered;
        private static bool _compilationInProgress;
        private static bool _assemblyReloading;

        public static event Action StateChanged;

        public static bool IsRunning => _runtime?.IsRunning == true;
        public static string AssetsRoot =>
            GameAssetModPathPolicy.GetAssetsRootPath();
        public static string ProjectRoot =>
            DingoCmsMcpClientConfigUtils.GetProjectRoot();
        public static string McpUrl =>
            $"http://127.0.0.1:{Port}/mcp";
        public static string LastError => _lastError;
        public static bool HasToken =>
            !string.IsNullOrWhiteSpace(ReadExistingToken());
        public static string Token => ReadExistingToken();
        public static bool TokenIsUserPersistent =>
            UnityEngine.Application.platform == RuntimePlatform.WindowsEditor
            && IsValidToken(ReadUserEnvironmentToken());

        public static int Port
        {
            get => EditorPrefs.GetInt(
                PORT_PREFS_KEY,
                DingoCmsEditorServerOptions.DEFAULT_PORT);
            set
            {
                if (value == Port)
                    return;
                RequireDetachedHostReleased(
                    "changing the DingoCMS port");

                DingoCmsEditorServerOptions.CreateEnabled(
                    value,
                    new string('x', 24));
                var wasRunning = IsRunning;
                if (wasRunning)
                    DisposeRuntime();
                EditorPrefs.SetInt(PORT_PREFS_KEY, value);
                if (wasRunning)
                {
                    try
                    {
                        Start();
                    }
                    catch
                    {
                        ScheduleResume();
                        throw;
                    }
                    return;
                }
                NotifyStateChanged();
            }
        }

        public static bool AutoStart
        {
            get => EditorPrefs.GetBool(AUTO_START_PREFS_KEY, false);
            set
            {
                EditorPrefs.SetBool(AUTO_START_PREFS_KEY, value);
                NotifyStateChanged();
            }
        }

        static DingoCmsEditorServerEditorHost()
        {
            if (!SessionState.GetBool(
                    SESSION_INITIALIZED_KEY,
                    defaultValue: false))
            {
                SessionState.SetBool(SESSION_INITIALIZED_KEY, true);
                SessionState.SetBool(
                    SESSION_DESIRED_RUNNING_KEY,
                    AutoStart);
            }

            CompilationPipeline.compilationStarted +=
                OnCompilationStarted;
            CompilationPipeline.compilationFinished +=
                OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += StopRuntimePreservingIntent;
            ScheduleResume();
        }

        public static void Start()
        {
            if (IsRunning)
            {
                return;
            }
            if (DingoCmsDetachedEditorHost.Mode
                != DingoCmsEditorHostMode.UnityProcess)
            {
                throw new InvalidOperationException(
                    "Select Unity Process mode before starting the embedded "
                    + "DingoCMS host.");
            }
            RequireDetachedHostReleased(
                "starting the Unity-process DingoCMS host");
            if (_compilationInProgress
                || _assemblyReloading
                || EditorApplication.isCompiling)
            {
                throw new InvalidOperationException(
                    "DingoCMS Editor Server cannot start while scripts are "
                    + "compiling or assemblies are reloading.");
            }

            DisposeRuntime();
            _lastError = null;
            try
            {
                var options = DingoCmsEditorServerOptions.CreateEnabled(
                    Port,
                    EnsureToken(),
                    authoringOnly: true);
                _runtime = DingoCmsEditorServerRuntime.Start(
                    options,
                    AssetsRoot);
                SessionState.SetBool(
                    SESSION_DESIRED_RUNNING_KEY,
                    true);
                StopResumePump();
            }
            catch (Exception exception)
            {
                // The intent flag survives a failed start on purpose. A resume
                // that loses a race with the closing socket must stay wanted,
                // or the retry it is running inside would disarm itself on its
                // first attempt.
                _lastError = exception.Message;
                DisposeRuntime();
                NotifyStateChanged();
                throw;
            }
            NotifyStateChanged();
        }

        public static void Stop()
        {
            SessionState.SetBool(
                SESSION_DESIRED_RUNNING_KEY,
                false);
            _lastError = null;
            StopResumePump();
            DisposeRuntime();
            NotifyStateChanged();
        }

        public static void OpenWebEditor()
        {
            if (!IsRunning)
            {
                throw new InvalidOperationException(
                    "Start the DingoCMS Editor Server before opening its Web editor.");
            }
            UnityEngine.Application.OpenURL(_runtime.CreateBrowserBootstrapUrl());
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
            if (IsRunning)
            {
                throw new InvalidOperationException(
                    "Stop the DingoCMS Editor Server before rotating its token.");
            }
            RequireDetachedHostReleased("rotating the DingoCMS token");

            var bytes = new byte[32];
            using (var generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }
            var token = BitConverter.ToString(bytes)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
            SetProcessToken(token);
            PersistUserToken(token);
            _lastError = null;
            NotifyStateChanged();
            return token;
        }

        public static DingoCmsMcpClientContext CreateClientContext()
        {
            return new DingoCmsMcpClientContext(
                ProjectRoot,
                McpUrl,
                DingoCmsEditorServerOptions.TOKEN_ENVIRONMENT_VARIABLE);
        }

        private static void ScheduleResume(double delaySeconds = 0d)
        {
            if (_compilationInProgress
                || _assemblyReloading
                || IsRunning
                || DingoCmsDetachedEditorHost.Mode
                   != DingoCmsEditorHostMode.UnityProcess
                || DingoCmsDetachedEditorHost.ProcessState
                   != DingoCmsDetachedProcessState.Stopped
                || !SessionState.GetBool(
                    SESSION_DESIRED_RUNNING_KEY,
                    defaultValue: false))
            {
                return;
            }

            _resumeAttempt = 0;
            _nextResumeTime = EditorApplication.timeSinceStartup
                              + Math.Max(0d, delaySeconds);
            if (_resumePumpRegistered)
                return;

            _resumePumpRegistered = true;
            EditorApplication.update += PumpResume;
        }

        private static void PumpResume()
        {
            if (_compilationInProgress
                || _assemblyReloading
                || DingoCmsDetachedEditorHost.Mode
                   != DingoCmsEditorHostMode.UnityProcess
                || DingoCmsDetachedEditorHost.ProcessState
                   != DingoCmsDetachedProcessState.Stopped)
            {
                StopResumePump();
                return;
            }
            if (IsRunning
                || !SessionState.GetBool(
                    SESSION_DESIRED_RUNNING_KEY,
                    defaultValue: false))
            {
                StopResumePump();
                return;
            }
            if (EditorApplication.isUpdating
                || EditorApplication.timeSinceStartup < _nextResumeTime)
            {
                return;
            }

            try
            {
                Start();
                return;
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                _resumeAttempt++;
            }

            if (_resumeAttempt >= RESUME_RETRY_DELAYS_SECONDS.Length)
            {
                StopResumePump();
                Debug.LogError(
                    "DingoCMS Editor Server could not take port "
                    + $"{Port} after {_resumeAttempt} attempts: {_lastError}");
                NotifyStateChanged();
                return;
            }

            _nextResumeTime = EditorApplication.timeSinceStartup
                              + RESUME_RETRY_DELAYS_SECONDS[_resumeAttempt];
        }

        private static void StopResumePump()
        {
            if (!_resumePumpRegistered)
                return;

            _resumePumpRegistered = false;
            EditorApplication.update -= PumpResume;
        }

        private static void StopRuntimePreservingIntent()
        {
            StopResumePump();
            DisposeRuntime();
            NotifyStateChanged();
        }

        private static void OnCompilationStarted(object _)
        {
            _compilationInProgress = true;
            StopRuntimePreservingIntent();
        }

        private static void OnCompilationFinished(object _)
        {
            _compilationInProgress = false;
            ScheduleResume(POST_COMPILATION_RESUME_DELAY_SECONDS);
        }

        private static void OnBeforeAssemblyReload()
        {
            _assemblyReloading = true;
            StopRuntimePreservingIntent();
        }

        private static void DisposeRuntime()
        {
            var runtime = _runtime;
            _runtime = null;
            runtime?.Dispose();
        }

        private static void RequireDetachedHostReleased(string operation)
        {
            var state = DingoCmsDetachedEditorHost.ProcessState;
            if (state == DingoCmsDetachedProcessState.Stopped)
                return;
            throw new InvalidOperationException(
                $"Stop the detached DingoCMS host before {operation}. "
                + $"PID {DingoCmsDetachedEditorHost.ProcessId} remains "
                + (state == DingoCmsDetachedProcessState.Running
                    ? "owned and running."
                    : "retained while ownership verification is pending."));
        }

        private static string ReadExistingToken()
        {
            var processToken = Environment.GetEnvironmentVariable(
                DingoCmsEditorServerOptions.TOKEN_ENVIRONMENT_VARIABLE,
                EnvironmentVariableTarget.Process);
            if (IsValidToken(processToken))
            {
                return processToken;
            }

            var userToken = ReadUserEnvironmentToken();
            return IsValidToken(userToken) ? userToken : null;
        }

        private static string ReadUserEnvironmentToken()
        {
            if (UnityEngine.Application.platform != RuntimePlatform.WindowsEditor)
            {
                return null;
            }
            try
            {
                return Environment.GetEnvironmentVariable(
                    DingoCmsEditorServerOptions.TOKEN_ENVIRONMENT_VARIABLE,
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
                DingoCmsEditorServerOptions.TOKEN_ENVIRONMENT_VARIABLE,
                token,
                EnvironmentVariableTarget.Process);
        }

        private static void PersistUserToken(string token)
        {
            if (UnityEngine.Application.platform != RuntimePlatform.WindowsEditor)
            {
                return;
            }
            Environment.SetEnvironmentVariable(
                DingoCmsEditorServerOptions.TOKEN_ENVIRONMENT_VARIABLE,
                token,
                EnvironmentVariableTarget.User);
            SendMessageTimeout(
                new IntPtr(0xffff),
                WM_SETTING_CHANGE,
                UIntPtr.Zero,
                "Environment",
                SMTO_ABORT_IF_HUNG,
                1000,
                out _);
        }

        private static bool IsValidToken(string token)
        {
            return !string.IsNullOrWhiteSpace(token)
                   && token.Trim().Length >= 24;
        }

        private static void NotifyStateChanged()
        {
            StateChanged?.Invoke();
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
