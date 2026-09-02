#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMSEditorServer.Editor
{
    class DingoCmsEditorHostBuildMetadata
    {
        public readonly string SourceFingerprint;
        public readonly long BuiltUtcTicks;

        public DingoCmsEditorHostBuildMetadata(
            string sourceFingerprint,
            long builtUtcTicks)
        {
            SourceFingerprint = RequireText(
                sourceFingerprint,
                nameof(sourceFingerprint));
            if (builtUtcTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(builtUtcTicks),
                    builtUtcTicks,
                    "Build time ticks must be positive.");
            }
            BuiltUtcTicks = builtUtcTicks;
        }

        private static string RequireText(string value, string parameterName)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(
                    "A non-empty value is required.",
                    parameterName);
            }
            return value;
        }
    }

    class DingoCmsEditorHostFingerprintInput
    {
        public readonly string Path;
        public readonly string ContentHash;

        public DingoCmsEditorHostFingerprintInput(
            string path,
            string contentHash)
        {
            Path = RequireText(path, nameof(path));
            ContentHash = RequireText(contentHash, nameof(contentHash));
        }

        private static string RequireText(string value, string parameterName)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(
                    "A non-empty value is required.",
                    parameterName);
            }
            return value;
        }
    }

    static class DingoCmsEditorHostBuildMetadataUtils
    {
        private static readonly HashSet<string> SOURCE_EXTENSIONS = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ".asmdef",
            ".asmref",
            ".compute",
            ".cs",
            ".html",
            ".json",
            ".shader",
            ".uss",
            ".uxml",
        };

        private static readonly HashSet<string> BUILD_DEPENDENCY_EXTENSIONS =
            new(
                SOURCE_EXTENSIONS.Concat(new[]
                {
                    ".asset",
                    ".bytes",
                    ".dll",
                    ".prefab",
                }),
                StringComparer.OrdinalIgnoreCase);

        private static readonly string[] PROJECT_BUILD_CONFIGURATION_PATHS =
        {
            "Packages/manifest.json",
            "Packages/packages-lock.json",
            "ProjectSettings/ProjectVersion.txt",
            "ProjectSettings/ProjectSettings.asset",
        };

        public static string MetadataPath(string executablePath)
        {
            return Path.GetFullPath(executablePath) + ".dingocms-build.json";
        }

        public static bool MayAffectBuildFingerprint(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return false;
            return string.Equals(
                       Path.GetExtension(assetPath),
                       ".unity",
                       StringComparison.OrdinalIgnoreCase)
                   || IsBuildDependency(assetPath);
        }

        public static string ComputeSourceFingerprint(string projectRoot)
        {
            return ComputeSourceFingerprint(
                projectRoot,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<DingoCmsEditorHostFingerprintInput>());
        }

        public static string ComputeSourceFingerprint(
            string projectRoot,
            IEnumerable<string> enabledScenePaths)
        {
            return ComputeSourceFingerprint(
                projectRoot,
                enabledScenePaths,
                Array.Empty<string>(),
                Array.Empty<DingoCmsEditorHostFingerprintInput>());
        }

        public static string ComputeSourceFingerprint(
            string projectRoot,
            IEnumerable<string> enabledScenePaths,
            IEnumerable<string> additionalBuildInputPaths)
        {
            return ComputeSourceFingerprint(
                projectRoot,
                enabledScenePaths,
                additionalBuildInputPaths,
                Array.Empty<DingoCmsEditorHostFingerprintInput>());
        }

        public static string ComputeSourceFingerprint(
            string projectRoot,
            IEnumerable<string> enabledScenePaths,
            IEnumerable<string> additionalBuildInputPaths,
            IEnumerable<DingoCmsEditorHostFingerprintInput>
                additionalHashedInputs)
        {
            var normalizedProjectRoot = Path.GetFullPath(projectRoot);
            var orderedScenePaths = (enabledScenePaths
                                     ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            var sourceRoots = new[]
            {
                Path.Combine(
                    normalizedProjectRoot,
                    "Assets",
                    "AppSDK",
                    "DingoGameObjectsCMS"),
                Path.Combine(
                    normalizedProjectRoot,
                    "Assets",
                    "AppSDK",
                    "DingoGameObjectsCMSEditorServer"),
            };
            var sourceFiles = new HashSet<string>(
                sourceRoots
                .Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateFiles(
                    root,
                    "*",
                    SearchOption.AllDirectories))
                .Where(IsPlayerSource)
                .Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            var assetsRoot = Path.Combine(
                normalizedProjectRoot,
                "Assets");
            if (Directory.Exists(assetsRoot))
            {
                foreach (var playerLibrary in Directory.EnumerateFiles(
                             assetsRoot,
                             "*.dll",
                             SearchOption.AllDirectories)
                         .Where(IsPlayerPath))
                {
                    sourceFiles.Add(Path.GetFullPath(playerLibrary));
                }
            }
            foreach (var configurationPath in
                     PROJECT_BUILD_CONFIGURATION_PATHS)
            {
                AddExplicitBuildInput(
                    sourceFiles,
                    normalizedProjectRoot,
                    configurationPath,
                    required: false,
                    buildDependencyOnly: false);
            }
            foreach (var scenePath in orderedScenePaths)
            {
                AddExplicitBuildInput(
                    sourceFiles,
                    normalizedProjectRoot,
                    scenePath,
                    required: true,
                    buildDependencyOnly: false);
            }
            foreach (var buildInputPath in additionalBuildInputPaths
                         ?? Array.Empty<string>())
            {
                AddExplicitBuildInput(
                    sourceFiles,
                    normalizedProjectRoot,
                    buildInputPath,
                    required: false,
                    buildDependencyOnly: true);
            }

            var hashedInputs = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var hashedInput in additionalHashedInputs
                         ?? Array.Empty<DingoCmsEditorHostFingerprintInput>())
            {
                if (hashedInput == null)
                    continue;
                var normalizedInputPath = NormalizeFingerprintPath(
                    hashedInput.Path,
                    normalizedProjectRoot);
                if (hashedInputs.TryGetValue(
                        normalizedInputPath,
                        out var previousHash)
                    && !string.Equals(
                        previousHash,
                        hashedInput.ContentHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Conflicting hashes were supplied for build input '"
                        + normalizedInputPath + "'.");
                }
                hashedInputs[normalizedInputPath] = hashedInput.ContentHash;
            }
            sourceFiles.RemoveWhere(path => hashedInputs.ContainsKey(
                NormalizeRelativePath(path, normalizedProjectRoot)));

            var orderedSourceFiles = sourceFiles
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (orderedSourceFiles.Length == 0 && hashedInputs.Count == 0)
            {
                throw new InvalidOperationException(
                    "No DingoCMS player source files were found.");
            }

            var aggregate = new StringBuilder(
                orderedSourceFiles.Length * 96);
            for (var index = 0; index < orderedScenePaths.Length; index++)
            {
                aggregate.Append("scene-order:");
                aggregate.Append(index);
                aggregate.Append(':');
                aggregate.Append(NormalizeRelativePath(
                    orderedScenePaths[index],
                    normalizedProjectRoot));
                aggregate.Append('\n');
            }
            foreach (var sourceFile in orderedSourceFiles)
            {
                var relativePath = NormalizeRelativePath(
                    sourceFile,
                    normalizedProjectRoot);
                aggregate.Append(relativePath);
                aggregate.Append(':');
                aggregate.Append(ComputeFileHash(sourceFile));
                aggregate.Append('\n');
            }
            foreach (var hashedInput in hashedInputs
                         .OrderBy(
                             pair => pair.Key,
                             StringComparer.OrdinalIgnoreCase))
            {
                aggregate.Append("asset-hash:");
                aggregate.Append(hashedInput.Key);
                aggregate.Append(':');
                aggregate.Append(hashedInput.Value);
                aggregate.Append('\n');
            }
            return ComputeBytesHash(
                Encoding.UTF8.GetBytes(aggregate.ToString()));
        }

        private static void AddExplicitBuildInput(
            ISet<string> sourceFiles,
            string projectRoot,
            string path,
            bool required,
            bool buildDependencyOnly)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var fullPath = Path.GetFullPath(
                Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(projectRoot, path));
            if (!File.Exists(fullPath))
            {
                if (required)
                {
                    throw new FileNotFoundException(
                        "An enabled build scene is missing.",
                        fullPath);
                }
                return;
            }
            if (!IsUnderProjectRoot(fullPath, projectRoot))
            {
                throw new InvalidOperationException(
                    "Detached host build inputs must be inside the Unity "
                    + "project: " + fullPath);
            }
            if (buildDependencyOnly && !IsBuildDependency(fullPath))
                return;
            sourceFiles.Add(fullPath);
        }

        private static string NormalizeRelativePath(
            string path,
            string projectRoot)
        {
            var fullPath = Path.GetFullPath(
                Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(projectRoot, path));
            return fullPath
                .Substring(projectRoot.Length)
                .TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }

        private static string NormalizeFingerprintPath(
            string path,
            string projectRoot)
        {
            if (Path.IsPathRooted(path))
            {
                var fullPath = Path.GetFullPath(path);
                if (IsUnderProjectRoot(fullPath, projectRoot))
                {
                    return NormalizeRelativePath(fullPath, projectRoot);
                }
                return fullPath.Replace('\\', '/');
            }
            return path.Trim()
                .TrimStart('/', '\\')
                .Replace('\\', '/');
        }

        private static bool IsUnderProjectRoot(
            string path,
            string projectRoot)
        {
            var rootWithSeparator = projectRoot.TrimEnd(
                                        Path.DirectorySeparatorChar,
                                        Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;
            return path.StartsWith(
                rootWithSeparator,
                StringComparison.OrdinalIgnoreCase);
        }

        public static void WriteAtomic(
            string path,
            DingoCmsEditorHostBuildMetadata metadata)
        {
            if (metadata == null)
            {
                throw new ArgumentNullException(nameof(metadata));
            }

            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            var temporaryPath = fullPath + "."
                                + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var document = new JObject
                {
                    ["sourceFingerprint"] = metadata.SourceFingerprint,
                    ["builtUtcTicks"] = metadata.BuiltUtcTicks,
                };
                File.WriteAllText(
                    temporaryPath,
                    document.ToString(Formatting.None),
                    new UTF8Encoding(false));
                if (File.Exists(fullPath))
                {
                    File.Replace(temporaryPath, fullPath, null);
                }
                else
                {
                    File.Move(temporaryPath, fullPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        public static bool TryRead(
            string path,
            out DingoCmsEditorHostBuildMetadata metadata)
        {
            metadata = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var document = JObject.Parse(
                    File.ReadAllText(Path.GetFullPath(path)));
                var sourceFingerprint = document
                    .Value<string>("sourceFingerprint")?.Trim();
                var builtUtcTicks = document
                    .Value<long?>("builtUtcTicks") ?? 0;
                if (string.IsNullOrEmpty(sourceFingerprint)
                    || builtUtcTicks <= 0)
                {
                    return false;
                }
                metadata = new DingoCmsEditorHostBuildMetadata(
                    sourceFingerprint,
                    builtUtcTicks);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsPlayerSource(string path)
        {
            return SOURCE_EXTENSIONS.Contains(Path.GetExtension(path))
                   && IsPlayerPath(path);
        }

        private static bool IsBuildDependency(string path)
        {
            return BUILD_DEPENDENCY_EXTENSIONS.Contains(
                       Path.GetExtension(path))
                   && IsPlayerPath(path);
        }

        private static bool IsPlayerPath(string path)
        {
            var normalized = path.Replace('\\', '/');
            return normalized.IndexOf(
                       "/Editor/",
                       StringComparison.OrdinalIgnoreCase) < 0
                   && normalized.IndexOf(
                       "/Tests/",
                       StringComparison.OrdinalIgnoreCase) < 0;
        }

        public static string ComputeFileContentHash(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException(
                    "A non-empty path is required.",
                    nameof(path));
            return ComputeFileHash(Path.GetFullPath(path));
        }

        private static string ComputeFileHash(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sha256 = SHA256.Create();
            return ToLowerHex(sha256.ComputeHash(stream));
        }

        private static string ComputeBytesHash(byte[] bytes)
        {
            using var sha256 = SHA256.Create();
            return ToLowerHex(sha256.ComputeHash(bytes));
        }

        private static string ToLowerHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes)
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }
    }
}
#endif
