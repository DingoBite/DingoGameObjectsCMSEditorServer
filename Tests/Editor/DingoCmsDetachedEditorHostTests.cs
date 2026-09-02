#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using DingoGameObjectsCMSEditorServer.Editor;
using DingoGameObjectsCMSEditorServer.Runtime;
using DingoGameObjectsCMSEditorServer.Web;
using NUnit.Framework;

namespace DingoGameObjectsCMSEditorServer.Tests.Editor
{
    public class DingoCmsDetachedEditorHostTests
    {
        [TestCase(
            true,
            31415,
            DingoCmsDetachedProcessState.Running)]
        [TestCase(
            false,
            31415,
            DingoCmsDetachedProcessState.OwnershipVerificationPending)]
        [TestCase(
            false,
            0,
            DingoCmsDetachedProcessState.Stopped)]
        public void ProcessState_DoesNotDiscardUnverifiedOwnership(
            bool processVerified,
            int retainedProcessId,
            DingoCmsDetachedProcessState expected)
        {
            Assert.That(
                DingoCmsDetachedEditorHost.ResolveProcessState(
                    processVerified,
                    retainedProcessId),
                Is.EqualTo(expected));
        }

        [Test]
        public void Stop_CannotCompleteWhileOwnershipIsUnverified()
        {
            Assert.That(
                DingoCmsDetachedEditorHost.CanCompleteStop(
                    processVerified: false,
                    retainedIdentityExists: true),
                Is.False);
            Assert.That(
                DingoCmsDetachedEditorHost.CanCompleteStop(
                    processVerified: true,
                    retainedIdentityExists: true),
                Is.True);
            Assert.That(
                DingoCmsDetachedEditorHost.CanCompleteStop(
                    processVerified: false,
                    retainedIdentityExists: false),
                Is.True);
        }

        [Test]
        public void LegacyIdentity_AuthenticatedHealthMigratesExactOwnership()
        {
            var legacy = new DingoCmsEditorHostIdentity(
                1234,
                17847,
                "project",
                "instance-token",
                @"C:\Build\Host.exe");
            const long observedStartTicks = 638922816000000000L;

            Assert.That(
                DingoCmsDetachedEditorHost.TryCreateHealthVerifiedIdentity(
                    legacy,
                    observedStartTicks,
                    identityMatches: true,
                    reportedBuildFingerprint: " reported-fingerprint ",
                    out var migrated),
                Is.True);
            Assert.That(
                migrated.ProcessStartUtcTicks,
                Is.EqualTo(observedStartTicks));
            Assert.That(
                migrated.BuildFingerprint,
                Is.EqualTo("reported-fingerprint"));
            Assert.That(
                DingoCmsDetachedEditorHost.CanControlProcessAtCreationTime(
                    migrated,
                    observedStartTicks),
                Is.True);
            Assert.That(
                DingoCmsDetachedEditorHost.CanControlProcessAtCreationTime(
                    migrated,
                    observedStartTicks + 1),
                Is.False);
        }

        [Test]
        public void LegacyIdentity_HealthMismatchCannotMigrate()
        {
            var legacy = new DingoCmsEditorHostIdentity(
                1234,
                17847,
                "project",
                "instance-token",
                @"C:\Build\Host.exe");

            Assert.That(
                DingoCmsDetachedEditorHost.TryCreateHealthVerifiedIdentity(
                    legacy,
                    638922816000000000L,
                    identityMatches: false,
                    reportedBuildFingerprint: "fingerprint",
                    out _),
                Is.False);
        }

        [Test]
        public void LegacyIdentity_AuthenticatedPreFingerprintHostMigratesTicks()
        {
            var legacy = new DingoCmsEditorHostIdentity(
                1234,
                17847,
                "project",
                "instance-token",
                @"C:\Build\Host.exe");
            const long observedStartTicks = 638922816000000000L;

            Assert.That(
                DingoCmsDetachedEditorHost.TryCreateHealthVerifiedIdentity(
                    legacy,
                    observedStartTicks,
                    identityMatches: true,
                    reportedBuildFingerprint: null,
                    out var migrated),
                Is.True);
            Assert.That(
                migrated.ProcessStartUtcTicks,
                Is.EqualTo(observedStartTicks));
            Assert.That(migrated.BuildFingerprint, Is.Null);
        }

