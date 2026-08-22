using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RedDot.Demo;
using RedDot.Events;

namespace RedDot.Tests
{
    /// <summary>
    /// Drives the real Lua engine through the real bridge.
    /// </summary>
    /// <remarks>
    /// Nothing here is a mock of the system under test: every assertion goes through
    /// xLua into <c>Assets/Lua/reddot/*.lua</c> and back. The only doubles are the fake
    /// game managers and the in-memory seen store, which stand in for data the red dot
    /// system reads but does not own.
    /// </remarks>
    [TestFixture]
    public sealed class RedDotCoreTests
    {
        private const string Main = "Main";
        private const string Mail = "Main.Mail";
        private const string Inbox = "Main.Mail.Inbox";
        private const string SystemNotices = "Main.Mail.System";
        private const string Quests = "Main.Quests";
        private const string Daily = "Main.Quests.Daily";
        private const string Achievements = "Main.Quests.Achievements";
        private const string Shop = "Main.Shop";
        private const string DailyDeals = "Main.Shop.DailyDeals";
        private const string Bundles = "Main.Shop.Bundles";

        private EventBus _bus;
        private FakeMailService _mail;
        private FakeQuestService _quests;
        private FakeShopService _shop;
        private RedDotContext _context;
        private InMemorySeenPersistence _seen;
        private RedDotBridge _bridge;
        private List<(string Path, RedDotState State)> _notifications;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _mail = new FakeMailService(_bus);
            _quests = new FakeQuestService(_bus);
            _shop = new FakeShopService(_bus);
            _context = new RedDotContext(_mail, _quests, _shop);
            _seen = new InMemorySeenPersistence();
            _bridge = CreateBridge();
        }

        [TearDown]
        public void TearDown()
        {
            _bridge?.Dispose();
            _bridge = null;
        }

        private RedDotBridge CreateBridge()
        {
            var options = new RedDotBridgeOptions
            {
                Bus = _bus,
                Context = _context,
                SeenPersistence = _seen,
                Log = message => TestContext.WriteLine("[lua] " + message),
            };

            var bridge = new RedDotBridge(options);
            _notifications = new List<(string, RedDotState)>();
            bridge.Changed += (path, state) => _notifications.Add((path, state));
            bridge.ResetStats();
            return bridge;
        }

        #region Tree and aggregation

        [Test]
        public void EveryNodeStartsHiddenWhenTheGameHasNothingPending()
        {
            foreach (var pair in _bridge.ReadAllStates())
            {
                Assert.That(pair.Value.Visible, Is.False, pair.Key + " should start hidden");
                Assert.That(pair.Value.Count, Is.Zero, pair.Key + " should start at zero");
            }
        }

        [Test]
        public void DeclaredTreeContainsEveryShippedNode()
        {
            var paths = _bridge.ReadAllStates().Keys.OrderBy(p => p, StringComparer.Ordinal).ToArray();

            Assert.That(paths, Is.EqualTo(new[]
            {
                Main, Mail, Inbox, SystemNotices, Quests, Achievements, Daily, Shop, DailyDeals,
            }.OrderBy(p => p, StringComparer.Ordinal).ToArray()));
        }

        [Test]
        public void SumPolicyAddsTheCountsOfVisibleChildren()
        {
            _mail.Receive(3);
            _mail.PostSystemNotice();
            _bridge.Flush();

            Assert.That(_bridge.GetState(Inbox), Is.EqualTo(new RedDotState(true, 3)));
            Assert.That(_bridge.GetState(SystemNotices), Is.EqualTo(new RedDotState(true, 1)));
            Assert.That(_bridge.GetState(Mail), Is.EqualTo(new RedDotState(true, 4)));
        }

        [Test]
        public void MaxPolicyShowsTheLargestChildRatherThanTheTotal()
        {
            _quests.CompleteDaily(2);
            _quests.UnlockAchievement(5);
            _bridge.Flush();

            Assert.That(_bridge.GetState(Daily), Is.EqualTo(new RedDotState(true, 2)));
            Assert.That(_bridge.GetState(Achievements), Is.EqualTo(new RedDotState(true, 5)));
            Assert.That(_bridge.GetState(Quests), Is.EqualTo(new RedDotState(true, 5)),
                "the quests tab uses the max policy, so it shows the most urgent single number");
        }

