#if NEWTONSOFT_EXISTS
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
                    retainedProcessId: 31415),
                Is.False);
            Assert.That(
                DingoCmsDetachedEditorHost.CanCompleteStop(
                    processVerified: true,
                    retainedProcessId: 31415),
                Is.True);
            Assert.That(
                DingoCmsDetachedEditorHost.CanCompleteStop(
                    processVerified: false,
                    retainedProcessId: 0),
                Is.True);
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
