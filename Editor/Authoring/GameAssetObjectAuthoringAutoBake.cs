#if NEWTONSOFT_EXISTS
using System.Collections.Concurrent;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DingoGameObjectsCMSEditorServer.Editor.Authoring
{
    [InitializeOnLoad]
    public static class GameAssetObjectAuthoringAutoBake
    {
        private const double BAKE_DELAY_SECONDS = 0.35;
        private static readonly ConcurrentQueue<GameAssetObjectAuthoring> Incoming = new();
        private static readonly Dictionary<EntityId, PendingBake> Pending = new();
        private static readonly Dictionary<EntityId, BakeStatus> Statuses = new();
        private static readonly List<EntityId> Ready = new();

        static GameAssetObjectAuthoringAutoBake()
        {
            GameAssetObjectAuthoring.Validated += Queue;
            GameAssetObjectAuthoring.ResetRequested += GameAssetObjectAuthoringInspector.ResetLink;
            GameAssetObjectAuthoring.RefreshRequested += Refresh;
            GameAssetObjectAuthoring.DeleteRequested += GameAssetObjectAuthoringInspector.DeleteAsset;
            Undo.postprocessModifications += OnPostprocessModifications;
            Undo.undoRedoPerformed += QueueLoadedRoots;
            EditorApplication.hierarchyChanged += QueueLoadedRoots;
            EditorApplication.update += Process;
        }

        public static bool TryGetStatus(GameAssetObjectAuthoring source, out string message, out MessageType type)
        {
            if (source != null && Statuses.TryGetValue(source.GetEntityId(), out var status))
            {
                message = status.Message;
                type = status.Type;
                return true;
            }
            message = null;
            type = MessageType.None;
            return false;
        }

        public static void SetStatus(GameAssetObjectAuthoring source, string message, MessageType type)
        {
            if (source == null)
            {
                return;
            }
            Statuses[source.GetEntityId()] = new BakeStatus(message, type);
        }

        public static bool IsEditableRoot(GameAssetObjectAuthoring source)
        {
            if (source == null || source.hideFlags != HideFlags.None || source.gameObject.hideFlags != HideFlags.None)
            {
                return false;
            }
            if (EditorUtility.IsPersistent(source))
            {
                return AssetDatabase.GetAssetPath(source).EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase);
            }

            var scene = source.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return false;
            }
            if (!EditorSceneManager.IsPreviewScene(scene))
            {
                return true;
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            return stage != null && stage.scene == scene && stage.prefabContentsRoot != null && (source.gameObject == stage.prefabContentsRoot || source.transform.IsChildOf(stage.prefabContentsRoot.transform));
        }

        private static void Queue(GameAssetObjectAuthoring source)
        {
            Incoming.Enqueue(source);
        }

        private static void Refresh(GameAssetObjectAuthoring source)
        {
            if (source != null)
            {
                GameAssetObjectAuthoringInspector.Save(source, create: false);
            }
        }

        private static UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
        {
            for (var i = 0; i < modifications.Length; i++)
            {
                var target = modifications[i].currentValue.target;
                var component = target as Component;
                if (component == null && target is GameObject gameObject)
                {
                    component = gameObject.transform;
                }
                if (component != null)
                {
                    var root = component.GetComponentInParent<GameAssetObjectAuthoring>();
                    if (root != null)
                    {
                        Queue(root);
                    }
                }
            }
            return modifications;
        }

        private static void QueueLoadedRoots()
        {
            var roots = Resources.FindObjectsOfTypeAll<GameAssetObjectAuthoring>();
            for (var i = 0; i < roots.Length; i++)
            {
                if (!EditorUtility.IsPersistent(roots[i]) && IsEditableRoot(roots[i]))
                {
                    Queue(roots[i]);
                }
            }
        }

        private static void Process()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                while (Incoming.TryDequeue(out _)) { }
                Pending.Clear();
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            while (Incoming.TryDequeue(out var source))
            {
                if (source != null)
                {
                    Pending[source.GetEntityId()] = new PendingBake(source, now + BAKE_DELAY_SECONDS);
                }
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            Ready.Clear();
            foreach (var pair in Pending)
            {
                if (pair.Value.DueTime <= now)
                {
                    Ready.Add(pair.Key);
                }
            }
            for (var i = 0; i < Ready.Count; i++)
            {
                var id = Ready[i];
                var source = Pending[id].Source;
                Pending.Remove(id);
                if (!IsEditableRoot(source) || !source.AutoBake || !source.HasAsset || PrefabUtility.IsPartOfPrefabInstance(source))
                {
                    continue;
                }
                GameAssetObjectAuthoringInspector.Save(source, create: false);
            }
        }

        private readonly struct PendingBake
        {
            public readonly GameAssetObjectAuthoring Source;
            public readonly double DueTime;

            public PendingBake(GameAssetObjectAuthoring source, double dueTime)
            {
                Source = source;
                DueTime = dueTime;
            }
        }

        private readonly struct BakeStatus
        {
            public readonly string Message;
            public readonly MessageType Type;

            public BakeStatus(string message, MessageType type)
            {
                Message = message;
                Type = type;
            }
        }
    }
}
#endif
