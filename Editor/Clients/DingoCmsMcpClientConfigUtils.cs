#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using System.Text;

namespace DingoGameObjectsCMSEditorServer.Editor.Clients
{
    public static class DingoCmsMcpClientConfigUtils
    {
        public static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(
                UnityEngine.Application.dataPath,
                ".."));
        }

        public static void WriteAtomically(
            string path,
            string content,
            string backupRoot,
            string backupName)
        {
            var directory = Path.GetDirectoryName(path)
                            ?? throw new InvalidOperationException(
                                $"Configuration path '{path}' has no directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                if (!File.Exists(path))
                {
                    File.Move(temporaryPath, path);
                    return;
                }

                Directory.CreateDirectory(backupRoot);
                var backupPath = Path.Combine(backupRoot, backupName);
                File.Replace(
                    temporaryPath,
                    path,
                    backupPath,
                    ignoreMetadataErrors: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        public static string NormalizeLineEndings(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);
        }

        public static string ToPlatformLineEndings(string value)
        {
            return NormalizeLineEndings(value)
                .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        }

        public static string EscapeTomlString(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
        }
    }
}
#endif