        [Test]
        public void RestartBackoff_AdvancesAndCapsWithoutResetting()
        {
            Assert.That(
                new[] { 0, 1, 2, 3, 4, 5, 6, 100 },
                Is.All.Matches<int>(attempt =>
                    DingoCmsDetachedEditorHost
                        .RestartDelaySecondsForAttempt(attempt) >= 0d));
            Assert.That(
                DingoCmsDetachedEditorHost.RestartDelaySecondsForAttempt(0),
                Is.EqualTo(0d));
            Assert.That(
                DingoCmsDetachedEditorHost.RestartDelaySecondsForAttempt(1),
                Is.EqualTo(1d));
            Assert.That(
                DingoCmsDetachedEditorHost.RestartDelaySecondsForAttempt(2),
                Is.EqualTo(3d));
            Assert.That(
                DingoCmsDetachedEditorHost.RestartDelaySecondsForAttempt(5),
                Is.EqualTo(30d));
            Assert.That(
                DingoCmsDetachedEditorHost.RestartDelaySecondsForAttempt(100),
                Is.EqualTo(30d));
        }

        [Test]
        public void StartupHealthTimeout_UsesFifteenSecondBoundary()
        {
            var started = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

            Assert.That(
                DingoCmsDetachedEditorHost.StartupHealthTimedOut(
                    started,
                    started.AddSeconds(14.999)),
                Is.False);
            Assert.That(
                DingoCmsDetachedEditorHost.StartupHealthTimedOut(
                    started,
                    started.AddSeconds(15)),
                Is.True);
        }

        [Test]
        public void ReadyHostRecovery_RequiresFailuresAndSustainedOutage()
        {
            Assert.That(
                DingoCmsDetachedEditorHost.ShouldRecoverReadyHost(
                    consecutiveFailures: 2,
                    unhealthySeconds: 60),
                Is.False);
            Assert.That(
                DingoCmsDetachedEditorHost.ShouldRecoverReadyHost(
                    consecutiveFailures: 3,
                    unhealthySeconds: 14.999),
                Is.False);
            Assert.That(
                DingoCmsDetachedEditorHost.ShouldRecoverReadyHost(
                    consecutiveFailures: 3,
                    unhealthySeconds: 15),
                Is.True);
        }

        [Test]
        public void RestartBackoff_ResetsOnlyAfterStableHealthWindow()
        {
            Assert.That(
                DingoCmsDetachedEditorHost.ShouldResetRestartBackoff(29.999),
                Is.False);
            Assert.That(
                DingoCmsDetachedEditorHost.ShouldResetRestartBackoff(30),
                Is.True);
        }

        [Test]
        public void BuildValidationCache_RetriesOnlyRetryableFailures()
        {
            Assert.That(
                DingoCmsDetachedEditorHost.ShouldRefreshBuildValidation(
                    forceRefresh: false,
                    initialized: true,
                    previousFailureIsRetryable: true,
                    now: 9.999,
                    retryAt: 10),
                Is.False);
            Assert.That(
                DingoCmsDetachedEditorHost.ShouldRefreshBuildValidation(
                    forceRefresh: false,
                    initialized: true,
                    previousFailureIsRetryable: true,
                    now: 10,
                    retryAt: 10),
                Is.True);
            Assert.That(
                DingoCmsDetachedEditorHost.ShouldRefreshBuildValidation(
                    forceRefresh: false,
                    initialized: true,
                    previousFailureIsRetryable: false,
                    now: 100,
                    retryAt: 10),
                Is.False);
            Assert.That(
                DingoCmsDetachedEditorHost.ShouldRefreshBuildValidation(
                    forceRefresh: true,
                    initialized: true,
                    previousFailureIsRetryable: false,
                    now: 0,
                    retryAt: double.PositiveInfinity),
                Is.True);
        }

        [Test]
        public void BuildMetadata_RoundTripsAndSourceChangesFingerprint()
        {
            var projectRoot = Path.Combine(
                Path.GetTempPath(),
                nameof(BuildMetadata_RoundTripsAndSourceChangesFingerprint),
                Guid.NewGuid().ToString("N"));
            var sourceDirectory = Path.Combine(
                projectRoot,
                "Assets",
                "AppSDK",
                "DingoGameObjectsCMSEditorServer",
                "Runtime");
            var sourcePath = Path.Combine(sourceDirectory, "HostSource.cs");
            var executablePath = Path.Combine(
                projectRoot,
                "Build",
                "Host.exe");
            var metadataPath =
                DingoCmsEditorHostBuildMetadataUtils.MetadataPath(
                    executablePath);
            try
            {
                Directory.CreateDirectory(sourceDirectory);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(executablePath));
                File.WriteAllText(sourcePath, "class HostSource { }");
                File.WriteAllText(executablePath, "test-player");
                var firstFingerprint =
                    DingoCmsEditorHostBuildMetadataUtils
                        .ComputeSourceFingerprint(projectRoot);
                var metadata = new DingoCmsEditorHostBuildMetadata(
                    firstFingerprint,
                    DateTime.UtcNow.Ticks);

                DingoCmsEditorHostBuildMetadataUtils.WriteAtomic(
                    metadataPath,
                    metadata);

                Assert.That(
                    DingoCmsEditorHostBuildMetadataUtils.TryRead(
                        metadataPath,
                        out var restored),
                    Is.True);
                Assert.That(
                    restored.SourceFingerprint,
                    Is.EqualTo(firstFingerprint));
                File.WriteAllText(
                    sourcePath,
                    "class HostSource { public int Value; }");
                Assert.That(
                    DingoCmsEditorHostBuildMetadataUtils
                        .ComputeSourceFingerprint(projectRoot),
                    Is.Not.EqualTo(firstFingerprint));
            }
            finally
            {
                if (Directory.Exists(projectRoot))
                {
                    Directory.Delete(projectRoot, recursive: true);
                }
            }
        }

