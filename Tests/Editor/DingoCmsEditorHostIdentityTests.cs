#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using System.Linq;
using DingoGameObjectsCMSEditorServer.Runtime;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DingoGameObjectsCMSEditorServer.Tests.Editor
{
    public class DingoCmsEditorHostIdentityTests
    {
        private string _directory;
        private string _path;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                nameof(DingoCmsEditorHostIdentityTests),
                Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_directory, "host.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public void WriteAtomicAndTryRead_RoundTripAndReplaceState()
        {
            var first = CreateIdentity(12001, 17844, "first-token");
            var replacement = new DingoCmsEditorHostIdentity(
                12002,
                17845,
                "project-a",
                "replacement-token",
                @"C:\Tools\DingoCmsHost.exe",
                processStartUtcTicks: 638606160000000000L,
                ready: true,
                buildFingerprint: "build-fingerprint-a");

            DingoCmsEditorHostIdentity.WriteAtomic(_path, first);
            DingoCmsEditorHostIdentity.WriteAtomic(_path, replacement);

            Assert.That(
                DingoCmsEditorHostIdentity.TryRead(_path, out var restored),
                Is.True);
            Assert.That(restored.ProcessId, Is.EqualTo(replacement.ProcessId));
            Assert.That(restored.Port, Is.EqualTo(replacement.Port));
            Assert.That(restored.ProjectId, Is.EqualTo(replacement.ProjectId));
            Assert.That(restored.InstanceToken, Is.EqualTo(replacement.InstanceToken));
            Assert.That(restored.ExecutablePath, Is.EqualTo(replacement.ExecutablePath));
            Assert.That(
                restored.ProcessStartUtcTicks,
                Is.EqualTo(replacement.ProcessStartUtcTicks));
            Assert.That(restored.Ready, Is.True);
            Assert.That(
                restored.BuildFingerprint,
                Is.EqualTo(replacement.BuildFingerprint));
            Assert.That(
                Directory.GetFiles(_directory, "*.tmp").Any(),
                Is.False);
        }

        [Test]
        public void TryRead_RejectsMalformedAndStaleState()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(_path, "not-json");
            Assert.That(
                DingoCmsEditorHostIdentity.TryRead(_path, out _),
                Is.False);

            File.WriteAllText(
                _path,
                new JObject
                {
                    ["processId"] = 0,
                    ["port"] = 17844,
                    ["projectId"] = "project-a",
                    ["instanceToken"] = "stale-token",
                    ["executablePath"] = @"C:\Tools\DingoCmsHost.exe",
                }.ToString());
            Assert.That(
                DingoCmsEditorHostIdentity.TryRead(_path, out _),
                Is.False);
        }

        [Test]
        public void TryRead_LegacyStateDefaultsToNotReady()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(
                _path,
                new JObject
                {
                    ["processId"] = 12001,
                    ["port"] = 17844,
                    ["projectId"] = "project-a",
                    ["instanceToken"] = "legacy-token",
                    ["executablePath"] = @"C:\Tools\DingoCmsHost.exe",
                }.ToString());

            Assert.That(
                DingoCmsEditorHostIdentity.TryRead(
                    _path,
                    out var identity),
                Is.True);
            Assert.That(identity.Ready, Is.False);
            Assert.That(identity.ProcessStartUtcTicks, Is.Zero);
            Assert.That(identity.BuildFingerprint, Is.Null);
        }

        [Test]
        public void DeleteIfOwned_RequiresMatchingProcessAndInstanceToken()
        {
            var identity = CreateIdentity(12001, 17844, "owner-token");
            DingoCmsEditorHostIdentity.WriteAtomic(_path, identity);

            Assert.That(
                DingoCmsEditorHostIdentity.DeleteIfOwned(
                    _path,
                    processId: 12002,
                    instanceToken: identity.InstanceToken),
                Is.False);
            Assert.That(File.Exists(_path), Is.True);
            Assert.That(
                DingoCmsEditorHostIdentity.DeleteIfOwned(
                    _path,
                    processId: identity.ProcessId,
                    instanceToken: "other-token"),
                Is.False);
            Assert.That(File.Exists(_path), Is.True);

            Assert.That(
                DingoCmsEditorHostIdentity.DeleteIfOwned(
                    _path,
                    identity.ProcessId,
                    identity.InstanceToken),
                Is.True);
            Assert.That(File.Exists(_path), Is.False);
        }

        [Test]
        public void Constructor_ValidatesRequiredIdentityFields()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => CreateIdentity(0, 17844, "token"));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => CreateIdentity(12001, 0, "token"));
            Assert.Throws<ArgumentException>(
                () => new DingoCmsEditorHostIdentity(
                    12001,
                    17844,
                    " ",
                    "token",
                    @"C:\Tools\DingoCmsHost.exe"));
            Assert.Throws<ArgumentException>(
                () => CreateIdentity(12001, 17844, " "));
            Assert.Throws<ArgumentException>(
                () => new DingoCmsEditorHostIdentity(
                    12001,
                    17844,
                    "project-a",
                    "token",
                    " "));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new DingoCmsEditorHostIdentity(
                    12001,
                    17844,
                    "project-a",
                    "token",
                    @"C:\Tools\DingoCmsHost.exe",
                    processStartUtcTicks: -1));
        }

        private static DingoCmsEditorHostIdentity CreateIdentity(
            int processId,
            int port,
            string instanceToken)
        {
            return new DingoCmsEditorHostIdentity(
                processId,
                port,
                "project-a",
                instanceToken,
                @"C:\Tools\DingoCmsHost.exe");
        }
    }
}
#endif
