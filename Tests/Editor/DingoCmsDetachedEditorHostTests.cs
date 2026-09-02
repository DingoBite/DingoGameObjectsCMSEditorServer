#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using DingoGameObjectsCMSEditorServer.Editor;
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
        public void CreateLaunchArguments_LaunchesStandaloneBroker()
        {
            var arguments = DingoCmsDetachedEditorHost.CreateLaunchArguments(
                @"C:\Package Folder\dingo_cms_broker.py",
                17844,
                @"C:\Temp Folder\dingo.log",
                @"C:\Package Folder\index.html");

            Assert.That(
                arguments,
                Does.Contain(
                    "-u \"C:\\Package Folder\\dingo_cms_broker.py\""));
            Assert.That(
                arguments,
                Does.Contain(
                    "--port 17844"));
            Assert.That(
                arguments,
                Does.Contain(
                    "--web-index \"C:\\Package Folder\\index.html\""));
            Assert.That(
                arguments,
                Does.Contain(
                    "--log-file \"C:\\Temp Folder\\dingo.log\""));
            Assert.That(
                arguments,
                Does.Not.Contain("-batchmode"));
            Assert.That(arguments, Does.Not.Contain("-nographics"));
        }

        [Test]
        public void WebUi_ReportsChildColliderCompositionOnChildCard()
        {
            var html = File.ReadAllText(
                DingoCmsDetachedEditorHost.WebIndexPath);

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
