using System.Collections.Generic;
using FairyGUI;
using NUnit.Framework;
using RedDot.Demo;
using RedDot.Events;
using UnityEngine;

namespace RedDot.Tests
{
    /// <summary>
    /// Tests for the FairyGUI view layer and the binding lifetime.
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
        private const string TypeMail = "Mail";
        private const string TypeMailItem = "MailItem";
        private const string TypeQuestItem = "QuestItem";
        private const string TypeLimitedOffer = "LimitedOffer";

        private readonly List<GObject> _created = new List<GObject>();

        private EventBus _bus;
        private FakeClock _clock;
        private FakeMailService _mail;
        private FakeQuestService _quests;
        private RedDotBridge _bridge;
        private RedDotBinder _binder;

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
            view.Apply(visible, count);
        }

        /// <summary>
        /// Whether a display gear is currently showing this child. A gear does not touch
        /// the `visible` flag — it takes the object out of the render list — so this is
        /// what "the controller hid it" actually looks like from outside.
        /// </summary>
        private static bool ShownByGear(GComponent host, string childName)
        {
            return Child(host, childName).displayObject.parent != null;
        }

        private static GObject Child(GComponent host, string name)
        {
            var badge = (GComponent)host.GetChild(RedDotView.BadgeChildName);
            return badge.GetChild(name);
        }

        private void StartEngine()
        {
            _bus = new EventBus();
            _clock = new FakeClock();
            _mail = new FakeMailService(_bus);
            _quests = new FakeQuestService(_bus);

            _bridge = new RedDotBridge(new RedDotBridgeOptions
            {
                Bus = _bus,
                Context = new RedDotContext(_mail, _quests, new FakeShopService(_bus, _clock), _clock),
                SeenPersistence = new InMemorySeenPersistence(),
                Log = message => TestContext.WriteLine("[lua] " + message),
            });

            _binder = new RedDotBinder(_bridge);
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
        public void ADotWithNoNumberIsWhatTheEngineAsksFor()
        {
            var host = NewHost();
            var view = new RedDotView(host);

            // The engine reports a boolean, so this is the only shape it ever produces.
            view.SetRedDot("Mail", true);

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageDot));
            Assert.That(view.Key, Is.EqualTo("Mail"));
            Assert.That(ShownByGear(host, "dot"), Is.True);
            Assert.That(ShownByGear(host, RedDotView.CountChildName), Is.False);
        }

        [Test]
        public void ASingleItemStaysAPlainDot()
        {
            var host = NewHost();
            var view = new RedDotView(host);

            Apply(view, true, 1);

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageDot),
                "a lone '1' next to an icon is decoration, not information");
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

            Apply(view, false, 0);
            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageHidden));

            Apply(view, true, 12);
            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageCount));
        }

        [Test]
        public void TheBadgeNeverEatsTheClickMeantForTheButton()
        {
            var host = NewHost();
            var _ = new RedDotView(host);

            Assert.That(host.GetChild(RedDotView.BadgeChildName).touchable, Is.False);
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
        }

        [Test]
        public void ADisposedHostIsNotTouched()
        {
            var host = NewHost();
            var view = new RedDotView(host);
            host.Dispose();

            Assert.DoesNotThrow(() => view.SetRedDot("Mail", true));
        }

        #endregion

        #region Binding lifetime

        [Test]
        public void BindingPushesTheCurrentValueStraightIntoTheBadge()
        {
            StartEngine();
            _mail.Receive();
            _bridge.Flush();

            var host = NewHost();
            var view = _binder.Bind(host, TypeMailItem, 1);

            Assert.That(_binder.KeyOf(host), Is.EqualTo("MailItem|1"));
            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageDot),
                "a row that appears late is still correct on its first frame");
        }

        [Test]
        public void ABoundBadgeFollowsTheEngine()
        {
            StartEngine();
            var mailId = _mail.Receive();
            _bridge.Flush();

            var host = NewHost();
            var view = _binder.Bind(host, TypeMailItem, mailId);
            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageDot));

            _mail.Open(mailId);
            _bridge.Flush();

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageHidden));
        }

        /// <summary>
        /// The regression this class exists for: a row that comes back out of a pool and
        /// is bound to a different mail must not still be listening to the old one — and
        /// the keyed dot it was holding open must go with it.
        /// </summary>
        [Test]
        public void RebindingAPooledRowDropsTheOldBindingAndItsDot()
        {
            StartEngine();
            var first = _mail.Receive();
            var second = _mail.Receive();
            _bridge.Flush();
            _mail.Open(second);
            _bridge.Flush();

            var host = NewHost();
            var firstView = _binder.Bind(host, TypeMailItem, first);
            Assert.That(firstView.Visible, Is.True);
            Assert.That(_bridge.Counts().Keyed, Is.EqualTo(1));

            // The list recycles the row for the mail below it.
            var secondView = _binder.Bind(host, TypeMailItem, second);

            Assert.That(_binder.Count, Is.EqualTo(1), "one component, one binding");
            Assert.That(_binder.KeyOf(host), Is.EqualTo("MailItem|" + second));
            Assert.That(_bridge.SubscriberCount("MailItem|" + first), Is.Zero);
            Assert.That(_bridge.Counts().Keyed, Is.EqualTo(1),
                "the dot the old row was holding open was destroyed with the subscription");
            Assert.That(secondView.Visible, Is.False, "the second mail is already read");

            // The old mail keeps changing; nothing must arrive.
            _mail.Open(first);
            _bridge.Flush();

            Assert.That(firstView.Visible, Is.True, "the stale view never heard that it was read");
        }

        [Test]
        public void RebindingToTheSameDotDoesNotStackSubscriptions()
        {
            StartEngine();
            var host = NewHost();

            _binder.Bind(host, TypeMailItem, 1);
            _binder.Bind(host, TypeMailItem, 1);
            _binder.Bind(host, TypeMailItem, 1);

            Assert.That(_bridge.SubscriberCount("MailItem|1"), Is.EqualTo(1));
            Assert.That(_binder.Count, Is.EqualTo(1));
        }

        [Test]
        public void UnbindReleasesTheRowAndIsSafeToRepeat()
        {
            StartEngine();
            var mailId = _mail.Receive();
            _bridge.Flush();

            var host = NewHost();
            var view = _binder.Bind(host, TypeMailItem, mailId);

            Assert.That(_binder.Unbind(host), Is.True);
            Assert.That(_binder.Unbind(host), Is.False, "unbinding twice is a no-op, not an error");
            Assert.That(_bridge.Counts().Keyed, Is.Zero);

            _mail.Open(mailId);
            _bridge.Flush();
            Assert.That(view.Visible, Is.True, "the released view heard nothing more");
        }

        [Test]
        public void UnbindAllReleasesEveryBadgeOnAScreen()
        {
            StartEngine();
            var inboxHost = NewHost();
            var questHost = NewHost();
            var otherHost = NewHost();

            _binder.BindOwned(inboxHost, "MailScreen", TypeMailItem, 1);
            _binder.BindOwned(questHost, "MailScreen", TypeQuestItem, 1, 1);
            _binder.BindOwned(otherHost, "MainScreen", TypeMail);
            Assert.That(_binder.Count, Is.EqualTo(3));
            Assert.That(_bridge.Counts().Keyed, Is.EqualTo(2));

            Assert.That(_binder.UnbindAll("MailScreen"), Is.EqualTo(2));

            Assert.That(_binder.Count, Is.EqualTo(1), "only the other screen survives");
            Assert.That(_bridge.Counts().Keyed, Is.Zero, "and its keyed dots are gone");
            Assert.That(_binder.KeyOf(otherHost), Is.EqualTo(TypeMail));
        }

        [Test]
        public void ADisposedComponentReleasesItselfOnTheNextUpdate()
        {
            StartEngine();
            var mailId = _mail.Receive();
            _bridge.Flush();

            var host = NewHost();
            _binder.Bind(host, TypeMailItem, mailId);

            // Screen teardown, without anybody telling the binder about it.
            host.Dispose();

            _mail.Open(mailId);
            _bridge.Flush();

            Assert.That(_binder.Count, Is.Zero, "the update found a dead component and let go");
            Assert.That(_bridge.Counts().Keyed, Is.Zero);
        }

        [Test]
        public void DisposedComponentsCanBeSweptWithoutWaitingForAnUpdate()
        {
            StartEngine();
            var host = NewHost();
            _binder.Bind(host, TypeMailItem, 1);
            host.Dispose();

            Assert.That(_binder.ReapDisposed(), Is.EqualTo(1));
            Assert.That(_bridge.Counts().Keyed, Is.Zero);
        }

        [Test]
        public void ABindingSurvivesAHotReloadThatIntroducesItsType()
        {
            StartEngine();
            var host = NewHost();
            var view = _binder.Bind(host, TypeLimitedOffer);

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageHidden),
                "binding a type with no rule yet is legal and simply reads as off");

            _bridge.Context.SetCounter("shop.limitedOffer", 1);
            _bridge.ReloadRules(System.IO.File.ReadAllText(
                System.IO.Path.Combine(Application.dataPath, "Lua/patches/rules_patch_example.lua")));

            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageDot),
                "the example patch lit up a badge the build never knew about");
            Assert.That(_binder.Count, Is.EqualTo(1));
        }

        #endregion

        #region The active kill switch

        [Test]
        public void AnInactiveBadgeStaysHiddenEvenWhenTheRuleSaysYes()
        {
            StartEngine();
            var mailId = _mail.Receive();
            _bridge.Flush();

            var host = NewHost();
            var view = _binder.Bind(host, TypeMailItem, mailId);
            Assert.That(view.Visible, Is.True);

            Assert.That(_binder.SetRedDotActive(host, false), Is.True);

            Assert.That(view.Visible, Is.False, "the screen vetoed it");
            Assert.That(view.CurrentPage, Is.EqualTo(RedDotView.PageHidden));
            Assert.That(_bridge.GetValue(TypeMailItem, mailId), Is.True,
                "and the rule still says what it always said -- the veto is a view concern");
        }

        [Test]
        public void ReactivatingRestoresWhateverTheRuleSaysNow()
        {
            StartEngine();
            var mailId = _mail.Receive();
            _bridge.Flush();

            var host = NewHost();
            var view = _binder.Bind(host, TypeMailItem, mailId);
            _binder.SetRedDotActive(host, false);
            Assert.That(view.Visible, Is.False);

            _binder.SetRedDotActive(host, true);
            Assert.That(view.Visible, Is.True);

            // And it tracks changes made while it was switched off.
            _binder.SetRedDotActive(host, false);
            _mail.Open(mailId);
            _bridge.Flush();
            _binder.SetRedDotActive(host, true);

            Assert.That(view.Visible, Is.False, "it came back to the current answer, not the old one");
        }

        [Test]
        public void AnInactiveBindingStillTracksTheRuleUnderneath()
        {
            StartEngine();
            var host = NewHost();
            _binder.Bind(host, TypeMailItem, 1);
            _binder.SetRedDotActive(host, false);

            var mailId = _mail.Receive();
            Assert.That(mailId, Is.EqualTo(1));
            _bridge.Flush();

            Assert.That(_binder.ViewOf(host).Visible, Is.False, "still vetoed");
            Assert.That(_bridge.GetValue(TypeMailItem, 1), Is.True, "but the dot is on underneath");

            _binder.SetRedDotActive(host, true);
            Assert.That(_binder.ViewOf(host).Visible, Is.True);
        }

        [Test]
        public void SettingActiveOnSomethingUnboundIsANoOp()
        {
            StartEngine();
            var host = NewHost();

            Assert.That(_binder.SetRedDotActive(host, false), Is.False);
            Assert.That(_binder.IsRedDotActive(host), Is.False);
        }

        #endregion
    }
}
