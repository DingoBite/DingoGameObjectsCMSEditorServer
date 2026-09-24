#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DingoGameObjectsCMSEditorServer.Editor.Authoring
{
    public readonly struct GameAssetObjectAuthoringSaveResult
    {
        public readonly string RelativeJsonPath;
        public readonly string AssetGuid;
        public readonly string DocumentSha256;
        public readonly bool Changed;

        public GameAssetObjectAuthoringSaveResult(string relativeJsonPath, string assetGuid, string documentSha256, bool changed)
        {
            RelativeJsonPath = relativeJsonPath;
            AssetGuid = assetGuid;
            DocumentSha256 = documentSha256;
            Changed = changed;
        }
    }

    public static class GameAssetObjectAuthoringPublisher
    {
        public static bool TryCreate(GameAssetKey key, string relativeJsonPath, IReadOnlyList<GameAssetComponent> components, out GameAssetObjectAuthoringSaveResult result, out string error)
        {
            var saved = default(GameAssetObjectAuthoringSaveResult);
            var failure = string.Empty;
            try
            {
                var succeeded = DingoCmsEditorSessionClient.UseLocalAuthoring(execute => TryCreateWithExecutor(execute, key, relativeJsonPath, components, out saved, out failure));
                result = saved;
                error = failure;
                return succeeded;
            }
            catch (Exception exception)
            {
                result = default;
                error = exception.Message;
                return false;
            }
        }

        public static bool TryBake(GameAssetKey key, string relativeJsonPath, string assetGuid, string documentSha256, IReadOnlyList<GameAssetComponent> components, out GameAssetObjectAuthoringSaveResult result, out string error)
        {
            var saved = default(GameAssetObjectAuthoringSaveResult);
            var failure = string.Empty;
            try
            {
                var succeeded = DingoCmsEditorSessionClient.UseLocalAuthoring(execute => TryBakeWithExecutor(execute, key, relativeJsonPath, assetGuid, documentSha256, components, out saved, out failure));
                result = saved;
                error = failure;
                return succeeded;
            }
            catch (Exception exception)
            {
                result = default;
                error = exception.Message;
                return false;
            }
        }

        public static bool TryDelete(GameAssetKey key, string relativeJsonPath, string assetGuid, string documentSha256, out string error)
        {
            var failure = string.Empty;
            try
            {
                var succeeded = DingoCmsEditorSessionClient.UseLocalAuthoring(execute => TryDeleteWithExecutor(execute, key, relativeJsonPath, assetGuid, documentSha256, out failure));
                error = failure;
                return succeeded;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static bool TryCreateWithExecutor(Func<string, JObject, JObject> execute, GameAssetKey key, string relativeJsonPath, IReadOnlyList<GameAssetComponent> components, out GameAssetObjectAuthoringSaveResult result, out string error)
        {
            result = default;
            error = string.Empty;
            if (!TryBuildNewDocument(key, components, out var document, out error))
            {
                return false;
            }
            if (!TryExecute(execute, "modules_list", new JObject(), out var modules, out error))
            {
                return false;
            }

            var contentHash = string.Empty;
            if (modules["modules"] is JArray entries)
            {
                foreach (var token in entries)
                {
                    if (token is JObject entry && string.Equals(entry.Value<string>("moduleId"), key.Mod, StringComparison.Ordinal))
                    {
                        contentHash = entry.Value<string>("contentHash") ?? string.Empty;
                        break;
                    }
                }
            }

            if (!TryExecute(execute, "changeset_begin", new JObject { ["moduleId"] = key.Mod, ["expectedContentHash"] = contentHash }, out var started, out error))
            {
                return false;
            }

            var changesetId = started.Value<string>("changesetId");
            var committed = false;
            var created = default(GameAssetObjectAuthoringSaveResult);
            try
            {
                var operation = new JObject { ["kind"] = "createAsset", ["document"] = document };
                if (!string.IsNullOrWhiteSpace(relativeJsonPath))
                {
                    operation["relativeJsonPath"] = relativeJsonPath;
                }
                if (!TryExecute(execute, "changeset_apply", new JObject { ["changesetId"] = changesetId, ["operations"] = new JArray { operation } }, out _, out error))
                {
                    return false;
                }
                if (!TryExecute(execute, "asset_get", new JObject { ["moduleId"] = key.Mod, ["changesetId"] = changesetId, ["key"] = document["Key"]?.DeepClone() }, out var staged, out error))
                {
                    return false;
                }
                created = ReadResult(staged, changed: true);
                if (string.IsNullOrWhiteSpace(created.RelativeJsonPath) || string.IsNullOrWhiteSpace(created.AssetGuid) || string.IsNullOrWhiteSpace(created.DocumentSha256))
                {
                    error = "DingoCMS did not return the staged GameAsset identity and document hash.";
                    return false;
                }
                if (!TryExecute(execute, "changeset_validate", new JObject { ["changesetId"] = changesetId }, out var validation, out error))
                {
                    return false;
                }
                if (validation.Value<bool?>("valid") != true)
                {
                    error = validation.SelectToken("diagnostics[0].message")?.Value<string>() ?? "DingoCMS rejected the generated GameAsset.";
                    return false;
                }
                if (!TryExecute(execute, "changeset_commit", new JObject { ["changesetId"] = changesetId }, out _, out error))
                {
                    return false;
                }
                committed = true;
                result = created;
                return true;
            }
            finally
            {
                if (!committed && !string.IsNullOrWhiteSpace(changesetId))
                {
                    TryExecute(execute, "changeset_abort", new JObject { ["changesetId"] = changesetId }, out _, out _);
                }
            }

        }

        public static bool TryBakeWithExecutor(Func<string, JObject, JObject> execute, GameAssetKey key, string relativeJsonPath, string assetGuid, string documentSha256, IReadOnlyList<GameAssetComponent> components, out GameAssetObjectAuthoringSaveResult result, out string error)
        {
            result = default;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(assetGuid) || string.IsNullOrWhiteSpace(documentSha256))
            {
                error = "The authoring component has no saved GameAsset identity and document hash. Create or reconnect it before baking.";
                return false;
            }
            var selector = new JObject { ["moduleId"] = key.Mod };
            if (string.IsNullOrWhiteSpace(relativeJsonPath))
            {
                selector["key"] = JObject.FromObject(key, GameAssetJson.JsonSerializer);
            }
            else
            {
                selector["relativeJsonPath"] = relativeJsonPath;
            }
            if (!TryExecute(execute, "asset_get", selector, out var current, out error))
            {
                return false;
            }
            if (current["document"] is not JObject currentDocument)
            {
                error = "DingoCMS did not return a GameAsset document.";
                return false;
            }
            if (!string.Equals(current.Value<string>("guid"), assetGuid, StringComparison.OrdinalIgnoreCase))
            {
                error = "The saved GameAsset GUID changed. Create or reconnect the authored object before baking.";
                return false;
            }
            if (!string.Equals(current.Value<string>("documentSha256"), documentSha256, StringComparison.Ordinal))
            {
                error = "The GameAsset changed outside this authoring component. Reload its source before baking.";
                return false;
            }

            var asset = GameAssetJson.FromJObject(currentDocument) as GameAsset;
            if (asset == null)
            {
                error = "The selected DingoCMS document is not a GameAsset.";
                return false;
            }
            JObject nextDocument;
            try
            {
                if (asset.Key != key)
                {
                    error = "The authored GameAsset key differs from the saved key. Create a new GameAsset for the new key.";
                    return false;
                }
                asset.SetComponents(components);
                nextDocument = JObject.Parse(asset.ToJson());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
            if (JToken.DeepEquals(currentDocument, nextDocument))
            {
                result = ReadResult(current, changed: false);
                return true;
            }

            var save = new JObject
            {
                ["moduleId"] = key.Mod,
                ["relativeJsonPath"] = current.Value<string>("relativeJsonPath"),
                ["expectedDocumentSha256"] = current.Value<string>("documentSha256"),
                ["document"] = nextDocument
            };
            if (!TryExecute(execute, "asset_save", save, out var saved, out error))
            {
                return false;
            }
            result = ReadResult(saved, saved.Value<bool?>("changed") == true);
            return true;
        }

        public static bool TryDeleteWithExecutor(Func<string, JObject, JObject> execute, GameAssetKey key, string relativeJsonPath, string assetGuid, string documentSha256, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(assetGuid) || string.IsNullOrWhiteSpace(documentSha256))
            {
                error = "The authoring component has no saved GameAsset identity and document hash.";
                return false;
            }
            var selector = new JObject { ["moduleId"] = key.Mod };
            if (string.IsNullOrWhiteSpace(relativeJsonPath))
            {
                selector["key"] = JObject.FromObject(key, GameAssetJson.JsonSerializer);
            }
            else
            {
                selector["relativeJsonPath"] = relativeJsonPath;
            }
            if (!TryExecute(execute, "asset_get", selector, out var current, out error))
            {
                return false;
            }
            if (!string.Equals(current.Value<string>("guid"), assetGuid, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(current.Value<string>("documentSha256"), documentSha256, StringComparison.Ordinal))
            {
                error = "The GameAsset identity or document hash changed. Delete was cancelled.";
                return false;
            }
            if (current["key"]?.ToObject<GameAssetKey>(GameAssetJson.JsonSerializer) != key)
            {
                error = "The GameAsset key changed. Delete was cancelled.";
                return false;
            }

            if (!TryExecute(execute, "changeset_begin", new JObject { ["moduleId"] = key.Mod, ["expectedContentHash"] = current.Value<string>("contentHash") }, out var started, out error))
            {
                return false;
            }
            var changesetId = started.Value<string>("changesetId");
            var committed = false;
            try
            {
                var operation = new JObject
                {
                    ["kind"] = "deleteAsset",
                    ["selector"] = new JObject { ["relativeJsonPath"] = current.Value<string>("relativeJsonPath") }
                };
                if (!TryExecute(execute, "changeset_apply", new JObject { ["changesetId"] = changesetId, ["operations"] = new JArray { operation } }, out _, out error))
                {
                    return false;
                }
                if (!TryExecute(execute, "changeset_validate", new JObject { ["changesetId"] = changesetId }, out var validation, out error))
                {
                    return false;
                }
                if (validation.Value<bool?>("valid") != true)
                {
                    error = validation.SelectToken("diagnostics[0].message")?.Value<string>() ?? "DingoCMS rejected the GameAsset deletion.";
                    return false;
                }
                if (!TryExecute(execute, "changeset_commit", new JObject { ["changesetId"] = changesetId }, out _, out error))
                {
                    return false;
                }
                committed = true;
                return true;
            }
            finally
            {
                if (!committed && !string.IsNullOrWhiteSpace(changesetId))
                {
                    TryExecute(execute, "changeset_abort", new JObject { ["changesetId"] = changesetId }, out _, out _);
                }
            }
        }

        private static bool TryBuildNewDocument(GameAssetKey key, IReadOnlyList<GameAssetComponent> components, out JObject document, out string error)
        {
            document = null;
            error = string.Empty;
            GameAsset asset = null;
            try
            {
                asset = ScriptableObject.CreateInstance<GameAsset>();
                asset.ResetToDefault(key);
                asset.SetComponents(components);
                document = JObject.Parse(asset.ToJson());
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                if (asset != null)
                {
                    UnityEngine.Object.DestroyImmediate(asset);
                }
            }
        }

        private static bool TryExecute(Func<string, JObject, JObject> execute, string operation, JObject args, out JObject result, out string error)
        {
            result = null;
            error = string.Empty;
            JObject envelope;
            try
            {
                envelope = execute(operation, args);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
            if (envelope?.Value<bool?>("ok") == true && envelope["result"] is JObject payload)
            {
                result = payload;
                return true;
            }
            var details = envelope?["error"] as JObject;
            var code = details?.Value<string>("code") ?? "authoring_error";
            error = code + ": " + (details?.Value<string>("message") ?? "DingoCMS operation failed.");
            return false;
        }

        private static GameAssetObjectAuthoringSaveResult ReadResult(JObject data, bool changed)
        {
            return new GameAssetObjectAuthoringSaveResult(data.Value<string>("relativeJsonPath"), data.Value<string>("guid"), data.Value<string>("documentSha256"), changed);
        }

    }
}
#endif
