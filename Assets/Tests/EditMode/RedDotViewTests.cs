using System.Collections.Generic;
using FairyGUI;
using NUnit.Framework;
using RedDot.Demo;
using RedDot.Events;
using UnityEngine;

namespace RedDot.Tests
{
    /// <summary>
    /// Tests for the FairyGUI view layer.
    /// </summary>
    /// <remarks>
    /// The components here are the same ones <see cref="DemoUIFactory"/> builds for the
    /// fallback UI: real <c>GComponent</c>s with a real controller and real display gears,
    /// so a page change hides real children. That makes these tests a check on the
    /// authoring contract in <c>docs/PACKAGE_SPEC.md</c> as much as on the C#.
    /// </remarks>
    [TestFixture]
    public sealed class RedDotViewTests
    {
        private const string Inbox = "Main.Mail.Inbox";
        private const string Daily = "Main.Quests.Daily";
        private const string Mail = "Main.Mail";

        private readonly List<GObject> _created = new List<GObject>();

        [SetUp]
        public void SetUp()
        {
            FairyGuiEnvironment.EnsureDefaultFont();
        }

        [TearDown]
        public void TearDown()
        {
            _bridge?.Dispose();
            _bridge = null;
            _binder = null;

            foreach (var obj in _created)
            {
                if (!obj.isDisposed)
                {
                    obj.Dispose();
                }
            }

            _created.Clear();
        }

        private GComponent Track(GComponent component)
        {
            _created.Add(component);
            return component;
        }

        private GComponent NewHost(bool withBadge = true, bool withCountField = true)
        {
            var host = new GComponent { name = "btnMail" };
            host.SetSize(220, 150);
            if (withBadge)
            {
                host.AddChild(DemoUIFactory.CreateBadge(withCountField));
            }

            return Track(host);
        }

        private static void Apply(RedDotView view, bool visible, int count)
        {
            view.SetRedDot("Main.Test", visible, count);
        }

        /// <summary>
        /// Whether a display gear is currently showing this child. A gear does not touch
        /// the `visible` flag -- it takes the object out of the render list -- so this is
        /// what "the controller hid it" actually looks like from outside.
        /// </summary>
        private static bool ShownByGear(GComponent host, string childName)
        {
            return Child(host, childName).displayObject.parent != null;
        }

        #region State selection

