using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace DingoGameObjectsCMSEditorServer.Editor
{
    public class DingoCmsBuildWorkspaceCleanup :
        IPostprocessBuildWithReport
    {
        public const string EDITOR_WORKSPACE_DIRECTORY_NAME =
            ".dingocms-edit";

        public int callbackOrder => int.MaxValue;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            var streamingAssetsRoot = ResolveStandaloneStreamingAssetsRoot(
                report.summary.platform,
                report.summary.outputPath);
            if (streamingAssetsRoot == null
                || !RemoveEditorWorkspace(streamingAssetsRoot))
            {
                return;
            }

            Debug.Log(
                "DingoCMS removed the editor authoring workspace from "
                + $"standalone build '{streamingAssetsRoot}'.");
        }

        public static string ResolveStandaloneStreamingAssetsRoot(
            BuildTarget target,
            string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException(
                    "Build output path is required.",
                    nameof(outputPath));
            }

            var fullOutputPath = Path.GetFullPath(outputPath);
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneLinux64:
                    var outputDirectory = Path.GetDirectoryName(
                        fullOutputPath);
                    if (string.IsNullOrWhiteSpace(outputDirectory))
                    {
                        throw new ArgumentException(
                            "Standalone output path must include a directory.",
                            nameof(outputPath));
                    }

                    var productName = Path.GetFileNameWithoutExtension(
                        fullOutputPath);
                    return Path.Combine(
                        outputDirectory,
                        productName + "_Data",
                        "StreamingAssets");

                case BuildTarget.StandaloneOSX:
                    return Path.Combine(
                        fullOutputPath,
                        "Contents",
                        "Resources",
                        "Data",
                        "StreamingAssets");

                default:
                    return null;
            }
        }

        public static bool RemoveEditorWorkspace(string streamingAssetsRoot)
        {
            if (string.IsNullOrWhiteSpace(streamingAssetsRoot))
            {
                throw new ArgumentException(
                    "StreamingAssets root is required.",
                    nameof(streamingAssetsRoot));
            }

            var fullStreamingAssetsRoot = Path.GetFullPath(
                    streamingAssetsRoot)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            var workspaceRoot = Path.GetFullPath(Path.Combine(
                fullStreamingAssetsRoot,
                EDITOR_WORKSPACE_DIRECTORY_NAME));
            if (!string.Equals(
                    Path.GetDirectoryName(workspaceRoot),
                    fullStreamingAssetsRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "DingoCMS editor workspace must be a direct child of "
                    + "the standalone StreamingAssets directory.");
            }

            if (!Directory.Exists(workspaceRoot))
            {
                return false;
            }

            Directory.Delete(workspaceRoot, recursive: true);
            return true;
        }
    }
}
