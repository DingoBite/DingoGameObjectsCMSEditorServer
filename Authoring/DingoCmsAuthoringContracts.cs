using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary.Manifest;

namespace DingoGameObjectsCMSEditorServer.Authoring
{
    public class DingoCmsAuthoringException : Exception
    {
        public readonly string Code;

        public DingoCmsAuthoringException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public DingoCmsAuthoringException(string code, string message, Exception innerException)
            : base(message, innerException)
        {
            Code = code;
        }
    }

    public enum DingoCmsChangesetState
    {
        Active,
        Committed,
        Aborted
    }

    public readonly struct DingoCmsFileFingerprint
    {
        public readonly string RelativePath;
        public readonly string Kind;
        public readonly long Size;
        public readonly string Sha256;

        public DingoCmsFileFingerprint(string relativePath, string kind, long size, string sha256)
        {
            RelativePath = relativePath;
            Kind = kind;
            Size = size;
            Sha256 = sha256;
        }
    }

    public class DingoCmsChangeset
    {
        public readonly object Sync = new();
        public readonly string Id;
        public readonly string ModuleId;
        public readonly string BaseContentHash;
        public readonly string StageRoot;
        public readonly GameAssetModuleContentSnapshot BaseSnapshot;
        public readonly IReadOnlyDictionary<string, DingoCmsFileFingerprint> BaseFiles;
        public readonly DateTime CreatedUtc;

        public DingoCmsChangesetState State;

        public DingoCmsChangeset(
            string id,
            string moduleId,
            string baseContentHash,
            string stageRoot,
            GameAssetModuleContentSnapshot baseSnapshot,
            IReadOnlyDictionary<string, DingoCmsFileFingerprint> baseFiles)
        {
            Id = id;
            ModuleId = moduleId;
            BaseContentHash = baseContentHash;
            StageRoot = stageRoot;
            BaseSnapshot = baseSnapshot;
            BaseFiles = baseFiles;
            CreatedUtc = DateTime.UtcNow;
            State = DingoCmsChangesetState.Active;
        }

        public void RequireActive()
        {
            if (State != DingoCmsChangesetState.Active)
            {
                throw new DingoCmsAuthoringException(
                    "changeset_not_active",
                    $"Changeset '{Id}' is {State.ToString().ToLowerInvariant()} and cannot be modified.");
            }
        }
    }
}
