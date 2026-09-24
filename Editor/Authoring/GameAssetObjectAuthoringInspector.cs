#if NEWTONSOFT_EXISTS
using DingoGameObjectsCMS.AssetObjects.Authoring;
using NaughtyAttributes.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DingoGameObjectsCMSEditorServer.Editor.Authoring
{
    [CustomEditor(typeof(GameAssetObjectAuthoring), true)]
    public class GameAssetObjectAuthoringInspector : NaughtyInspector
    {
        public override void OnInspectorGUI()
        {
            var authoring = (GameAssetObjectAuthoring)target;
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(authoring.HasAsset))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_key"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_relativeJsonPath"));
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_autoBake"));
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_assetGuid"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_authoringSourceId"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_documentSha256"));
            }
            serializedObject.ApplyModifiedProperties();

            var prefabInstance = PrefabUtility.IsPartOfPrefabInstance(authoring);
            EditorGUILayout.HelpBox("GA is saved in the mounted DingoCMS library. Choose its module in Key.Mod and optionally set a path within that module.", MessageType.Info);
            if (prefabInstance)
            {
                EditorGUILayout.HelpBox("Edit the prefab source to author its shared GameAsset. Prefab instances cannot bake a separate definition.", MessageType.Warning);
            }
            using (new EditorGUI.DisabledScope(prefabInstance))
            {
                if (!authoring.HasAsset && GUILayout.Button("Create GA"))
                {
                    Save(authoring, create: true);
                }
                using (new EditorGUI.DisabledScope(!authoring.HasAsset))
                {
                    DrawButtons();
                }
            }
            if (GameAssetObjectAuthoringAutoBake.TryGetStatus(authoring, out var status, out var type))
            {
                EditorGUILayout.HelpBox(status, type);
            }
        }

        public static bool Save(GameAssetObjectAuthoring authoring, bool create)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(authoring))
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, "Edit the prefab source before baking its GameAsset.", MessageType.Error);
                return false;
            }
            if (create && authoring.HasAsset)
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, "This object already owns a GameAsset. Reset or delete its current link before creating another.", MessageType.Error);
                return false;
            }
            if (!TryGetSourceId(authoring, out var sourceId, out var sourceError))
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, sourceError, MessageType.Error);
                return false;
            }
            if (!create && authoring.AuthoringSourceId != sourceId)
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, "This GameAsset link belongs to another scene or prefab object. Its copied authoring root cannot bake the same GA.", MessageType.Error);
                return false;
            }
            if (!GameAssetObjectAuthoringCollector.TryCollect(authoring, out var components, out var error))
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, error, MessageType.Error);
                return false;
            }

            GameAssetObjectAuthoringSaveResult result;
            var succeeded = create
                ? GameAssetObjectAuthoringPublisher.TryCreate(authoring.Key, authoring.RelativeJsonPath, components, out result, out error)
                : GameAssetObjectAuthoringPublisher.TryBake(authoring.Key, authoring.RelativeJsonPath, authoring.AssetGuid, authoring.DocumentSha256, components, out result, out error);
            if (!succeeded)
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, error, MessageType.Error);
                return false;
            }

            if (authoring.RelativeJsonPath != result.RelativeJsonPath || authoring.AssetGuid != result.AssetGuid || authoring.DocumentSha256 != result.DocumentSha256 || authoring.AuthoringSourceId != sourceId)
            {
                var serialized = new SerializedObject(authoring);
                serialized.FindProperty("_relativeJsonPath").stringValue = result.RelativeJsonPath;
                serialized.FindProperty("_assetGuid").stringValue = result.AssetGuid;
                serialized.FindProperty("_documentSha256").stringValue = result.DocumentSha256;
                serialized.FindProperty("_authoringSourceId").stringValue = sourceId;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(authoring);
            }
            GameAssetObjectAuthoringAutoBake.SetStatus(authoring, result.Changed ? "GA saved in DingoCMS." : "GA is up to date.", MessageType.Info);
            return true;
        }

        public static void ResetLink(GameAssetObjectAuthoring authoring)
        {
            if (authoring == null)
            {
                return;
            }
            if (PrefabUtility.IsPartOfPrefabInstance(authoring))
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, "Edit the prefab source before resetting its GameAsset link.", MessageType.Error);
                return;
            }
            if (string.IsNullOrEmpty(authoring.AssetGuid) && string.IsNullOrEmpty(authoring.DocumentSha256))
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, "The GameAsset link is already empty.", MessageType.Info);
                return;
            }

            ClearLink(authoring, "Local link cleared; the CMS document remains saved. To create another GA, choose a new key/path or delete the existing document.", recordUndo: true);
        }

        public static void DeleteAsset(GameAssetObjectAuthoring authoring)
        {
            if (authoring == null)
            {
                return;
            }
            if (PrefabUtility.IsPartOfPrefabInstance(authoring))
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, "Edit the prefab source before deleting its GameAsset.", MessageType.Error);
                return;
            }
            if (!authoring.HasAsset || string.IsNullOrWhiteSpace(authoring.DocumentSha256))
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, "The authoring component has no complete saved GameAsset link.", MessageType.Error);
                return;
            }
            if (!TryGetSourceId(authoring, out var sourceId, out var sourceError) || authoring.AuthoringSourceId != sourceId)
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, sourceError ?? "This copied authoring root does not own the linked GameAsset.", MessageType.Error);
                return;
            }
            var path = string.IsNullOrWhiteSpace(authoring.RelativeJsonPath) ? "<path from key>" : authoring.RelativeJsonPath;
            var message = $"Delete GameAsset {authoring.Key} from module '{authoring.Key.Mod}' at '{path}'?\n\nThis removes the CMS document and cannot be undone by Unity Undo.";
            if (!EditorUtility.DisplayDialog("Delete GameAsset", message, "Delete GA", "Cancel"))
            {
                return;
            }
            if (!GameAssetObjectAuthoringPublisher.TryDelete(authoring.Key, authoring.RelativeJsonPath, authoring.AssetGuid, authoring.DocumentSha256, out var error))
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, error, MessageType.Error);
                return;
            }
            ClearLink(authoring, "GameAsset deleted from DingoCMS. Local link cleared; Unity Undo cannot restore the CMS document.", recordUndo: false);
        }

        private static void ClearLink(GameAssetObjectAuthoring authoring, string status, bool recordUndo)
        {
            var serialized = new SerializedObject(authoring);
            if (recordUndo)
            {
                Undo.RecordObject(authoring, "Reset GameAsset Link");
            }
            serialized.FindProperty("_assetGuid").stringValue = string.Empty;
            serialized.FindProperty("_documentSha256").stringValue = string.Empty;
            serialized.FindProperty("_authoringSourceId").stringValue = string.Empty;
            if (recordUndo)
            {
                serialized.ApplyModifiedProperties();
            }
            else
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            EditorUtility.SetDirty(authoring);
            GameAssetObjectAuthoringAutoBake.SetStatus(authoring, status, MessageType.Info);
        }

        private static bool TryGetSourceId(GameAssetObjectAuthoring authoring, out string sourceId, out string error)
        {
            sourceId = null;
            error = null;
            if (!GameAssetObjectAuthoringAutoBake.IsEditableRoot(authoring))
            {
                error = "The GameAsset authoring root must be in an editable scene or prefab source.";
                return false;
            }

            if (EditorUtility.IsPersistent(authoring))
            {
                if (string.IsNullOrWhiteSpace(AssetDatabase.GetAssetPath(authoring)))
                {
                    error = "Save the prefab asset before creating its GameAsset.";
                    return false;
                }
            }
            else
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                var prefabStageRoot = stage != null && stage.scene == authoring.gameObject.scene && stage.prefabContentsRoot != null;
                if (!prefabStageRoot && string.IsNullOrWhiteSpace(authoring.gameObject.scene.path))
                {
                    error = "Save the scene before creating its GameAsset, so the authoring source keeps a stable identity.";
                    return false;
                }
            }
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(authoring);
            sourceId = globalId.ToString();
            if (globalId.assetGUID.Equals(default(GUID)))
            {
                error = "Unity did not provide a stable scene or prefab object identity. Save the source and try again.";
                return false;
            }
            return true;
        }
    }
}
#endif