        [Test]
        public void BuildFingerprint_IncludesSceneBootGlueAndSceneOrder()
        {
            var projectRoot = Path.Combine(
                Path.GetTempPath(),
                nameof(BuildFingerprint_IncludesSceneBootGlueAndSceneOrder),
                Guid.NewGuid().ToString("N"));
            var sourceDirectory = Path.Combine(
                projectRoot,
                "Assets",
                "AppSDK",
                "DingoGameObjectsCMSEditorServer",
                "Runtime");
            var scenesDirectory = Path.Combine(
                projectRoot,
                "Assets",
                "Scenes");
            var bootPath = Path.Combine(
                projectRoot,
                "Assets",
                "Game",
                "Boot.cs");
            var settingsPath = Path.Combine(
                projectRoot,
                "Assets",
                "Game",
                "HostSettings.asset");
            var firstScenePath = Path.Combine(
                scenesDirectory,
                "First.unity");
            var secondScenePath = Path.Combine(
                scenesDirectory,
                "Second.unity");
            try
            {
                Directory.CreateDirectory(sourceDirectory);
                Directory.CreateDirectory(scenesDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(bootPath));
                File.WriteAllText(
                    Path.Combine(sourceDirectory, "HostSource.cs"),
                    "class HostSource { }");
                File.WriteAllText(firstScenePath, "first-scene");
                File.WriteAllText(secondScenePath, "second-scene");
                File.WriteAllText(bootPath, "class Boot { }");
                File.WriteAllText(settingsPath, "host-settings");
                var scenePaths = new[]
                {
                    "Assets/Scenes/First.unity",
                    "Assets/Scenes/Second.unity",
                };
                var dependencyPaths = new[]
                {
                    "Assets/Game/Boot.cs",
                    "Assets/Game/HostSettings.asset",
                };
                var baseline = DingoCmsEditorHostBuildMetadataUtils
                    .ComputeSourceFingerprint(
                        projectRoot,
                        scenePaths,
                        dependencyPaths);

                File.WriteAllText(bootPath, "class Boot { int Value; }");
                Assert.That(
                    DingoCmsEditorHostBuildMetadataUtils
                        .ComputeSourceFingerprint(
                            projectRoot,
                            scenePaths,
                            dependencyPaths),
                    Is.Not.EqualTo(baseline));
                File.WriteAllText(bootPath, "class Boot { }");
                File.WriteAllText(settingsPath, "changed-host-settings");
                Assert.That(
                    DingoCmsEditorHostBuildMetadataUtils
                        .ComputeSourceFingerprint(
                            projectRoot,
                            scenePaths,
                            dependencyPaths),
                    Is.Not.EqualTo(baseline));
                File.WriteAllText(settingsPath, "host-settings");
                File.WriteAllText(firstScenePath, "changed-first-scene");
                Assert.That(
                    DingoCmsEditorHostBuildMetadataUtils
                        .ComputeSourceFingerprint(
                            projectRoot,
                            scenePaths,
                            dependencyPaths),
                    Is.Not.EqualTo(baseline));
                File.WriteAllText(firstScenePath, "first-scene");
                Assert.That(
                    DingoCmsEditorHostBuildMetadataUtils
                        .ComputeSourceFingerprint(
                            projectRoot,
                            new[] { scenePaths[1], scenePaths[0] },
                            dependencyPaths),
                    Is.Not.EqualTo(baseline));
            }
            finally
            {
                if (Directory.Exists(projectRoot))
                {
                    Directory.Delete(projectRoot, recursive: true);
                }
            }
        }