        [Test]
        public void AnyPolicyShowsADotWithoutANumber()
        {
            _shop.RefreshDailyDeals(4);
            _bridge.Flush();

            Assert.That(_bridge.GetState(DailyDeals), Is.EqualTo(new RedDotState(true, 4)));
            Assert.That(_bridge.GetState(Shop), Is.EqualTo(new RedDotState(true, 0)),
                "the shop tab is a plain dot: visible, but deliberately without a count");
        }

        [Test]
        public void TheRootAggregatesAcrossUnrelatedBranches()
        {
            _mail.Receive(2);
            _quests.CompleteDaily(3);
            _shop.RefreshDailyDeals(1);
            _bridge.Flush();

            // Main is a sum node: Mail(2) + Quests(max = 3) + Shop(any = 0).
            Assert.That(_bridge.GetState(Main), Is.EqualTo(new RedDotState(true, 5)));
        }

        [Test]
        public void AParentGoesDarkOnlyWhenEveryChildDoes()
        {
            _mail.Receive(1);
            _mail.PostSystemNotice();
            _bridge.Flush();
            Assert.That(_bridge.IsVisible(Mail), Is.True);

            _mail.ReadAll();
            _bridge.Flush();
            Assert.That(_bridge.IsVisible(Inbox), Is.False);
            Assert.That(_bridge.IsVisible(Mail), Is.True, "the system notice is still unseen");

            _bridge.MarkSeen(SystemNotices);
            _bridge.Flush();
            Assert.That(_bridge.IsVisible(Mail), Is.False);
            Assert.That(_bridge.IsVisible(Main), Is.False);
        }

        #endregion

        #region Rule modes

        [Test]
        public void PersistentBadgesIgnoreMarkSeen()
        {
            _mail.Receive(3);
            _bridge.Flush();

            var marked = _bridge.MarkSeen(Inbox);
            _bridge.Flush();

            Assert.That(marked, Is.Zero, "a persistent node has no seen state to flip");
            Assert.That(_bridge.GetState(Inbox), Is.EqualTo(new RedDotState(true, 3)),
                "unread mail is still unread after looking at the tab");
        }

        [Test]
        public void PersistentBadgesClearWhenTheConditionClears()
        {
            _mail.Receive(3);
            _bridge.Flush();
            Assert.That(_bridge.IsVisible(Inbox), Is.True);

            _mail.ReadAll();
            _bridge.Flush();
            Assert.That(_bridge.IsVisible(Inbox), Is.False);
        }

        [Test]
        public void TransientBadgesHideOnceSeen()
        {
            _quests.UnlockAchievement(2);
            _bridge.Flush();
            Assert.That(_bridge.GetState(Achievements), Is.EqualTo(new RedDotState(true, 2)));

            _bridge.MarkSeen(Achievements);
            _bridge.Flush();

            Assert.That(_bridge.IsVisible(Achievements), Is.False);
            Assert.That(_quests.UnclaimedAchievements, Is.EqualTo(2),
                "the badge is gone but the underlying data is untouched");
        }

        [Test]
        public void ATriggerMakesASeenTransientBadgeUnseenAgain()
        {
            _quests.UnlockAchievement(1);
            _bridge.Flush();
            _bridge.MarkSeen(Achievements);
            _bridge.Flush();
            Assert.That(_bridge.IsVisible(Achievements), Is.False);

            _quests.UnlockAchievement(1); // raises achievement.unlocked again
            _bridge.Flush();

            Assert.That(_bridge.GetState(Achievements), Is.EqualTo(new RedDotState(true, 2)),
                "new content re-arms a TransientUntilSeen badge");
        }