        [Test]
        public void AHiddenBadgeSelectsTheHiddenPage()
        {
            var host = NewHost();
            var view = new RedDotView(host);

            Apply(view, false, 0);

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageHidden));
            Assert.That(ShownByGear(host, "dot"), Is.False,
                "the display gear really hides the artwork, exactly as an authored package would");
        }

        [Test]
        public void ASingleItemStaysAPlainDot()
        {
            var host = NewHost();
            var view = new RedDotView(host);

            Apply(view, true, 1);

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageDot),
                "a lone '1' next to an icon is decoration, not information");
            Assert.That(ShownByGear(host, "dot"), Is.True);
            Assert.That(ShownByGear(host, RedDotView.CountChildName), Is.False);
        }

        [Test]
        public void ADotOnlyNodeWithNoCountStaysADot()
        {
            var host = NewHost();
            var view = new RedDotView(host);

            // What an `any` policy parent reports: visible, but deliberately countless.
            Apply(view, true, 0);

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageDot));
        }

        [Test]
        public void ACountAboveOneSelectsTheCountPage()
        {
            var host = NewHost();
            var view = new RedDotView(host);

            Apply(view, true, 7);

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageCount));
            Assert.That(view.CountText, Is.EqualTo("7"));
            Assert.That(ShownByGear(host, RedDotView.CountChildName), Is.True);
        }

        [Test]
        public void CountsAreCappedAtNinetyNinePlus()
        {
            var host = NewHost();
            var view = new RedDotView(host);

            Apply(view, true, 99);
            Assert.That(view.CountText, Is.EqualTo("99"), "ninety-nine still fits");

            Apply(view, true, 100);
            Assert.That(view.CountText, Is.EqualTo(RedDotView.OverflowText));

            Apply(view, true, 41235);
            Assert.That(view.CountText, Is.EqualTo("99+"),
                "so the badge never has to be wider than two digits");
        }

        [Test]
        public void ABadgeGoesBackAndForthBetweenPages()
        {
            var host = NewHost();
            var view = new RedDotView(host);

            Apply(view, true, 4);
            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageCount));

            Apply(view, true, 1);
            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageDot));
            Assert.That(ShownByGear(host, RedDotView.CountChildName), Is.False);

            Apply(view, false, 0);
            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageHidden));

            Apply(view, true, 12);
            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageCount));
            Assert.That(view.CountText, Is.EqualTo("12"));
        }

        #endregion

        #region Degrading

        [Test]
        public void ABadgeWithoutACountFieldFallsBackToTheDot()
        {
            var host = NewHost(withCountField: false);
            var view = new RedDotView(host);

            Assert.That(view.HasBadge, Is.True);
            Assert.That(view.HasCountField, Is.False);

            Apply(view, true, 9);

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageDot),
                "a package that has not grown a number yet still shows that something is there");
            Assert.That(view.Count, Is.EqualTo(9), "the state is still recorded, only not drawn");
        }

        [Test]
        public void ABadgeWithoutAStateControllerFallsBackToPlainVisibility()
        {
            var host = new GComponent { name = "btnMail" };
            var badge = new GComponent { name = RedDotView.BadgeChildName };
            var dot = new GGraph { name = "dot" };
            dot.DrawEllipse(40, 40, Color.red);
            badge.AddChild(dot);
            host.AddChild(badge);
            Track(host);

            var view = new RedDotView(host);
            Assert.That(view.HasStateController, Is.False);

            Apply(view, true, 3);
            Assert.That(badge.visible, Is.True);
            Assert.That(view.CurrentPage, Is.Null);

            Apply(view, false, 0);
            Assert.That(badge.visible, Is.False);
        }

        [Test]
        public void AHostWithoutABadgeIsInert()
        {
            var host = NewHost(withBadge: false);
            var view = new RedDotView(host);

            Assert.That(view.HasBadge, Is.False);
            Assert.DoesNotThrow(() => Apply(view, true, 5),
                "a screen the designer has not finished must not take the game down");
            Assert.That(view.Count, Is.EqualTo(5));
        }

        [Test]
        public void ADisposedHostIsNotTouched()
        {
            var host = NewHost();
            var view = new RedDotView(host);
            host.Dispose();

            Assert.DoesNotThrow(() => Apply(view, true, 3));
        }

        #endregion

        #region Binding lifetime

        private EventBus _bus;
        private FakeMailService _mail;
        private FakeQuestService _quests;
        private RedDotBridge _bridge;
        private RedDotBinder _binder;

        private void StartEngine()
        {
            _bus = new EventBus();
            _mail = new FakeMailService(_bus);
            _quests = new FakeQuestService(_bus);

            _bridge = new RedDotBridge(new RedDotBridgeOptions
            {
                Bus = _bus,
                Context = new RedDotContext(_mail, _quests, new FakeShopService(_bus)),
                SeenPersistence = new InMemorySeenPersistence(),
                Log = message => TestContext.WriteLine("[lua] " + message),
            });

            _binder = new RedDotBinder(_bridge);
        }

        [Test]
        public void BindingPushesTheCurrentStateStraightIntoTheBadge()
        {
            StartEngine();
            _mail.Receive(3);
            _bridge.Flush();

            var host = NewHost();
            var view = _binder.Bind(host, Inbox, "MailScreen");

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageCount));
            Assert.That(view.CountText, Is.EqualTo("3"),
                "a screen that opens late is still correct on its first frame");
        }

        [Test]
        public void ABoundBadgeFollowsTheEngine()
        {
            StartEngine();
            var host = NewHost();
            var view = _binder.Bind(host, Inbox, "MailScreen");
            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageHidden));

            _mail.Receive(2);
            _bridge.Flush();
            Assert.That(view.CountText, Is.EqualTo("2"));

            _mail.ReadAll();
            _bridge.Flush();
            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageHidden));
        }

        /// <summary>
        /// The regression this class exists for: a component that comes back out of a pool
        /// and is bound to a different node must not still be listening to the old one.
        /// </summary>
        [Test]
        public void RebindingAPooledComponentDropsTheOldBindingEntirely()
        {
            StartEngine();
            _mail.Receive(3);
            _bridge.Flush();

            var host = NewHost();
            var firstView = _binder.Bind(host, Inbox, "MailScreen");
            Assert.That(firstView.CountText, Is.EqualTo("3"));

            // The list recycles the row for a different node.
            var secondView = _binder.Bind(host, Daily, "QuestsScreen");

            Assert.That(_binder.Count, Is.EqualTo(1), "one component, one binding");
            Assert.That(_binder.PathOf(host), Is.EqualTo(Daily));
            Assert.That(_bridge.BindingCount(Inbox), Is.Zero, "the old callback is gone from Lua");
            Assert.That(_bridge.BindingCount(Daily), Is.EqualTo(1));

            // The old node keeps changing; nothing must arrive.
            _mail.Receive(5);
            _quests.CompleteDaily(2);
            _bridge.Flush();

            Assert.That(firstView.Count, Is.EqualTo(3),
                "the stale view never heard about the five new mails");
            Assert.That(secondView.CountText, Is.EqualTo("2"));
            Assert.That(secondView.Path, Is.EqualTo(Daily));
        }

        [Test]
        public void RebindingToTheSamePathDoesNotStackCallbacks()
        {
            StartEngine();
            var host = NewHost();

            _binder.Bind(host, Inbox, "MailScreen");
            _binder.Bind(host, Inbox, "MailScreen");
            _binder.Bind(host, Inbox, "MailScreen");

            Assert.That(_bridge.BindingCount(Inbox), Is.EqualTo(1));
            Assert.That(_binder.Count, Is.EqualTo(1));
        }

        [Test]
        public void UnbindReleasesTheComponentAndIsSafeToRepeat()
        {
            StartEngine();
            var host = NewHost();
            var view = _binder.Bind(host, Inbox, "MailScreen");

            Assert.That(_binder.Unbind(host), Is.True);
            Assert.That(_binder.Unbind(host), Is.False, "unbinding twice is a no-op, not an error");
            Assert.That(_bridge.BindingCount(Inbox), Is.Zero);

            _mail.Receive(4);
            _bridge.Flush();
            Assert.That(view.Count, Is.Zero);
        }

        [Test]
        public void UnbindAllReleasesEveryBadgeOnAScreen()
        {
            StartEngine();
            var inboxHost = NewHost();
            var dailyHost = NewHost();
            var otherHost = NewHost();

            _binder.Bind(inboxHost, Inbox, "MailScreen");
            _binder.Bind(dailyHost, Daily, "MailScreen");
            _binder.Bind(otherHost, Mail, "MainScreen");
            Assert.That(_binder.Count, Is.EqualTo(3));

            _binder.UnbindAll("MailScreen");

            Assert.That(_binder.Count, Is.EqualTo(1), "only the other screen survives");
            Assert.That(_bridge.BindingCount(Inbox), Is.Zero);
            Assert.That(_bridge.BindingCount(Daily), Is.Zero);
            Assert.That(_bridge.BindingCount(Mail), Is.EqualTo(1));
            Assert.That(_binder.PathOf(otherHost), Is.EqualTo(Mail));
        }

        [Test]
        public void ADisposedComponentReleasesItselfOnTheNextUpdate()
        {
            StartEngine();
            var host = NewHost();
            _binder.Bind(host, Inbox, "MailScreen");

            // Screen teardown, without anybody telling the binder about it.
            host.Dispose();

            _mail.Receive(1);
            _bridge.Flush();

            Assert.That(_binder.Count, Is.Zero, "the update found a dead component and let go");
            Assert.That(_bridge.BindingCount(Inbox), Is.Zero);
        }

        [Test]
        public void DisposedComponentsCanBeSweptWithoutWaitingForAnUpdate()
        {
            StartEngine();
            var host = NewHost();
            _binder.Bind(host, Inbox, "MailScreen");
            host.Dispose();

            Assert.That(_binder.ReapDisposed(), Is.EqualTo(1));
            Assert.That(_bridge.BindingCount(Inbox), Is.Zero);
        }

        [Test]
        public void ABindingSurvivesAHotReloadThatIntroducesItsNode()
        {
            StartEngine();
            var host = NewHost();
            var view = _binder.Bind(host, "Main.Shop.LimitedOffer", "ShopScreen");

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageHidden),
                "binding a path with no rule yet is legal and simply reads as hidden");

            _bridge.ReloadRules(System.IO.File.ReadAllText(
                System.IO.Path.Combine(Application.dataPath, "Lua/patches/rules_patch_example.lua")));

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageDot),
                "the example patch lit up a badge the build never knew about");
            Assert.That(_binder.Count, Is.EqualTo(1));
        }

        #endregion

        private static GObject Child(GComponent host, string name)
        {
            var badge = (GComponent)host.GetChild(RedDotView.BadgeChildName);
            return badge.GetChild(name);
        }
    }
}
