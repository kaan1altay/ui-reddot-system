using System.Collections;
using FairyGUI;
using NUnit.Framework;
using RedDot.Demo;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace RedDot.Tests
{
    /// <summary>
    /// End-to-end passes over the demo scene: it loads, the UI builds, and the two
    /// lifecycles behave on screen.
    /// </summary>
    /// <remarks>
    /// Everything else is covered by the EditMode suite, which is faster and does not
    /// need a stage. These exist to catch the wiring the EditMode tests cannot see — that
    /// the scene really references the bootstrap, that FairyGUI's root comes up, and that
    /// the per-frame driver actually ticks.
    /// </remarks>
    public sealed class RedDotDemoSmokeTests
    {
        private const string SceneName = "RedDotDemo";

        private static IEnumerator LoadDemo()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
        }

        private static DemoMain Demo => Object.FindFirstObjectByType<DemoMain>();

        [UnityTest]
        public IEnumerator TheDemoSceneComesUpAndABadgeFollowsAnEvent()
        {
            yield return LoadDemo();

            var demo = Demo;
            Assert.That(demo, Is.Not.Null, "the scene should carry a DemoMain");
            Assert.That(demo.Bridge, Is.Not.Null, "the Lua engine booted");
            Assert.That(demo.CurrentScreen, Is.EqualTo("Main"));

            TestContext.WriteLine(demo.UsingFallbackUI
                ? "running on the code-built fallback UI"
                : "running on the authored RedDotDemo package");

            Assert.That(demo.LogPanel, Is.Not.Null);
            Assert.That(demo.LogPanel.Target, Is.EqualTo(
                demo.UsingFallbackUI ? DemoLogTarget.TextField : DemoLogTarget.List));
            Assert.That(demo.LogPanel.LineCount, Is.GreaterThan(0));

            var main = demo.GetScreen("Main");
            Assert.That(main.parent, Is.EqualTo(GRoot.inst), "the main screen is on the root");

            var mailButton = main.GetChild("btnMail") as GComponent;
            Assert.That(mailButton, Is.Not.Null, "the main screen has a Mail button");

            var view = demo.Binder.ViewOf(mailButton);
            Assert.That(view, Is.Not.Null, "the Mail button is bound");
            Assert.That(demo.Binder.KeyOf(mailButton), Is.EqualTo("Mail"),
                "to the global dot, which exists whether or not the mail screen was ever opened");

            // Boot seeds two mails, so the button is already on. Claim them and it goes
            // off on the frame after the driver ticks -- not before.
            Assert.That(view.Visible, Is.True);
            demo.Mail.ClaimAll();
            demo.Bridge.MarkSeen("Mail");
            Assert.That(view.Visible, Is.True, "events queue; they do not evaluate anything");

            yield return null;

            Assert.That(view.Visible, Is.False, "the driver flushed and the badge caught up");
        }

        [UnityTest]
        public IEnumerator OpeningTheMailScreenCreatesAKeyedDotPerRowAndLeavingDestroysThem()
        {
            yield return LoadDemo();

            var demo = Demo;
            var before = demo.Bridge.Counts();
            Assert.That(before.Keyed, Is.Zero,
                "the main screen only watches global dots, so nothing keyed is alive yet");

            (demo.GetScreen("Main").GetChild("btnMail") as GComponent)?.onClick.Call();
            yield return null;

            Assert.That(demo.CurrentScreen, Is.EqualTo("MailScreen"));

            var open = demo.Bridge.Counts();
            TestContext.WriteLine("mail rows: " + demo.MailRows.Count + ", keyed dots: " + open.Keyed);
            Assert.That(open.Keyed, Is.GreaterThan(before.Keyed),
                "the screen created keyed dots when its rows bound");

            (demo.GetScreen("MailScreen").GetChild("btnBack") as GComponent)?.onClick.Call();
            yield return null;

            Assert.That(demo.CurrentScreen, Is.EqualTo("Main"));
            Assert.That(demo.MailRows, Is.Empty);
            Assert.That(demo.Bridge.Counts().Keyed, Is.EqualTo(before.Keyed),
                "and they were destroyed with the last subscriber, not left to leak");
        }

        [UnityTest]
        public IEnumerator TheExamplePatchAddsATypeTheBuildNeverKnewAbout()
        {
            yield return LoadDemo();

            var demo = Demo;
            var shopButton = demo.GetScreen("Main").GetChild("btnShop") as GComponent;
            var shopView = demo.Binder.ViewOf(shopButton);

            Assert.That(demo.Bridge.ReadAllValues().ContainsKey("LimitedOffer"), Is.False);

            (demo.GetScreen("Main").GetChild("btnApplyPatch") as GComponent)?.onClick.Call();
            yield return null;

            Assert.That(demo.Bridge.ReadAllValues().ContainsKey("LimitedOffer"), Is.True,
                "a global dot for a type that did not exist a moment ago");
            Assert.That(demo.Bridge.GetValue("LimitedOffer"), Is.False, "no offer is running yet");

            (demo.GetScreen("Main").GetChild("btnStartOffer") as GComponent)?.onClick.Call();
            yield return null;

            Assert.That(demo.Bridge.GetValue("LimitedOffer"), Is.True);
            Assert.That(shopView, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator AdvancingADayFiresTheScheduledResetWithNoEventAtAll()
        {
            yield return LoadDemo();

            var demo = Demo;
            demo.Bridge.MarkSeen("Shop");
            yield return null;
            Assert.That(demo.Bridge.GetValue("Shop"), Is.False);

            var deadline = demo.Bridge.NextDeadline();
            Assert.That(deadline, Is.Not.Null);

            // Nothing is raised. The only thing that knows midnight happened is the clock,
            // and the manager's single deadline timer is what notices.
            demo.Clock.AdvanceDays(1);
            yield return null;

            Assert.That(demo.Bridge.GetValue("Shop"), Is.True,
                "a new rotation is new content, and only the clock knew");
            Assert.That(demo.Bridge.NextDeadline(), Is.GreaterThan(deadline));
        }
    }
}