        [Test]
        public void MarkSeenOnAParentClearsTheWholeSubtree()
        {
            _mail.Receive(2);          // persistent, must survive
            _mail.PostSystemNotice();  // transient, must clear
            _bridge.Flush();

            var marked = _bridge.MarkSeen(Mail);
            _bridge.Flush();

            Assert.That(marked, Is.EqualTo(1), "only the transient child had seen state to flip");
            Assert.That(_bridge.IsVisible(SystemNotices), Is.False);
            Assert.That(_bridge.GetState(Inbox), Is.EqualTo(new RedDotState(true, 2)));
        }

        [Test]
        public void MarkSeenOnAnUnknownPathIsANoOp()
        {
            Assert.That(_bridge.MarkSeen("Main.Nope.Missing"), Is.Zero);
            Assert.That(_bridge.Flush(), Is.Zero);
        }

        [Test]
        public void SeenStateSurvivesARestartThroughThePersistenceCallback()
        {
            _shop.RefreshDailyDeals(2);
            _bridge.Flush();
            _bridge.MarkSeen(DailyDeals);
            _bridge.Flush();

            Assert.That(_seen.Blob, Is.EqualTo(DailyDeals), "Lua wrote the seen set through the C# callback");
            Assert.That(_seen.SaveCount, Is.GreaterThan(0));

            // Restart: a brand new Lua environment reading the same persisted blob.
            _bridge.Dispose();
            _bridge = CreateBridge();

            Assert.That(_seen.LoadCount, Is.GreaterThan(0));
            Assert.That(_bridge.IsVisible(DailyDeals), Is.False,
                "a badge the player already dismissed must not come back after a restart");
        }

        #endregion

        #region Dirty batching

        [Test]
        public void ManyEventsCollapseIntoOneEvaluationPerNodePerFlush()
        {
            for (var i = 0; i < 5; i++)
            {
                _mail.Receive();
                _mail.Read();
            }

            var stats = _bridge.Stats();
            Assert.That(stats.Dispatches, Is.EqualTo(10), "ten events reached Lua");
            Assert.That(stats.LeafEvaluations, Is.Zero, "but nothing was evaluated yet");

            _bridge.Flush();

            Assert.That(_bridge.Stats().LeafEvaluations, Is.EqualTo(1),
                "ten events on one node still cost exactly one rule evaluation");
            Assert.That(_bridge.Stats().Flushes, Is.EqualTo(1));
        }

        [Test]
        public void AFlushWithoutEventsDoesNoWorkAtAll()
        {
            Assert.That(_bridge.Flush(), Is.Zero);
            Assert.That(_bridge.Flush(), Is.Zero);

            var stats = _bridge.Stats();
            Assert.That(stats.Flushes, Is.Zero, "an idle flush is not even counted as a flush");
            Assert.That(stats.LeafEvaluations, Is.Zero, "the system never polls");
            Assert.That(stats.Aggregations, Is.Zero);
            Assert.That(stats.Notifications, Is.Zero);
        }

        [Test]
        public void OnlyNodesWhoseStateChangedAreNotified()
        {
            _mail.Receive(2);
            _bridge.Flush();
            Assert.That(_notifications.Select(n => n.Path),
                Is.EquivalentTo(new[] { Inbox, Mail, Main }));

            _notifications.Clear();
            _bridge.ResetStats();

            // An event fires, the rule runs, and the answer is the same as before.
            _bridge.RaiseEvent("mail.received");
            var changed = _bridge.Flush();

            Assert.That(changed, Is.Zero);
            Assert.That(_notifications, Is.Empty, "an unchanged node must not wake the view layer");
            Assert.That(_bridge.Stats().LeafEvaluations, Is.EqualTo(1), "the rule did run");
            Assert.That(_bridge.Stats().Notifications, Is.Zero);
        }

        [Test]
        public void AggregatesStopBubblingWhereNothingChanged()
        {
            _mail.Receive(1);
            _quests.CompleteDaily(1);
            _bridge.Flush();

            _notifications.Clear();
            _bridge.ResetStats();

            // Daily goes 1 -> 2. Quests uses max so it changes too, and Main sums so it
            // changes as well; Mail is on another branch and must not be touched.
            _quests.CompleteDaily(1);
            _bridge.Flush();

            Assert.That(_notifications.Select(n => n.Path), Is.EquivalentTo(new[] { Daily, Quests, Main }));
            Assert.That(_notifications.Select(n => n.Path).ToList().IndexOf(Daily),
                Is.LessThan(_notifications.Select(n => n.Path).ToList().IndexOf(Main)),
                "children are notified before their ancestors");
        }

