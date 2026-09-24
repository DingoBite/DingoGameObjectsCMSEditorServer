#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMSEditorServer.Authoring;
using DingoGameObjectsCMSEditorServer.Editor.Authoring;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMSEditorServer.Tests.Editor
{
    [Serializable, Preserve]
    public class GameAssetObjectAuthoringPublisherTestComponent : GameAssetComponent
    {
        public int Value;
    }

    public class GameAssetObjectAuthoringPublisherTests
    {
        [Test]
        public void CreateBakeAndDeletePreserveIdentityAndRejectStaleDocumentHash()
        {
            var root = Path.Combine(Path.GetTempPath(), "DingoCmsObjectAuthoring-" + Guid.NewGuid().ToString("N"));
            var assetsRoot = Path.Combine(root, "assets");
            DingoCmsAuthoringApplication application = null;
            try
            {
                application = new DingoCmsAuthoringApplication(assetsRoot);
                var key = new GameAssetKey("authoringtest", "object", "sample", "1.0.0");
                var initial = new List<GameAssetComponent> { new GameAssetObjectAuthoringPublisherTestComponent { Value = 1 } };

                Assert.That(GameAssetObjectAuthoringPublisher.TryCreateWithExecutor(application.Execute, key, null, initial, out var created, out var error), Is.True, error);
                Assert.That(created.RelativeJsonPath, Is.EqualTo("object/sample/sample@1.0.0.json"));
                Assert.That(created.AssetGuid, Is.Not.Empty);
                Assert.That(created.DocumentSha256, Is.Not.Empty);

                var changed = new List<GameAssetComponent> { new GameAssetObjectAuthoringPublisherTestComponent { Value = 2 } };
                Assert.That(GameAssetObjectAuthoringPublisher.TryBakeWithExecutor(application.Execute, key, created.RelativeJsonPath, created.AssetGuid, created.DocumentSha256, changed, out var baked, out error), Is.True, error);
                Assert.That(baked.AssetGuid, Is.EqualTo(created.AssetGuid));
                Assert.That(baked.DocumentSha256, Is.Not.EqualTo(created.DocumentSha256));
                Assert.That(baked.Changed, Is.True);

                Assert.That(GameAssetObjectAuthoringPublisher.TryBakeWithExecutor(application.Execute, key, created.RelativeJsonPath, created.AssetGuid, created.DocumentSha256, initial, out _, out error), Is.False);
                Assert.That(error, Does.Contain("changed outside"));
                Assert.That(GameAssetObjectAuthoringPublisher.TryDeleteWithExecutor(application.Execute, key, created.RelativeJsonPath, created.AssetGuid, created.DocumentSha256, out error), Is.False);

                Assert.That(GameAssetObjectAuthoringPublisher.TryDeleteWithExecutor(application.Execute, key, baked.RelativeJsonPath, baked.AssetGuid, baked.DocumentSha256, out error), Is.True, error);
                var missing = application.Execute("asset_get", new JObject { ["moduleId"] = key.Mod, ["relativeJsonPath"] = baked.RelativeJsonPath });
                Assert.That(missing.Value<bool>("ok"), Is.False);
                Assert.That(missing.SelectToken("error.code")?.Value<string>(), Is.EqualTo("not_found"));
            }
            finally
            {
                application?.Shutdown();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }
    }
}
#endif
