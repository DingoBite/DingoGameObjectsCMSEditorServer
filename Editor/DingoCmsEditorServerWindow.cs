#if NEWTONSOFT_EXISTS
using System;
using System.Threading;
using System.Threading.Tasks;
using DingoGameObjectsCMSEditorServer.Editor.Clients;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace DingoGameObjectsCMSEditorServer.Editor
{
    public class DingoCmsEditorServerWindow : EditorWindow
    {
        private const string MENU_PATH =
            "Window/DingoCMS/Editor Server";
        private const string STYLE_PATH =
            "Assets/AppSDK/DingoGameObjectsCMSEditorServer/Editor/DingoCmsEditorServerWindow.uss";

        private Label _localServerStatus;
        private Label _sessionStatus;
        private VisualElement _sessionIndicator;
        private Label _serverMessage;
        private Label _tokenHint;
        private Label _hostDetail;
        private IntegerField _portField;
        private TextField _detachedExecutableField;
        private TextField _tokenField;
        private Button _serverToggleButton;
        private Button _hostToggleButton;
        private Button _openWebButton;
        private Button _copyMcpButton;
        private Button _copyTokenButton;
        private Button _rotateTokenButton;
        private Button _browseExecutableButton;
        private Toggle _autoStartToggle;
        private VisualElement _clients;
        private string _actionError;
        private bool _serverOperationInProgress;

        [MenuItem(MENU_PATH)]
        public static void Open()
        {
            var window = GetWindow<DingoCmsEditorServerWindow>();
            window.titleContent = new GUIContent("DingoCMS Server");
            window.minSize = new Vector2(520f, 560f);
            window.Show();
        }

        private void OnEnable()
        {
            DingoCmsEditorServerSettings.StateChanged -= RefreshAll;
            DingoCmsEditorServerSettings.StateChanged += RefreshAll;
            DingoCmsDetachedEditorHost.StateChanged -= RefreshAll;
            DingoCmsDetachedEditorHost.StateChanged += RefreshAll;
            DingoCmsEditorSessionClient.StateChanged -= RefreshAll;
            DingoCmsEditorSessionClient.StateChanged += RefreshAll;
        }

        private void OnDisable()
        {
            DingoCmsEditorServerSettings.StateChanged -= RefreshAll;
            DingoCmsDetachedEditorHost.StateChanged -= RefreshAll;
            DingoCmsEditorSessionClient.StateChanged -= RefreshAll;
        }

        private void OnFocus()
        {
            RefreshAll();
        }

        public void CreateGUI()
        {
            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(STYLE_PATH);
            if (style != null)
            {
                rootVisualElement.styleSheets.Add(style);
            }
            rootVisualElement.AddToClassList("dingocms-root");

            var scroll = new ScrollView();
            scroll.AddToClassList("dingocms-scroll");
            rootVisualElement.Add(scroll);

            var hero = new VisualElement();
            hero.AddToClassList("hero");
            scroll.Add(hero);

            var eyebrow = new Label("DINGO CMS  /  LOCAL AUTHORING");
            eyebrow.AddToClassList("eyebrow");
            hero.Add(eyebrow);

            var heading = new VisualElement();
            heading.AddToClassList("hero-heading");
            hero.Add(heading);
            var title = new Label("Editor Server");
            title.AddToClassList("title");
            heading.Add(title);
            var subtitle = new Label(
                "Edit the mounted AppData library without entering Play Mode.");
            subtitle.AddToClassList("subtitle");
            heading.Add(subtitle);

            var serverCard = CreateCard(
                "SHARED SERVER",
                "One server can host sessions from several Unity projects. Start it here, or connect this project to a server that is already running elsewhere.");
            scroll.Add(serverCard);

            serverCard.Add(CreateReadOnlyField(
                "HTTP URL",
                DingoCmsEditorServerSettings.BaseUrl));

            var localServerRow = new VisualElement();
            localServerRow.AddToClassList("control-row");
            serverCard.Add(localServerRow);
            var localServerLabel = new Label("Local Server:");
            localServerLabel.AddToClassList("control-label");
            localServerRow.Add(localServerLabel);
            _localServerStatus = new Label();
            _localServerStatus.AddToClassList("control-status");
            localServerRow.Add(_localServerStatus);
            _hostToggleButton = new Button(() =>
                RunAsyncAction(ToggleOwnedHostAsync));
            _hostToggleButton.AddToClassList("server-control-button");
            localServerRow.Add(_hostToggleButton);

            var sessionRow = new VisualElement();
            sessionRow.AddToClassList("control-row");
            serverCard.Add(sessionRow);
            var sessionIdentity = new VisualElement();
            sessionIdentity.AddToClassList("session-identity");
            sessionRow.Add(sessionIdentity);
            _sessionIndicator = new VisualElement();
            _sessionIndicator.AddToClassList("status-dot");
            sessionIdentity.Add(_sessionIndicator);
            _sessionStatus = new Label();
            _sessionStatus.AddToClassList("session-status");
            sessionIdentity.Add(_sessionStatus);
            _serverToggleButton = new Button(() =>
                RunAsyncAction(ToggleSessionAsync));
            _serverToggleButton.AddToClassList("session-control-button");
            sessionRow.Add(_serverToggleButton);

            var actionRail = new VisualElement();
            actionRail.AddToClassList("secondary-actions");
            serverCard.Add(actionRail);
            _openWebButton = new Button(() => RunAsyncAction(
                DingoCmsEditorSessionClient.OpenWebEditorAsync))
            {
                text = "Open Web editor",
            };
            actionRail.Add(_openWebButton);
            _copyMcpButton = new Button(() =>
            {
                EditorGUIUtility.systemCopyBuffer =
                    DingoCmsEditorServerSettings.McpUrl;
                ShowNotification(new GUIContent("MCP URL copied"));
            })
            {
                text = "Copy MCP URL",
            };
            actionRail.Add(_copyMcpButton);

            _serverMessage = new Label();
            _serverMessage.AddToClassList("server-message");
            serverCard.Add(_serverMessage);

            var manualLaunch = new Foldout
            {
                text = "Manual Server Launch",
                value = false,
            };
            manualLaunch.AddToClassList("manual-launch");
            serverCard.Add(manualLaunch);

            var manualHint = new Label(
                "Advanced host configuration. Normal use only needs Start Server and Connect.");
            manualHint.AddToClassList("manual-hint");
            manualLaunch.Add(manualHint);

            manualLaunch.Add(CreateReadOnlyField(
                "Assets root",
                DingoCmsEditorServerSettings.AssetsRoot));

            var executableRow = new VisualElement();
            executableRow.AddToClassList("executable-row");
            manualLaunch.Add(executableRow);
            _detachedExecutableField = new TextField("Python runtime")
            {
                isDelayed = true,
            };
            _detachedExecutableField.AddToClassList("executable-field");
            _detachedExecutableField.RegisterValueChangedCallback(change =>
                RunAction(() => DingoCmsDetachedEditorHost.ExecutablePath =
                    change.newValue));
            executableRow.Add(_detachedExecutableField);
            _browseExecutableButton = new Button(BrowseDetachedExecutable)
            {
                text = "Browse",
            };
            _browseExecutableButton.AddToClassList("compact-button");
            executableRow.Add(_browseExecutableButton);

            _hostDetail = new Label();
            _hostDetail.AddToClassList("host-detail");
            manualLaunch.Add(_hostDetail);

            _portField = new IntegerField("Loopback port");
            _portField.AddToClassList("form-field");
            _portField.RegisterValueChangedCallback(change =>
            {
                RunAction(() =>
                    DingoCmsEditorServerSettings.Port = change.newValue);
            });
            manualLaunch.Add(_portField);

            var tokenRow = new VisualElement();
            tokenRow.AddToClassList("token-row");
            manualLaunch.Add(tokenRow);
            _tokenField = new TextField("Bearer token")
            {
                isPasswordField = true,
                isReadOnly = true,
            };
            _tokenField.AddToClassList("token-field");
            tokenRow.Add(_tokenField);
            _copyTokenButton = new Button(() =>
            {
                EditorGUIUtility.systemCopyBuffer =
                    DingoCmsEditorServerSettings.Token;
                ShowNotification(new GUIContent("Token copied"));
            })
            {
                text = "Copy",
            };
            _copyTokenButton.AddToClassList("compact-button");
            tokenRow.Add(_copyTokenButton);
            _rotateTokenButton = new Button(RotateToken)
            {
                text = "Generate",
            };
            _rotateTokenButton.AddToClassList("compact-button");
            tokenRow.Add(_rotateTokenButton);
            _tokenHint = new Label();
            _tokenHint.AddToClassList("field-hint");
            manualLaunch.Add(_tokenHint);

            _autoStartToggle = new Toggle(
                "Keep owned shared server running");
            _autoStartToggle.AddToClassList("auto-start");
            _autoStartToggle.tooltip =
                "Restarts the verified detached host after a crash or Editor restart. Compilation and domain reload do not restart it.";
            _autoStartToggle.RegisterValueChangedCallback(change =>
                DingoCmsDetachedEditorHost.KeepRunning = change.newValue);
            manualLaunch.Add(_autoStartToggle);

            var clientsCard = CreateCard(
                "CLIENTS",
                "Configuration is project-local. The bearer value stays in DINGO_CMS_EDITOR_TOKEN and is never written to the repository.");
            scroll.Add(clientsCard);
            _clients = new VisualElement();
            _clients.AddToClassList("clients");
            clientsCard.Add(_clients);

            var footer = new Label(
                "Commits update the disk library only. Restart gameplay explicitly to mount the new revision.");
            footer.AddToClassList("footer-note");
            scroll.Add(footer);

            RefreshAll();
        }

        private static VisualElement CreateCard(
            string label,
            string description)
        {
            var card = new VisualElement();
            card.AddToClassList("card");
            var marker = new VisualElement();
            marker.AddToClassList("card-rail");
            card.Add(marker);
            var labelElement = new Label(label);
            labelElement.AddToClassList("card-label");
            card.Add(labelElement);
            var descriptionElement = new Label(description);
            descriptionElement.AddToClassList("card-description");
            card.Add(descriptionElement);
            return card;
        }

        private static VisualElement CreateReadOnlyField(
            string label,
            string value)
        {
            var field = new TextField(label)
            {
                value = value,
                isReadOnly = true,
            };
            field.AddToClassList("form-field");
            return field;
        }

        private void RefreshAll()
        {
            if (_localServerStatus == null)
            {
                return;
            }

            var detachedState = DingoCmsDetachedEditorHost.ProcessState;
            var detachedRunning = detachedState
                                  == DingoCmsDetachedProcessState.Running;
            var detachedRestartRequired = detachedState
                                        == DingoCmsDetachedProcessState
                                            .RestartRequired;
            var detachedIdentityRetained = detachedState
                                           != DingoCmsDetachedProcessState.Stopped;
            var hostOccupied = detachedIdentityRetained;
            var sessionState = DingoCmsEditorSessionClient.State;
            var sessionConnected = sessionState
                                   == DingoCmsEditorSessionState.Connected;
            var sessionConnecting = sessionState
                                    == DingoCmsEditorSessionState.Connecting;

            _localServerStatus.text = detachedRunning
                ? $"Running locally · PID {DingoCmsDetachedEditorHost.ProcessId}"
                : detachedRestartRequired
                    ? $"Restart required · PID {DingoCmsDetachedEditorHost.ProcessId}"
                    : detachedIdentityRetained
                        ? $"Checking ownership · PID {DingoCmsDetachedEditorHost.ProcessId}"
                        : "Not running from this project";
            _localServerStatus.EnableInClassList(
                "control-status-running",
                detachedRunning);
            _localServerStatus.EnableInClassList(
                "control-status-pending",
                detachedIdentityRetained && !detachedRunning);

            _hostToggleButton.text = _serverOperationInProgress
                ? detachedIdentityRetained
                    ? "Stopping…"
                    : "Starting…"
                : detachedIdentityRetained
                    ? "Stop Server"
                    : "Start Server";
            _hostToggleButton.EnableInClassList(
                "danger-button",
                detachedIdentityRetained && !_serverOperationInProgress);
            _hostToggleButton.SetEnabled(
                !_serverOperationInProgress
                && detachedState != DingoCmsDetachedProcessState
                    .OwnershipVerificationPending);

            _sessionStatus.text = sessionConnected
                ? $"Session Active ({DingoCmsEditorSessionClient.ProjectName})"
                : sessionConnecting
                    ? $"Connecting ({DingoCmsEditorSessionClient.ProjectName})"
                    : $"Session Inactive ({DingoCmsEditorSessionClient.ProjectName})";
            _sessionIndicator.EnableInClassList(
                "status-dot-running",
                sessionConnected);
            _sessionIndicator.EnableInClassList(
                "status-dot-pending",
                sessionConnecting);
            _sessionIndicator.EnableInClassList(
                "status-dot-stopped",
                !sessionConnected && !sessionConnecting);

            _detachedExecutableField.SetValueWithoutNotify(
                DingoCmsDetachedEditorHost.ExecutablePath);
            _detachedExecutableField.SetEnabled(
                !hostOccupied && !_serverOperationInProgress);
            _browseExecutableButton.SetEnabled(
                !hostOccupied && !_serverOperationInProgress);
            _hostDetail.text = detachedRunning
                ? $"This project owns standalone broker PID {DingoCmsDetachedEditorHost.ProcessId} · runtime and health identity verified · log: "
                  + DingoCmsDetachedEditorHost.LogPath
                : detachedRestartRequired
                    ? $"Owned broker PID {DingoCmsDetachedEditorHost.ProcessId} uses older broker files. Stop it, then press Start Server again."
                : detachedIdentityRetained
                    ? $"PID {DingoCmsDetachedEditorHost.ProcessId} is retained while its runtime and health identity are being verified. Host configuration stays locked."
                    : "No process owned by this project. Start Server launches the standalone broker directly; no Unity build is performed.";

            _portField.SetValueWithoutNotify(
                DingoCmsEditorServerSettings.Port);
            _portField.SetEnabled(
                !hostOccupied
                && !sessionConnected
                && !sessionConnecting
                && !_serverOperationInProgress);
            _autoStartToggle.SetValueWithoutNotify(
                DingoCmsDetachedEditorHost.KeepRunning);
            _autoStartToggle.SetEnabled(!_serverOperationInProgress);

            var token = DingoCmsEditorServerSettings.Token;
            _tokenField.SetValueWithoutNotify(token ?? string.Empty);
            _tokenField.tooltip = string.IsNullOrEmpty(token)
                ? "Generated when the server or a client configuration is first created."
                : "Stored in the current process and the Windows user environment, not in the Unity project.";
            _copyTokenButton.SetEnabled(!string.IsNullOrEmpty(token));
            _rotateTokenButton.text = string.IsNullOrEmpty(token)
                ? "Generate"
                : "Rotate";
            _rotateTokenButton.SetEnabled(
                !hostOccupied
                && !sessionConnected
                && !sessionConnecting
                && !_serverOperationInProgress);
            _tokenHint.text = string.IsNullOrEmpty(token)
                ? "No token yet. Start or configure a client to create one."
                : DingoCmsEditorServerSettings.TokenIsUserPersistent
                    ? "Available to newly started Codex and Claude Code processes. Restart an already open client after rotation."
                    : "Available only to this Unity process. Export DINGO_CMS_EDITOR_TOKEN before starting a client.";

            _serverToggleButton.text = sessionConnected
                ? "Disconnect"
                : sessionConnecting
                    ? "Connecting…"
                    : "Connect";
            _serverToggleButton.EnableInClassList(
                "danger-button",
                sessionConnected);
            _serverToggleButton.SetEnabled(
                !sessionConnecting && !_serverOperationInProgress);
            _openWebButton.SetEnabled(sessionConnected);
            _copyMcpButton.SetEnabled(true);

            var message = _actionError
                          ?? DingoCmsEditorSessionClient.LastError
                          ?? (detachedIdentityRetained
                              ? DingoCmsDetachedEditorHost.LastError
                              : null)
                          ?? DingoCmsDetachedEditorHost.LastError;
            _serverMessage.text = !string.IsNullOrWhiteSpace(message)
                ? message
                : sessionConnected
                    ? $"Connected as {DingoCmsEditorSessionClient.InstanceId} · session {DingoCmsEditorSessionClient.SessionId} · MCP {DingoCmsEditorServerSettings.McpUrl}"
                    : sessionConnecting
                        ? "Connecting this project to the shared DingoCMS server…"
                        : detachedRunning
                            ? "The local server is running. Connect attaches this project's assets session."
                            : detachedIdentityRetained
                                ? $"PID {DingoCmsDetachedEditorHost.ProcessId} remains retained while ownership is verified."
                                : "Press Start Server to host locally. If another project already runs the server, press Connect.";
            _serverMessage.EnableInClassList(
                "message-error",
                !string.IsNullOrWhiteSpace(message));

            RebuildClients();
        }

        private void RebuildClients()
        {
            if (_clients == null)
            {
                return;
            }
            _clients.Clear();
            var context = DingoCmsEditorServerSettings
                .CreateClientContext();
            foreach (var configurator in
                     DingoCmsMcpClientRegistry.Configurators)
            {
                _clients.Add(CreateClientRow(configurator, context));
            }
        }

        private VisualElement CreateClientRow(
            IDingoCmsMcpClientConfigurator configurator,
            DingoCmsMcpClientContext context)
        {
            var state = configurator.Inspect(context);
            var row = new VisualElement();
            row.AddToClassList("client-row");

            var top = new VisualElement();
            top.AddToClassList("client-top");
            row.Add(top);
            var identity = new VisualElement();
            identity.AddToClassList("client-identity");
            top.Add(identity);
            var name = new Label(configurator.DisplayName);
            name.AddToClassList("client-name");
            identity.Add(name);
            var path = new Label(state.ConfigPath);
            path.AddToClassList("client-path");
            identity.Add(path);

            var status = new Label(StatusText(state.Status));
            status.AddToClassList("client-status");
            status.AddToClassList(StatusClass(state.Status));
            top.Add(status);

            var detail = new Label(state.Message);
            detail.AddToClassList("client-detail");
            row.Add(detail);

            var actions = new VisualElement();
            actions.AddToClassList("client-actions");
            row.Add(actions);
            var configure = new Button(() => RunAction(() =>
            {
                DingoCmsEditorServerSettings.EnsureToken();
                configurator.Configure(
                    DingoCmsEditorServerSettings.CreateClientContext());
                ShowNotification(new GUIContent(
                    $"{configurator.DisplayName} configured"));
            }))
            {
                text = state.Status == DingoCmsMcpClientStatus.NeedsUpdate
                    ? "Update config"
                    : "Configure",
            };
            configure.AddToClassList("client-primary");
            actions.Add(configure);

            var remove = new Button(() =>
            {
                if (!EditorUtility.DisplayDialog(
                        "Remove DingoCMS MCP",
                        $"Remove only the DingoCMS entry from {configurator.DisplayName}?",
                        "Remove",
                        "Cancel"))
                {
                    return;
                }
                RunAction(() =>
                {
                    configurator.Remove(context);
                });
            })
            {
                text = "Remove",
            };
            remove.SetEnabled(
                state.Status == DingoCmsMcpClientStatus.Configured
                || state.Status == DingoCmsMcpClientStatus.NeedsUpdate);
            actions.Add(remove);

            var copy = new Button(() =>
            {
                EditorGUIUtility.systemCopyBuffer =
                    configurator.GetManualConfiguration(context);
                ShowNotification(new GUIContent("Configuration copied"));
            })
            {
                text = "Copy config",
            };
            actions.Add(copy);
            return row;
        }

        private async Task ToggleSessionAsync()
        {
            if (DingoCmsEditorSessionClient.IsConnected)
            {
                await DingoCmsEditorSessionClient.DisconnectAsync();
            }
            else
            {
                await DingoCmsEditorSessionClient.ConnectAsync();
            }
        }

        private async Task ToggleOwnedHostAsync()
        {
            if (_serverOperationInProgress)
                return;

            _serverOperationInProgress = true;
            RefreshAll();
            try
            {
                var state = DingoCmsDetachedEditorHost.ProcessState;
                if (state != DingoCmsDetachedProcessState.Stopped)
                {
                    if (DingoCmsEditorSessionClient.IsConnected
                        || DingoCmsEditorSessionClient.IsConnecting)
                    {
                        await DingoCmsEditorSessionClient.DisconnectAsync();
                    }
                    DingoCmsDetachedEditorHost.Stop();
                    return;
                }

                var existingServer = await DingoCmsEditorSessionClient
                    .ProbeServerAsync(CancellationToken.None);
                if (existingServer ==
                    DingoCmsSharedServerProbeState.Compatible)
                {
                    throw new InvalidOperationException(
                        "A shared DingoCMS server is already running at "
                        + DingoCmsEditorServerSettings.BaseUrl
                        + ". Press Connect to attach this project.");
                }
                if (existingServer ==
                    DingoCmsSharedServerProbeState.Incompatible)
                {
                    throw new InvalidOperationException(
                        "Another process is using "
                        + DingoCmsEditorServerSettings.BaseUrl
                        + " but does not support DingoCMS project sessions. "
                        + "Stop that process before starting this server.");
                }

                DingoCmsDetachedEditorHost.Start();
                var ready = await DingoCmsEditorSessionClient
                    .WaitForServerAsync(
                        TimeSpan.FromSeconds(20),
                        CancellationToken.None);
                if (ready != DingoCmsSharedServerProbeState.Compatible)
                {
                    throw new InvalidOperationException(
                        ready == DingoCmsSharedServerProbeState.Incompatible
                            ? "The local DingoCMS broker started, but its WebSocket bridge protocol is incompatible. Stop it and press Start Server again after updating AppSDK."
                            : "The local DingoCMS broker did not become ready within 20 seconds. Check its log under Manual Server Launch.");
                }
                await DingoCmsEditorSessionClient.ConnectAsync();
            }
            finally
            {
                _serverOperationInProgress = false;
                RefreshAll();
            }
        }

        private void RotateToken()
        {
            var hasToken = DingoCmsEditorServerSettings.HasToken;
            if (hasToken
                && !EditorUtility.DisplayDialog(
                    "Rotate DingoCMS token",
                    "Codex and Claude Code must be restarted after rotation. Continue?",
                    "Rotate",
                    "Cancel"))
            {
                return;
            }
            RunAction(() =>
                DingoCmsEditorServerSettings.RegenerateToken());
        }

        private void BrowseDetachedExecutable()
        {
            var selected = EditorUtility.OpenFilePanel(
                "Select Python runtime for DingoCMS broker",
                System.IO.Path.GetDirectoryName(
                    DingoCmsDetachedEditorHost.ExecutablePath),
                "exe");
            if (string.IsNullOrWhiteSpace(selected))
                return;
            RunAction(() =>
                DingoCmsDetachedEditorHost.ExecutablePath = selected);
        }

        private void RunAction(Action action)
        {
            _actionError = null;
            try
            {
                action();
            }
            catch (Exception exception)
            {
                _actionError = exception.Message;
                Debug.LogException(exception);
            }
            RefreshAll();
        }

        private async void RunAsyncAction(Func<Task> action)
        {
            _actionError = null;
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                _actionError = exception.Message;
                Debug.LogException(exception);
            }
            RefreshAll();
        }

        private static string StatusText(
            DingoCmsMcpClientStatus status)
        {
            return status switch
            {
                DingoCmsMcpClientStatus.Configured => "CONFIGURED",
                DingoCmsMcpClientStatus.NeedsUpdate => "UPDATE",
                DingoCmsMcpClientStatus.NotInstalled => "NOT FOUND",
                DingoCmsMcpClientStatus.Error => "ERROR",
                _ => "NOT CONFIGURED",
            };
        }

        private static string StatusClass(
            DingoCmsMcpClientStatus status)
        {
            return status switch
            {
                DingoCmsMcpClientStatus.Configured => "client-ok",
                DingoCmsMcpClientStatus.NeedsUpdate => "client-warn",
                DingoCmsMcpClientStatus.Error => "client-error",
                _ => "client-idle",
            };
        }
    }
}
#endif