        [Test]
        public void EventsNobodySubscribedToNeverReachLua()
        {
            _bridge.RaiseEvent("guild.applicationReceived");
            _bridge.RaiseEvent("some.event.that.does.not.exist");

            Assert.That(_bus.PublishCount, Is.EqualTo(2));
            Assert.That(_bridge.Stats().Dispatches, Is.Zero,
                "the bridge only forwards events the current rules named as triggers");
            Assert.That(_bridge.Flush(), Is.Zero);
        }

        [Test]
        public void TheBridgeSubscribesToExactlyTheTriggersTheRulesName()
        {
            var expected = new[]
            {
                "achievement.unlocked",
                "day.rollover",
                "mail.deleted",
                "mail.read",
                "mail.received",
                "mail.systemNoticePosted",
                "quest.claimed",
                "quest.progress",
                "shop.dailyDealsRefreshed",
            };

            Assert.That(_bridge.SubscribedEvents(), Is.EqualTo(expected));
            Assert.That(_bus.SubscribedEvents(), Is.EqualTo(expected));
        }

        #endregion

        #region Bindings

        private sealed class RecordingHandle : IRedDotHandle
        {
            public readonly List<(string Path, bool Visible, int Count)> Calls =
                new List<(string, bool, int)>();

            public void SetRedDot(string path, bool visible, int count)
            {
                Calls.Add((path, visible, count));
            }

            public (string Path, bool Visible, int Count) Last => Calls[Calls.Count - 1];
        }

        [Test]
        public void BindingPushesTheCurrentStateImmediately()
        {
            _mail.Receive(4);
            _bridge.Flush();

            var handle = new RecordingHandle();
            _bridge.Bind(Inbox, handle);

            Assert.That(handle.Calls.Count, Is.EqualTo(1));
            Assert.That(handle.Last, Is.EqualTo((Inbox, true, 4)),
                "a view that binds late must still be correct on its first frame");
        }