        [Test]
        public void BuildFingerprint_IncludesCompiledPlayerInputHashes()
        {
            var projectRoot = Path.Combine(
                Path.GetTempPath(),
                nameof(BuildFingerprint_IncludesCompiledPlayerInputHashes),
                Guid.NewGuid().ToString("N"));
            var hostSourceDirectory = Path.Combine(
                projectRoot,
                "Assets",
                "AppSDK",
                "DingoGameObjectsCMSEditorServer",
                "Runtime");
            try
            {
                Directory.CreateDirectory(hostSourceDirectory);
                File.WriteAllText(
                    Path.Combine(hostSourceDirectory, "HostSource.cs"),
                    "class HostSource { }");
                var baseline = DingoCmsEditorHostBuildMetadataUtils
                    .ComputeSourceFingerprint(
                        projectRoot,
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        new[]
                        {
                            new DingoCmsEditorHostFingerprintInput(
                                "Assets/Game/UnreferencedSchema.cs",
                                "schema-hash-a"),
                            new DingoCmsEditorHostFingerprintInput(
                                "Assets/Plugins/RuntimeSchema.dll",
                                "plugin-hash-a"),
                        });

                Assert.That(
                    DingoCmsEditorHostBuildMetadataUtils
                        .ComputeSourceFingerprint(
                            projectRoot,
                            Array.Empty<string>(),
                            Array.Empty<string>(),
                            new[]
                            {
                                new DingoCmsEditorHostFingerprintInput(
                                    "Assets/Game/UnreferencedSchema.cs",
                                    "schema-hash-b"),
                                new DingoCmsEditorHostFingerprintInput(
                                    "Assets/Plugins/RuntimeSchema.dll",
                                    "plugin-hash-a"),
                            }),
                    Is.Not.EqualTo(baseline));
                Assert.That(
                    DingoCmsEditorHostBuildMetadataUtils
                        .ComputeSourceFingerprint(
                            projectRoot,
                            Array.Empty<string>(),
                            Array.Empty<string>(),
                            new[]
                            {
                                new DingoCmsEditorHostFingerprintInput(
                                    "Assets/Game/UnreferencedSchema.cs",
                                    "schema-hash-a"),
                                new DingoCmsEditorHostFingerprintInput(
                                    "Assets/Plugins/RuntimeSchema.dll",
                                    "plugin-hash-b"),
                            }),
                    Is.Not.EqualTo(baseline));
            }
            finally
            {
                if (Directory.Exists(projectRoot))
                {
                    Directory.Delete(projectRoot, recursive: true);
                }
            }
        }

        [Test]
        public void BuildFingerprint_ChangesWhenCompiledReferenceIsReplaced()
        {
            var projectRoot = Path.Combine(
                Path.GetTempPath(),
                nameof(BuildFingerprint_ChangesWhenCompiledReferenceIsReplaced),
                Guid.NewGuid().ToString("N"));
            var referencePath = Path.Combine(
                projectRoot,
                "Packages",
                "com.example.runtime",
                "Runtime.dll");
            var fingerprintPath = "compiled-reference-content/"
                                  + referencePath.Replace('\\', '/');
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(
                    referencePath));
                File.WriteAllText(referencePath, "compiled-reference-v1");
                var baseline = DingoCmsEditorHostBuildMetadataUtils
                    .ComputeSourceFingerprint(
                        projectRoot,
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        new[]
                        {
                            new DingoCmsEditorHostFingerprintInput(
                                fingerprintPath,
                                DingoCmsEditorHostBuildMetadataUtils
                                    .ComputeFileContentHash(referencePath)),
                        });

                File.WriteAllText(referencePath, "compiled-reference-v2");

