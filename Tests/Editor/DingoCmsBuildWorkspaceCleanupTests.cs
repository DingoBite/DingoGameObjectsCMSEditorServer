using System;
using System.IO;
using DingoGameObjectsCMSEditorServer.Editor;
using NUnit.Framework;
using UnityEditor;

namespace DingoGameObjectsCMSEditorServer.Tests.Editor
{
    public class DingoCmsBuildWorkspaceCleanupTests
    {
        [Test]
        public void RemoveEditorWorkspace_DeletesOnlyBuildCopy()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "dingo-cms-build-cleanup-" + Guid.NewGuid().ToString("N"));
            var streamingAssetsRoot = Path.Combine(root, "StreamingAssets");
            var workspaceRoot = Path.Combine(
                streamingAssetsRoot,
                DingoCmsBuildWorkspaceCleanup
                    .EDITOR_WORKSPACE_DIRECTORY_NAME);
            var packagedAssetPath = Path.Combine(
                streamingAssetsRoot,
                "PoliticsGameAssets",
                "base",
                "manifest.json");

            try
            {
                Directory.CreateDirectory(workspaceRoot);
                File.WriteAllText(
                    Path.Combine(workspaceRoot, "writer.lock"),
                    string.Empty);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(packagedAssetPath)
                    ?? throw new InvalidOperationException());
                File.WriteAllText(packagedAssetPath, "{}");

                Assert.That(
                    DingoCmsBuildWorkspaceCleanup.RemoveEditorWorkspace(
                        streamingAssetsRoot),
                    Is.True);
                Assert.That(Directory.Exists(workspaceRoot), Is.False);
                Assert.That(File.Exists(packagedAssetPath), Is.True);
                Assert.That(
                    DingoCmsBuildWorkspaceCleanup.RemoveEditorWorkspace(
                        streamingAssetsRoot),
                    Is.False);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [TestCase(BuildTarget.StandaloneWindows64)]
        [TestCase(BuildTarget.StandaloneLinux64)]
        public void ResolveStandaloneStreamingAssetsRoot_UsesDataDirectory(
            BuildTarget target)
        {
            var outputPath = Path.Combine(
                Path.GetTempPath(),
                "PoliticsBuild",
                "Politics.exe");

            Assert.That(
                DingoCmsBuildWorkspaceCleanup
                    .ResolveStandaloneStreamingAssetsRoot(target, outputPath),
                Is.EqualTo(Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(outputPath))
                    ?? throw new InvalidOperationException(),
                    "Politics_Data",
                    "StreamingAssets")));
        }
    }
}
