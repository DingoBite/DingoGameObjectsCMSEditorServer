using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DingoGameObjectsCMS.AssetLibrary.AssetsEdit;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DingoGameObjectsCMSEditorServer.Authoring
{
    public static class DingoCmsAuthoringUtils
    {
        public const string MISSING_CONTENT_HASH = "";
        public const long MAX_RESOURCE_BYTES = 128L * 1024 * 1024;
        public const string LEGACY_SOURCE_ASSET_GUID_PROPERTY =
            "SourceAssetGUID";

        public static void NormalizeRootAssetDocument(JObject document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            document.Remove(LEGACY_SOURCE_ASSET_GUID_PROPERTY);
        }

        public static string RequireAssetsRoot(string assetsRoot)
        {
            if (string.IsNullOrWhiteSpace(assetsRoot))
            {
                throw new ArgumentException("A fixed DingoCMS assets root is required.", nameof(assetsRoot));
            }

            var result = Path.GetFullPath(assetsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Directory.CreateDirectory(result);
            RejectReparsePoint(result);
            return result;
        }

        internal static void RequireNoReparsePoint(string path)
        {
            RejectReparsePoint(path);
        }

        public static string RequireModuleId(string moduleId)
        {
            return GameAssetModuleContentScanner.RequireCanonicalModuleId(moduleId);
        }

        public static string GetModuleRoot(string assetsRoot, string moduleId)
        {
            return GameAssetPathPolicy.CombineAbsolute(assetsRoot, RequireModuleId(moduleId));
        }

        public static string GetCurrentContentHash(string moduleRoot, string moduleId)
        {
            return Directory.Exists(moduleRoot)
                ? GameAssetModuleContentScanner.Scan(moduleRoot, moduleId).ContentHash
                : MISSING_CONTENT_HASH;
        }

        public static GameAssetModuleContentSnapshot TryCaptureModule(string moduleRoot, string moduleId)
        {
            return Directory.Exists(moduleRoot)
                ? GameAssetModuleContentScanner.Scan(moduleRoot, moduleId)
                : null;
        }

        public static IReadOnlyDictionary<string, DingoCmsFileFingerprint> CaptureFingerprints(GameAssetModuleContentSnapshot snapshot)
        {
            var result = new Dictionary<string, DingoCmsFileFingerprint>(StringComparer.Ordinal);
            if (snapshot == null)
            {
                return result;
            }

            for (var index = 0; index < snapshot.Files.Count; index++)
            {
                var file = snapshot.Files[index];
                result.Add(
                    file.RelativePath,
                    new DingoCmsFileFingerprint(file.RelativePath, file.Kind, file.Size, file.Sha256));
            }

            return result;
        }

        public static IReadOnlyDictionary<string, DingoCmsFileFingerprint> CaptureFingerprints(string moduleRoot)
        {
            var result = new Dictionary<string, DingoCmsFileFingerprint>(StringComparer.Ordinal);
            if (!Directory.Exists(moduleRoot))
            {
                return result;
            }

            RejectReparsePoint(moduleRoot);
            var assetPaths = LoadManifestAssetPathsForClassification(
                moduleRoot);
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(moduleRoot);
            while (pendingDirectories.Count > 0)
            {
                var directory = pendingDirectories.Pop();
                var childDirectories = Directory.GetDirectories(directory);
                Array.Sort(childDirectories, StringComparer.Ordinal);
                for (var index = childDirectories.Length - 1; index >= 0; index--)
                {
                    RejectReparsePoint(childDirectories[index]);
                    pendingDirectories.Push(childDirectories[index]);
                }

                var files = Directory.GetFiles(directory);
                Array.Sort(files, StringComparer.Ordinal);
                for (var index = 0; index < files.Length; index++)
                {
                    RejectReparsePoint(files[index]);
                    var relativePath = GameAssetModuleContentScanner.RequireCanonicalRelativePath(
                        Path.GetRelativePath(moduleRoot, files[index]).Replace('\\', '/'));
                    var bytes = File.ReadAllBytes(files[index]);
                    result.Add(
                        relativePath,
                        new DingoCmsFileFingerprint(
                            relativePath,
                            Classify(relativePath, assetPaths),
                            bytes.LongLength,
                            GameAssetModuleContentScanner.CalculateBytesHash(bytes)));
                }
            }

            return result;
        }

        public static ModManifest LoadManifest(string moduleRoot, string moduleId)
        {
            var path = Path.Combine(moduleRoot, ModManifest.FILE_NAME);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Module '{moduleId}' has no manifest.json.", path);
            }

            var manifest = JsonConvert.DeserializeObject<ModManifest>(File.ReadAllText(path), GameAssetJson.DataSettings);
            if (manifest == null || !string.Equals(manifest.Mod, moduleId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Module manifest at '{path}' must declare exact module '{moduleId}'.");
            }

            manifest.Assets ??= new List<ModManifestEntry>();
            return manifest;
        }

        public static HashSet<string> LoadAssetPaths(string moduleRoot, string moduleId)
        {
            var manifest = LoadManifest(moduleRoot, moduleId);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < manifest.Assets.Count; index++)
            {
                var relativePath = GameAssetModuleContentScanner.RequireCanonicalRelativePath(
                    manifest.Assets[index]?.RelativeJsonPath
                    ?? throw new InvalidDataException($"Module '{moduleId}' manifest contains a null entry."));
                if (!result.Add(relativePath))
                {
                    throw new InvalidDataException($"Module '{moduleId}' manifest contains duplicate or case-colliding path '{relativePath}'.");
                }
            }

            return result;
        }

        public static JObject LoadJObject(string moduleRoot, string relativePath)
        {
            var absolutePath = GameAssetPathPolicy.CombineAbsolute(moduleRoot, relativePath);
            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException($"Module file '{relativePath}' does not exist.", absolutePath);
            }

            try
            {
                return JObject.Parse(File.ReadAllText(absolutePath));
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Module file '{relativePath}' is not a JSON object.", exception);
            }
        }

        public static void WriteJObject(string moduleRoot, string relativePath, JObject document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var absolutePath = GameAssetPathPolicy.CombineAbsolute(moduleRoot, relativePath);
            WriteTextAtomic(absolutePath, document.ToString(Formatting.Indented));
        }

        public static void WriteBytes(string moduleRoot, string relativePath, byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            var absolutePath = GameAssetPathPolicy.CombineAbsolute(moduleRoot, relativePath);
            var directory = Path.GetDirectoryName(absolutePath)
                            ?? throw new InvalidOperationException($"Invalid output path '{absolutePath}'.");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".~{Guid.NewGuid():N}".Substring(0, 10) + ".tmp");
            try
            {
                File.WriteAllBytes(temporaryPath, bytes);
                ReplaceFile(temporaryPath, absolutePath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        public static string ResolveResourceRelativePath(string relativePath, string sourcePath)
        {
            var sourceFileName = string.IsNullOrWhiteSpace(sourcePath)
                ? null
                : Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                relativePath = RequireSourceFileName(sourceFileName);
            }
            else if (relativePath.EndsWith("/", StringComparison.Ordinal))
            {
                relativePath += RequireSourceFileName(sourceFileName);
            }
            return GameAssetModuleContentScanner.RequireCanonicalRelativePath(relativePath);
        }

        public static byte[] RequireResourceBytes(JObject source)
        {
            var base64 = source["base64"];
            var sourcePath = source.Value<string>("sourcePath");
            if ((base64 != null) == !string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new DingoCmsAuthoringException(
                    "invalid_request",
                    "A resource requires exactly one of base64 or sourcePath.");
            }
            if (base64 == null)
            {
                return ReadSourceFile(sourcePath);
            }
            try
            {
                return Convert.FromBase64String(base64.Value<string>());
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                throw new DingoCmsAuthoringException("invalid_request", "Resource base64 data is invalid.", exception);
            }
        }

        private static string RequireSourceFileName(string sourceFileName)
        {
            if (string.IsNullOrWhiteSpace(sourceFileName))
            {
                throw new DingoCmsAuthoringException(
                    "invalid_request",
                    "A resource requires relativePath unless sourcePath supplies the target file name.");
            }
            return sourceFileName;
        }

        private static byte[] ReadSourceFile(string sourcePath)
        {
            if (!Path.IsPathRooted(sourcePath))
            {
                throw new DingoCmsAuthoringException(
                    "invalid_request",
                    $"Resource sourcePath '{sourcePath}' must be an absolute host path.");
            }

            var absolutePath = Path.GetFullPath(sourcePath);
            if (Directory.Exists(absolutePath))
            {
                throw new DingoCmsAuthoringException(
                    "invalid_request",
                    $"Resource sourcePath '{absolutePath}' is a directory; upload one file per resource.");
            }
            var file = new FileInfo(absolutePath);
            if (!file.Exists)
            {
                throw new DingoCmsAuthoringException("not_found", $"Resource sourcePath '{absolutePath}' does not exist.");
            }
            if (file.Length > MAX_RESOURCE_BYTES)
            {
                throw new DingoCmsAuthoringException(
                    "resource_too_large",
                    $"Resource sourcePath '{absolutePath}' is {file.Length} bytes; the limit is {MAX_RESOURCE_BYTES}.");
            }
            return File.ReadAllBytes(absolutePath);
        }

        public static string BuildDefaultAssetPath(GameAssetKey key)
        {
            var result = $"{key.Type}/{key.Key}/{key.Key}@{key.Version}.json";
            return GameAssetModuleContentScanner.RequireCanonicalRelativePath(result);
        }

        public static void WriteManifest(string moduleRoot, ModManifest manifest)
        {
            var json = JsonConvert.SerializeObject(manifest, Formatting.Indented, GameAssetJson.DataSettings);
            WriteTextAtomic(Path.Combine(moduleRoot, ModManifest.FILE_NAME), json);
        }

        public static ModManifest RebuildManifest(string moduleRoot, string moduleId, IReadOnlyCollection<string> assetPaths)
        {
            var manifestVersion = 1;
            var manifestPath = Path.Combine(moduleRoot, ModManifest.FILE_NAME);
            if (File.Exists(manifestPath))
            {
                var previous = JsonConvert.DeserializeObject<ModManifest>(File.ReadAllText(manifestPath), GameAssetJson.DataSettings);
                manifestVersion = Math.Max(1, previous?.ManifestVersion ?? 1);
            }

            var entries = new List<ModManifestEntry>(assetPaths.Count);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var guids = new HashSet<Hash128>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sourcePath in assetPaths)
            {
                var relativePath = GameAssetModuleContentScanner.RequireCanonicalRelativePath(sourcePath);
                if (!paths.Add(relativePath))
                {
                    throw new InvalidDataException($"Asset path '{relativePath}' is duplicated or differs only by case.");
                }

                var document = LoadJObject(moduleRoot, relativePath);
                var key = RequireAssetKey(document["Key"], moduleId);
                var guid = RequireHash128(document.Value<string>("GUID"), $"asset '{relativePath}' GUID");
                var keyIdentity = BuildKeyIdentity(key);
                if (!keys.Add(keyIdentity))
                {
                    throw new InvalidDataException($"Asset key '{key}' is duplicated or differs only by case.");
                }
                if (!guids.Add(guid))
                {
                    throw new InvalidDataException($"Asset GUID '{guid}' is duplicated.");
                }

                entries.Add(new ModManifestEntry
                {
                    Key = key,
                    GUID = guid,
                    RelativeJsonPath = relativePath
                });
            }

            entries = entries
                .OrderBy(entry => entry.Key.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Key.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Key.Version, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.RelativeJsonPath, StringComparer.Ordinal)
                .ToList();
            var manifest = new ModManifest
            {
                Mod = moduleId,
                GeneratedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ManifestVersion = manifestVersion,
                Assets = entries
            };
            manifest.ContentHash = GameAssetModuleContentScanner.CalculateAssetContentHash(
                moduleId,
                manifest,
                CaptureAssetFiles(moduleRoot, entries));
            WriteManifest(moduleRoot, manifest);
            return manifest;
        }

        private static Dictionary<string, GameAssetModuleContentFile> CaptureAssetFiles(
            string moduleRoot,
            IReadOnlyList<ModManifestEntry> entries)
        {
            var result = new Dictionary<string, GameAssetModuleContentFile>(entries.Count, StringComparer.Ordinal);
            for (var index = 0; index < entries.Count; index++)
            {
                var relativePath = entries[index].RelativeJsonPath;
                var absolutePath = GameAssetModuleContentScanner.ResolveInsideRoot(moduleRoot, relativePath);
                result.Add(
                    relativePath,
                    new GameAssetModuleContentFile(relativePath, "gameAsset", File.ReadAllBytes(absolutePath)));
            }

            return result;
        }

        public static GameAssetModuleContentSnapshot RequireValidModule(string moduleRoot, string moduleId)
        {
            var manifest = LoadManifest(moduleRoot, moduleId);
            for (var index = 0; index < manifest.Assets.Count; index++)
            {
                var entry = manifest.Assets[index]
                            ?? throw new InvalidDataException($"Module '{moduleId}' manifest contains a null entry at index {index}.");
                var document = LoadJObject(moduleRoot, entry.RelativeJsonPath);
                var rootType = ResolveAuthoredType(
                    document.Value<string>("$type"),
                    typeof(GameAssetScriptableObject),
                    $"asset '{entry.RelativeJsonPath}' root");
                if (typeof(GameAsset).IsAssignableFrom(rootType))
                {
                    var hasPrefabBase = GameAssetDocumentComposer.HasBase(document);
                    if (document["Components"] is JArray components)
                    {
                        ValidateAuthoredComponents(components, entry.RelativeJsonPath);
                    }
                    else if (!hasPrefabBase)
                    {
                        throw new InvalidDataException(
                            $"Asset '{entry.RelativeJsonPath}' must contain a Components array or a Prefab base.");
                    }
                    if (hasPrefabBase)
                    {
                        ValidatePrefabOverrides(document, entry.RelativeJsonPath);
                    }
                }
            }

            return GameAssetModuleContentScanner.Scan(moduleRoot, moduleId);
        }

        private static void ValidateAuthoredComponents(JArray components, string relativeJsonPath)
        {
            for (var index = 0; index < components.Count; index++)
            {
                if (components[index] is not JObject component)
                {
                    throw new InvalidDataException(
                        $"Asset '{relativeJsonPath}' component {index} must be an object.");
                }
                ResolveAuthoredType(
                    component.Value<string>("$type"),
                    typeof(GameAssetComponent),
                    $"asset '{relativeJsonPath}' component {index}");
            }
        }

        private static void ValidatePrefabOverrides(JObject document, string relativeJsonPath)
        {
            var prefab = document["Prefab"];
            if (prefab?["OverrideComponents"] is JArray overrides)
            {
                ValidateAuthoredComponents(overrides, $"{relativeJsonPath}' prefab override");
            }
            if (prefab?["RemovedComponents"] is not JArray removed)
            {
                return;
            }

            for (var index = 0; index < removed.Count; index++)
            {
                ResolveAuthoredType(
                    removed[index]?.Value<string>(),
                    typeof(GameAssetComponent),
                    $"asset '{relativeJsonPath}' removed component {index}");
            }
        }

        public static GameAssetKey RequireAssetKey(JToken token, string expectedModuleId)
        {
            if (token is not JObject keyObject)
            {
                throw new InvalidDataException("A GameAsset Key JSON object is required.");
            }

            var moduleId = keyObject.Value<string>("Mod");
            var type = keyObject.Value<string>("Type");
            var key = keyObject.Value<string>("Key");
            var version = keyObject.Value<string>("Version");
            RequireModuleId(moduleId);
            if (!string.Equals(moduleId, expectedModuleId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Asset module '{moduleId}' does not match changeset module '{expectedModuleId}'.");
            }
            RequireIdentitySegment(type, "asset type");
            RequireIdentitySegment(key, "asset key");
            GameAssetVersionUtils.RequireCanonical(version, nameof(version));
            return new GameAssetKey(moduleId, type, key, version);
        }

        public static JObject AssetKeyToJObject(GameAssetKey key)
        {
            return new JObject
            {
                ["Mod"] = key.Mod,
                ["Type"] = key.Type,
                ["Key"] = key.Key,
                ["Version"] = key.Version
            };
        }

        public static string BuildKeyIdentity(GameAssetKey key)
        {
            return $"{key.Mod}\u001f{key.Type}\u001f{key.Key}\u001f{key.Version}";
        }

        public static Hash128 RequireHash128(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException($"{field} is required.");
            }

            Hash128 result;
            try
            {
                result = Hash128.Parse(value);
            }
            catch (Exception exception) when (exception is ArgumentException || exception is FormatException)
            {
                throw new InvalidDataException($"{field} '{value}' is invalid.", exception);
            }
            if (!result.isValid)
            {
                throw new InvalidDataException($"{field} must be a non-zero Hash128.");
            }
            return result;
        }

        public static string NewHash128Text()
        {
            return DingoGameObjectsCMS.IdUtils.NewHash128FromGuid().ToString();
        }

        public static void SetRootIdentity(JObject document, GameAssetKey key, bool createNewGuid)
        {
            document["Key"] = AssetKeyToJObject(key);
            if (createNewGuid || string.IsNullOrWhiteSpace(document.Value<string>("GUID")))
            {
                document["GUID"] = NewHash128Text();
            }
        }

        public static void RebaseInstanceGuids(JToken token)
        {
            if (token is JObject objectToken)
            {
                foreach (var property in objectToken.Properties().ToArray())
                {
                    if (string.Equals(property.Name, "InstanceGuid", StringComparison.Ordinal))
                    {
                        property.Value = NewHash128Text();
                    }
                    else
                    {
                        RebaseInstanceGuids(property.Value);
                    }
                }
                return;
            }

            if (token is JArray arrayToken)
            {
                for (var index = 0; index < arrayToken.Count; index++)
                {
                    RebaseInstanceGuids(arrayToken[index]);
                }
            }
        }

        public static void CopyDirectory(string sourceRoot, string destinationRoot)
        {
            if (!Directory.Exists(sourceRoot))
            {
                throw new DirectoryNotFoundException($"Source directory '{sourceRoot}' does not exist.");
            }
            if (Directory.Exists(destinationRoot))
            {
                throw new IOException($"Destination directory '{destinationRoot}' already exists.");
            }

            RejectReparsePoint(sourceRoot);
            Directory.CreateDirectory(destinationRoot);
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(sourceRoot);
            while (pendingDirectories.Count > 0)
            {
                var directory = pendingDirectories.Pop();
                var directories = Directory.GetDirectories(directory);
                Array.Sort(directories, StringComparer.Ordinal);
                for (var index = directories.Length - 1; index >= 0; index--)
                {
                    RejectReparsePoint(directories[index]);
                    var relative = Path.GetRelativePath(sourceRoot, directories[index]);
                    Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
                    pendingDirectories.Push(directories[index]);
                }

                var files = Directory.GetFiles(directory);
                Array.Sort(files, StringComparer.Ordinal);
                for (var index = 0; index < files.Length; index++)
                {
                    RejectReparsePoint(files[index]);
                    var relative = Path.GetRelativePath(sourceRoot, files[index]);
                    var destination = Path.Combine(destinationRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)
                                              ?? throw new InvalidOperationException($"Invalid destination path '{destination}'."));
                    File.Copy(files[index], destination, overwrite: false);
                }
            }
        }

        public static void DeleteOwnedDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            DeleteOwnedDirectoryTree(path);
        }

        public static void DeleteEmptyParents(string moduleRoot, string filePath)
        {
            var module = Path.GetFullPath(moduleRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetDirectoryName(Path.GetFullPath(filePath));
            while (!string.IsNullOrWhiteSpace(current)
                   && !string.Equals(current, module, StringComparison.OrdinalIgnoreCase)
                   && Directory.Exists(current)
                   && Directory.GetFileSystemEntries(current).Length == 0)
            {
                Directory.Delete(current);
                current = Path.GetDirectoryName(current);
            }
        }

        private static void WriteTextAtomic(string path, string text)
        {
            var directory = Path.GetDirectoryName(path)
                            ?? throw new InvalidOperationException($"Invalid output path '{path}'.");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".~{Guid.NewGuid():N}".Substring(0, 10) + ".tmp");
            try
            {
                File.WriteAllText(temporaryPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                ReplaceFile(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static void ReplaceFile(string temporaryPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }
        }

        private static void RequireIdentitySegment(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value)
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
                || string.Equals(value, ".", StringComparison.Ordinal)
                || string.Equals(value, "..", StringComparison.Ordinal)
                || value.IndexOf('/') >= 0
                || value.IndexOf('\\') >= 0
                || value.IndexOf(':') >= 0
                || value.IndexOf('|') >= 0
                || value.Any(character => character < ' '))
            {
                throw new InvalidDataException($"{field} '{value}' must be one canonical identity segment.");
            }
        }

        private static Type ResolveAuthoredType(string alias, Type requiredBaseType, string field)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                throw new InvalidDataException($"{field} must declare a $type alias.");
            }

            Type result;
            try
            {
                result = GameAssetJson.Settings.SerializationBinder.BindToType(assemblyName: null, typeName: alias);
            }
            catch (Exception exception) when (exception is JsonSerializationException || exception is ArgumentException)
            {
                throw new InvalidDataException($"{field} declares unknown $type alias '{alias}'.", exception);
            }
            if (result == null || result.IsAbstract || !requiredBaseType.IsAssignableFrom(result))
            {
                throw new InvalidDataException(
                    $"{field} $type '{alias}' must resolve to a concrete {requiredBaseType.Name}.");
            }
            return result;
        }

        private static void RejectReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"DingoCMS authoring path '{path}' uses a reparse point.");
            }
        }

        private static void DeleteOwnedDirectoryTree(string path)
        {
            RejectReparsePoint(path);
            var directories = Directory.GetDirectories(path);
            Array.Sort(directories, StringComparer.Ordinal);
            for (var index = 0; index < directories.Length; index++)
            {
                DeleteOwnedDirectoryTree(directories[index]);
            }

            var files = Directory.GetFiles(path);
            Array.Sort(files, StringComparer.Ordinal);
            for (var index = 0; index < files.Length; index++)
            {
                RejectReparsePoint(files[index]);
                File.Delete(files[index]);
            }
            Directory.Delete(path);
        }

        private static HashSet<string>
            LoadManifestAssetPathsForClassification(string moduleRoot)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var manifestPath = Path.Combine(
                moduleRoot,
                ModManifest.FILE_NAME);
            if (!File.Exists(manifestPath))
            {
                return result;
            }

            var manifest = JsonConvert.DeserializeObject<ModManifest>(
                File.ReadAllText(manifestPath),
                GameAssetJson.DataSettings);
            if (manifest?.Assets == null)
            {
                return result;
            }

            for (var index = 0; index < manifest.Assets.Count; index++)
            {
                var entry = manifest.Assets[index]
                            ?? throw new InvalidDataException(
                                $"Module manifest contains a null entry at "
                                + $"index {index}.");
                result.Add(GameAssetModuleContentScanner
                    .RequireCanonicalRelativePath(
                        entry.RelativeJsonPath));
            }
            return result;
        }

        private static string Classify(
            string relativePath,
            HashSet<string> assetPaths)
        {
            if (string.Equals(relativePath, ModManifest.FILE_NAME, StringComparison.Ordinal))
            {
                return "manifest";
            }

            var extension = Path.GetExtension(relativePath).ToLowerInvariant();
            if (extension == ".json")
            {
                if (string.Equals(relativePath, SpriteRenderModulePresets.RESOURCE_PATH, StringComparison.Ordinal))
                {
                    return "spriteRenderPresets";
                }
                if (relativePath.EndsWith(".recipe.json", StringComparison.Ordinal))
                {
                    return "spriteRecipe";
                }
                return assetPaths.Contains(relativePath)
                    ? "gameAsset"
                    : "json";
            }
            return extension == ".png" ? "sprite" : "metadata";
        }
    }
}