                Assert.That(
                    DingoCmsEditorHostBuildMetadataUtils
                        .ComputeSourceFingerprint(
                            projectRoot,
                            Array.Empty<string>(),
                            Array.Empty<string>(),
                            new[]
                            {
                                new DingoCmsEditorHostFingerprintInput(
                                    fingerprintPath,
                                    DingoCmsEditorHostBuildMetadataUtils
                                        .ComputeFileContentHash(
                                            referencePath)),
                            }),
                    Is.Not.EqualTo(baseline));
            }
            finally
            {
                if (Directory.Exists(projectRoot))
                {
                    Directory.Delete(projectRoot, recursive: true);
                }
            }
        }

        [Test]
        public void CompiledReferenceHashing_SkipsCoveredAssetAndUnityDlls()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                nameof(CompiledReferenceHashing_SkipsCoveredAssetAndUnityDlls),
                Guid.NewGuid().ToString("N"));
            var assetsRoot = Path.Combine(root, "Project", "Assets");
            var unityContentsRoot = Path.Combine(root, "Unity", "Editor", "Data");

            Assert.That(
                DingoCmsDetachedEditorHost
                    .ShouldHashCompiledReferenceContent(
                        Path.Combine(assetsRoot, "Plugins", "Runtime.dll"),
                        assetsRoot,
                        unityContentsRoot),
                Is.False);
            Assert.That(
                DingoCmsDetachedEditorHost
                    .ShouldHashCompiledReferenceContent(
                        Path.Combine(unityContentsRoot, "Managed", "Unity.dll"),
                        assetsRoot,
                        unityContentsRoot),
                Is.False);
            Assert.That(
                DingoCmsDetachedEditorHost
                    .ShouldHashCompiledReferenceContent(
                        Path.Combine(
                            root,
                            "Project",
                            "Library",
                            "PackageCache",
                            "Runtime.dll"),
                        assetsRoot,
                        unityContentsRoot),
                Is.True);
            Assert.That(
                DingoCmsDetachedEditorHost
                    .ShouldHashCompiledReferenceContent(
                        Path.Combine(root, "LocalPackage", "Runtime.dll"),
                        assetsRoot,
                        unityContentsRoot),
                Is.True);
        }

        [Test]
        public void BuildFingerprint_IgnoresDisabledSceneChanges()
        {
            var projectRoot = Path.Combine(
                Path.GetTempPath(),
                nameof(BuildFingerprint_IgnoresDisabledSceneChanges),
                Guid.NewGuid().ToString("N"));
            var sourceDirectory = Path.Combine(
                projectRoot,
                "Assets",
                "AppSDK",
                "DingoGameObjectsCMSEditorServer",
                "Runtime");
            var enabledScenePath = Path.Combine(
                projectRoot,
                "Assets",
                "Scenes",
                "Enabled.unity");
            var disabledScenePath = Path.Combine(
                projectRoot,
                "Assets",
                "Scenes",
                "Disabled.unity");
            try
            {
                Directory.CreateDirectory(sourceDirectory);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(enabledScenePath));
                File.WriteAllText(
                    Path.Combine(sourceDirectory, "HostSource.cs"),
                    "class HostSource { }");
                File.WriteAllText(enabledScenePath, "enabled-scene");
                File.WriteAllText(disabledScenePath, "disabled-scene");
                var baseline = DingoCmsEditorHostBuildMetadataUtils
                    .ComputeSourceFingerprint(
                        projectRoot,
                        new[] { "Assets/Scenes/Enabled.unity" });

                File.WriteAllText(
                    disabledScenePath,
                    "changed-disabled-scene");
                Assert.That(
                    DingoCmsEditorHostBuildMetadataUtils
                        .ComputeSourceFingerprint(
                            projectRoot,
                            new[] { "Assets/Scenes/Enabled.unity" }),
                    Is.EqualTo(baseline));
            }
            finally
            {
                if (Directory.Exists(projectRoot))
                {
                    Directory.Delete(projectRoot, recursive: true);
                }
            }
        }

        [Test]
        public void BuildArguments_LaunchesHiddenAuthoringOnlyPlayer()
        {
            var arguments = DingoCmsDetachedEditorHost.BuildArguments(
                17844,
                @"C:\Temp Folder\dingo.log");

            Assert.That(arguments, Does.Contain("-batchmode -nographics"));
            Assert.That(
                arguments,
                Does.Contain(
                    "-logFile \"C:\\Temp Folder\\dingo.log\""));
            Assert.That(
                arguments,
                Does.Contain(DingoCmsEditorServerOptions.ENABLE_ARGUMENT));
            Assert.That(
                arguments,
                Does.Contain(
                    DingoCmsEditorServerOptions.AUTHORING_ONLY_ARGUMENT));
            Assert.That(
                arguments,
                Does.Contain(
                    DingoCmsEditorServerOptions.PORT_ARGUMENT + "=17844"));
        }

        [Test]
        public void WebUi_ReportsChildColliderCompositionOnChildCard()
        {
            var html = DingoCmsEditorWebUi.LoadHtml();

            Assert.That(html, Does.Contain("componentSetWarning"));
            Assert.That(
                html,
                Does.Contain("componentSetWarning(value.Components, 'Child set')"));
            Assert.That(
                html,
                Does.Contain("contains a duplicate component type."));
            Assert.That(
                html,
                Does.Contain("Solid and trigger roles cannot share one entity."));
            Assert.That(
                html,
                Does.Contain("Sphere radius must be finite and positive."));
        }
    }
}
#endif
