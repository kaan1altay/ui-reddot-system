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
    /// One end-to-end pass over the demo scene: it loads, the UI builds, an event moves a
    /// badge.
    /// </summary>
    /// <remarks>
    /// Everything else is covered by the EditMode suite, which is faster and does not need
    /// a stage. This exists to catch the wiring the EditMode tests cannot see — that the
    /// scene really references the bootstrap, that FairyGUI's root comes up, and that the
    /// per-frame driver actually flushes.
    /// </remarks>
    public sealed class RedDotDemoSmokeTests
    {
        private const string SceneName = "RedDotDemo";

        [UnityTest]
        public IEnumerator TheDemoSceneComesUpAndABadgeFollowsAnEvent()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;

            var demo = Object.FindFirstObjectByType<DemoMain>();
            Assert.That(demo, Is.Not.Null, "the scene should carry a DemoMain");
            Assert.That(demo.Bridge, Is.Not.Null, "the Lua engine booted");
            Assert.That(demo.CurrentScreen, Is.EqualTo("Main"));

            TestContext.WriteLine(demo.UsingFallbackUI
                ? "running on the code-built fallback UI"
                : "running on the authored RedDotDemo package");

            var main = demo.GetScreen("Main");
            Assert.That(main, Is.Not.Null);
            Assert.That(main.parent, Is.EqualTo(GRoot.inst), "the main screen is on the root");

            var mailButton = main.GetChild("btnMail") as GComponent;
            Assert.That(mailButton, Is.Not.Null, "the main screen has a Mail button");

            var view = demo.Binder.ViewOf(mailButton);
            Assert.That(view, Is.Not.Null, "the Mail button is bound");
            Assert.That(view.HasBadge, Is.True);
            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageHidden), "nothing pending yet");

            // Three mails arrive. The badge must not move until the driver flushes, and it
            // must have moved by the frame after that.
            demo.Mail.Receive(3);
            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageHidden),
                "events mark nodes dirty; they do not evaluate anything");

            yield return null;

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageCount));
            Assert.That(view.CountText, Is.EqualTo("3"));
            Assert.That(demo.Bridge.GetState("Main").Visible, Is.True, "and it bubbled to the root");
        }

        [UnityTest]
        public IEnumerator TheExamplePatchAddsABadgeTheBuildNeverKnewAbout()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;

            var demo = Object.FindFirstObjectByType<DemoMain>();
            var shopButton = demo.GetScreen("Main").GetChild("btnShop") as GComponent;
            var shopView = demo.Binder.ViewOf(shopButton);

            Assert.That(shopView.CurrentPage, Is.EqualTo(RedDotView.PageHidden));
            Assert.That(demo.Bridge.ReadAllStates().ContainsKey("Main.Shop.LimitedOffer"), Is.False);

            var patch = System.IO.File.ReadAllText(
                System.IO.Path.Combine(Application.dataPath, "Lua/patches/rules_patch_example.lua"));
            demo.Bridge.ReloadRules(patch);
            yield return null;

            Assert.That(shopView.CurrentPage, Is.EqualTo(RedDotView.PageDot),
                "the Shop tab lit up through a child no C# file mentions");
            Assert.That(demo.Bridge.GetState("Main.Shop.LimitedOffer").Visible, Is.True);
        }
    }
}
