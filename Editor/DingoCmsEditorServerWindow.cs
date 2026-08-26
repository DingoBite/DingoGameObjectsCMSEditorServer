#if NEWTONSOFT_EXISTS
using System;
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

        private Label _serverStatus;
        private Label _serverMessage;
        private Label _tokenHint;
        private Label _hostDetail;
        private IntegerField _portField;
        private EnumField _hostModeField;
        private TextField _detachedExecutableField;
        private TextField _tokenField;
        private Button _serverToggleButton;
        private Button _openWebButton;
        private Button _copyMcpButton;
        private Button _copyTokenButton;
        private Button _rotateTokenButton;
        private Button _browseExecutableButton;
        private Button _buildDetachedHostButton;
        private Toggle _autoStartToggle;
        private VisualElement _clients;
        private string _actionError;

        [MenuItem(MENU_PATH)]
        public static void Open()
        {
            var window = GetWindow<DingoCmsEditorServerWindow>();
            window.titleContent = new GUIContent("DingoCMS Server");
            window.minSize = new Vector2(520f, 620f);
            window.Show();
        }

        private void OnEnable()
        {
            DingoCmsEditorServerEditorHost.StateChanged -= RefreshAll;
            DingoCmsEditorServerEditorHost.StateChanged += RefreshAll;
            DingoCmsDetachedEditorHost.StateChanged -= RefreshAll;
            DingoCmsDetachedEditorHost.StateChanged += RefreshAll;
        }

        private void OnDisable()
        {
            DingoCmsEditorServerEditorHost.StateChanged -= RefreshAll;
            DingoCmsDetachedEditorHost.StateChanged -= RefreshAll;
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

            var heroRow = new VisualElement();
            heroRow.AddToClassList("hero-row");
            hero.Add(heroRow);

            var heading = new VisualElement();
            heading.AddToClassList("hero-heading");
            heroRow.Add(heading);
            var title = new Label("Editor Server");
            title.AddToClassList("title");
            heading.Add(title);
            var subtitle = new Label(
                "Edit the mounted AppData library without entering Play Mode.");
            subtitle.AddToClassList("subtitle");
            heading.Add(subtitle);

            _serverStatus = new Label();
            _serverStatus.AddToClassList("status-chip");
            heroRow.Add(_serverStatus);

            var serverCard = CreateCard(
                "SERVER",
                "Detached mode runs authoring in its own hidden player process. It survives domain reloads, Play Mode and Unity restarts; the tracked PID keeps Stop safe and exact.");
            scroll.Add(serverCard);

            serverCard.Add(CreateReadOnlyField(
                "Assets root",
                DingoCmsEditorServerEditorHost.AssetsRoot));

            _hostModeField = new EnumField(
                "Host mode",
                DingoCmsDetachedEditorHost.Mode);
            _hostModeField.AddToClassList("form-field");
            _hostModeField.RegisterValueChangedCallback(change => RunAction(
                () => DingoCmsDetachedEditorHost.Mode =
                    (DingoCmsEditorHostMode)change.newValue));
            serverCard.Add(_hostModeField);

            var executableRow = new VisualElement();
            executableRow.AddToClassList("executable-row");
            serverCard.Add(executableRow);
            _detachedExecutableField = new TextField("Player executable")
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
            _buildDetachedHostButton = new Button(() => RunAction(
                DingoCmsDetachedEditorHost.BuildPlayer))
            {
                text = "Build host",
            };
            _buildDetachedHostButton.AddToClassList("compact-button");
            executableRow.Add(_buildDetachedHostButton);

            _hostDetail = new Label();
            _hostDetail.AddToClassList("host-detail");
            serverCard.Add(_hostDetail);

            _portField = new IntegerField("Loopback port");
            _portField.AddToClassList("form-field");
            _portField.RegisterValueChangedCallback(change =>
            {
                RunAction(() =>
                    DingoCmsEditorServerEditorHost.Port = change.newValue);
            });
            serverCard.Add(_portField);

            var tokenRow = new VisualElement();
            tokenRow.AddToClassList("token-row");
            serverCard.Add(tokenRow);
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
                    DingoCmsEditorServerEditorHost.Token;
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
            serverCard.Add(_tokenHint);

            _autoStartToggle = new Toggle(
                "Start with Unity Editor (Edit Mode only)");
            _autoStartToggle.AddToClassList("auto-start");
            _autoStartToggle.tooltip =
                "Off by default. Enabling this affects the next Unity Editor session; it never enables a gameplay feature.";
            _autoStartToggle.RegisterValueChangedCallback(change =>
                DingoCmsEditorServerEditorHost.AutoStart = change.newValue);
            serverCard.Add(_autoStartToggle);

            var actionRail = new VisualElement();
            actionRail.AddToClassList("action-rail");
            serverCard.Add(actionRail);
            _serverToggleButton = new Button(ToggleServer);
            _serverToggleButton.AddToClassList("primary-button");
            actionRail.Add(_serverToggleButton);
            _openWebButton = new Button(() => RunAction(OpenWebEditor))
            {
                text = "Open Web editor",
            };
            actionRail.Add(_openWebButton);
            _copyMcpButton = new Button(() =>
            {
                EditorGUIUtility.systemCopyBuffer =
                    DingoCmsEditorServerEditorHost.McpUrl;
                ShowNotification(new GUIContent("MCP URL copied"));
            })
            {
                text = "Copy MCP URL",
            };
            actionRail.Add(_copyMcpButton);

            _serverMessage = new Label();
            _serverMessage.AddToClassList("server-message");
            serverCard.Add(_serverMessage);

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
            if (_serverStatus == null)
            {
                return;
            }

            var detachedMode = DingoCmsDetachedEditorHost.Mode
                               == DingoCmsEditorHostMode.DetachedProcess;
            var detachedState = DingoCmsDetachedEditorHost.ProcessState;
            var detachedRunning = detachedState
                                  == DingoCmsDetachedProcessState.Running;
            var detachedIdentityRetained = detachedState
                                           != DingoCmsDetachedProcessState.Stopped;
            var unityRunning = DingoCmsEditorServerEditorHost.IsRunning;
            var running = detachedRunning || unityRunning;
            var hostOccupied = detachedIdentityRetained || unityRunning;
            _serverStatus.text = detachedRunning
                ? $"●  DETACHED · {DingoCmsDetachedEditorHost.ProcessId} · VERIFIED"
                : detachedIdentityRetained
                    ? $"◐  DETACHED · {DingoCmsDetachedEditorHost.ProcessId} · VERIFYING"
                : unityRunning
                    ? "●  UNITY PROCESS"
                    : "○  STOPPED";
            _serverStatus.EnableInClassList(
                "status-running",
                running);
            _serverStatus.EnableInClassList(
                "status-pending",
                detachedIdentityRetained && !detachedRunning);
            _serverStatus.EnableInClassList(
                "status-stopped",
                !hostOccupied);

            _hostModeField.SetValueWithoutNotify(
                DingoCmsDetachedEditorHost.Mode);
            _hostModeField.SetEnabled(!hostOccupied);
            _detachedExecutableField.SetValueWithoutNotify(
                DingoCmsDetachedEditorHost.ExecutablePath);
            _detachedExecutableField.SetEnabled(
                detachedMode && !hostOccupied);
            _browseExecutableButton.SetEnabled(
                detachedMode && !hostOccupied);
            _buildDetachedHostButton.SetEnabled(
                detachedMode && !hostOccupied);
            _hostDetail.text = detachedMode
                ? detachedRunning
                    ? $"Owned PID {DingoCmsDetachedEditorHost.ProcessId} · executable verified · log: "
                      + DingoCmsDetachedEditorHost.LogPath
                    : detachedIdentityRetained
                        ? $"PID {DingoCmsDetachedEditorHost.ProcessId} is retained, but executable ownership could not be verified on this poll. Start, Stop and host configuration stay locked."
                    : "Detached host is independent from Unity. Build or "
                      + "select a player, then start it once."
                : "Legacy in-process mode stops for compilation and exits "
                  + "with Unity.";

            _portField.SetValueWithoutNotify(
                DingoCmsEditorServerEditorHost.Port);
            _portField.SetEnabled(!hostOccupied);
            _autoStartToggle.SetValueWithoutNotify(
                DingoCmsEditorServerEditorHost.AutoStart);
            _autoStartToggle.SetEnabled(!detachedMode && !hostOccupied);

            var token = DingoCmsEditorServerEditorHost.Token;
            _tokenField.SetValueWithoutNotify(token ?? string.Empty);
            _tokenField.tooltip = string.IsNullOrEmpty(token)
                ? "Generated when the server or a client configuration is first created."
                : "Stored in the current process and the Windows user environment, not in the Unity project.";
            _copyTokenButton.SetEnabled(!string.IsNullOrEmpty(token));
            _rotateTokenButton.text = string.IsNullOrEmpty(token)
                ? "Generate"
                : "Rotate";
            _rotateTokenButton.SetEnabled(!hostOccupied);
            _tokenHint.text = string.IsNullOrEmpty(token)
                ? "No token yet. Start or configure a client to create one."
                : DingoCmsEditorServerEditorHost.TokenIsUserPersistent
                    ? "Available to newly started Codex and Claude Code processes. Restart an already open client after rotation."
                    : "Available only to this Unity process. Export DINGO_CMS_EDITOR_TOKEN before starting a client.";

            _serverToggleButton.text = detachedIdentityRetained
                                       && !detachedRunning
                ? "Ownership check pending"
                : running
                ? detachedRunning
                    ? "Stop detached host"
                    : "Stop Unity host"
                : detachedMode
                    ? "Start detached host"
                    : "Start Unity host";
            _serverToggleButton.EnableInClassList(
                "danger-button",
                running);
            _serverToggleButton.SetEnabled(
                !detachedIdentityRetained || detachedRunning);
            _openWebButton.SetEnabled(running);
            _copyMcpButton.SetEnabled(true);

            var message = _actionError
                          ?? DingoCmsDetachedEditorHost.LastError
                          ?? DingoCmsEditorServerEditorHost.LastError;
            _serverMessage.text = !string.IsNullOrWhiteSpace(message)
                ? message
                : detachedIdentityRetained && !detachedRunning
                    ? $"PID {DingoCmsDetachedEditorHost.ProcessId} remains retained. DingoCMS will not start a duplicate or discard ownership until that exact executable is verified or is confirmed gone."
                : running
                    ? $"Listening at {DingoCmsEditorServerEditorHost.McpUrl}"
                    : detachedMode
                        ? "Ready. The detached host remains alive when Unity "
                          + "restarts."
                        : "Ready. Starting the server does not start the game.";
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
            var context = DingoCmsEditorServerEditorHost
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
                DingoCmsEditorServerEditorHost.EnsureToken();
                configurator.Configure(
                    DingoCmsEditorServerEditorHost.CreateClientContext());
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

        private void ToggleServer()
        {
            RunAction(() =>
            {
                if (DingoCmsDetachedEditorHost.IsRunning)
                {
                    DingoCmsDetachedEditorHost.Stop();
                }
                else if (DingoCmsEditorServerEditorHost.IsRunning)
                {
                    DingoCmsEditorServerEditorHost.Stop();
                }
                else if (DingoCmsDetachedEditorHost.Mode
                         == DingoCmsEditorHostMode.DetachedProcess)
                {
                    DingoCmsDetachedEditorHost.Start();
                }
                else
                {
                    DingoCmsEditorServerEditorHost.Start();
                }
            });
        }

        private void RotateToken()
        {
            var hasToken = DingoCmsEditorServerEditorHost.HasToken;
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
                DingoCmsEditorServerEditorHost.RegenerateToken());
        }

        private void BrowseDetachedExecutable()
        {
            var selected = EditorUtility.OpenFilePanel(
                "Select DingoCMS detached player",
                System.IO.Path.GetDirectoryName(
                    DingoCmsDetachedEditorHost.ExecutablePath),
                "exe");
            if (string.IsNullOrWhiteSpace(selected))
                return;
            RunAction(() =>
                DingoCmsDetachedEditorHost.ExecutablePath = selected);
        }

        private static void OpenWebEditor()
        {
            if (DingoCmsDetachedEditorHost.IsRunning)
            {
                DingoCmsDetachedEditorHost.OpenWebEditor();
                return;
            }
            DingoCmsEditorServerEditorHost.OpenWebEditor();
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
