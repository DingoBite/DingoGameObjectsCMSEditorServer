#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMSEditorServer.Authoring;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DingoGameObjectsCMSEditorServer.Tests.Editor
{
    public class DingoCmsAuthoringApplicationTests
    {
        [Test]
        public void WorkspaceLeaseRejectsSecondApplicationUntilShutdown()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication first = null;
            DingoCmsAuthoringApplication next = null;
            try
            {
                first = new DingoCmsAuthoringApplication(environment.AssetsRoot);

                var exception = Assert.Throws<DingoCmsAuthoringException>(
                    () => new DingoCmsAuthoringApplication(environment.AssetsRoot));
                Assert.That(exception.Code, Is.EqualTo("authoring_locked"));

                first.Shutdown();
                first = null;
                next = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                Assert.That(next.Execute("cms_status", new JObject()).Value<bool>("ok"), Is.True);
            }
            finally
            {
                next?.Shutdown();
                first?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void DotPrefixedLibraryDirectoriesAreNotModules()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                Directory.CreateDirectory(Path.Combine(
                    environment.AssetsRoot,
                    ".git"));
                Directory.CreateDirectory(Path.Combine(
                    environment.AssetsRoot,
                    ".cache"));
                Directory.CreateDirectory(Path.Combine(
                    environment.AssetsRoot,
                    "test"));
                application = new DingoCmsAuthoringApplication(
                    environment.AssetsRoot);

                var modules = RequireResult(application.Execute(
                    "modules_list",
                    new JObject()));
                var status = RequireResult(application.Execute(
                    "cms_status",
                    new JObject()));

                Assert.That(modules["modules"], Has.Count.EqualTo(1));
                Assert.That(
                    modules.SelectToken("modules[0].moduleId")?.Value<string>(),
                    Is.EqualTo("test"));
                Assert.That(status.Value<int>("moduleCount"), Is.EqualTo(1));
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void AssetGetPointerProjectsAnyJsonTokenAndKeepsAssetMetadata()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                var contentHash = CreateAndCommitTestAsset(application);

                var result = RequireResult(application.Execute(
                    "asset_get",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["key"] = TestKey(),
                        ["pointer"] = "/Nested/Items/1/label"
                    }));

                Assert.That(result.Value<string>("moduleId"), Is.EqualTo("test"));
                Assert.That(result.Value<string>("contentHash"), Is.Not.Empty);
                Assert.That(result.Value<string>("relativeJsonPath"), Is.EqualTo("fixture/one/one@0.0.0.json"));
                Assert.That(result.Value<string>("guid"), Is.Not.Empty);
                Assert.That(result.Value<string>("pointer"), Is.EqualTo("/Nested/Items/1/label"));
                Assert.That(result["document"]?.Type, Is.EqualTo(JTokenType.String));
                Assert.That(result["document"]?.Value<string>(), Is.EqualTo("second"));

                var escaped = RequireResult(application.Execute(
                    "asset_get",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["key"] = TestKey(),
                        ["pointer"] = "/Nested/a~1b/~0name"
                    }));
                Assert.That(escaped["document"]?.Value<int>(), Is.EqualTo(17));

                var staged = RequireResult(application.Execute(
                    "changeset_begin",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["expectedContentHash"] = contentHash
                    }));
                var changesetId = staged.Value<string>("changesetId");
                RequireResult(application.Execute(
                    "changeset_apply",
                    new JObject
                    {
                        ["changesetId"] = changesetId,
                        ["operations"] = new JArray
                        {
                            new JObject
                            {
                                ["kind"] = "patchAsset",
                                ["selector"] = new JObject { ["key"] = TestKey() },
                                ["patches"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["op"] = "set",
                                        ["path"] = "/Nested/Items/1/label",
                                        ["value"] = "staged"
                                    }
                                }
                            }
                        }
                    }));

                var stagedRead = RequireResult(application.Execute(
                    "asset_get",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["changesetId"] = changesetId,
                        ["key"] = TestKey(),
                        ["pointer"] = "/Nested/Items/1/label"
                    }));
                Assert.That(stagedRead.Value<string>("changesetId"), Is.EqualTo(changesetId));
                Assert.That(stagedRead["document"]?.Value<string>(), Is.EqualTo("staged"));

                var liveRead = RequireResult(application.Execute(
                    "asset_get",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["key"] = TestKey(),
                        ["pointer"] = "/Nested/Items/1/label"
                    }));
                Assert.That(liveRead["document"]?.Value<string>(), Is.EqualTo("second"));
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void AssetSavePublishesOneDocumentWithoutAnExplicitChangeset()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                var baseHash = CreateAndCommitTestAsset(application);
                var read = ReadTestAsset(application);
                var document = (JObject)read["document"];
                document["Nested"]["Items"][1]["label"] = "saved";

                var result = RequireResult(application.Execute(
                    "asset_save",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["relativeJsonPath"] = read.Value<string>("relativeJsonPath"),
                        ["document"] = document,
                        ["expectedDocumentSha256"] = read.Value<string>("documentSha256")
                    }));

                Assert.That(result.Value<bool>("changed"), Is.True);
                Assert.That(result.Value<string>("previousContentHash"), Is.EqualTo(baseHash));
                Assert.That(result.Value<string>("contentHash"), Is.Not.EqualTo(baseHash));

                var reread = RequireResult(application.Execute(
                    "asset_get",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["key"] = TestKey(),
                        ["pointer"] = "/Nested/Items/1/label"
                    }));
                Assert.That(reread["document"]?.Value<string>(), Is.EqualTo("saved"));
                Assert.That(reread.Value<string>("contentHash"), Is.EqualTo(result.Value<string>("contentHash")));
                Assert.That(reread.Value<string>("documentSha256"), Is.EqualTo(result.Value<string>("documentSha256")));
                AssertNoActiveChangesets(application);
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void AssetSaveOfAnUnchangedDocumentKeepsTheModuleRevision()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                var baseHash = CreateAndCommitTestAsset(application);
                var read = ReadTestAsset(application);

                var result = RequireResult(application.Execute(
                    "asset_save",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["relativeJsonPath"] = read.Value<string>("relativeJsonPath"),
                        ["document"] = read["document"],
                        ["expectedDocumentSha256"] = read.Value<string>("documentSha256")
                    }));

                Assert.That(result.Value<bool>("changed"), Is.False);
                Assert.That(result.Value<string>("contentHash"), Is.EqualTo(baseHash));
                Assert.That(result.Value<string>("documentSha256"), Is.EqualTo(read.Value<string>("documentSha256")));
                AssertNoActiveChangesets(application);
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void AssetSaveRejectsAStaleDocumentSha256()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                var baseHash = CreateAndCommitTestAsset(application);
                var read = ReadTestAsset(application);
                var document = (JObject)read["document"];
                document["Nested"]["Items"][1]["label"] = "loser";

                var conflict = application.Execute(
                    "asset_save",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["relativeJsonPath"] = read.Value<string>("relativeJsonPath"),
                        ["document"] = document,
                        ["expectedDocumentSha256"] = new string('0', 64)
                    });

                Assert.That(conflict.Value<bool>("ok"), Is.False);
                Assert.That(conflict.SelectToken("error.code")?.Value<string>(), Is.EqualTo("content_conflict"));
                Assert.That(ReadTestAsset(application).Value<string>("contentHash"), Is.EqualTo(baseHash));
                AssertNoActiveChangesets(application);
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void AssetSaveDiscardsItsOwnChangesetWhenTheDocumentIsInvalid()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                var baseHash = CreateAndCommitTestAsset(application);
                var read = ReadTestAsset(application);
                var document = (JObject)read["document"];
                document["$type"] = "ThisTypeAliasDoesNotExist";

                var failure = application.Execute(
                    "asset_save",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["relativeJsonPath"] = read.Value<string>("relativeJsonPath"),
                        ["document"] = document
                    });

                Assert.That(failure.Value<bool>("ok"), Is.False);
                Assert.That(failure.SelectToken("error.code")?.Value<string>(), Is.EqualTo("invalid_data"));
                var reread = ReadTestAsset(application);
                Assert.That(reread.Value<string>("contentHash"), Is.EqualTo(baseHash));
                Assert.That(reread.SelectToken("document.$type")?.Value<string>(), Is.EqualTo("GameAsset"));
                AssertNoActiveChangesets(application);
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void ChangesetBeginRequiresExpectedContentHash()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);

                var response = application.Execute(
                    "changeset_begin",
                    new JObject { ["moduleId"] = "test" });

                Assert.That(response.Value<bool>("ok"), Is.False);
                Assert.That(response.SelectToken("error.code")?.Value<string>(), Is.EqualTo("invalid_request"));
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void CreateApplyValidateCommitAndGetPublishesValidAsset()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);

                var contentHash = CreateAndCommitTestAsset(application);
                var read = RequireResult(application.Execute(
                    "asset_get",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["key"] = TestKey()
                    }));

                Assert.That(contentHash, Is.Not.Empty);
                Assert.That(read.Value<string>("contentHash"), Is.EqualTo(contentHash));
                Assert.That(read.SelectToken("document.Key.Mod")?.Value<string>(), Is.EqualTo("test"));
                Assert.That(read.SelectToken("document.GUID")?.Value<string>(), Is.Not.Empty);
                Assert.That(read.SelectToken("document.Components")?.Type, Is.EqualTo(JTokenType.Array));
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void TwoChangesetsWithSameBaseRejectSecondCommitByCas()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                var baseHash = CreateAndCommitTestAsset(application);
                var firstId = BeginChangeset(application, baseHash);
                var secondId = BeginChangeset(application, baseHash);
                PatchLabel(application, firstId, "winner");

                var firstCommit = RequireResult(application.Execute(
                    "changeset_commit",
                    new JObject { ["changesetId"] = firstId }));
                Assert.That(firstCommit.Value<string>("contentHash"), Is.Not.EqualTo(baseHash));

                var conflict = application.Execute(
                    "changeset_commit",
                    new JObject { ["changesetId"] = secondId });
                Assert.That(conflict.Value<bool>("ok"), Is.False);
                Assert.That(conflict.SelectToken("error.code")?.Value<string>(), Is.EqualTo("content_conflict"));

                RequireResult(application.Execute(
                    "changeset_abort",
                    new JObject { ["changesetId"] = secondId }));
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void ResourceMutationRejectsDerivedManifest()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                var changesetId = BeginChangeset(application, string.Empty);

                var response = application.Execute(
                    "changeset_apply",
                    new JObject
                    {
                        ["changesetId"] = changesetId,
                        ["operations"] = new JArray
                        {
                            new JObject
                            {
                                ["kind"] = "putResource",
                                ["relativePath"] = "manifest.json",
                                ["base64"] = "AQ=="
                            }
                        }
                    });

                Assert.That(response.Value<bool>("ok"), Is.False);
                Assert.That(response.SelectToken("error.code")?.Value<string>(), Is.EqualTo("protected_path"));
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [TestCase("package.lock.json")]
        [TestCase(".snakeandmice-managed-base")]
        [TestCase(".snakeandmice-managed-module")]
        public void ResourceMutationTreatsLegacyOperationalNamesAsOrdinaryContent(string relativePath)
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                var changesetId = BeginChangeset(application, string.Empty);

                var response = application.Execute(
                    "changeset_apply",
                    new JObject
                    {
                        ["changesetId"] = changesetId,
                        ["operations"] = new JArray
                        {
                            new JObject
                            {
                                ["kind"] = "putResource",
                                ["relativePath"] = relativePath,
                                ["base64"] = "AQ=="
                            }
                        }
                    });

                var result = RequireResult(response);
                Assert.That(result["operations"], Has.Count.EqualTo(1));
                var preview = RequireResult(application.Execute(
                    "changeset_preview",
                    new JObject { ["changesetId"] = changesetId }));
                Assert.That(
                    preview.SelectToken("added[0].relativePath")?.Value<string>(),
                    Is.EqualTo(relativePath));
                RequireResult(application.Execute(
                    "changeset_commit",
                    new JObject { ["changesetId"] = changesetId }));

                var snapshot = GameAssetModuleContentScanner.Scan(
                    Path.Combine(environment.AssetsRoot, "test"),
                    "test");
                Assert.That(snapshot.Contains(relativePath), Is.True);
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void ColdStartRestoresSingleValidBackupWhenLiveModuleIsMissing()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                CreateAndCommitTestAsset(application);
                application.Shutdown();
                application = null;

                var liveRoot = Path.Combine(environment.AssetsRoot, "test");
                var recoveryRoot = Path.Combine(environment.Root, ".dingocms-edit", "r", "test", "crash0000000");
                Directory.CreateDirectory(Path.GetDirectoryName(recoveryRoot)
                                          ?? throw new InvalidOperationException("Recovery root has no parent."));
                Directory.Move(liveRoot, recoveryRoot);

                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);

                Assert.That(Directory.Exists(liveRoot), Is.True);
                Assert.That(Directory.Exists(recoveryRoot), Is.False);
                var modules = RequireResult(application.Execute("modules_list", new JObject()));
                Assert.That(modules.SelectToken("modules[0].moduleId")?.Value<string>(), Is.EqualTo("test"));
                Assert.That(modules.SelectToken("modules[0].valid")?.Value<bool>(), Is.True);
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void CreateAssetRemovesLegacySourceGuidAndKeepsSourceKey()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(
                    environment.AssetsRoot);
                var changesetId = BeginChangeset(
                    application,
                    string.Empty);
                var sourceKey = new JObject
                {
                    ["Mod"] = "test",
                    ["Type"] = "presentation",
                    ["Key"] = "road",
                    ["Version"] = "0.0.0"
                };
                RequireResult(application.Execute(
                    "changeset_apply",
                    new JObject
                    {
                        ["changesetId"] = changesetId,
                        ["operations"] = new JArray
                        {
                            new JObject
                            {
                                ["kind"] = "createAsset",
                                ["document"] = new JObject
                                {
                                    ["$type"] = "GameAsset",
                                    ["Components"] = new JArray(),
                                    ["SourceAssetGUID"] =
                                        "11111111111111111111111111111111",
                                    ["SourceAssetKey"] = sourceKey,
                                    ["Key"] = TestKey(),
                                    ["GUID"] =
                                        "00000000000000000000000000000000"
                                }
                            }
                        }
                    }));

                var staged = RequireResult(application.Execute(
                    "asset_get",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["changesetId"] = changesetId,
                        ["key"] = TestKey()
                    }));
                var document = (JObject)staged["document"];
                Assert.That(document.Property("SourceAssetGUID"), Is.Null);
                Assert.That(
                    JToken.DeepEquals(document["SourceAssetKey"], sourceKey),
                    Is.True);
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void CloneAssetDoesNotCopyLegacySourceGuid()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(
                    environment.AssetsRoot);
                CreateAndCommitTestAsset(application);
                var moduleRoot = Path.Combine(
                    environment.AssetsRoot,
                    "test");
                const string sourcePath =
                    "fixture/one/one@0.0.0.json";
                var source = DingoCmsAuthoringUtils.LoadJObject(
                    moduleRoot,
                    sourcePath);
                source["SourceAssetGUID"] =
                    "22222222222222222222222222222222";
                DingoCmsAuthoringUtils.WriteJObject(
                    moduleRoot,
                    sourcePath,
                    source);

                var modules = RequireResult(application.Execute(
                    "modules_list",
                    new JObject()));
                var contentHash = modules
                    .SelectToken("modules[0].contentHash")
                    ?.Value<string>();
                var changesetId = BeginChangeset(
                    application,
                    contentHash);
                var targetKey = new JObject
                {
                    ["Mod"] = "test",
                    ["Type"] = "fixture",
                    ["Key"] = "clone",
                    ["Version"] = "0.0.0"
                };
                RequireResult(application.Execute(
                    "changeset_apply",
                    new JObject
                    {
                        ["changesetId"] = changesetId,
                        ["operations"] = new JArray
                        {
                            new JObject
                            {
                                ["kind"] = "cloneAsset",
                                ["source"] = new JObject
                                {
                                    ["key"] = TestKey()
                                },
                                ["targetKey"] = targetKey
                            }
                        }
                    }));

                var staged = RequireResult(application.Execute(
                    "asset_get",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["changesetId"] = changesetId,
                        ["key"] = targetKey
                    }));
                var document = (JObject)staged["document"];
                Assert.That(document.Property("SourceAssetGUID"), Is.Null);
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void ResourcePutWritesInlineBytesAndPublishesOneRevision()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                var contentHash = CreateAndCommitTestAsset(application);

                var result = RequireResult(application.Execute(
                    "resource_put",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["relativePath"] = "sprites/icon.bin",
                        ["base64"] = Convert.ToBase64String(new byte[] { 1, 2, 3 })
                    }));

                Assert.That(result.Value<bool>("changed"), Is.True);
                Assert.That(result.Value<long>("size"), Is.EqualTo(3));
                Assert.That(result.Value<string>("previousContentHash"), Is.EqualTo(contentHash));
                Assert.That(result.Value<string>("contentHash"), Is.Not.EqualTo(contentHash));
                Assert.That(
                    File.ReadAllBytes(Path.Combine(environment.AssetsRoot, "test", "sprites", "icon.bin")),
                    Is.EqualTo(new byte[] { 1, 2, 3 }));
                AssertNoActiveChangesets(application);
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void ResourcePutCopiesSourceFileAndDerivesTargetPath()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                CreateAndCommitTestAsset(application);
                var sourcePath = Path.Combine(environment.Root, "hero.png");
                File.WriteAllBytes(sourcePath, new byte[] { 9, 8, 7, 6 });

                var atRoot = RequireResult(application.Execute(
                    "resource_put",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["sourcePath"] = sourcePath
                    }));
                var inFolder = RequireResult(application.Execute(
                    "resource_put",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["relativePath"] = "sprites/",
                        ["sourcePath"] = sourcePath
                    }));

                Assert.That(atRoot.Value<string>("relativePath"), Is.EqualTo("hero.png"));
                Assert.That(inFolder.Value<string>("relativePath"), Is.EqualTo("sprites/hero.png"));
                Assert.That(
                    File.ReadAllBytes(Path.Combine(environment.AssetsRoot, "test", "sprites", "hero.png")),
                    Is.EqualTo(new byte[] { 9, 8, 7, 6 }));
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void ResourcePutOfIdenticalBytesDoesNotPublishRevision()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                CreateAndCommitTestAsset(application);
                var upload = new JObject
                {
                    ["moduleId"] = "test",
                    ["relativePath"] = "sprites/icon.bin",
                    ["base64"] = Convert.ToBase64String(new byte[] { 1, 2, 3 })
                };
                var first = RequireResult(application.Execute("resource_put", (JObject)upload.DeepClone()));

                var second = RequireResult(application.Execute("resource_put", upload));

                Assert.That(second.Value<bool>("changed"), Is.False);
                Assert.That(second.Value<string>("contentHash"), Is.EqualTo(first.Value<string>("contentHash")));
                Assert.That(second.Value<string>("sha256"), Is.EqualTo(first.Value<string>("sha256")));
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void ResourcePutRejectsStaleExpectedSha256()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                CreateAndCommitTestAsset(application);
                RequireResult(application.Execute(
                    "resource_put",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["relativePath"] = "sprites/icon.bin",
                        ["base64"] = Convert.ToBase64String(new byte[] { 1 })
                    }));

                var response = application.Execute(
                    "resource_put",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["relativePath"] = "sprites/icon.bin",
                        ["base64"] = Convert.ToBase64String(new byte[] { 2 }),
                        ["expectedSha256"] = string.Empty
                    });

                Assert.That(response.Value<bool>("ok"), Is.False);
                Assert.That(response.SelectToken("error.code")?.Value<string>(), Is.EqualTo("content_conflict"));
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void ResourcePutRejectsDerivedManifest()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                var contentHash = CreateAndCommitTestAsset(application);

                var response = application.Execute(
                    "resource_put",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["relativePath"] = "manifest.json",
                        ["base64"] = "AQ=="
                    });

                Assert.That(response.Value<bool>("ok"), Is.False);
                Assert.That(response.SelectToken("error.code")?.Value<string>(), Is.EqualTo("protected_path"));
                var modules = RequireResult(application.Execute("modules_list", new JObject()));
                Assert.That(
                    modules.SelectToken("modules[0].contentHash")?.Value<string>(),
                    Is.EqualTo(contentHash));
                AssertNoActiveChangesets(application);
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void ResourcePutRequiresExactlyOneContentSource()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                CreateAndCommitTestAsset(application);

                var both = application.Execute(
                    "resource_put",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["relativePath"] = "sprites/icon.bin",
                        ["base64"] = "AQ==",
                        ["sourcePath"] = Path.Combine(environment.Root, "missing.bin")
                    });
                var neither = application.Execute(
                    "resource_put",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["relativePath"] = "sprites/icon.bin"
                    });
                var missing = application.Execute(
                    "resource_put",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["sourcePath"] = Path.Combine(environment.Root, "missing.bin")
                    });

                Assert.That(both.SelectToken("error.code")?.Value<string>(), Is.EqualTo("invalid_request"));
                Assert.That(neither.SelectToken("error.code")?.Value<string>(), Is.EqualTo("invalid_request"));
                Assert.That(missing.SelectToken("error.code")?.Value<string>(), Is.EqualTo("not_found"));
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void ResourceListReportsFilesWithoutAssetsOrManifest()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                CreateAndCommitTestAsset(application);
                RequireResult(application.Execute(
                    "resource_put",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["relativePath"] = "sprites/icon.bin",
                        ["base64"] = Convert.ToBase64String(new byte[] { 1, 2, 3 })
                    }));
                RequireResult(application.Execute(
                    "resource_put",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["relativePath"] = "audio/step.bin",
                        ["base64"] = Convert.ToBase64String(new byte[] { 4 })
                    }));

                var all = RequireResult(application.Execute(
                    "resource_list",
                    new JObject { ["moduleId"] = "test" }));
                var sprites = RequireResult(application.Execute(
                    "resource_list",
                    new JObject { ["moduleId"] = "test", ["prefix"] = "sprites/" }));

                Assert.That(all.Value<int>("total"), Is.EqualTo(2));
                Assert.That(
                    all.SelectToken("items[0].relativePath")?.Value<string>(),
                    Is.EqualTo("audio/step.bin"));
                Assert.That(sprites.Value<int>("total"), Is.EqualTo(1));
                Assert.That(
                    sprites.SelectToken("items[0].relativePath")?.Value<string>(),
                    Is.EqualTo("sprites/icon.bin"));
                Assert.That(sprites.SelectToken("items[0].size")?.Value<long>(), Is.EqualTo(3));
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        [Test]
        public void ChangesetPutResourceReadsHostSourceFile()
        {
            var environment = CreateEnvironment();
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(environment.AssetsRoot);
                var changesetId = BeginChangeset(application, string.Empty);
                var sourcePath = Path.Combine(environment.Root, "table.bin");
                File.WriteAllBytes(sourcePath, new byte[] { 5, 5 });

                var result = RequireResult(application.Execute(
                    "changeset_apply",
                    new JObject
                    {
                        ["changesetId"] = changesetId,
                        ["operations"] = new JArray
                        {
                            new JObject
                            {
                                ["kind"] = "putResource",
                                ["relativePath"] = "data/",
                                ["sourcePath"] = sourcePath
                            }
                        }
                    }));
                RequireResult(application.Execute(
                    "changeset_commit",
                    new JObject { ["changesetId"] = changesetId }));

                Assert.That(
                    result.SelectToken("operations[0].relativePath")?.Value<string>(),
                    Is.EqualTo("data/table.bin"));
                Assert.That(
                    File.ReadAllBytes(Path.Combine(environment.AssetsRoot, "test", "data", "table.bin")),
                    Is.EqualTo(new byte[] { 5, 5 }));
            }
            finally
            {
                application?.Shutdown();
                DeleteEnvironment(environment.Root);
            }
        }

        private static string CreateAndCommitTestAsset(DingoCmsAuthoringApplication application)
        {
            var begin = RequireResult(application.Execute(
                "changeset_begin",
                new JObject
                {
                    ["moduleId"] = "test",
                    ["expectedContentHash"] = string.Empty
                }));
            var changesetId = begin.Value<string>("changesetId");
            var document = new JObject
            {
                ["$type"] = "GameAsset",
                ["Components"] = new JArray(),
                ["Key"] = TestKey(),
                ["GUID"] = "00000000000000000000000000000000",
                ["Nested"] = new JObject
                {
                    ["Items"] = new JArray(
                        new JObject { ["label"] = "first" },
                        new JObject { ["label"] = "second" }),
                    ["a/b"] = new JObject { ["~name"] = 17 }
                }
            };

            RequireResult(application.Execute(
                "changeset_apply",
                new JObject
                {
                    ["changesetId"] = changesetId,
                    ["operations"] = new JArray
                    {
                        new JObject
                        {
                            ["kind"] = "createAsset",
                            ["document"] = document
                        }
                    }
                }));
            var validation = RequireResult(application.Execute(
                "changeset_validate",
                new JObject { ["changesetId"] = changesetId }));
            Assert.That(validation.Value<bool>("valid"), Is.True);
            var commit = RequireResult(application.Execute(
                "changeset_commit",
                new JObject { ["changesetId"] = changesetId }));
            return commit.Value<string>("contentHash");
        }

        private static JObject ReadTestAsset(DingoCmsAuthoringApplication application)
        {
            return RequireResult(application.Execute(
                "asset_get",
                new JObject
                {
                    ["moduleId"] = "test",
                    ["key"] = TestKey()
                }));
        }

        private static void AssertNoActiveChangesets(DingoCmsAuthoringApplication application)
        {
            var status = RequireResult(application.Execute("cms_status", new JObject()));
            Assert.That(status["activeChangesets"], Is.Empty);
        }

        private static string BeginChangeset(DingoCmsAuthoringApplication application, string expectedContentHash)
        {
            return RequireResult(application.Execute(
                    "changeset_begin",
                    new JObject
                    {
                        ["moduleId"] = "test",
                        ["expectedContentHash"] = expectedContentHash
                    }))
                .Value<string>("changesetId");
        }

        private static void PatchLabel(
            DingoCmsAuthoringApplication application,
            string changesetId,
            string value)
        {
            RequireResult(application.Execute(
                "changeset_apply",
                new JObject
                {
                    ["changesetId"] = changesetId,
                    ["operations"] = new JArray
                    {
                        new JObject
                        {
                            ["kind"] = "patchAsset",
                            ["selector"] = new JObject { ["key"] = TestKey() },
                            ["patches"] = new JArray
                            {
                                new JObject
                                {
                                    ["op"] = "set",
                                    ["path"] = "/Nested/Items/1/label",
                                    ["value"] = value
                                }
                            }
                        }
                    }
                }));
        }

        private static JObject TestKey()
        {
            return new JObject
            {
                ["Mod"] = "test",
                ["Type"] = "fixture",
                ["Key"] = "one",
                ["Version"] = "0.0.0"
            };
        }

        private static JObject RequireResult(JObject response)
        {
            Assert.That(
                response.Value<bool>("ok"),
                Is.True,
                response.SelectToken("error.message")?.Value<string>());
            return (JObject)response["result"];
        }

        private static TestEnvironment CreateEnvironment()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "DingoCmsAuthoringTests",
                Guid.NewGuid().ToString("N").Substring(0, 12));
            var assetsRoot = Path.Combine(root, "assets");
            Directory.CreateDirectory(assetsRoot);
            return new TestEnvironment(root, assetsRoot);
        }

        private static void DeleteEnvironment(string root)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private readonly struct TestEnvironment
        {
            public readonly string Root;
            public readonly string AssetsRoot;

            public TestEnvironment(string root, string assetsRoot)
            {
                Root = root;
                AssetsRoot = assetsRoot;
            }
        }
    }
}
#endif
