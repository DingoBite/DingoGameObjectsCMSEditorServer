using System;
using System.Collections.Generic;
using System.IO;
using DingoGameObjectsCMS.AssetLibrary.AssetsEdit;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMSEditorServer.Authoring
{
    public class DingoCmsChangesetMutator
    {
        public JArray Apply(string moduleRoot, string moduleId, JArray operations)
        {
            if (operations == null || operations.Count == 0)
            {
                throw new DingoCmsAuthoringException("invalid_request", "A non-empty operations array is required.");
            }

            var assetPaths = DingoCmsAuthoringUtils.LoadAssetPaths(moduleRoot, moduleId);
            var results = new JArray();
            for (var index = 0; index < operations.Count; index++)
            {
                if (operations[index] is not JObject operation)
                {
                    throw new DingoCmsAuthoringException(
                        "invalid_request",
                        $"Changeset operation at index {index} must be an object.");
                }

                results.Add(ApplyOne(moduleRoot, moduleId, assetPaths, operation, index));
            }

            DingoCmsAuthoringUtils.RebuildManifest(moduleRoot, moduleId, assetPaths);
            return results;
        }

        private JObject ApplyOne(
            string moduleRoot,
            string moduleId,
            HashSet<string> assetPaths,
            JObject operation,
            int operationIndex)
        {
            var kind = operation.Value<string>("kind")
                       ?? throw new DingoCmsAuthoringException(
                           "invalid_request",
                           $"Changeset operation at index {operationIndex} has no kind.");
            return kind switch
            {
                "createAsset" => CreateAsset(moduleRoot, moduleId, assetPaths, operation),
                "cloneAsset" => CloneAsset(moduleRoot, moduleId, assetPaths, operation),
                "patchAsset" => PatchAsset(moduleRoot, moduleId, assetPaths, operation),
                "deleteAsset" => DeleteAsset(moduleRoot, moduleId, assetPaths, operation),
                "putResource" => PutResource(moduleRoot, assetPaths, operation),
                "deleteResource" => DeleteResource(moduleRoot, assetPaths, operation),
                _ => throw new DingoCmsAuthoringException(
                    "invalid_request",
                    $"Changeset operation kind '{kind}' is unsupported.")
            };
        }

        private JObject CreateAsset(
            string moduleRoot,
            string moduleId,
            HashSet<string> assetPaths,
            JObject operation)
        {
            if (operation["document"] is not JObject source)
            {
                throw new DingoCmsAuthoringException("invalid_request", "createAsset requires a document object.");
            }

            var document = (JObject)source.DeepClone();
            DingoCmsAuthoringUtils.NormalizeRootAssetDocument(document);
            var key = DingoCmsAuthoringUtils.RequireAssetKey(document["Key"], moduleId);
            EnsureKeyAvailable(moduleRoot, moduleId, assetPaths, key, exceptPath: null);
            DingoCmsAuthoringUtils.SetRootIdentity(document, key, createNewGuid: true);
            var relativePath = RequireTargetAssetPath(operation.Value<string>("relativeJsonPath"), key);
            EnsureTargetPathAvailable(moduleRoot, assetPaths, relativePath);
            DingoCmsAuthoringUtils.WriteJObject(moduleRoot, relativePath, document);
            assetPaths.Add(relativePath);
            return AssetMutationResult("createAsset", relativePath, document);
        }

        private JObject CloneAsset(
            string moduleRoot,
            string moduleId,
            HashSet<string> assetPaths,
            JObject operation)
        {
            if (operation["source"] is not JObject selector)
            {
                throw new DingoCmsAuthoringException("invalid_request", "cloneAsset requires a source selector.");
            }
            var targetKey = DingoCmsAuthoringUtils.RequireAssetKey(operation["targetKey"], moduleId);
            EnsureKeyAvailable(moduleRoot, moduleId, assetPaths, targetKey, exceptPath: null);
            var sourcePath = FindAssetPath(moduleRoot, moduleId, assetPaths, selector);
            var document = DingoCmsAuthoringUtils.LoadJObject(moduleRoot, sourcePath);
            DingoCmsAuthoringUtils.NormalizeRootAssetDocument(document);
            DingoCmsAuthoringUtils.SetRootIdentity(document, targetKey, createNewGuid: true);
            DingoCmsAuthoringUtils.RebaseInstanceGuids(document);
            var targetPath = RequireTargetAssetPath(operation.Value<string>("relativeJsonPath"), targetKey);
            EnsureTargetPathAvailable(moduleRoot, assetPaths, targetPath);
            DingoCmsAuthoringUtils.WriteJObject(moduleRoot, targetPath, document);
            assetPaths.Add(targetPath);
            var result = AssetMutationResult("cloneAsset", targetPath, document);
            result["sourceRelativeJsonPath"] = sourcePath;
            return result;
        }

        private JObject PatchAsset(
            string moduleRoot,
            string moduleId,
            HashSet<string> assetPaths,
            JObject operation)
        {
            if (operation["selector"] is not JObject selector)
            {
                throw new DingoCmsAuthoringException("invalid_request", "patchAsset requires an asset selector.");
            }
            if (operation["patches"] is not JArray patches)
            {
                throw new DingoCmsAuthoringException("invalid_request", "patchAsset requires a patches array.");
            }

            var relativePath = FindAssetPath(moduleRoot, moduleId, assetPaths, selector);
            var current = DingoCmsAuthoringUtils.LoadJObject(moduleRoot, relativePath);
            var document = DingoCmsJsonPatch.Apply(current, patches);
            DingoCmsAuthoringUtils.NormalizeRootAssetDocument(document);
            var key = DingoCmsAuthoringUtils.RequireAssetKey(document["Key"], moduleId);
            DingoCmsAuthoringUtils.RequireHash128(document.Value<string>("GUID"), $"asset '{relativePath}' GUID");
            EnsureKeyAvailable(moduleRoot, moduleId, assetPaths, key, relativePath);
            DingoCmsAuthoringUtils.WriteJObject(moduleRoot, relativePath, document);
            var result = AssetMutationResult("patchAsset", relativePath, document);
            result["patchCount"] = patches.Count;
            return result;
        }

        private JObject DeleteAsset(
            string moduleRoot,
            string moduleId,
            HashSet<string> assetPaths,
            JObject operation)
        {
            if (operation["selector"] is not JObject selector)
            {
                throw new DingoCmsAuthoringException("invalid_request", "deleteAsset requires an asset selector.");
            }

            var relativePath = FindAssetPath(moduleRoot, moduleId, assetPaths, selector);
            var absolutePath = GameAssetPathPolicy.CombineAbsolute(moduleRoot, relativePath);
            File.Delete(absolutePath);
            assetPaths.Remove(relativePath);
            DingoCmsAuthoringUtils.DeleteEmptyParents(moduleRoot, absolutePath);
            return new JObject
            {
                ["kind"] = "deleteAsset",
                ["relativeJsonPath"] = relativePath
            };
        }

        private JObject PutResource(string moduleRoot, HashSet<string> assetPaths, JObject operation)
        {
            var relativePath = RequireResourcePath(
                DingoCmsAuthoringUtils.ResolveResourceRelativePath(
                    operation.Value<string>("relativePath"),
                    operation.Value<string>("sourcePath")),
                assetPaths);
            var bytes = DingoCmsAuthoringUtils.RequireResourceBytes(operation);
            DingoCmsAuthoringUtils.WriteBytes(moduleRoot, relativePath, bytes);
            return new JObject
            {
                ["kind"] = "putResource",
                ["relativePath"] = relativePath,
                ["size"] = bytes.LongLength,
                ["sha256"] = GameAssetModuleContentScanner.CalculateBytesHash(bytes)
            };
        }

        private JObject DeleteResource(string moduleRoot, HashSet<string> assetPaths, JObject operation)
        {
            var relativePath = RequireResourcePath(operation.Value<string>("relativePath"), assetPaths);
            var absolutePath = GameAssetPathPolicy.CombineAbsolute(moduleRoot, relativePath);
            if (!File.Exists(absolutePath))
            {
                throw new DingoCmsAuthoringException("not_found", $"Resource '{relativePath}' does not exist.");
            }
            File.Delete(absolutePath);
            DingoCmsAuthoringUtils.DeleteEmptyParents(moduleRoot, absolutePath);
            return new JObject
            {
                ["kind"] = "deleteResource",
                ["relativePath"] = relativePath
            };
        }

        private static string FindAssetPath(
            string moduleRoot,
            string moduleId,
            IReadOnlyCollection<string> assetPaths,
            JObject selector)
        {
            var selectedPath = selector.Value<string>("relativeJsonPath");
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                selectedPath = GameAssetModuleContentScanner.RequireCanonicalRelativePath(selectedPath);
                foreach (var assetPath in assetPaths)
                {
                    if (string.Equals(assetPath, selectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return assetPath;
                    }
                }
                throw new DingoCmsAuthoringException("not_found", $"Asset path '{selectedPath}' is not in the module manifest.");
            }

            if (selector["key"] == null)
            {
                throw new DingoCmsAuthoringException(
                    "invalid_request",
                    "An asset selector requires relativeJsonPath or key.");
            }
            var requiredKey = DingoCmsAuthoringUtils.RequireAssetKey(selector["key"], moduleId);
            var requiredIdentity = DingoCmsAuthoringUtils.BuildKeyIdentity(requiredKey);
            foreach (var assetPath in assetPaths)
            {
                var document = DingoCmsAuthoringUtils.LoadJObject(moduleRoot, assetPath);
                var key = DingoCmsAuthoringUtils.RequireAssetKey(document["Key"], moduleId);
                if (string.Equals(
                        DingoCmsAuthoringUtils.BuildKeyIdentity(key),
                        requiredIdentity,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return assetPath;
                }
            }
            throw new DingoCmsAuthoringException("not_found", $"Asset '{requiredKey}' does not exist.");
        }

        private static void EnsureKeyAvailable(
            string moduleRoot,
            string moduleId,
            IReadOnlyCollection<string> assetPaths,
            GameAssetKey requiredKey,
            string exceptPath)
        {
            var requiredIdentity = DingoCmsAuthoringUtils.BuildKeyIdentity(requiredKey);
            foreach (var assetPath in assetPaths)
            {
                if (!string.IsNullOrWhiteSpace(exceptPath)
                    && string.Equals(assetPath, exceptPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var document = DingoCmsAuthoringUtils.LoadJObject(moduleRoot, assetPath);
                var key = DingoCmsAuthoringUtils.RequireAssetKey(document["Key"], moduleId);
                if (string.Equals(
                        DingoCmsAuthoringUtils.BuildKeyIdentity(key),
                        requiredIdentity,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new DingoCmsAuthoringException("asset_conflict", $"Asset key '{requiredKey}' already exists.");
                }
            }
        }

        private static void EnsureTargetPathAvailable(
            string moduleRoot,
            IReadOnlyCollection<string> assetPaths,
            string relativePath)
        {
            foreach (var assetPath in assetPaths)
            {
                if (string.Equals(assetPath, relativePath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DingoCmsAuthoringException("asset_conflict", $"Asset path '{relativePath}' already exists.");
                }
            }
            var absolutePath = GameAssetPathPolicy.CombineAbsolute(moduleRoot, relativePath);
            if (File.Exists(absolutePath) || Directory.Exists(absolutePath))
            {
                throw new DingoCmsAuthoringException("path_conflict", $"Module path '{relativePath}' already exists.");
            }
        }

        private static string RequireTargetAssetPath(string relativePath, GameAssetKey key)
        {
            return string.IsNullOrWhiteSpace(relativePath)
                ? DingoCmsAuthoringUtils.BuildDefaultAssetPath(key)
                : GameAssetModuleContentScanner.RequireCanonicalRelativePath(relativePath);
        }

        private static string RequireResourcePath(string relativePath, IReadOnlyCollection<string> assetPaths)
        {
            relativePath = GameAssetModuleContentScanner.RequireCanonicalRelativePath(relativePath);
            if (string.Equals(relativePath, ModManifest.FILE_NAME, StringComparison.OrdinalIgnoreCase))
            {
                throw new DingoCmsAuthoringException("protected_path", "manifest.json is derived and cannot be edited as a resource.");
            }
            foreach (var assetPath in assetPaths)
            {
                if (string.Equals(assetPath, relativePath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DingoCmsAuthoringException(
                        "protected_path",
                        $"Asset JSON '{relativePath}' must be changed through an asset operation.");
                }
            }
            return relativePath;
        }

        private static JObject AssetMutationResult(string kind, string relativePath, JObject document)
        {
            return new JObject
            {
                ["kind"] = kind,
                ["relativeJsonPath"] = relativePath,
                ["key"] = document["Key"]?.DeepClone(),
                ["guid"] = document.Value<string>("GUID")
            };
        }
    }
}
