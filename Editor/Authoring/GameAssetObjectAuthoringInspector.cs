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

            var prefabInstance = GameAssetObjectAuthoringAutoBake.IsPlacedPrefabInstance(authoring);
            var editableSource = GameAssetObjectAuthoringAutoBake.IsEditableRoot(authoring);
            EditorGUILayout.HelpBox("GA is saved in the mounted DingoCMS library. Choose its module in Key.Mod and optionally set a path within that module.", MessageType.Info);
            if (prefabInstance)
            {
                EditorGUILayout.HelpBox("Edit the prefab asset or its Prefab Stage root. Placed prefab instances cannot bake a separate definition.", MessageType.Warning);
            }
            else if (!editableSource)
            {
                EditorGUILayout.HelpBox("The Unity source is read-only. GameAsset authoring is unavailable in this Editor.", MessageType.Info);
            }
            using (new EditorGUI.DisabledScope(prefabInstance || !editableSource))
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
            if (GameAssetObjectAuthoringAutoBake.IsPlacedPrefabInstance(authoring))
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
            if (create)
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null && stage.scene == authoring.gameObject.scene && stage.scene.isDirty)
                {
                    GameAssetObjectAuthoringAutoBake.SetStatus(authoring, "Save the prefab before creating its GameAsset.", MessageType.Error);
                    return false;
                }
            }
            if (!create && !IsSameSource(authoring.AuthoringSourceId, sourceId))
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
            if (GameAssetObjectAuthoringAutoBake.IsPlacedPrefabInstance(authoring))
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, "Edit the prefab source before resetting its GameAsset link.", MessageType.Error);
                return;
            }
            if (!GameAssetObjectAuthoringAutoBake.IsEditableRoot(authoring))
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, "The GameAsset authoring source is not editable.", MessageType.Error);
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
            if (GameAssetObjectAuthoringAutoBake.IsPlacedPrefabInstance(authoring))
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, "Edit the prefab source before deleting its GameAsset.", MessageType.Error);
                return;
            }
            if (!authoring.HasAsset || string.IsNullOrWhiteSpace(authoring.DocumentSha256))
            {
                GameAssetObjectAuthoringAutoBake.SetStatus(authoring, "The authoring component has no complete saved GameAsset link.", MessageType.Error);
                return;
            }
            if (!TryGetSourceId(authoring, out var sourceId, out var sourceError) || !IsSameSource(authoring.AuthoringSourceId, sourceId))
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

        private static bool IsSameSource(string linkedSourceId, string sourceId)
        {
            if (linkedSourceId == sourceId)
                return true;
            if (string.IsNullOrEmpty(linkedSourceId) || !GlobalObjectId.TryParse(linkedSourceId, out var linkedId) || !GlobalObjectId.TryParse(sourceId, out var currentId))
                return false;
            return linkedId.identifierType == 2 && currentId.identifierType == 1 && linkedId.assetGUID.Equals(currentId.assetGUID) && linkedId.targetObjectId == currentId.targetObjectId && linkedId.targetPrefabId == currentId.targetPrefabId;
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

            var persistent = EditorUtility.IsPersistent(authoring);
            var stage = persistent ? null : PrefabStageUtility.GetCurrentPrefabStage();
            var inPrefabStage = stage != null && stage.scene == authoring.gameObject.scene && stage.prefabContentsRoot != null;
            if (persistent)
            {
                if (string.IsNullOrWhiteSpace(AssetDatabase.GetAssetPath(authoring)))
                {
                    error = "Save the prefab asset before creating its GameAsset.";
                    return false;
                }
            }
            else if (!inPrefabStage)
            {
                if (string.IsNullOrWhiteSpace(authoring.gameObject.scene.path))
                {
                    error = "Save the scene before creating its GameAsset, so the authoring source keeps a stable identity.";
                    return false;
                }
            }
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(authoring);
            if (globalId.assetGUID.Equals(default(GUID)))
            {
                error = "Unity did not provide a stable scene or prefab object identity. Save the source and try again.";
                return false;
            }
            if (inPrefabStage)
            {
                var assetGuid = AssetDatabase.GUIDFromAssetPath(stage.assetPath);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(stage.assetPath);
                if (assetGuid.Equals(default(GUID)) || !globalId.assetGUID.Equals(assetGuid) || prefab == null)
                {
                    error = "Save the prefab before creating or baking its GameAsset.";
                    return false;
                }

                var stageSource = PrefabUtility.GetCorrespondingObjectFromSource(authoring);
                var sources = prefab.GetComponentsInChildren<GameAssetObjectAuthoring>(true);
                for (var i = 0; i < sources.Length; i++)
                {
                    var candidateId = GlobalObjectId.GetGlobalObjectIdSlow(sources[i]);
                    if (candidateId.identifierType != 1 || !candidateId.assetGUID.Equals(assetGuid))
                        continue;
                    var sameObjectId = candidateId.targetObjectId == globalId.targetObjectId && candidateId.targetPrefabId == globalId.targetPrefabId;
                    // An inherited Variant root has a Stage instance ID; its saved root shares the same immediate source.
                    var sameVariantRoot = authoring.gameObject == stage.prefabContentsRoot && sources[i].gameObject == prefab && EditorUtility.IsPersistent(sources[i]) && AssetDatabase.GetAssetPath(sources[i]) == stage.assetPath && stageSource != null && stageSource == PrefabUtility.GetCorrespondingObjectFromSource(sources[i]);
                    if (!sameObjectId && !sameVariantRoot)
                        continue;
                    if (sourceId != null)
                    {
                        error = "The prefab contains more than one GameAsset authoring source with the same identity.";
                        return false;
                    }
                    sourceId = candidateId.ToString();
                }
                if (sourceId == null)
                {
                    error = "Save the prefab before creating or baking its GameAsset.";
                    return false;
                }
                return true;
            }

            sourceId = globalId.ToString();
            return true;
        }
    }
}
#endif
