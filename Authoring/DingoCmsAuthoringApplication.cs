using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using DingoGameObjectsCMS.AssetLibrary.AssetsEdit;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMSEditorServer.Authoring
{
    public class DingoCmsAuthoringApplication
    {
        private const int DEFAULT_SEARCH_LIMIT = 50;
        private const int MAX_SEARCH_LIMIT = 200;
        private const int MAX_JSON_DIFF_POINTERS = 200;
        private static readonly int[] DIRECTORY_MOVE_RETRY_DELAYS_MS =
            { 0, 5, 20, 50 };

        private static readonly ConcurrentDictionary<string, object> MODULE_LOCKS =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly object _changesetsSync = new();
        private readonly object _lifecycleSync = new();
        private readonly Dictionary<string, DingoCmsChangeset> _changesets = new(StringComparer.Ordinal);
        private readonly DingoCmsChangesetMutator _mutator = new();
        private readonly FileStream _workspaceLease;
        private readonly string _sessionRoot;
        private readonly string _stagingRoot;
        private readonly string _backupsRoot;
        private readonly string _failedRecoveryRoot;

        private bool _shutdown;

        public readonly string AssetsRoot;

        public DingoCmsAuthoringApplication(string assetsRoot)
        {
            AssetsRoot = DingoCmsAuthoringUtils.RequireAssetsRoot(assetsRoot);
            var parent = Path.GetDirectoryName(AssetsRoot)
                         ?? throw new InvalidOperationException($"Assets root '{AssetsRoot}' has no parent directory.");
            var workspaceRoot = Path.Combine(parent, ".dingocms-edit");
            Directory.CreateDirectory(workspaceRoot);
            var workspaceLease = AcquireWorkspaceLease(workspaceRoot);
            try
            {
                _backupsRoot = Path.Combine(workspaceRoot, "r");
                _failedRecoveryRoot = Path.Combine(workspaceRoot, "f");
                Directory.CreateDirectory(_backupsRoot);
                Directory.CreateDirectory(_failedRecoveryRoot);
                RecoverInterruptedCommits();

                var sessionsRoot = Path.Combine(workspaceRoot, "s");
                DeleteStaleSessions(sessionsRoot);
                _sessionRoot = Path.Combine(sessionsRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
                _stagingRoot = _sessionRoot;
                Directory.CreateDirectory(_stagingRoot);
                _workspaceLease = workspaceLease;
            }
            catch
            {
                workspaceLease.Dispose();
                throw;
            }
        }

        public JObject Execute(string operation, JObject args)
        {
            lock (_lifecycleSync)
            {
                try
                {
                    if (_shutdown)
                    {
                        throw new DingoCmsAuthoringException(
                            "server_shutdown",
                            "The DingoCMS authoring application has been shut down.");
                    }

                    args ??= new JObject();
                    var result = operation switch
                    {
                        "cms_status" => GetStatus(),
                        "modules_list" => ListModules(),
                        "asset_search" => SearchAssets(args),
                        "asset_get" => GetAsset(args),
                        "asset_save" => SaveAsset(args),
                        "resource_list" => ListResources(args),
                        "resource_put" => PutResource(args),
                        "changeset_begin" => BeginChangeset(args),
                        "changeset_apply" => ApplyChangeset(args),
                        "changeset_preview" => PreviewChangeset(args),
                        "changeset_validate" => ValidateChangeset(args),
                        "changeset_commit" => CommitChangeset(args),
                        "changeset_abort" => AbortChangeset(args),
                        _ => throw new DingoCmsAuthoringException(
                            "unknown_operation",
                            $"DingoCMS authoring operation '{operation}' is unknown.")
                    };
                    return Success(result);
                }
                catch (Exception exception)
                {
                    return Failure(exception);
                }
            }
        }

        public void Shutdown()
        {
            lock (_lifecycleSync)
            {
                if (_shutdown)
                {
                    return;
                }

                _shutdown = true;
                try
                {
                    DingoCmsChangeset[] active;
                    lock (_changesetsSync)
                    {
                        active = _changesets.Values.ToArray();
                    }
                    for (var index = 0; index < active.Length; index++)
                    {
                        lock (active[index].Sync)
                        {
                            if (active[index].State != DingoCmsChangesetState.Active)
                            {
                                continue;
                            }
                            DingoCmsAuthoringUtils.DeleteOwnedDirectory(active[index].StageRoot);
                            active[index].State = DingoCmsChangesetState.Aborted;
                        }
                    }
                    lock (_changesetsSync)
                    {
                        _changesets.Clear();
                    }
                    DingoCmsAuthoringUtils.DeleteOwnedDirectory(_sessionRoot);
                }
                finally
                {
                    _workspaceLease.Dispose();
                }
            }
        }

        private JObject GetStatus()
        {
            var active = new JArray();
            lock (_changesetsSync)
            {
                foreach (var changeset in _changesets.Values.OrderBy(item => item.CreatedUtc))
                {
                    active.Add(ChangesetSummary(changeset));
                }
            }
            return new JObject
            {
                ["assetsRoot"] = AssetsRoot,
                ["sessionRoot"] = _sessionRoot,
                ["activeChangesets"] = active,
                ["moduleCount"] = GameAssetLibraryDirectoryPolicy
                    .EnumerateModuleDirectories(AssetsRoot).Length
            };
        }

        private JObject ListModules()
        {
            var items = new JArray();
            var directories = GameAssetLibraryDirectoryPolicy
                .EnumerateModuleDirectories(AssetsRoot);
            Array.Sort(directories, StringComparer.Ordinal);
            for (var index = 0; index < directories.Length; index++)
            {
                var moduleId = Path.GetFileName(directories[index]);
                try
                {
                    DingoCmsAuthoringUtils.RequireModuleId(moduleId);
                    var snapshot = GameAssetModuleContentScanner.Scan(directories[index], moduleId);
                    items.Add(new JObject
                    {
                        ["moduleId"] = moduleId,
                        ["valid"] = true,
                        ["contentHash"] = snapshot.ContentHash,
                        ["assetCount"] = snapshot.Assets.Count,
                        ["fileCount"] = snapshot.Files.Count,
                        ["manifestVersion"] = snapshot.ManifestVersion
                    });
                }
                catch (Exception exception)
                {
                    items.Add(new JObject
                    {
                        ["moduleId"] = moduleId,
                        ["valid"] = false,
                        ["error"] = exception.Message
                    });
                }
            }
            return new JObject { ["modules"] = items };
        }

        private JObject SearchAssets(JObject args)
        {
            var requestedModule = args.Value<string>("moduleId");
            var typeFilter = args.Value<string>("type");
            var keyFilter = args.Value<string>("key");
            var versionFilter = args.Value<string>("version");
            var componentFilter = args.Value<string>("componentType");
            var textFilter = args.Value<string>("text");
            var skip = Math.Max(0, args.Value<int?>("skip") ?? 0);
            var limit = Math.Clamp(args.Value<int?>("limit") ?? DEFAULT_SEARCH_LIMIT, 1, MAX_SEARCH_LIMIT);
            var moduleIds = string.IsNullOrWhiteSpace(requestedModule)
                ? EnumerateCanonicalModuleIds()
                : new[] { DingoCmsAuthoringUtils.RequireModuleId(requestedModule) };
            var matches = new List<JObject>();
            var revisions = new JArray();
            var errors = new JArray();
            for (var moduleIndex = 0; moduleIndex < moduleIds.Count; moduleIndex++)
            {
                var moduleId = moduleIds[moduleIndex];
                try
                {
                    var moduleRoot = ResolveExistingModuleRoot(moduleId);
                    var snapshot = GameAssetModuleContentScanner.Scan(moduleRoot, moduleId);
                    revisions.Add(new JObject
                    {
                        ["moduleId"] = moduleId,
                        ["contentHash"] = snapshot.ContentHash
                    });
                    for (var assetIndex = 0; assetIndex < snapshot.Assets.Count; assetIndex++)
                    {
                        var entry = snapshot.Assets[assetIndex];
                        if (!Matches(entry.Key.Type, typeFilter)
                            || !Matches(entry.Key.Key, keyFilter)
                            || !Matches(entry.Key.Version, versionFilter))
                        {
                            continue;
                        }
                        var document = JObject.Parse(snapshot.ReadAllText(entry.RelativeJsonPath));
                        var summary = BuildAssetSummary(moduleId, entry, document);
                        if (!ContainsComponent(summary, componentFilter)
                            || !ContainsText(summary, textFilter))
                        {
                            continue;
                        }
                        matches.Add(summary);
                    }
                }
                catch (Exception exception)
                {
                    if (!string.IsNullOrWhiteSpace(requestedModule))
                    {
                        throw;
                    }
                    errors.Add(new JObject
                    {
                        ["moduleId"] = moduleId,
                        ["message"] = exception.Message
                    });
                }
            }

            matches = matches
                .OrderBy(item => item.Value<string>("moduleId"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item["key"]?.Value<string>("Type"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item["key"]?.Value<string>("Key"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item["key"]?.Value<string>("Version"), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var page = new JArray(matches.Skip(skip).Take(limit));
            return new JObject
            {
                ["total"] = matches.Count,
                ["skip"] = skip,
                ["limit"] = limit,
                ["items"] = page,
                ["moduleRevisions"] = revisions,
                ["errors"] = errors
            };
        }

        private JObject GetAsset(JObject args)
        {
            var moduleId = DingoCmsAuthoringUtils.RequireModuleId(args.Value<string>("moduleId"));
            if (args.TryGetValue("changesetId", StringComparison.Ordinal, out var changesetToken))
            {
                if (changesetToken.Type != JTokenType.String
                    || string.IsNullOrWhiteSpace(changesetToken.Value<string>()))
                {
                    throw new DingoCmsAuthoringException(
                        "invalid_request",
                        "asset_get changesetId must be a non-empty string when supplied.");
                }

                var changeset = RequireChangeset(args);
                lock (changeset.Sync)
                {
                    changeset.RequireActive();
                    if (!string.Equals(changeset.ModuleId, moduleId, StringComparison.Ordinal))
                    {
                        throw new DingoCmsAuthoringException(
                            "invalid_request",
                            $"Changeset '{changeset.Id}' belongs to module '{changeset.ModuleId}', not '{moduleId}'.");
                    }
                    return ReadAsset(args, moduleId, changeset.StageRoot, changeset.Id);
                }
            }

            return ReadAsset(args, moduleId, ResolveExistingModuleRoot(moduleId), changesetId: null);
        }

        private JObject ReadAsset(JObject args, string moduleId, string moduleRoot, string changesetId)
        {
            var snapshot = GameAssetModuleContentScanner.Scan(moduleRoot, moduleId);
            var entry = FindSnapshotAsset(snapshot, moduleId, args);
            var document = JObject.Parse(snapshot.ReadAllText(entry.RelativeJsonPath));
            var pointer = string.Empty;
            if (args.TryGetValue("pointer", StringComparison.Ordinal, out var pointerToken))
            {
                if (pointerToken.Type != JTokenType.String)
                {
                    throw new DingoCmsAuthoringException(
                        "invalid_request",
                        "asset_get pointer must be an RFC 6901 string.");
                }
                pointer = pointerToken.Value<string>();
            }
            var selectedDocument = DingoCmsJsonPatch.ResolvePointer(document, pointer);
            return new JObject
            {
                ["moduleId"] = moduleId,
                ["changesetId"] = changesetId,
                ["contentHash"] = snapshot.ContentHash,
                ["relativeJsonPath"] = entry.RelativeJsonPath,
                ["key"] = DingoCmsAuthoringUtils.AssetKeyToJObject(entry.Key),
                ["guid"] = entry.GUID.ToString(),
                ["documentSha256"] = snapshot.RequireFile(entry.RelativeJsonPath).Sha256,
                ["pointer"] = pointer,
                ["document"] = selectedDocument.DeepClone()
            };
        }

        private JObject SaveAsset(JObject args)
        {
            var moduleId = DingoCmsAuthoringUtils.RequireModuleId(args.Value<string>("moduleId"));
            if (args["document"] is not JObject document)
            {
                throw new DingoCmsAuthoringException(
                    "invalid_request",
                    "asset_save requires a document object.");
            }

            var liveRoot = ResolveExistingModuleRoot(moduleId);
            var snapshot = GameAssetModuleContentScanner.Scan(liveRoot, moduleId);
            var entry = FindSnapshotAsset(snapshot, moduleId, args);
            var currentSha256 = snapshot.RequireFile(entry.RelativeJsonPath).Sha256;
            RequireExpectedDocumentSha256(args, entry.RelativeJsonPath, currentSha256);

            var currentDocument = JObject.Parse(snapshot.ReadAllText(entry.RelativeJsonPath));
            if (JToken.DeepEquals(currentDocument, document))
            {
                return SavedAssetResult(
                    moduleId,
                    entry.RelativeJsonPath,
                    document,
                    snapshot.ContentHash,
                    snapshot.ContentHash,
                    currentSha256,
                    changed: false);
            }

            var commit = PublishSingleOperation(
                moduleId,
                snapshot.ContentHash,
                new JObject
                {
                    ["kind"] = "patchAsset",
                    ["selector"] = new JObject
                    {
                        ["relativeJsonPath"] = entry.RelativeJsonPath
                    },
                    ["patches"] = new JArray
                    {
                        new JObject
                        {
                            ["op"] = "set",
                            ["path"] = string.Empty,
                            ["value"] = document.DeepClone()
                        }
                    }
                });
            var savedSha256 = GameAssetModuleContentScanner.CalculateBytesHash(
                File.ReadAllBytes(GameAssetModuleContentScanner.ResolveInsideRoot(
                    liveRoot,
                    entry.RelativeJsonPath)));
            var result = SavedAssetResult(
                moduleId,
                entry.RelativeJsonPath,
                document,
                commit.Value<string>("previousContentHash"),
                commit.Value<string>("contentHash"),
                savedSha256,
                changed: true);
            result["backupRetained"] = commit.Value<bool?>("backupRetained") ?? false;
            return result;
        }

        private JObject ListResources(JObject args)
        {
            var moduleId = DingoCmsAuthoringUtils.RequireModuleId(args.Value<string>("moduleId"));
            var prefix = args.Value<string>("prefix");
            var skip = Math.Max(0, args.Value<int?>("skip") ?? 0);
            var limit = Math.Clamp(args.Value<int?>("limit") ?? DEFAULT_SEARCH_LIMIT, 1, MAX_SEARCH_LIMIT);
            var snapshot = GameAssetModuleContentScanner.Scan(ResolveExistingModuleRoot(moduleId), moduleId);
            var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < snapshot.Assets.Count; index++)
            {
                assetPaths.Add(snapshot.Assets[index].RelativeJsonPath);
            }

            var items = new JArray();
            var total = 0;
            for (var index = 0; index < snapshot.Files.Count; index++)
            {
                var file = snapshot.Files[index];
                if (assetPaths.Contains(file.RelativePath)
                    || string.Equals(file.RelativePath, ModManifest.FILE_NAME, StringComparison.Ordinal)
                    || !HasPrefix(file.RelativePath, prefix))
                {
                    continue;
                }
                total++;
                if (total <= skip || items.Count >= limit)
                {
                    continue;
                }
                items.Add(new JObject
                {
                    ["relativePath"] = file.RelativePath,
                    ["kind"] = file.Kind,
                    ["size"] = file.Size,
                    ["sha256"] = file.Sha256
                });
            }
            return new JObject
            {
                ["moduleId"] = moduleId,
                ["contentHash"] = snapshot.ContentHash,
                ["total"] = total,
                ["skip"] = skip,
                ["limit"] = limit,
                ["items"] = items
            };
        }

        private JObject PutResource(JObject args)
        {
            var moduleId = DingoCmsAuthoringUtils.RequireModuleId(args.Value<string>("moduleId"));
            var relativePath = DingoCmsAuthoringUtils.ResolveResourceRelativePath(
                args.Value<string>("relativePath"),
                args.Value<string>("sourcePath"));
            var bytes = DingoCmsAuthoringUtils.RequireResourceBytes(args);
            var sha256 = GameAssetModuleContentScanner.CalculateBytesHash(bytes);
            var snapshot = GameAssetModuleContentScanner.Scan(ResolveExistingModuleRoot(moduleId), moduleId);
            var currentSha256 = snapshot.Contains(relativePath)
                ? snapshot.RequireFile(relativePath).Sha256
                : string.Empty;
            RequireExpectedResourceSha256(args, relativePath, currentSha256);
            if (string.Equals(currentSha256, sha256, StringComparison.Ordinal))
            {
                return SavedResourceResult(
                    moduleId,
                    relativePath,
                    bytes.LongLength,
                    sha256,
                    snapshot.ContentHash,
                    snapshot.ContentHash,
                    changed: false);
            }

            var commit = PublishSingleOperation(
                moduleId,
                snapshot.ContentHash,
                new JObject
                {
                    ["kind"] = "putResource",
                    ["relativePath"] = relativePath,
                    ["base64"] = Convert.ToBase64String(bytes)
                });
            var result = SavedResourceResult(
                moduleId,
                relativePath,
                bytes.LongLength,
                sha256,
                commit.Value<string>("previousContentHash"),
                commit.Value<string>("contentHash"),
                changed: true);
            result["backupRetained"] = commit.Value<bool?>("backupRetained") ?? false;
            return result;
        }

        private JObject PublishSingleOperation(string moduleId, string baseContentHash, JObject operation)
        {
            var changesetId = BeginChangeset(new JObject
                {
                    ["moduleId"] = moduleId,
                    ["expectedContentHash"] = baseContentHash
                })
                .Value<string>("changesetId");
            var committed = false;
            try
            {
                ApplyChangeset(new JObject
                {
                    ["changesetId"] = changesetId,
                    ["operations"] = new JArray { operation }
                });
                var commit = CommitChangeset(new JObject { ["changesetId"] = changesetId });
                committed = true;
                return commit;
            }
            finally
            {
                if (!committed)
                {
                    TryAbortChangeset(changesetId);
                }
            }
        }

        private void TryAbortChangeset(string changesetId)
        {
            try
            {
                AbortChangeset(new JObject { ["changesetId"] = changesetId });
            }
            catch
            {
            }
        }

        private static void RequireExpectedDocumentSha256(
            JObject args,
            string relativeJsonPath,
            string currentSha256)
        {
            if (!args.TryGetValue("expectedDocumentSha256", StringComparison.Ordinal, out var expectedToken))
            {
                return;
            }
            if (expectedToken.Type != JTokenType.String
                || string.IsNullOrWhiteSpace(expectedToken.Value<string>()))
            {
                throw new DingoCmsAuthoringException(
                    "invalid_request",
                    "asset_save expectedDocumentSha256 must be a non-empty string when supplied.");
            }

            var expected = expectedToken.Value<string>();
            if (!string.Equals(expected, currentSha256, StringComparison.Ordinal))
            {
                throw new DingoCmsAuthoringException(
                    "content_conflict",
                    $"Asset '{relativeJsonPath}' changed on disk. Expected '{expected}', current '{currentSha256}'.");
            }
        }

        private static void RequireExpectedResourceSha256(
            JObject args,
            string relativePath,
            string currentSha256)
        {
            if (!args.TryGetValue("expectedSha256", StringComparison.Ordinal, out var expectedToken))
            {
                return;
            }
            if (expectedToken.Type != JTokenType.String)
            {
                throw new DingoCmsAuthoringException(
                    "invalid_request",
                    "resource_put expectedSha256 must be a string. Use an empty string only for a new resource.");
            }

            var expected = expectedToken.Value<string>();
            if (!string.Equals(expected, currentSha256, StringComparison.Ordinal))
            {
                throw new DingoCmsAuthoringException(
                    "content_conflict",
                    $"Resource '{relativePath}' changed on disk. Expected '{expected}', current '{currentSha256}'.");
            }
        }

        private static bool HasPrefix(string relativePath, string prefix)
        {
            return string.IsNullOrWhiteSpace(prefix)
                   || relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static JObject SavedResourceResult(
            string moduleId,
            string relativePath,
            long size,
            string sha256,
            string previousContentHash,
            string contentHash,
            bool changed)
        {
            return new JObject
            {
                ["moduleId"] = moduleId,
                ["relativePath"] = relativePath,
                ["size"] = size,
                ["sha256"] = sha256,
                ["previousContentHash"] = previousContentHash,
                ["contentHash"] = contentHash,
                ["changed"] = changed
            };
        }

        private static JObject SavedAssetResult(
            string moduleId,
            string relativeJsonPath,
            JObject document,
            string previousContentHash,
            string contentHash,
            string documentSha256,
            bool changed)
        {
            return new JObject
            {
                ["moduleId"] = moduleId,
                ["relativeJsonPath"] = relativeJsonPath,
                ["key"] = document["Key"]?.DeepClone(),
                ["guid"] = document.Value<string>("GUID"),
                ["previousContentHash"] = previousContentHash,
                ["contentHash"] = contentHash,
                ["documentSha256"] = documentSha256,
                ["changed"] = changed
            };
        }

        private JObject BeginChangeset(JObject args)
        {
            var moduleId = DingoCmsAuthoringUtils.RequireModuleId(args.Value<string>("moduleId"));
            if (!args.TryGetValue("expectedContentHash", StringComparison.Ordinal, out var expectedToken)
                || expectedToken.Type != JTokenType.String)
            {
                throw new DingoCmsAuthoringException(
                    "invalid_request",
                    "changeset_begin requires expectedContentHash. Use an empty string only for a new module.");
            }
            var expectedContentHash = expectedToken.Value<string>();
            var changesetId = Guid.NewGuid().ToString("N");
            var stageRoot = Path.Combine(_stagingRoot, changesetId.Substring(0, 12));
            GameAssetModuleContentSnapshot baseSnapshot;
            string currentContentHash;
            lock (GetModuleLock(moduleId))
            {
                var liveRoot = ResolveModuleRoot(moduleId);
                baseSnapshot = DingoCmsAuthoringUtils.TryCaptureModule(liveRoot, moduleId);
                currentContentHash = baseSnapshot?.ContentHash ?? DingoCmsAuthoringUtils.MISSING_CONTENT_HASH;
                RequireExpectedContentHash(moduleId, expectedContentHash, currentContentHash);
                if (baseSnapshot != null)
                {
                    DingoCmsAuthoringUtils.CopyDirectory(liveRoot, stageRoot);
                    var stagedSnapshot = GameAssetModuleContentScanner.Scan(stageRoot, moduleId);
                    if (!string.Equals(stagedSnapshot.ContentHash, currentContentHash, StringComparison.Ordinal))
                    {
                        DingoCmsAuthoringUtils.DeleteOwnedDirectory(stageRoot);
                        throw new IOException($"Module '{moduleId}' changed while its changeset staging copy was created.");
                    }
                }
                else
                {
                    Directory.CreateDirectory(stageRoot);
                    DingoCmsAuthoringUtils.RebuildManifest(stageRoot, moduleId, Array.Empty<string>());
                }
            }

            var changeset = new DingoCmsChangeset(
                changesetId,
                moduleId,
                currentContentHash,
                stageRoot,
                baseSnapshot,
                DingoCmsAuthoringUtils.CaptureFingerprints(baseSnapshot));
            lock (_changesetsSync)
            {
                _changesets.Add(changeset.Id, changeset);
            }
            return ChangesetSummary(changeset);
        }

        private JObject ApplyChangeset(JObject args)
        {
            var changeset = RequireChangeset(args);
            if (args["operations"] is not JArray operations)
            {
                throw new DingoCmsAuthoringException("invalid_request", "changeset_apply requires an operations array.");
            }

            lock (changeset.Sync)
            {
                changeset.RequireActive();
                var workingRoot = $"{changeset.StageRoot}.a-{Guid.NewGuid():N}".Substring(0, changeset.StageRoot.Length + 11);
                try
                {
                    DingoCmsAuthoringUtils.CopyDirectory(changeset.StageRoot, workingRoot);
                    var operationResults = _mutator.Apply(workingRoot, changeset.ModuleId, operations);
                    ReplaceStagingCopy(changeset.StageRoot, workingRoot);
                    workingRoot = null;
                    var result = BuildPreview(changeset);
                    result["operations"] = operationResults;
                    return result;
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(workingRoot))
                    {
                        DingoCmsAuthoringUtils.DeleteOwnedDirectory(workingRoot);
                    }
                }
            }
        }

        private JObject PreviewChangeset(JObject args)
        {
            var changeset = RequireChangeset(args);
            lock (changeset.Sync)
            {
                changeset.RequireActive();
                return BuildPreview(changeset);
            }
        }

        private JObject ValidateChangeset(JObject args)
        {
            var changeset = RequireChangeset(args);
            lock (changeset.Sync)
            {
                changeset.RequireActive();
                try
                {
                    var snapshot = DingoCmsAuthoringUtils.RequireValidModule(changeset.StageRoot, changeset.ModuleId);
                    return new JObject
                    {
                        ["changesetId"] = changeset.Id,
                        ["moduleId"] = changeset.ModuleId,
                        ["valid"] = true,
                        ["candidateContentHash"] = snapshot.ContentHash,
                        ["assetCount"] = snapshot.Assets.Count,
                        ["diagnostics"] = new JArray()
                    };
                }
                catch (Exception exception)
                {
                    return new JObject
                    {
                        ["changesetId"] = changeset.Id,
                        ["moduleId"] = changeset.ModuleId,
                        ["valid"] = false,
                        ["diagnostics"] = new JArray
                        {
                            new JObject
                            {
                                ["code"] = ErrorCode(exception),
                                ["message"] = exception.Message,
                                ["exceptionType"] = exception.GetType().FullName
                            }
                        }
                    };
                }
            }
        }

        private JObject CommitChangeset(JObject args)
        {
            var changeset = RequireChangeset(args);
            lock (changeset.Sync)
            {
                changeset.RequireActive();
                var candidate = DingoCmsAuthoringUtils.RequireValidModule(changeset.StageRoot, changeset.ModuleId);
                var liveRoot = ResolveModuleRoot(changeset.ModuleId);
                var moduleRecoveryRoot = Path.Combine(_backupsRoot, changeset.ModuleId);
                var backupRoot = Path.Combine(moduleRecoveryRoot, changeset.Id.Substring(0, 12));
                var liveMoved = false;
                var stageMoved = false;
                lock (GetModuleLock(changeset.ModuleId))
                {
                    var currentContentHash = DingoCmsAuthoringUtils.GetCurrentContentHash(liveRoot, changeset.ModuleId);
                    RequireExpectedContentHash(changeset.ModuleId, changeset.BaseContentHash, currentContentHash);
                    RequireClearRecoveryRoot(moduleRecoveryRoot);
                    GameAssetModuleContentSnapshot committed;
                    try
                    {
                        Directory.CreateDirectory(moduleRecoveryRoot);
                        if (Directory.Exists(liveRoot))
                        {
                            Directory.Move(liveRoot, backupRoot);
                            liveMoved = true;
                        }
                        Directory.Move(changeset.StageRoot, liveRoot);
                        stageMoved = true;
                        committed = GameAssetModuleContentScanner.Scan(liveRoot, changeset.ModuleId);
                        if (!string.Equals(committed.ContentHash, candidate.ContentHash, StringComparison.Ordinal))
                        {
                            throw new IOException(
                                $"Committed module '{changeset.ModuleId}' does not match validated candidate '{candidate.ContentHash}'.");
                        }
                    }
                    catch
                    {
                        if (stageMoved && Directory.Exists(liveRoot) && !Directory.Exists(changeset.StageRoot))
                        {
                            Directory.Move(liveRoot, changeset.StageRoot);
                        }
                        if (liveMoved && Directory.Exists(backupRoot) && !Directory.Exists(liveRoot))
                        {
                            Directory.Move(backupRoot, liveRoot);
                        }
                        throw;
                    }

                    changeset.State = DingoCmsChangesetState.Committed;
                    RemoveChangeset(changeset.Id);
                    var backupRetained = liveMoved && !TryDeleteRecoveryBackup(backupRoot, moduleRecoveryRoot);
                    return new JObject
                    {
                        ["changesetId"] = changeset.Id,
                        ["moduleId"] = changeset.ModuleId,
                        ["previousContentHash"] = changeset.BaseContentHash,
                        ["contentHash"] = committed.ContentHash,
                        ["assetCount"] = committed.Assets.Count,
                        ["backupRetained"] = backupRetained,
                        ["backupPath"] = backupRetained ? backupRoot : null
                    };
                }
            }
        }

        private JObject AbortChangeset(JObject args)
        {
            var changeset = RequireChangeset(args);
            lock (changeset.Sync)
            {
                changeset.RequireActive();
                DingoCmsAuthoringUtils.DeleteOwnedDirectory(changeset.StageRoot);
                changeset.State = DingoCmsChangesetState.Aborted;
                RemoveChangeset(changeset.Id);
                return new JObject
                {
                    ["changesetId"] = changeset.Id,
                    ["moduleId"] = changeset.ModuleId,
                    ["aborted"] = true
                };
            }
        }

        private JObject BuildPreview(DingoCmsChangeset changeset)
        {
            var current = DingoCmsAuthoringUtils.CaptureFingerprints(changeset.StageRoot);
            var added = new JArray();
            var removed = new JArray();
            var modified = new JArray();
            foreach (var pair in current.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (string.Equals(pair.Key, ModManifest.FILE_NAME, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!changeset.BaseFiles.TryGetValue(pair.Key, out var previous))
                {
                    added.Add(FileChange(null, pair.Value, null));
                    continue;
                }
                if (!string.Equals(previous.Sha256, pair.Value.Sha256, StringComparison.Ordinal))
                {
                    modified.Add(FileChange(previous, pair.Value, CollectJsonDiff(changeset, pair.Key)));
                }
            }
            foreach (var pair in changeset.BaseFiles.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (string.Equals(pair.Key, ModManifest.FILE_NAME, StringComparison.Ordinal)
                    || current.ContainsKey(pair.Key))
                {
                    continue;
                }
                removed.Add(FileChange(pair.Value, null, null));
            }

            string candidateContentHash = null;
            try
            {
                candidateContentHash = GameAssetModuleContentScanner.Scan(changeset.StageRoot, changeset.ModuleId).ContentHash;
            }
            catch
            {
            }
            var baseManifestHash = changeset.BaseFiles.TryGetValue(ModManifest.FILE_NAME, out var baseManifest)
                ? baseManifest.Sha256
                : null;
            var currentManifestHash = current.TryGetValue(ModManifest.FILE_NAME, out var stagedManifest)
                ? stagedManifest.Sha256
                : null;
            return new JObject
            {
                ["changesetId"] = changeset.Id,
                ["moduleId"] = changeset.ModuleId,
                ["baseContentHash"] = changeset.BaseContentHash,
                ["candidateContentHash"] = candidateContentHash,
                ["manifestChanged"] = !string.Equals(baseManifestHash, currentManifestHash, StringComparison.Ordinal),
                ["summary"] = new JObject
                {
                    ["added"] = added.Count,
                    ["modified"] = modified.Count,
                    ["removed"] = removed.Count
                },
                ["added"] = added,
                ["modified"] = modified,
                ["removed"] = removed
            };
        }

        private JArray CollectJsonDiff(DingoCmsChangeset changeset, string relativePath)
        {
            if (changeset.BaseSnapshot == null
                || !changeset.BaseSnapshot.Contains(relativePath)
                || !string.Equals(Path.GetExtension(relativePath), ".json", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            try
            {
                var before = JToken.Parse(changeset.BaseSnapshot.ReadAllText(relativePath));
                var after = JToken.Parse(File.ReadAllText(
                    GameAssetModuleContentScanner.ResolveInsideRoot(changeset.StageRoot, relativePath)));
                var pointers = new JArray();
                CollectJsonDiffPointers(before, after, string.Empty, pointers);
                return pointers;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static void CollectJsonDiffPointers(JToken before, JToken after, string pointer, JArray result)
        {
            if (result.Count >= MAX_JSON_DIFF_POINTERS || JToken.DeepEquals(before, after))
            {
                return;
            }
            if (before is JObject beforeObject && after is JObject afterObject)
            {
                var names = beforeObject.Properties().Select(property => property.Name)
                    .Concat(afterObject.Properties().Select(property => property.Name))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal);
                foreach (var name in names)
                {
                    if (result.Count >= MAX_JSON_DIFF_POINTERS)
                    {
                        return;
                    }
                    var childPointer = $"{pointer}/{EscapeJsonPointer(name)}";
                    var left = beforeObject[name];
                    var right = afterObject[name];
                    if (left == null || right == null)
                    {
                        result.Add(childPointer);
                    }
                    else
                    {
                        CollectJsonDiffPointers(left, right, childPointer, result);
                    }
                }
                return;
            }
            if (before is JArray beforeArray && after is JArray afterArray && beforeArray.Count == afterArray.Count && beforeArray.Count <= 64)
            {
                for (var index = 0; index < beforeArray.Count && result.Count < MAX_JSON_DIFF_POINTERS; index++)
                {
                    CollectJsonDiffPointers(beforeArray[index], afterArray[index], $"{pointer}/{index}", result);
                }
                return;
            }
            result.Add(string.IsNullOrEmpty(pointer) ? string.Empty : pointer);
        }

        private GameAssetModuleAssetEntry FindSnapshotAsset(
            GameAssetModuleContentSnapshot snapshot,
            string moduleId,
            JObject selector)
        {
            var relativePath = selector.Value<string>("relativeJsonPath");
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                relativePath = GameAssetModuleContentScanner.RequireCanonicalRelativePath(relativePath);
                for (var index = 0; index < snapshot.Assets.Count; index++)
                {
                    if (string.Equals(snapshot.Assets[index].RelativeJsonPath, relativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return snapshot.Assets[index];
                    }
                }
                throw new DingoCmsAuthoringException("not_found", $"Asset path '{relativePath}' does not exist.");
            }

            if (selector["key"] == null)
            {
                throw new DingoCmsAuthoringException("invalid_request", "An asset selector requires relativeJsonPath or key.");
            }
            var key = DingoCmsAuthoringUtils.RequireAssetKey(selector["key"], moduleId);
            var identity = DingoCmsAuthoringUtils.BuildKeyIdentity(key);
            for (var index = 0; index < snapshot.Assets.Count; index++)
            {
                if (string.Equals(
                        DingoCmsAuthoringUtils.BuildKeyIdentity(snapshot.Assets[index].Key),
                        identity,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return snapshot.Assets[index];
                }
            }
            throw new DingoCmsAuthoringException("not_found", $"Asset '{key}' does not exist.");
        }

        private DingoCmsChangeset RequireChangeset(JObject args)
        {
            var changesetId = args.Value<string>("changesetId");
            if (string.IsNullOrWhiteSpace(changesetId))
            {
                throw new DingoCmsAuthoringException("invalid_request", "A changesetId is required.");
            }
            lock (_changesetsSync)
            {
                if (_changesets.TryGetValue(changesetId, out var changeset))
                {
                    return changeset;
                }
            }
            throw new DingoCmsAuthoringException("not_found", $"Changeset '{changesetId}' does not exist.");
        }

        private static FileStream AcquireWorkspaceLease(string workspaceRoot)
        {
            DingoCmsAuthoringUtils.RequireNoReparsePoint(workspaceRoot);
            var leasePath = Path.Combine(workspaceRoot, "writer.lock");
            try
            {
                return new FileStream(
                    leasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException exception)
            {
                throw new DingoCmsAuthoringException(
                    "authoring_locked",
                    $"DingoCMS assets root '{workspaceRoot}' already has an active authoring server.",
                    exception);
            }
        }

        private static void DeleteStaleSessions(string sessionsRoot)
        {
            Directory.CreateDirectory(sessionsRoot);
            DingoCmsAuthoringUtils.RequireNoReparsePoint(sessionsRoot);
            var staleSessions = Directory.GetDirectories(sessionsRoot);
            Array.Sort(staleSessions, StringComparer.Ordinal);
            for (var index = 0; index < staleSessions.Length; index++)
            {
                DingoCmsAuthoringUtils.DeleteOwnedDirectory(staleSessions[index]);
            }
        }

        private object GetModuleLock(string moduleId)
        {
            var identity = $"{AssetsRoot}\u001f{moduleId}";
            return MODULE_LOCKS.GetOrAdd(identity, _ => new object());
        }

        private string ResolveModuleRoot(string moduleId)
        {
            var target = DingoCmsAuthoringUtils.GetModuleRoot(AssetsRoot, moduleId);
            var directories = GameAssetLibraryDirectoryPolicy
                .EnumerateModuleDirectories(AssetsRoot);
            for (var index = 0; index < directories.Length; index++)
            {
                var existingId = Path.GetFileName(directories[index]);
                if (!string.Equals(existingId, moduleId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                DingoCmsAuthoringUtils.RequireNoReparsePoint(directories[index]);
                if (!string.Equals(existingId, moduleId, StringComparison.Ordinal))
                {
                    throw new DingoCmsAuthoringException(
                        "module_casing_conflict",
                        $"Requested module '{moduleId}' collides with disk directory '{existingId}'.");
                }
                return directories[index];
            }
            return target;
        }

        private void RecoverInterruptedCommits()
        {
            var moduleDirectories = Directory.GetDirectories(_backupsRoot);
            Array.Sort(moduleDirectories, StringComparer.Ordinal);
            for (var moduleIndex = 0; moduleIndex < moduleDirectories.Length; moduleIndex++)
            {
                DingoCmsAuthoringUtils.RequireNoReparsePoint(moduleDirectories[moduleIndex]);
                var moduleId = DingoCmsAuthoringUtils.RequireModuleId(Path.GetFileName(moduleDirectories[moduleIndex]));
                var backups = Directory.GetDirectories(moduleDirectories[moduleIndex]);
                Array.Sort(backups, StringComparer.Ordinal);
                for (var backupIndex = 0; backupIndex < backups.Length; backupIndex++)
                {
                    DingoCmsAuthoringUtils.RequireNoReparsePoint(backups[backupIndex]);
                }
                if (backups.Length == 0)
                {
                    DeleteDirectoryIfEmpty(moduleDirectories[moduleIndex]);
                    continue;
                }

                var liveRoot = ResolveModuleRoot(moduleId);
                lock (GetModuleLock(moduleId))
                {
                    if (Directory.Exists(liveRoot))
                    {
                        var liveValid = false;
                        try
                        {
                            GameAssetModuleContentScanner.Scan(liveRoot, moduleId);
                            liveValid = true;
                        }
                        catch
                        {
                        }

                        if (liveValid)
                        {
                            for (var backupIndex = 0; backupIndex < backups.Length; backupIndex++)
                            {
                                TryDeleteOwnedDirectory(backups[backupIndex]);
                            }
                            try
                            {
                                DeleteDirectoryIfEmpty(moduleDirectories[moduleIndex]);
                            }
                            catch
                            {
                            }
                            continue;
                        }

                        if (backups.Length != 1)
                        {
                            throw new InvalidDataException(
                                $"Module '{moduleId}' is invalid and has {backups.Length} recovery candidates. Manual recovery is required.");
                        }
                        GameAssetModuleContentScanner.Scan(backups[0], moduleId);
                        var failedRoot = Path.Combine(
                            _failedRecoveryRoot,
                            $"{moduleId}-{Guid.NewGuid():N}".Substring(0, moduleId.Length + 9));
                        Directory.Move(liveRoot, failedRoot);
                        Directory.Move(backups[0], liveRoot);
                        DeleteDirectoryIfEmpty(moduleDirectories[moduleIndex]);
                        continue;
                    }

                    if (backups.Length != 1)
                    {
                        throw new InvalidDataException(
                            $"Missing module '{moduleId}' has {backups.Length} recovery candidates. Manual recovery is required.");
                    }
                    GameAssetModuleContentScanner.Scan(backups[0], moduleId);
                    Directory.Move(backups[0], liveRoot);
                    DeleteDirectoryIfEmpty(moduleDirectories[moduleIndex]);
                }
            }
        }

        private string ResolveExistingModuleRoot(string moduleId)
        {
            var result = ResolveModuleRoot(moduleId);
            if (!Directory.Exists(result))
            {
                throw new DingoCmsAuthoringException("not_found", $"Module '{moduleId}' does not exist.");
            }
            return result;
        }

        private IReadOnlyList<string> EnumerateCanonicalModuleIds()
        {
            var result = new List<string>();
            var directories = GameAssetLibraryDirectoryPolicy
                .EnumerateModuleDirectories(AssetsRoot);
            Array.Sort(directories, StringComparer.Ordinal);
            for (var index = 0; index < directories.Length; index++)
            {
                var moduleId = Path.GetFileName(directories[index]);
                try
                {
                    result.Add(DingoCmsAuthoringUtils.RequireModuleId(moduleId));
                }
                catch
                {
                }
            }
            return result;
        }

        private void RemoveChangeset(string changesetId)
        {
            lock (_changesetsSync)
            {
                _changesets.Remove(changesetId);
            }
        }

        private static void ReplaceStagingCopy(string stageRoot, string workingRoot)
        {
            var previousRoot = $"{stageRoot}.p-{Guid.NewGuid():N}".Substring(0, stageRoot.Length + 11);
            var stageMoved = false;
            try
            {
                MoveDirectoryWithTransientRetry(stageRoot, previousRoot);
                stageMoved = true;
                MoveDirectoryWithTransientRetry(workingRoot, stageRoot);
            }
            catch (Exception replacementFailure)
            {
                if (!Directory.Exists(stageRoot) && stageMoved && Directory.Exists(previousRoot))
                {
                    try
                    {
                        MoveDirectoryWithTransientRetry(
                            previousRoot,
                            stageRoot);
                    }
                    catch (Exception rollbackMoveFailure)
                    {
                        try
                        {
                            DingoCmsAuthoringUtils.CopyDirectory(
                                previousRoot,
                                stageRoot);
                            TryDeleteOwnedDirectory(previousRoot);
                        }
                        catch (Exception rollbackCopyFailure)
                        {
                            throw new AggregateException(
                                "The staging replacement and rollback both "
                                + "failed. The original staging copy was "
                                + $"retained at '{previousRoot}'.",
                                replacementFailure,
                                rollbackMoveFailure,
                                rollbackCopyFailure);
                        }
                    }
                }
                ExceptionDispatchInfo.Capture(replacementFailure).Throw();
                throw new InvalidOperationException(
                    "Unreachable staging rollback state.");
            }

            TryDeleteOwnedDirectory(previousRoot);
        }

        private static void MoveDirectoryWithTransientRetry(
            string sourcePath,
            string destinationPath)
        {
            try
            {
                Directory.Move(sourcePath, destinationPath);
                return;
            }
            catch (Exception firstFailure) when (
                firstFailure is IOException
                || firstFailure is UnauthorizedAccessException)
            {
                for (var retryIndex = 0;
                     retryIndex < DIRECTORY_MOVE_RETRY_DELAYS_MS.Length;
                     retryIndex++)
                {
                    if (!Directory.Exists(sourcePath)
                        || Directory.Exists(destinationPath))
                    {
                        ExceptionDispatchInfo.Capture(firstFailure).Throw();
                    }

                    var delayMilliseconds =
                        DIRECTORY_MOVE_RETRY_DELAYS_MS[retryIndex];
                    if (delayMilliseconds > 0)
                    {
                        Thread.Sleep(delayMilliseconds);
                    }
                    try
                    {
                        Directory.Move(sourcePath, destinationPath);
                        return;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                ExceptionDispatchInfo.Capture(firstFailure).Throw();
                throw new InvalidOperationException(
                    "Unreachable directory move retry state.");
            }
        }

        private static bool TryDeleteRecoveryBackup(string backupRoot, string moduleRecoveryRoot)
        {
            if (!TryDeleteOwnedDirectory(backupRoot))
            {
                return false;
            }
            try
            {
                DeleteDirectoryIfEmpty(moduleRecoveryRoot);
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static void RequireClearRecoveryRoot(string moduleRecoveryRoot)
        {
            if (!Directory.Exists(moduleRecoveryRoot))
            {
                return;
            }

            DingoCmsAuthoringUtils.RequireNoReparsePoint(moduleRecoveryRoot);
            var backups = Directory.GetDirectories(moduleRecoveryRoot);
            for (var index = 0; index < backups.Length; index++)
            {
                DingoCmsAuthoringUtils.RequireNoReparsePoint(backups[index]);
                TryDeleteOwnedDirectory(backups[index]);
            }
            try
            {
                DeleteDirectoryIfEmpty(moduleRecoveryRoot);
            }
            catch
            {
            }

            if (Directory.Exists(moduleRecoveryRoot)
                && Directory.GetFileSystemEntries(moduleRecoveryRoot).Length > 0)
            {
                throw new DingoCmsAuthoringException(
                    "recovery_pending",
                    $"Recovery workspace '{moduleRecoveryRoot}' is not clear; resolve the retained backup before publishing another revision.");
            }
        }

        private static bool TryDeleteOwnedDirectory(string path)
        {
            try
            {
                DingoCmsAuthoringUtils.DeleteOwnedDirectory(path);
                return !Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static void DeleteDirectoryIfEmpty(string path)
        {
            if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
            {
                Directory.Delete(path);
            }
        }

        private static void RequireExpectedContentHash(string moduleId, string expected, string current)
        {
            if (!string.Equals(expected, current, StringComparison.Ordinal))
            {
                throw new DingoCmsAuthoringException(
                    "content_conflict",
                    $"Module '{moduleId}' content changed. Expected '{expected}', current '{current}'.");
            }
        }

        private static JObject BuildAssetSummary(
            string moduleId,
            GameAssetModuleAssetEntry entry,
            JObject document)
        {
            var componentTypes = new JArray();
            if (document["Components"] is JArray components)
            {
                for (var index = 0; index < components.Count; index++)
                {
                    if (components[index] is JObject component
                        && !string.IsNullOrWhiteSpace(component.Value<string>("$type")))
                    {
                        componentTypes.Add(component.Value<string>("$type"));
                    }
                }
            }
            return new JObject
            {
                ["moduleId"] = moduleId,
                ["key"] = DingoCmsAuthoringUtils.AssetKeyToJObject(entry.Key),
                ["guid"] = entry.GUID.ToString(),
                ["relativeJsonPath"] = entry.RelativeJsonPath,
                ["rootType"] = document.Value<string>("$type"),
                ["componentTypes"] = componentTypes
            };
        }

        private static bool ContainsComponent(JObject summary, string componentFilter)
        {
            if (string.IsNullOrWhiteSpace(componentFilter))
            {
                return true;
            }
            return summary["componentTypes"] is JArray components
                   && components.Values<string>().Any(value =>
                       value.Contains(componentFilter, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ContainsText(JObject summary, string textFilter)
        {
            if (string.IsNullOrWhiteSpace(textFilter))
            {
                return true;
            }
            return summary.ToString(Formatting.None).Contains(textFilter, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Matches(string value, string filter)
        {
            return string.IsNullOrWhiteSpace(filter)
                   || string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);
        }

        private static JObject ChangesetSummary(DingoCmsChangeset changeset)
        {
            return new JObject
            {
                ["changesetId"] = changeset.Id,
                ["moduleId"] = changeset.ModuleId,
                ["baseContentHash"] = changeset.BaseContentHash,
                ["state"] = changeset.State.ToString().ToLowerInvariant(),
                ["createdUtc"] = changeset.CreatedUtc.ToString("O")
            };
        }

        private static JObject FileChange(
            DingoCmsFileFingerprint? before,
            DingoCmsFileFingerprint? after,
            JArray jsonPointers)
        {
            var result = new JObject
            {
                ["relativePath"] = after?.RelativePath ?? before?.RelativePath,
                ["kind"] = after?.Kind ?? before?.Kind,
                ["beforeSha256"] = before?.Sha256,
                ["afterSha256"] = after?.Sha256,
                ["beforeSize"] = before?.Size,
                ["afterSize"] = after?.Size
            };
            if (jsonPointers != null)
            {
                result["changedJsonPointers"] = jsonPointers;
                result["jsonDiffTruncated"] = jsonPointers.Count >= MAX_JSON_DIFF_POINTERS;
            }
            return result;
        }

        private static string EscapeJsonPointer(string value)
        {
            return value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        }

        private static JObject Success(JObject result)
        {
            return new JObject
            {
                ["ok"] = true,
                ["result"] = result ?? new JObject()
            };
        }

        private static JObject Failure(Exception exception)
        {
            return new JObject
            {
                ["ok"] = false,
                ["error"] = new JObject
                {
                    ["code"] = ErrorCode(exception),
                    ["message"] = exception.Message,
                    ["exceptionType"] = exception.GetType().FullName
                }
            };
        }

        private static string ErrorCode(Exception exception)
        {
            if (exception is DingoCmsAuthoringException authoringException)
            {
                return authoringException.Code;
            }
            if (exception is FileNotFoundException || exception is DirectoryNotFoundException)
            {
                return "not_found";
            }
            if (exception is InvalidDataException || exception is JsonException)
            {
                return "invalid_data";
            }
            if (exception is ArgumentException || exception is FormatException)
            {
                return "invalid_request";
            }
            if (exception is IOException || exception is UnauthorizedAccessException)
            {
                return "io_error";
            }
            return "internal_error";
        }
    }
}
