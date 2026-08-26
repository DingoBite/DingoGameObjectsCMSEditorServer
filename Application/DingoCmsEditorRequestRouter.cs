#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary.AssetsEdit;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Serialization;
using DingoGameObjectsCMSEditorServer.Transport;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMSEditorServer.Application
{
    public sealed class DingoCmsEditorRequestRouter :
        IDingoCmsEditorRequestRouter
    {
        private readonly Func<string, JObject, JObject> _authoringExecutor;
        private readonly Func<JObject> _runtimeStatus;
        private readonly Func<JObject> _runtimeReload;
        private readonly JObject _typeCatalog;
        private readonly JArray _tools;

        public string WebIndexHtml { get; }

        public DingoCmsEditorRequestRouter(
            Func<string, JObject, JObject> authoringExecutor,
            string webIndexHtml,
            Func<JObject> runtimeStatus = null,
            Func<JObject> runtimeReload = null)
        {
            _authoringExecutor = authoringExecutor
                                 ?? throw new ArgumentNullException(
                                     nameof(authoringExecutor));
            WebIndexHtml = webIndexHtml ?? string.Empty;
            _runtimeStatus = runtimeStatus ?? CreateUnavailableRuntimeStatus;
            _runtimeReload = runtimeReload ?? CreateUnavailableRuntimeReload;
            _typeCatalog = BuildTypeCatalog();
            _tools = BuildTools();
        }

        public JArray DescribeTools()
        {
            return (JArray)_tools.DeepClone();
        }

        public JObject Execute(string operation, JObject arguments)
        {
            if (string.IsNullOrWhiteSpace(operation))
                throw new ArgumentException("An editor operation is required.");

            arguments ??= new JObject();
            switch (operation)
            {
                case "schema_list":
                    return (JObject)_typeCatalog.DeepClone();
                case "runtime_status":
                    return BuildRuntimeStatus();
                case "runtime_reload":
                    return _runtimeReload();
                case "cms_status":
                {
                    var status = ExecuteAuthoring(operation, arguments);
                    var disk = ExecuteAuthoring("modules_list", new JObject());
                    status["modules"] = disk["modules"]?.DeepClone()
                                        ?? new JArray();
                    status["runtime"] = BuildRuntimeStatus(disk);
                    return status;
                }
                default:
                    return ExecuteAuthoring(operation, arguments);
            }
        }

        private JObject ExecuteAuthoring(
            string operation,
            JObject arguments)
        {
            var envelope = _authoringExecutor(operation, arguments)
                           ?? throw new InvalidOperationException(
                               $"Authoring operation '{operation}' returned null.");
            if (envelope.Value<bool?>("ok") != false)
            {
                return envelope["result"] as JObject
                       ?? envelope;
            }

            var error = envelope["error"] as JObject;
            var code = error?.Value<string>("code")
                       ?? "authoring_error";
            var message = error?.Value<string>("message")
                          ?? $"Authoring operation '{operation}' failed.";
            throw new DingoCmsEditorRequestException(code, message, error);
        }

        private JObject BuildRuntimeStatus(JObject diskSnapshot = null)
        {
            var runtime = _runtimeStatus() ?? new JObject();
            diskSnapshot ??= ExecuteAuthoring("modules_list", new JObject());
            var diskModules = diskSnapshot["modules"] as JArray
                              ?? new JArray();
            runtime["diskModules"] = diskModules.DeepClone();

            if (runtime.Value<bool?>("available") != true
                || runtime["modules"] is not JArray activeModules)
            {
                runtime["inSync"] = JValue.CreateNull();
                runtime["differences"] = new JArray();
                return runtime;
            }

            var diskById = diskModules
                .OfType<JObject>()
                .Where(item => !string.IsNullOrWhiteSpace(
                    item.Value<string>("moduleId")))
                .ToDictionary(
                    item => item.Value<string>("moduleId"),
                    item => item,
                    StringComparer.OrdinalIgnoreCase);
            var activeById = activeModules
                .OfType<JObject>()
                .Where(item => !string.IsNullOrWhiteSpace(
                    item.Value<string>("moduleId")))
                .ToDictionary(
                    item => item.Value<string>("moduleId"),
                    item => item,
                    StringComparer.OrdinalIgnoreCase);
            var ids = diskById.Keys
                .Concat(activeById.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
            var differences = new JArray();
            foreach (var moduleId in ids)
            {
                diskById.TryGetValue(moduleId, out var diskModule);
                activeById.TryGetValue(moduleId, out var activeModule);
                var diskHash = diskModule?.Value<string>("contentHash");
                var activeHash = activeModule?.Value<string>("contentHash");
                if (diskModule?.Value<bool?>("valid") == true
                    && activeModule != null
                    && string.Equals(
                        diskHash,
                        activeHash,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                differences.Add(new JObject
                {
                    ["moduleId"] = moduleId,
                    ["diskContentHash"] = diskHash,
                    ["runtimeContentHash"] = activeHash,
                    ["kind"] = diskModule == null
                        ? "missingOnDisk"
                        : activeModule == null
                            ? "addedOnDisk"
                            : diskModule.Value<bool?>("valid") != true
                                ? "invalidOnDisk"
                                : "changed",
                });
            }

            runtime["inSync"] = differences.Count == 0;
            runtime["differences"] = differences;
            return runtime;
        }

        private static JObject BuildTypeCatalog()
        {
            var rootTypes = EnumerateConcreteTypes(
                    typeof(GameAssetScriptableObject))
                .Select(type => DescribeType(type, "asset"))
                .OrderBy(item => item.Value<string>("typeId"), StringComparer.Ordinal);
            var componentTypes = GameAssetComponentTypeCache.Types
                .Select(type => DescribeType(type, "component"))
                .OrderBy(item => item.Value<string>("typeId"), StringComparer.Ordinal);

            return new JObject
            {
                ["documentModel"] = "JObject",
                ["description"] =
                    "DingoCMS edits canonical JSON documents. Type discovery "
                    + "does not expose a second reflected field model.",
                ["rootTypes"] = new JArray(rootTypes),
                ["componentTypes"] = new JArray(componentTypes),
            };
        }

        private static IEnumerable<Type> EnumerateConcreteTypes(Type baseType)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type != null
                        && !type.IsAbstract
                        && !type.IsInterface
                        && baseType.IsAssignableFrom(type))
                    {
                        yield return type;
                    }
                }
            }
        }

        private static JObject DescribeType(Type type, string category)
        {
            GameAssetJson.Settings.SerializationBinder.BindToName(
                type,
                out _,
                out var typeId);
            return new JObject
            {
                ["typeId"] = typeId,
                ["category"] = category,
                ["displayName"] = category == "component"
                    ? GameAssetComponentTypeCache.GetMenuName(type)
                    : type.Name,
                ["clrType"] = type.FullName,
            };
        }

        private static JArray BuildTools()
        {
            return new JArray
            {
                Tool(
                    "cms_status",
                    "CMS status",
                    "Reports the disk authoring session, module hashes, and the immutable active runtime revision.",
                    EmptyObjectSchema(),
                    readOnly: true),
                Tool(
                    "schema_list",
                    "List authored types",
                    "Lists stable JSON type ids for root assets and components. Asset fields remain JObject documents.",
                    EmptyObjectSchema(),
                    readOnly: true),
                Tool(
                    "modules_list",
                    "List modules",
                    "Lists disk modules and current content hashes.",
                    EmptyObjectSchema(),
                    readOnly: true),
                Tool(
                    "asset_search",
                    "Search assets",
                    "Searches summaries without returning full asset documents. moduleId is optional; filters are case-insensitive.",
                    ObjectSchema(
                        new JObject
                        {
                            ["moduleId"] = StringSchema("Optional canonical module id."),
                            ["type"] = StringSchema("Optional exact asset type."),
                            ["key"] = StringSchema("Optional exact asset key."),
                            ["version"] = StringSchema("Optional exact canonical version."),
                            ["componentType"] = StringSchema("Optional component type substring."),
                            ["text"] = StringSchema("Optional key, type, path, or component text."),
                            ["skip"] = IntegerSchema("Number of sorted matches to skip."),
                            ["limit"] = IntegerSchema("Maximum result count."),
                        }),
                    readOnly: true),
                Tool(
                    "asset_get",
                    "Get asset document",
                    "Returns one canonical JObject asset document by manifest path or key.",
                    AssetGetSchema(),
                    readOnly: true),
                Tool(
                    "asset_save",
                    "Save asset document",
                    "Replaces one canonical JObject asset document and publishes the module in a single transaction: staged copy, structural validation, then a hash-guarded swap. Pass the documentSha256 from asset_get to reject a save over a concurrent edit.",
                    AssetSaveSchema(),
                    destructive: true),
                Tool(
                    "resource_list",
                    "List module files",
                    "Lists the physical files of a module with size and sha256. Asset JSON documents and the derived manifest are excluded; use asset_search for those.",
                    ObjectSchema(
                        new JObject
                        {
                            ["moduleId"] = StringSchema("Canonical module id."),
                            ["prefix"] = StringSchema("Optional module-relative path prefix."),
                            ["skip"] = IntegerSchema("Number of sorted matches to skip."),
                            ["limit"] = IntegerSchema("Maximum result count."),
                        },
                        "moduleId"),
                    readOnly: true),
                Tool(
                    "resource_put",
                    "Upload module file",
                    "Uploads one file of any type into a module and publishes it in a single transaction. Either send the bytes inline as base64, or pass sourcePath and the server reads that file from the editor host itself. Uploading bytes identical to the current file is reported as changed: false and publishes nothing.",
                    ResourcePutSchema(),
                    destructive: true),
                Tool(
                    "changeset_begin",
                    "Begin changeset",
                    "Creates an isolated staged module revision guarded by expectedContentHash.",
                    ObjectSchema(
                        new JObject
                        {
                            ["moduleId"] = StringSchema("Canonical module id."),
                            ["expectedContentHash"] = StringSchema("Exact current content hash; use an empty string only when creating a module."),
                        },
                        "moduleId",
                        "expectedContentHash")),
                Tool(
                    "changeset_apply",
                    "Apply changes",
                    "Applies a batch of generic JObject and resource operations to a staged revision.",
                    ObjectSchema(
                        new JObject
                        {
                            ["changesetId"] = StringSchema("Active changeset id."),
                            ["operations"] = new JObject
                            {
                                ["type"] = "array",
                                ["items"] = MutationOperationSchema(),
                            },
                        },
                        "changesetId",
                        "operations")),
                Tool(
                    "changeset_preview",
                    "Preview changes",
                    "Returns structural file and asset changes without touching the live module.",
                    ChangesetIdSchema(),
                    readOnly: true),
                Tool(
                    "changeset_validate",
                    "Validate changes",
                    "Validates JObject structure, authored type aliases, identities, paths, and the complete staged module snapshot without constructing Unity objects.",
                    ChangesetIdSchema(),
                    readOnly: true),
                Tool(
                    "changeset_commit",
                    "Commit changes",
                    "Rechecks the live content hash and performs a recoverable staged module swap.",
                    ChangesetIdSchema(),
                    destructive: true),
                Tool(
                    "changeset_abort",
                    "Abort changes",
                    "Discards an uncommitted staged module revision.",
                    ChangesetIdSchema(),
                    destructive: true),
                Tool(
                    "runtime_status",
                    "Runtime status",
                    "Reports whether disk content differs from the immutable active runtime session.",
                    EmptyObjectSchema(),
                    readOnly: true),
                Tool(
                    "runtime_reload",
                    "Reload runtime library",
                    "Requests an explicit host-provided safe runtime library reload. Never runs after commit automatically.",
                    EmptyObjectSchema(),
                    destructive: true),
            };
        }

        private static JObject Tool(
            string name,
            string title,
            string description,
            JObject inputSchema,
            bool readOnly = false,
            bool destructive = false)
        {
            return new JObject
            {
                ["name"] = name,
                ["title"] = title,
                ["description"] = description,
                ["inputSchema"] = inputSchema,
                ["annotations"] = new JObject
                {
                    ["readOnlyHint"] = readOnly,
                    ["destructiveHint"] = destructive,
                    ["openWorldHint"] = false,
                },
            };
        }

        private static JObject EmptyObjectSchema()
        {
            return ObjectSchema(new JObject());
        }

        private static JObject ChangesetIdSchema()
        {
            return ObjectSchema(
                new JObject
                {
                    ["changesetId"] = StringSchema("Active changeset id."),
                },
                "changesetId");
        }

        private static JObject MutationOperationSchema()
        {
            var patchSchema = ObjectSchema(
                new JObject
                {
                    ["op"] = EnumStringSchema(
                        "JSON Pointer operation. set replaces an existing value; add may insert; remove deletes.",
                        "add",
                        "set",
                        "remove"),
                    ["path"] = StringSchema("RFC 6901 JSON Pointer; an empty pointer targets the root document."),
                    ["value"] = new JObject(),
                },
                "op",
                "path");

            return new JObject
            {
                ["oneOf"] = new JArray
                {
                    ObjectSchema(
                        new JObject
                        {
                            ["kind"] = ConstantStringSchema("createAsset"),
                            ["document"] = new JObject { ["type"] = "object" },
                            ["relativeJsonPath"] = StringSchema("Optional canonical target path; otherwise derived from Key."),
                        },
                        "kind",
                        "document"),
                    ObjectSchema(
                        new JObject
                        {
                            ["kind"] = ConstantStringSchema("cloneAsset"),
                            ["source"] = AssetSelectorSchema(),
                            ["targetKey"] = AssetKeySchema(),
                            ["relativeJsonPath"] = StringSchema("Optional canonical target path; otherwise derived from targetKey."),
                        },
                        "kind",
                        "source",
                        "targetKey"),
                    ObjectSchema(
                        new JObject
                        {
                            ["kind"] = ConstantStringSchema("patchAsset"),
                            ["selector"] = AssetSelectorSchema(),
                            ["patches"] = new JObject
                            {
                                ["type"] = "array",
                                ["items"] = patchSchema,
                            },
                        },
                        "kind",
                        "selector",
                        "patches"),
                    ObjectSchema(
                        new JObject
                        {
                            ["kind"] = ConstantStringSchema("deleteAsset"),
                            ["selector"] = AssetSelectorSchema(),
                        },
                        "kind",
                        "selector"),
                    ObjectSchema(
                        new JObject
                        {
                            ["kind"] = ConstantStringSchema("putResource"),
                            ["relativePath"] = ResourceTargetPathSchema(),
                            ["sourcePath"] = ResourceSourcePathSchema(),
                            ["base64"] = StringSchema("Complete resource bytes encoded as base64."),
                        },
                        "kind"),
                    ObjectSchema(
                        new JObject
                        {
                            ["kind"] = ConstantStringSchema("deleteResource"),
                            ["relativePath"] = StringSchema("Canonical module-relative resource path."),
                        },
                        "kind",
                        "relativePath"),
                },
            };
        }

        private static JObject AssetGetSchema()
        {
            var result = ObjectSchema(
                new JObject
                {
                    ["moduleId"] = StringSchema("Canonical module id."),
                    ["relativeJsonPath"] = StringSchema("Manifest-relative JSON path."),
                    ["key"] = AssetKeySchema(),
                    ["pointer"] = StringSchema("Optional RFC 6901 JSON Pointer projection."),
                    ["changesetId"] = StringSchema("Optional active changeset whose staged document should be read."),
                },
                "moduleId");
            result["anyOf"] = new JArray
            {
                new JObject { ["required"] = new JArray("relativeJsonPath") },
                new JObject { ["required"] = new JArray("key") },
            };
            return result;
        }

        private static JObject AssetSaveSchema()
        {
            var result = ObjectSchema(
                new JObject
                {
                    ["moduleId"] = StringSchema("Canonical module id."),
                    ["relativeJsonPath"] = StringSchema("Manifest-relative JSON path."),
                    ["key"] = AssetKeySchema(),
                    ["document"] = new JObject { ["type"] = "object" },
                    ["expectedDocumentSha256"] = StringSchema("Optional asset_get documentSha256 that must still match on disk."),
                },
                "moduleId",
                "document");
            result["anyOf"] = new JArray
            {
                new JObject { ["required"] = new JArray("relativeJsonPath") },
                new JObject { ["required"] = new JArray("key") },
            };
            return result;
        }

        private static JObject ResourcePutSchema()
        {
            var result = ObjectSchema(
                new JObject
                {
                    ["moduleId"] = StringSchema("Canonical module id."),
                    ["relativePath"] = ResourceTargetPathSchema(),
                    ["sourcePath"] = ResourceSourcePathSchema(),
                    ["base64"] = StringSchema("Complete file bytes encoded as base64."),
                    ["expectedSha256"] = StringSchema("Optional resource_list sha256 that must still match on disk; an empty string requires the file to be new."),
                },
                "moduleId");
            result["anyOf"] = new JArray
            {
                new JObject { ["required"] = new JArray("sourcePath") },
                new JObject { ["required"] = new JArray("base64", "relativePath") },
            };
            return result;
        }

        private static JObject ResourceTargetPathSchema()
        {
            return StringSchema(
                "Canonical module-relative target path. Optional with sourcePath: omit it to keep the source file name at the module root, or end it with '/' to keep that name inside the given folder.");
        }

        private static JObject ResourceSourcePathSchema()
        {
            return StringSchema(
                "Absolute path of one existing file on the editor host. The server reads it and copies it into the module.");
        }

        private static JObject AssetSelectorSchema()
        {
            var result = ObjectSchema(
                new JObject
                {
                    ["relativeJsonPath"] = StringSchema("Manifest-relative JSON path."),
                    ["key"] = AssetKeySchema(),
                });
            result["anyOf"] = new JArray
            {
                new JObject { ["required"] = new JArray("relativeJsonPath") },
                new JObject { ["required"] = new JArray("key") },
            };
            return result;
        }

        private static JObject AssetKeySchema()
        {
            return ObjectSchema(
                new JObject
                {
                    ["Mod"] = StringSchema("Module id."),
                    ["Type"] = StringSchema("Asset type."),
                    ["Key"] = StringSchema("Asset key."),
                    ["Version"] = StringSchema("Canonical asset version."),
                },
                "Mod",
                "Type",
                "Key",
                "Version");
        }

        private static JObject ConstantStringSchema(string value)
        {
            return new JObject
            {
                ["type"] = "string",
                ["const"] = value,
            };
        }

        private static JObject EnumStringSchema(
            string description,
            params string[] values)
        {
            return new JObject
            {
                ["type"] = "string",
                ["description"] = description,
                ["enum"] = new JArray(values),
            };
        }

        private static JObject ObjectSchema(
            JObject properties,
            params string[] required)
        {
            var result = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false,
            };
            if (required != null && required.Length > 0)
                result["required"] = new JArray(required);
            return result;
        }

        private static JObject StringSchema(string description)
        {
            return new JObject
            {
                ["type"] = "string",
                ["description"] = description,
            };
        }

        private static JObject IntegerSchema(string description)
        {
            return new JObject
            {
                ["type"] = "integer",
                ["description"] = description,
            };
        }

        private static JObject CreateUnavailableRuntimeStatus()
        {
            return new JObject
            {
                ["available"] = false,
                ["reloadAvailable"] = false,
                ["message"] = "No game runtime bridge is registered.",
            };
        }

        private static JObject CreateUnavailableRuntimeReload()
        {
            return new JObject
            {
                ["accepted"] = false,
                ["restartRequired"] = true,
                ["message"] =
                    "The current host has no safe full-library reload boundary. Restart the gameplay session explicitly.",
            };
        }
    }
}
#endif