        [Test]
        public void BoundHandlesFollowSubsequentChanges()
        {
            var handle = new RecordingHandle();
            _bridge.Bind(Inbox, handle);

            _mail.Receive(1);
            _bridge.Flush();
            _mail.Receive(1);
            _bridge.Flush();

            Assert.That(handle.Calls.Select(c => c.Count), Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void UnbindStopsNotificationsAndIsSafeToRepeat()
        {
            var handle = new RecordingHandle();
            _bridge.Bind(Inbox, handle);
            Assert.That(_bridge.BindingCount(Inbox), Is.EqualTo(1));

            Assert.That(_bridge.Unbind(Inbox, handle), Is.True);
            Assert.That(_bridge.Unbind(Inbox, handle), Is.False, "unbinding twice is a no-op, not an error");
            Assert.That(_bridge.Unbind("Main.Nope", handle), Is.False);
            Assert.That(_bridge.BindingCount(Inbox), Is.Zero);

            handle.Calls.Clear();
            _mail.Receive(1);
            _bridge.Flush();

            Assert.That(handle.Calls, Is.Empty);
        }

        [Test]
        public void UnbindAllReleasesEverythingAnOwnerRegistered()
        {
            var inboxHandle = new RecordingHandle();
            var questHandle = new RecordingHandle();
            var otherHandle = new RecordingHandle();

            _bridge.Bind(Inbox, inboxHandle, "MainScreen");
            _bridge.Bind(Daily, questHandle, "MainScreen");
            _bridge.Bind(DailyDeals, otherHandle, "ShopScreen");
            Assert.That(_bridge.BindingCount(), Is.EqualTo(3));

            Assert.That(_bridge.UnbindAll("MainScreen"), Is.EqualTo(2));
            Assert.That(_bridge.UnbindAll("MainScreen"), Is.Zero);
            Assert.That(_bridge.UnbindAll("NeverBoundAnything"), Is.Zero);
            Assert.That(_bridge.BindingCount(), Is.EqualTo(1));

            inboxHandle.Calls.Clear();
            questHandle.Calls.Clear();
            otherHandle.Calls.Clear();

            _mail.Receive(1);
            _quests.CompleteDaily(1);
            _shop.RefreshDailyDeals(1);
            _bridge.Flush();

            Assert.That(inboxHandle.Calls, Is.Empty);
            Assert.That(questHandle.Calls, Is.Empty);
            Assert.That(otherHandle.Calls.Count, Is.EqualTo(1), "the surviving owner still updates");
        }

        [Test]
        public void SeveralHandlesCanShareOnePath()
        {
            var first = new RecordingHandle();
            var second = new RecordingHandle();
            _bridge.Bind(Main, first, "HeaderBar");
            _bridge.Bind(Main, second, "PauseMenu");

            _mail.Receive(1);
            _bridge.Flush();

            Assert.That(first.Last, Is.EqualTo((Main, true, 1)));
            Assert.That(second.Last, Is.EqualTo((Main, true, 1)));
        }

        #endregion

        #region Hot update

        /// <summary>
        /// A live-ops patch: adds a badge for a shop section that did not exist when the
        /// client shipped, driven entirely by the generic counter on the context.
        /// </summary>
        private const string PatchAddBundles = @"
local types = require('reddot.types')
local base  = require('reddot.rules')

local rules = {}
for path, rule in pairs(base) do rules[path] = rule end

rules['Main.Shop.Bundles'] = {
    mode     = types.MODE_TRANSIENT_UNTIL_SEEN,
    triggers = { 'shop.bundlesRefreshed' },
    evaluate = function(ctx) return ctx:Counter('shop.bundles') end,
}

return { nodes = { { path = 'Main.Shop.Bundles' } }, rules = rules }
";

        /// <summary>A patch that retires the inbox badge, so its three triggers go unused.</summary>
        private const string PatchRetireInbox = @"
local base = require('reddot.rules')

local rules = {}
for path, rule in pairs(base) do rules[path] = rule end
rules['Main.Mail.Inbox'] = nil

return rules
";

        /// <summary>A patch that is wrong: rules belong on leaves, never on aggregates.</summary>
        private const string PatchRuleOnInteriorNode = @"
local types = require('reddot.types')
return {
    ['Main.Mail'] = {
        mode     = types.MODE_PERSISTENT,
        triggers = { 'mail.received' },
        evaluate = function() return 1 end,
    },
}
";

        [Test]
        public void ReloadAddsABrandNewBadgeWithNoCSharpChange()
        {
            Assert.That(_bridge.ReadAllStates().ContainsKey(Bundles), Is.False);

            _context.SetCounter("shop.bundles", 3);
            _bridge.ReloadRules(PatchAddBundles);

            Assert.That(_bridge.GetState(Bundles), Is.EqualTo(new RedDotState(true, 3)));
            Assert.That(_bridge.IsVisible(Shop), Is.True, "the new leaf bubbles into its parent");
            Assert.That(_bridge.SubscribedEvents(), Contains.Item("shop.bundlesRefreshed"));
            Assert.That(_bus.HasSubscribers("shop.bundlesRefreshed"), Is.True);

            // And the new node behaves like any other from then on.
            _bridge.MarkSeen(Bundles);
            _bridge.Flush();
            Assert.That(_bridge.IsVisible(Bundles), Is.False);
        }

        [Test]
        public void ABindingMadeBeforeAReloadPicksUpTheNodeTheReloadIntroduces()
        {
            var handle = new RecordingHandle();
            _bridge.Bind(Bundles, handle, "ShopScreen");

            Assert.That(handle.Last, Is.EqualTo((Bundles, false, 0)),
                "binding a path that does not exist yet is legal and reads as hidden");

            _context.SetCounter("shop.bundles", 2);
            _bridge.ReloadRules(PatchAddBundles);

            Assert.That(handle.Last, Is.EqualTo((Bundles, true, 2)),
                "bindings hold paths, so a node the patch invents lands in an existing view");
        }

        [Test]
        public void ReloadUnsubscribesTheTriggersOfARetiredRule()
        {
            _mail.Receive(2);
            _bridge.Flush();
            Assert.That(_bridge.IsVisible(Inbox), Is.True);
            Assert.That(_bus.HasSubscribers("mail.received"), Is.True);

            _bridge.ReloadRules(PatchRetireInbox);

            Assert.That(_bridge.SubscribedEvents(), Has.No.Member("mail.received"));
            Assert.That(_bus.HasSubscribers("mail.received"), Is.False,
                "the C# bus really did lose the subscription, not just the Lua index");
            Assert.That(_bus.HasSubscribers("mail.systemNoticePosted"), Is.True,
                "triggers that survived the reload are never churned");
            Assert.That(_bridge.IsVisible(Inbox), Is.False, "a node with no rule has nothing to say");

            _bridge.ResetStats();
            _mail.Receive(5);
            Assert.That(_bridge.Stats().Dispatches, Is.Zero, "the retired trigger no longer crosses the bridge");
        }

        [Test]
        public void BindingsSurviveAReloadThatKeepsTheirNode()
        {
            var handle = new RecordingHandle();
            _bridge.Bind(Inbox, handle, "MailScreen");
            _mail.Receive(1);
            _bridge.Flush();
            Assert.That(handle.Last, Is.EqualTo((Inbox, true, 1)));

            _bridge.ReloadRules(PatchAddBundles);
            Assert.That(_bridge.BindingCount(Inbox), Is.EqualTo(1));

            handle.Calls.Clear();
            _mail.Receive(1);
            _bridge.Flush();

            Assert.That(handle.Last, Is.EqualTo((Inbox, true, 2)),
                "the binding kept working across the rule swap");
        }

        [Test]
        public void AReloadOnlyReportsBadgesThatActuallyMoved()
        {
            _mail.Receive(1);
            _bridge.Flush();
            _notifications.Clear();

            // The patch only adds a node, and the new node evaluates to zero, so nothing
            // visible changes anywhere in the tree.
            var changed = _bridge.ReloadRules(PatchAddBundles);

            Assert.That(changed, Is.Zero);
            Assert.That(_notifications, Is.Empty);
        }

        [Test]
        public void ReloadingFromAModuleReReadsItThroughTheLoader()
        {
            _mail.Receive(1);
            _bridge.Flush();

            // No patch root registered, so this re-reads the shipped file and changes nothing.
            Assert.That(_bridge.ReloadRulesFromModule(), Is.Zero);
            Assert.That(_bridge.GetState(Inbox), Is.EqualTo(new RedDotState(true, 1)));
            Assert.That(_bridge.Loader.TryGetSource("reddot.rules", out var source), Is.True);
            Assert.That(source, Does.EndWith("reddot/rules.lua"));
        }

        [Test]
        public void APatchThatPutsARuleOnAnAggregateIsRejected()
        {
            Assert.That(() => _bridge.ReloadRules(PatchRuleOnInteriorNode),
                Throws.Exception.With.Message.Contains("not a leaf"));

            // The rejected patch must not have half-applied.
            _mail.Receive(1);
            _bridge.Flush();
            Assert.That(_bridge.GetState(Inbox), Is.EqualTo(new RedDotState(true, 1)));
        }

        [Test]
        public void ABrokenRuleIsContainedAndTheRestOfTheTreeKeepsWorking()
        {
            const string patch = @"
local types = require('reddot.types')
local base  = require('reddot.rules')

local rules = {}
for path, rule in pairs(base) do rules[path] = rule end

rules['Main.Quests.Daily'] = {
    mode     = types.MODE_PERSISTENT,
    triggers = { 'quest.progress' },
    evaluate = function() error('this rule is broken on purpose') end,
}

return rules
";
            _bridge.ReloadRules(patch);
            _mail.Receive(2);
            _quests.CompleteDaily(1);
            _bridge.Flush();

            Assert.That(_bridge.IsVisible(Daily), Is.False, "the broken rule reads as hidden");
            Assert.That(_bridge.GetState(Inbox), Is.EqualTo(new RedDotState(true, 2)),
                "one bad rule must not take the rest of the UI down");
        }

        #endregion

        #region Diagnostics

        [Test]
        public void DebugDumpDescribesTheTreeTheSeenSetAndTheStats()
        {
            _mail.Receive(2);
            _shop.RefreshDailyDeals(1);
            _bridge.Flush();
            _bridge.MarkSeen(DailyDeals);
            _bridge.Flush();

            var dump = _bridge.DebugDump();
            TestContext.WriteLine(dump);

            Assert.That(dump, Does.Contain("Main"));
            Assert.That(dump, Does.Contain("Inbox"));
            Assert.That(dump, Does.Contain("rule Persistent"));
            Assert.That(dump, Does.Contain("aggregate any"));
            Assert.That(dump, Does.Contain("seen: " + DailyDeals));
            Assert.That(dump, Does.Contain("flushes="));
        }

        #endregion

        #region Fuzz

        /// <summary>
        /// Ten thousand random events, seen marks and rule reloads, checking after every
        /// flush that the tree is internally consistent and that nothing was notified
        /// without changing.
        /// </summary>
        /// <remarks>
        /// The seed is fixed, so a failure is reproducible; the point is coverage of
        /// orderings a hand-written test would never think to try.
        /// </remarks>
        [Test]
        public void FuzzingKeepsTheTreeConsistentAndNotificationsHonest()
        {
            const int eventCount = 10000;
            var random = new Random(20260822);

            var eventNames = new[]
            {
                "mail.received", "mail.read", "mail.deleted", "mail.systemNoticePosted",
                "quest.progress", "quest.claimed", "day.rollover", "achievement.unlocked",
                "shop.dailyDealsRefreshed", "shop.bundlesRefreshed",
                "guild.nobodyListensToThis",
            };

            var flushes = 0;
            var reloads = 0;
            var bundlesPatchApplied = false;

            var before = _bridge.ReadAllStates();
            _notifications.Clear();

            for (var i = 1; i <= eventCount; i++)
            {
                MutateGameState(random);
                _bridge.RaiseEvent(eventNames[random.Next(eventNames.Length)]);

                if (random.Next(20) == 0)
                {
                    _bridge.MarkSeen(RandomPath(random, before.Keys));
                }

                if (random.Next(10) != 0 && i != eventCount)
                {
                    continue;
                }

                _bridge.Flush();
                flushes++;

                var after = _bridge.ReadAllStates();
                AssertAggregatesAreConsistent(after, i);
                AssertNotificationsMatch(before, after, i);

                before = after;
                _notifications.Clear();

                if (random.Next(50) != 0)
                {
                    continue;
                }

                // A hot update in the middle of the storm, in both directions.
                if (bundlesPatchApplied)
                {
                    _bridge.ReloadRulesFromModule();
                    bundlesPatchApplied = false;
                }
                else
                {
                    _context.SetCounter("shop.bundles", random.Next(4));
                    _bridge.ReloadRules(PatchAddBundles);
                    bundlesPatchApplied = true;
                }

                reloads++;

                var afterReload = _bridge.ReadAllStates();
                AssertAggregatesAreConsistent(afterReload, i);
                AssertNotificationsMatch(before, afterReload, i);

                before = afterReload;
                _notifications.Clear();
            }

            TestContext.WriteLine($"{eventCount} events, {flushes} flushes, {reloads} reloads");
            TestContext.WriteLine(_bridge.DebugDump());

            Assert.That(flushes, Is.GreaterThan(100), "the fuzz should have flushed many times");
            Assert.That(reloads, Is.GreaterThan(5), "the fuzz should have hot-reloaded several times");
            Assert.That(_bridge.Stats().LeafEvaluations, Is.LessThan(eventCount),
                "batching means far fewer evaluations than events");
        }

        private void MutateGameState(Random random)
        {
            switch (random.Next(10))
            {
                case 0:
                    _mail.Receive(random.Next(1, 4));
                    break;
                case 1:
                    _mail.Read(random.Next(1, 3));
                    break;
                case 2:
                    _mail.PostSystemNotice();
                    break;
                case 3:
                    _mail.ClearSystemNotice();
                    break;
                case 4:
                    _quests.CompleteDaily(random.Next(1, 3));
                    break;
                case 5:
                    _quests.ClaimDaily(random.Next(1, 3));
                    break;
                case 6:
                    _quests.UnlockAchievement(random.Next(1, 3));
                    break;
                case 7:
                    _quests.RollOverDay();
                    break;
                case 8:
                    _shop.RefreshDailyDeals(random.Next(0, 5));
                    break;
                default:
                    _context.SetCounter("shop.bundles", random.Next(0, 4));
                    break;
            }
        }

        private static string RandomPath(Random random, IEnumerable<string> paths)
        {
            var list = paths.ToList();
            return list[random.Next(list.Count)];
        }

        /// <summary>
        /// Aggregation policies of the interior nodes, mirrored from
        /// <c>Assets/Lua/reddot/types.lua</c>. Anything not listed is a sum node.
        /// </summary>
        private static readonly Dictionary<string, string> Policies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { Main, "sum" },
            { Mail, "sum" },
            { Quests, "max" },
            { Shop, "any" },
        };

        /// <summary>Recomputes every parent from its children and demands the same answer.</summary>
        private static void AssertAggregatesAreConsistent(Dictionary<string, RedDotState> states, int iteration)
        {
            foreach (var pair in states)
            {
                var children = states.Keys
                    .Where(candidate => candidate.Length > pair.Key.Length &&
                                        candidate.StartsWith(pair.Key + ".", StringComparison.Ordinal) &&
                                        candidate.IndexOf('.', pair.Key.Length + 1) < 0)
                    .ToList();

                if (children.Count == 0)
                {
                    continue;
                }

                var policy = Policies.TryGetValue(pair.Key, out var declared) ? declared : "sum";
                var expectedVisible = false;
                var expectedCount = 0;

                foreach (var child in children)
                {
                    var childState = states[child];
                    if (!childState.Visible)
                    {
                        continue;
                    }

                    expectedVisible = true;
                    if (policy == "sum")
                    {
                        expectedCount += childState.Count;
                    }
                    else if (policy == "max")
                    {
                        expectedCount = Math.Max(expectedCount, childState.Count);
                    }
                }

                if (!expectedVisible)
                {
                    expectedCount = 0;
                }

                Assert.That(pair.Value, Is.EqualTo(new RedDotState(expectedVisible, expectedCount)),
                    $"iteration {iteration}: {pair.Key} ({policy}) disagrees with its children " +
                    $"[{string.Join(", ", children)}]");
            }
        }

        /// <summary>
        /// The set of notified paths must equal the set of paths whose state actually
        /// differs: nothing silent, and nothing spurious.
        /// </summary>
        private void AssertNotificationsMatch(
            Dictionary<string, RedDotState> before,
            Dictionary<string, RedDotState> after,
            int iteration)
        {
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in before.Keys.Union(after.Keys))
            {
                var wasState = before.TryGetValue(path, out var was) ? was : RedDotState.Hidden;
                var isState = after.TryGetValue(path, out var now) ? now : RedDotState.Hidden;
                if (!wasState.Equals(isState))
                {
                    expected.Add(path);
                }
            }

            var notified = _notifications.Select(n => n.Path).ToList();

            Assert.That(notified, Is.Unique, $"iteration {iteration}: a node was notified twice for one change");
            Assert.That(new HashSet<string>(notified, StringComparer.Ordinal), Is.EquivalentTo(expected),
                $"iteration {iteration}: notifications do not match the actual state diff");

            foreach (var notification in _notifications)
            {
                var expectedState = after.TryGetValue(notification.Path, out var state) ? state : RedDotState.Hidden;
                Assert.That(notification.State, Is.EqualTo(expectedState),
                    $"iteration {iteration}: {notification.Path} was notified with a stale state");
            }
        }

        #endregion
    }
}
