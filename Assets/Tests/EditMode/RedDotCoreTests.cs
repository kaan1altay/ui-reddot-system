using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RedDot.Demo;
using RedDot.Events;
using UnityEngine;

namespace RedDot.Tests
{
    /// <summary>
    /// Drives the real Lua engine through the real bridge.
    /// </summary>
    /// <remarks>
    /// Nothing here is a mock of the system under test: every assertion goes through xLua
    /// into <c>Assets/Lua/reddot/*.lua</c> and back. The only doubles are the fake game
    /// managers, the fake clock and the in-memory seen store, which stand in for data the
    /// red dot system reads but does not own.
    /// </remarks>
    [TestFixture]
    public sealed class RedDotCoreTests
    {
        private const string Mail = "Mail";
        private const string Quests = "Quests";
        private const string Shop = "Shop";
        private const string MailItem = "MailItem";
        private const string QuestItem = "QuestItem";
        private const string LimitedOffer = "LimitedOffer";

        private EventBus _bus;
        private FakeClock _clock;
        private FakeMailService _mail;
        private FakeQuestService _quests;
        private FakeShopService _shop;
        private RedDotContext _context;
        private InMemorySeenPersistence _seen;
        private RedDotBridge _bridge;
        private List<string> _logs;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _clock = new FakeClock();
            _mail = new FakeMailService(_bus);
            _quests = new FakeQuestService(_bus);
            _shop = new FakeShopService(_bus, _clock);
            _context = new RedDotContext(_mail, _quests, _shop, _clock);
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
            _logs = new List<string>();

            return new RedDotBridge(new RedDotBridgeOptions
            {
                Bus = _bus,
                Context = _context,
                SeenPersistence = _seen,
                Log = message =>
                {
                    _logs.Add(message);
                    TestContext.WriteLine("[lua] " + message);
                },
            });
        }

        /// <summary>Records what the engine pushed, so tests can count notifications.</summary>
        private sealed class Recorder : IRedDotHandle
        {
            public readonly List<(string Key, bool Value)> Calls = new List<(string, bool)>();

            public void SetRedDot(string registryKey, bool value)
            {
                Calls.Add((registryKey, value));
            }

            public (string Key, bool Value) Last => Calls[Calls.Count - 1];

            public bool LastValue => Last.Value;
        }

        private static string PatchSource()
        {
            return File.ReadAllText(Path.Combine(Application.dataPath, "Lua/patches/rules_patch_example.lua"));
        }

        private bool Logged(string fragment)
        {
            return _logs.Any(line => line.Contains(fragment));
        }

        #region Registry keys

        [Test]
        public void ARegistryKeyIsTheTypeAndItsKeyValuesJoined()
        {
            Assert.That(_bridge.BuildKey(Shop), Is.EqualTo("Shop"), "a global dot is just its type");
            Assert.That(_bridge.BuildKey(MailItem, 42), Is.EqualTo("MailItem|42"));
            Assert.That(_bridge.BuildKey(QuestItem, 3, 17), Is.EqualTo("QuestItem|3|17"),
                "keys keep the order the rule declares");
        }

        [Test]
        public void IntegerKeysNeverPickUpADecimalPoint()
        {
            // Lua 5.3 prints a float-typed 42 as "42.0", and "MailItem|42.0" would be a
            // different dot from "MailItem|42" for the rest of the session.
            Assert.That(_bridge.BuildKey(MailItem, 42), Is.EqualTo("MailItem|42"));
            Assert.That(_bridge.BuildKey(MailItem, 42L), Is.EqualTo("MailItem|42"));
            Assert.That(_bridge.BuildKey(MailItem, 42.0), Is.EqualTo("MailItem|42"));
            Assert.That(_bridge.BuildKey(MailItem, "abc"), Is.EqualTo("MailItem|abc"));
        }

        [Test]
        public void SubscribingReturnsTheRegistryKeyItUsed()
        {
            var handle = new Recorder();
            Assert.That(_bridge.Subscribe(handle, QuestItem, 3, 17), Is.EqualTo("QuestItem|3|17"));
        }

        #endregion

        #region Lifecycles

        [Test]
        public void GlobalDotsExistFromBootWithNobodyWatching()
        {
            var values = _bridge.ReadAllValues();

            Assert.That(values.Keys, Is.EquivalentTo(new[] { Mail, Quests, Shop }),
                "the three keyless types, and nothing else");
            foreach (var pair in values)
            {
                Assert.That(pair.Value.Subscribers, Is.Zero, pair.Key + " should have no subscribers yet");
            }
        }

        [Test]
        public void AGlobalDotIsCorrectBeforeItsScreenHasEverBeenOpened()
        {
            // The whole argument for per-type conditions over aggregation: nothing inside
            // Mail exists yet, and the Mail button still knows the answer.
            Assert.That(_bridge.GetValue(Mail), Is.False, "an empty inbox says nothing");

            _mail.Receive();
            _bridge.Flush();

            Assert.That(_bridge.GetValue(Mail), Is.True);
            Assert.That(_bridge.ReadAllValues().ContainsKey("MailItem|1"), Is.False,
                "and it did so without a single MailItem dot existing");
        }

        [Test]
        public void AKeyedDotIsCreatedOnTheFirstSubscribeAndDestroyedWithTheLast()
        {
            Assert.That(_bridge.Counts(), Is.EqualTo((3, 0)));

            var first = new Recorder();
            var second = new Recorder();
            var key = _bridge.Subscribe(first, MailItem, 7);
            Assert.That(_bridge.Counts(), Is.EqualTo((4, 1)));

            _bridge.Subscribe(second, MailItem, 7);
            Assert.That(_bridge.Counts(), Is.EqualTo((4, 1)), "the second subscriber shares the dot");
            Assert.That(_bridge.SubscriberCount(key), Is.EqualTo(2));

            _bridge.Unsubscribe(key, first);
            Assert.That(_bridge.Counts(), Is.EqualTo((4, 1)), "still one subscriber left");

            _bridge.Unsubscribe(key, second);
            Assert.That(_bridge.Counts(), Is.EqualTo((3, 0)), "the last one out destroys it");
        }

        [Test]
        public void GlobalDotsSurviveTheirLastSubscriber()
        {
            var handle = new Recorder();
            var key = _bridge.Subscribe(handle, Shop);
            _bridge.Unsubscribe(key, handle);

            Assert.That(_bridge.Counts(), Is.EqualTo((3, 0)));
            Assert.That(_bridge.ReadAllValues().ContainsKey(Shop), Is.True,
                "a global dot is not the property of whoever happened to be watching it");
        }

        [Test]
        public void ClearSubscriptionsDestroysEveryKeyedDotAndKeepsTheGlobals()
        {
            _bridge.Subscribe(new Recorder(), MailItem, 1);
            _bridge.Subscribe(new Recorder(), MailItem, 2);
            _bridge.Subscribe(new Recorder(), QuestItem, 1, 1);
            _bridge.Subscribe(new Recorder(), Mail);
            Assert.That(_bridge.Counts(), Is.EqualTo((6, 3)));

            var destroyed = _bridge.ClearSubscriptions();

            Assert.That(destroyed, Is.EqualTo(3));
            Assert.That(_bridge.Counts(), Is.EqualTo((3, 0)));
            Assert.That(_bridge.SubscriberCount(Mail), Is.Zero, "the global dot lost its watcher too");
        }

        [Test]
        public void AKeyedDotDoesNotLeakWhenTheSameHandleSubscribesTwice()
        {
            var handle = new Recorder();
            var key = _bridge.Subscribe(handle, MailItem, 5);
            _bridge.Subscribe(handle, MailItem, 5);

            Assert.That(_bridge.SubscriberCount(key), Is.EqualTo(2), "two subscriptions, honestly counted");

            _bridge.Unsubscribe(key, handle);
            _bridge.Unsubscribe(key, handle);
            Assert.That(_bridge.Counts(), Is.EqualTo((3, 0)));
        }

        #endregion

        #region Immediate compute on subscribe

        [Test]
        public void SubscribingComputesAndDeliversBeforeItReturns()
        {
            _mail.Receive();
            _bridge.Flush();

            var handle = new Recorder();
            _bridge.Subscribe(handle, MailItem, 1);

            Assert.That(handle.Calls.Count, Is.EqualTo(1), "one push, on the way out of Subscribe");
            Assert.That(handle.Last, Is.EqualTo(("MailItem|1", true)));
        }

        /// <summary>
        /// The play-test bug this whole rule exists for: leave a screen, come back, and
        /// the badges are blank until something happens to move them.
        /// </summary>
        [Test]
        public void ReEnteringAScreenShowsItsBadgesImmediately()
        {
            var mailId = _mail.Receive();
            _bridge.Flush();

            // Open the screen.
            var first = new Recorder();
            var key = _bridge.Subscribe(first, MailItem, mailId);
            Assert.That(first.LastValue, Is.True);

            // Leave it. The keyed dot goes with the last subscriber.
            _bridge.Unsubscribe(key, first);
            Assert.That(_bridge.ReadAllValues().ContainsKey(key), Is.False);

            // Re-enter, with no event of any kind in between.
            var second = new Recorder();
            _bridge.Subscribe(second, MailItem, mailId);

            Assert.That(second.Calls.Count, Is.EqualTo(1));
            Assert.That(second.LastValue, Is.True,
                "the badge is right on the frame the screen opens, not after the next event");
            Assert.That(_bridge.Flush(), Is.Zero, "and there was nothing left for the flush to do");
        }

        [Test]
        public void SubscribingToATypeNoRuleDefinesIsLegalAndReadsFalse()
        {
            var handle = new Recorder();
            _bridge.Subscribe(handle, LimitedOffer);

            Assert.That(handle.LastValue, Is.False);
            Assert.That(Logged("no rule for type 'LimitedOffer'"), Is.True, "and it says so, once");
        }

        #endregion

        #region Queue and flush

        [Test]
        public void EventsQueueAndComputeNothing()
        {
            _bridge.Subscribe(new Recorder(), MailItem, 1);
            _bridge.ResetStats();

            _mail.Receive();
            _mail.Receive();

            Assert.That(_bridge.Stats().Events, Is.EqualTo(2), "both reached Lua");
            Assert.That(_bridge.Stats().Computes, Is.Zero, "and neither evaluated a rule");
        }

        [Test]
        public void ManyEventsInOneFrameCostOneComputationPerDot()
        {
            _bridge.Subscribe(new Recorder(), MailItem, 1);
            _bridge.ResetStats();

            for (var i = 0; i < 50; i++)
            {
                _bridge.RaiseEvent("mail.received");
            }

            Assert.That(_bridge.Stats().Events, Is.EqualTo(50));

            _bridge.Flush();

            Assert.That(_bridge.Stats().Computes, Is.EqualTo(2),
                "two live dots watch mail events: the Mail global and the one MailItem");
            Assert.That(_bridge.Stats().Drains, Is.EqualTo(1));
        }

        [Test]
        public void OnlyDotsWhoseValueMovedAreNotified()
        {
            var handle = new Recorder();
            _bridge.Subscribe(handle, Mail);
            handle.Calls.Clear();
            _bridge.ResetStats();

            // An event that changes nothing: the rule runs, the answer is the same.
            _bridge.RaiseEvent("mail.received");
            var changed = _bridge.Flush();

            Assert.That(changed, Is.Zero);
            Assert.That(handle.Calls, Is.Empty, "an unchanged dot must not wake the view layer");
            Assert.That(_bridge.Stats().Computes, Is.GreaterThan(0), "the rule did run");
            Assert.That(_bridge.Stats().Notifications, Is.Zero);
        }

        [Test]
        public void AnIdleFlushDoesNoWorkAtAll()
        {
            _bridge.ResetStats();

            Assert.That(_bridge.Flush(), Is.Zero);
            Assert.That(_bridge.Flush(), Is.Zero);

            var stats = _bridge.Stats();
            Assert.That(stats.Drains, Is.Zero, "an empty queue is not even counted as a drain");
            Assert.That(stats.Computes, Is.Zero, "the system never polls");
            Assert.That(stats.Notifications, Is.Zero);
        }

        [Test]
        public void EventsNobodySubscribedToNeverReachLua()
        {
            _bridge.ResetStats();

            _bridge.RaiseEvent("guild.applicationReceived");
            _bridge.RaiseEvent("some.event.that.does.not.exist");

            Assert.That(_bus.PublishCount, Is.EqualTo(2));
            Assert.That(_bridge.Stats().Events, Is.Zero,
                "the bridge only forwards events the current rules named");
        }

        [Test]
        public void TheBridgeSubscribesToExactlyTheEventsTheRulesName()
        {
            Assert.That(_bridge.SubscribedEvents(), Is.EqualTo(new[]
            {
                "day.rollover",
                "mail.claimed",
                "mail.deleted",
                "mail.read",
                "mail.received",
                "quest.claimed",
                "quest.progress",
                "shop.purchased",
                "shop.refreshed",
            }));
        }

        [Test]
        public void AKeyedDotOnlyRespondsToEventsWhileItIsAlive()
        {
            var handle = new Recorder();
            var key = _bridge.Subscribe(handle, MailItem, 1);
            _bridge.Unsubscribe(key, handle);
            _bridge.ResetStats();

            _mail.Receive();
            _bridge.Flush();

            Assert.That(_bridge.Stats().Computes, Is.EqualTo(1),
                "only the Mail global is left to compute; the destroyed row costs nothing");
        }

        #endregion

        #region Seen tracking and tokens

        [Test]
        public void ADotThatTracksSeenStartsOnAndGoesOffWhenMarked()
        {
            // Nobody has seen today's shop rotation yet.
            Assert.That(_bridge.GetValue(Shop), Is.True);

            Assert.That(_bridge.MarkSeen(Shop), Is.True);
            _bridge.Flush();

            Assert.That(_bridge.GetValue(Shop), Is.False);
            Assert.That(_bridge.IsSeen(Shop), Is.True);
        }

        [Test]
        public void NewContentMovesTheTokenAndTheDotComesBackOn()
        {
            _mail.Receive();
            _bridge.Flush();

            _bridge.MarkSeen(Mail);
            _bridge.Flush();
            Assert.That(_bridge.GetValue(Mail), Is.True,
                "seen or not, there is still unclaimed mail sitting there");

            _mail.ClaimAll();
            _bridge.Flush();
            Assert.That(_bridge.GetValue(Mail), Is.False, "nothing to claim, and nothing unseen");

            // A mail arrives and is claimed in the same breath: nothing is actionable, so
            // the only thing that can light the button is the token having moved.
            _mail.Receive();
            _mail.ClaimAll();
            _bridge.Flush();

            Assert.That(_bridge.GetValue(Mail), Is.True,
                "the inbox token moved, so what the player saw is no longer what is there");
        }

        [Test]
        public void ANilTokenKeepsTheDotOff()
        {
            // The quest board has never had anything on it, so its token is nil.
            Assert.That(_bridge.GetValue(Quests), Is.False);

            _quests.Complete(1, 1);
            _bridge.Flush();
            Assert.That(_bridge.GetValue(Quests), Is.True, "now there is content, and it is unseen");
        }

        [Test]
        public void MarkSeenIsIgnoredByTypesThatTrackRealStateInstead()
        {
            _mail.Receive();
            _bridge.Flush();
            _bridge.Subscribe(new Recorder(), MailItem, 1);

            Assert.That(_bridge.MarkSeen(MailItem, 1), Is.False);
            _bridge.Flush();

            Assert.That(_bridge.GetValue(MailItem, 1), Is.True,
                "an unread mail is still unread after the player glanced at the list");

            _mail.Open(1);
            _bridge.Flush();
            Assert.That(_bridge.GetValue(MailItem, 1), Is.False, "reading it is what clears it");
        }

        [Test]
        public void MarkingSeenWithNoContentYetStoresNothing()
        {
            Assert.That(_bridge.MarkSeen(Mail), Is.False, "there is no token to remember");
            Assert.That(_bridge.IsSeen(Mail), Is.False);

            _mail.Receive();
            _bridge.Flush();
            Assert.That(_bridge.GetValue(Mail), Is.True,
                "so the first real mail is not hidden by a seen mark from before it existed");
        }

        #endregion

        #region Persistence

        [Test]
        public void SeenStateRoundTripsThroughOnePlayerPrefsBlob()
        {
            _mail.Receive();
            _mail.ClaimAll();
            _bridge.Flush();
            _bridge.MarkSeen(Mail);
            _bridge.MarkSeen(Shop);
            _bridge.Flush();

            Assert.That(_seen.Blob, Does.Contain("\"version\":1"));
            Assert.That(_seen.Blob, Does.Contain("Mail"));
            Assert.That(_seen.Blob, Does.Contain("[["), "entries are arrays, not an object keyed by id");
            TestContext.WriteLine(_seen.Blob);

            // Restart: a brand new Lua environment reading the same blob.
            _bridge.Dispose();
            _bridge = CreateBridge();

            Assert.That(_bridge.GetValue(Mail), Is.False, "what the player dismissed stays dismissed");
            Assert.That(_bridge.GetValue(Shop), Is.False);
        }

        [Test]
        public void AnOlderSaveVersionIsDiscardedRatherThanGuessedAt()
        {
            _mail.Receive();
            _mail.ClaimAll();
            _bridge.Flush();
            _bridge.MarkSeen(Mail);
            _bridge.Flush();
            Assert.That(_bridge.GetValue(Mail), Is.False);

            _bridge.Dispose();
            _seen.Overwrite(_seen.Blob.Replace("\"version\":1", "\"version\":0"));
            _bridge = CreateBridge();

            Assert.That(Logged("is not 1"), Is.True);
            Assert.That(_bridge.GetValue(Mail), Is.True, "clean slate: the badge is back");
        }

        [Test]
        public void ACorruptSaveIsDiscardedRatherThanCrashing()
        {
            _bridge.Dispose();
            _seen.Overwrite("{ this is not json");
            _bridge = CreateBridge();

            Assert.That(Logged("corrupt"), Is.True);
            Assert.That(_bridge.GetValue(Shop), Is.True, "and the engine came up anyway");
        }

        [Test]
        public void TheSaveIsWrittenAtMostOncePerFrame()
        {
            _mail.Receive();
            _quests.Complete(1, 1);
            _bridge.Flush();

            var before = _seen.SaveCount;

            _bridge.MarkSeen(Mail);
            _bridge.MarkSeen(Quests);
            _bridge.MarkSeen(Shop);
            Assert.That(_seen.SaveCount, Is.EqualTo(before), "marking does not write");

            _bridge.Flush();
            Assert.That(_seen.SaveCount, Is.EqualTo(before + 1), "three marks, one write");

            _bridge.Flush();
            Assert.That(_seen.SaveCount, Is.EqualTo(before + 1), "and nothing changed, so nothing written");
        }

        #endregion

        #region Scheduled resets

        [Test]
        public void TheSoonestDeadlineIsTheOneTheManagerKeeps()
        {
            Assert.That(_bridge.NextDeadline(), Is.EqualTo(_clock.NextDayBoundary()));
        }

        [Test]
        public void CrossingADeadlineRequeuesTheDotsAndMovesTheDeadlineOn()
        {
            _bridge.MarkSeen(Shop);
            _bridge.Flush();
            Assert.That(_bridge.GetValue(Shop), Is.False);

            var firstDeadline = _bridge.NextDeadline();
            _clock.AdvanceDays(1);

            // No event is raised. The only thing that knows midnight happened is the clock.
            var changed = _bridge.Flush();

            Assert.That(changed, Is.EqualTo(1));
            Assert.That(_bridge.GetValue(Shop), Is.True, "a new rotation is new content");
            Assert.That(_bridge.NextDeadline(), Is.GreaterThan(firstDeadline), "and the next one is scheduled");
        }

        [Test]
        public void ADeadlineThatHasNotArrivedCostsNothing()
        {
            _bridge.MarkSeen(Shop);
            _bridge.Flush();
            _bridge.ResetStats();

            _clock.Advance(60);
            Assert.That(_bridge.Flush(), Is.Zero);
            Assert.That(_bridge.Stats().Computes, Is.Zero, "a deadline check is a number comparison");
        }

        #endregion

        #region Safety

        private const string ThrowingRulePatch = @"
local base = require('reddot.RedDotRules')
local rules = {}
for typeName, rule in pairs(base) do rules[typeName] = rule end

rules['MailItem'] = {
    keys   = { 'mailId' },
    events = { 'mail.received', 'mail.read' },
    condition = function() error('this rule is broken on purpose') end,
}

return rules
";

        [Test]
        public void AThrowingRuleReadsFalseAndTheRestKeepsWorking()
        {
            _bridge.ReloadRules(ThrowingRulePatch);
            _bridge.Subscribe(new Recorder(), MailItem, 1);

            _mail.Receive();
            _bridge.Flush();

            Assert.That(_bridge.GetValue(MailItem, 1), Is.False, "the broken rule reads as off");
            Assert.That(_bridge.GetValue(Mail), Is.True, "one bad rule must not take the rest down");
            Assert.That(Logged("condition for 'MailItem' failed"), Is.True);
        }

        [Test]
        public void ABrokenRuleOnlyComplainsOnce()
        {
            _bridge.ReloadRules(ThrowingRulePatch);
            _bridge.Subscribe(new Recorder(), MailItem, 1);

            for (var i = 0; i < 10; i++)
            {
                _mail.Receive();
                _bridge.Flush();
            }

            Assert.That(_logs.Count(line => line.Contains("condition for 'MailItem' failed")), Is.EqualTo(1),
                "a rule that throws every frame must not drown the log");
        }

        #endregion

        #region Validation

        [Test]
        public void ATokenWithoutTracksSeenIsAnError()
        {
            var problems = _bridge.ValidateSource(@"
return { Thing = { events = { 'mail.read' }, condition = function() return true end,
                   token = function() return 'x' end } }");

            Assert.That(problems, Has.Some.Contains("error").And.Some.Contains("token but does not set tracksSeen"));
        }

        [Test]
        public void ARuleWithNeitherConditionNorTracksSeenIsAnError()
        {
            var problems = _bridge.ValidateSource("return { Thing = { events = { 'mail.read' } } }");

            Assert.That(problems, Has.Some.Contains("neither a condition nor tracksSeen"));
        }

        [Test]
        public void ARuleWithNoEventsAndNoResetAtIsAWarning()
        {
            var problems = _bridge.ValidateSource(
                "return { Thing = { condition = function() return true end } }");

            Assert.That(problems, Has.Some.Contains("warning").And.Some.Contains("never refreshes by itself"));
        }

        [Test]
        public void AnUnknownEventNameIsAnError()
        {
            var problems = _bridge.ValidateSource(@"
return { Thing = { events = { 'mail.recieved' }, condition = function() return true end } }");

            Assert.That(problems, Has.Some.Contains("unknown event 'mail.recieved'"),
                "a typo here is otherwise invisible: the dot just never refreshes");
        }

        [Test]
        public void APatchMayDeclareTheEventsItIntroduces()
        {
            Assert.That(_bridge.ValidateSource(PatchSource()), Is.Empty,
                "the example patch declares LimitedOfferStarted, so it validates clean");
        }

        [Test]
        public void TheShippedRulesValidateClean()
        {
            Assert.That(_logs.Where(line => line.Contains("reddot.rules")), Is.Empty);
        }

        [Test]
        public void AHoleInAnEventListDoesNotSwallowTheEntriesAfterIt()
        {
            // `#list` stops at the first nil, so a naive walk would never see the typo.
            var problems = _bridge.ValidateSource(@"
local events = { 'mail.read', nil, 'mail.recieved' }
return { Thing = { events = events, condition = function() return true end } }");

            Assert.That(problems, Has.Some.Contains("unknown event 'mail.recieved'"));
        }

        #endregion

        #region Reconcile checker

        [Test]
        public void TheReconcileCheckerFlagsARuleThatIsMissingAnEvent()
        {
            // MailItem now only listens for a shop event, so nothing tells it that a mail
            // was read. Its cached value is right about what it was told, and what it was
            // told is incomplete.
            _bridge.ReloadRules(@"
local base = require('reddot.RedDotRules')
local rules = {}
for typeName, rule in pairs(base) do rules[typeName] = rule end

rules['MailItem'] = {
    keys      = { 'mailId' },
    events    = { 'shop.refreshed' },
    condition = function(mailId) return Game.Mail:IsActionable(mailId) end,
}

return rules
");
            var mailId = _mail.Receive();
            _bridge.Subscribe(new Recorder(), MailItem, mailId);
            Assert.That(_bridge.GetValue(MailItem, mailId), Is.True);

            _mail.Open(mailId);
            _bridge.Flush();

            Assert.That(_bridge.GetValue(MailItem, mailId), Is.True, "the cache never heard about it");
            Assert.That(_bridge.Reconcile(), Is.EqualTo(1));
            Assert.That(Logged("MISMATCH MailItem|" + mailId), Is.True);
            Assert.That(_bridge.GetValue(MailItem, mailId), Is.True,
                "and the checker fixed nothing: a mismatch is a rule bug, not a glitch to paper over");
        }

        [Test]
        public void TheReconcileCheckerIsQuietWhenTheRulesAreComplete()
        {
            _bridge.Subscribe(new Recorder(), MailItem, 1);
            _mail.Receive();
            _quests.Complete(1, 1);
            _shop.AddFreeDeal();
            _bridge.Flush();

            Assert.That(_bridge.Reconcile(), Is.Zero);
        }

        [Test]
        public void TheReconcileSweepRunsOnItsOwnTimerWhenEnabled()
        {
            _bridge.ReloadRules(@"
local base = require('reddot.RedDotRules')
local rules = {}
for typeName, rule in pairs(base) do rules[typeName] = rule end
rules['MailItem'] = {
    keys      = { 'mailId' },
    events    = { 'shop.refreshed' },
    condition = function(mailId) return Game.Mail:IsActionable(mailId) end,
}
return rules
");
            _bridge.SetReconcileEnabled(true);
            _bridge.Subscribe(new Recorder(), MailItem, 1);
            _mail.Receive();
            _bridge.Flush();

            _bridge.ResetStats();
            _clock.Advance(2);
            _bridge.Flush();

            Assert.That(_bridge.Stats().Mismatches, Is.EqualTo(1), "the sweep found it without being asked");
        }

        #endregion

        #region Hot update

        [Test]
        public void ThePatchAddsATypeTheBuildNeverKnewAbout()
        {
            Assert.That(_bridge.ReadAllValues().ContainsKey(LimitedOffer), Is.False);
            Assert.That(_bus.HasSubscribers("LimitedOfferStarted"), Is.False);

            _bridge.ReloadRules(PatchSource());

            Assert.That(_bridge.ReadAllValues().ContainsKey(LimitedOffer), Is.True,
                "a global dot for a type that did not exist a moment ago");
            Assert.That(_bus.HasSubscribers("LimitedOfferStarted"), Is.True,
                "and the subscription diff really reached the C# bus");
            Assert.That(_bridge.GetValue(LimitedOffer), Is.False, "no offer is running yet");

            _context.SetCounter("shop.limitedOffer", 1);
            _bridge.RaiseEvent("LimitedOfferStarted");
            _bridge.Flush();

            Assert.That(_bridge.GetValue(LimitedOffer), Is.True);

            _bridge.MarkSeen(LimitedOffer);
            _bridge.Flush();
            Assert.That(_bridge.GetValue(LimitedOffer), Is.False, "and it clears like any other seen dot");
        }

        [Test]
        public void ABindingMadeBeforeTheReloadPicksUpTheTypeItIntroduces()
        {
            var handle = new Recorder();
            _bridge.Subscribe(handle, LimitedOffer);
            Assert.That(handle.LastValue, Is.False);

            _context.SetCounter("shop.limitedOffer", 1);
            _bridge.ReloadRules(PatchSource());

            Assert.That(handle.LastValue, Is.True,
                "bindings hold a type and keys, so a dot the patch invents lands in an existing view");
            Assert.That(_bridge.SubscriberCount(LimitedOffer), Is.EqualTo(1));
        }

        [Test]
        public void AReloadThatRetiresARuleUnsubscribesItsEvents()
        {
            _mail.Receive();
            _bridge.Flush();
            Assert.That(_bus.HasSubscribers("mail.received"), Is.True);

            _bridge.ReloadRules(@"
local base = require('reddot.RedDotRules')
local rules = {}
for typeName, rule in pairs(base) do rules[typeName] = rule end
rules['Mail'] = nil
rules['MailItem'] = nil
return rules
");

            Assert.That(_bus.HasSubscribers("mail.received"), Is.False,
                "the C# bus really lost the subscription, not just the Lua index");
            Assert.That(_bus.HasSubscribers("quest.progress"), Is.True,
                "events that survived the reload are never churned");
            Assert.That(_bridge.GetValue(Mail), Is.False, "a dot with no rule has nothing to say");
        }

        [Test]
        public void AReloadReportsOnlyTheDotsThatActuallyMoved()
        {
            _bridge.MarkSeen(Shop);
            _bridge.Flush();

            // The patch only adds a type, and the new dot evaluates to false.
            Assert.That(_bridge.ReloadRules(PatchSource()), Is.Zero);
        }

        #endregion

        #region Diagnostics

        [Test]
        public void DumpStateDescribesTheRegistryTheSeenSetAndTheStats()
        {
            _mail.Receive();
            _bridge.Subscribe(new Recorder(), MailItem, 1);
            _bridge.MarkSeen(Shop);
            _bridge.Flush();

            var dump = _bridge.DumpState();
            TestContext.WriteLine(dump);

            Assert.That(dump, Does.Contain("red dots: 4 live (3 global, 1 keyed)"));
            Assert.That(dump, Does.Contain("MailItem|1"));
            Assert.That(dump, Does.Contain("(global)"));
            Assert.That(dump, Does.Contain("seen: Shop"));
            Assert.That(dump, Does.Contain("stats:"));
        }

        #endregion

        #region Fuzz

        /// <summary>
        /// Ten thousand random events, marks, subscribes and reloads, checking after every
        /// flush that the cache agrees with a fresh recomputation and that nothing was
        /// notified without changing.
        /// </summary>
        /// <remarks>
        /// The seed is fixed, so a failure is reproducible; the point is coverage of
        /// orderings a hand-written test would never think to try.
        /// </remarks>
        [Test]
        public void FuzzingKeepsTheCacheHonestAndTheNotificationsQuiet()
        {
            const int eventCount = 10000;
            var random = new System.Random(20260822);

            var recorder = new FuzzRecorder();
            var live = new Dictionary<string, (string Type, object[] Keys)>(StringComparer.Ordinal);

            var flushes = 0;
            var reloads = 0;
            var drainComputes = 0;
            var patched = false;
            var offers = 0;

            for (var i = 1; i <= eventCount; i++)
            {
                ChurnSubscriptions(random, recorder, live);

                var before = _bridge.ReadAllValues();
                recorder.Calls.Clear();

                MutateGameState(random, ref offers, patched);

                if (random.Next(20) == 0)
                {
                    _bridge.MarkSeen(RandomType(random, patched));
                }

                if (random.Next(6) != 0 && i != eventCount)
                {
                    continue;
                }

                var computesBefore = _bridge.Stats().Computes;
                _bridge.Flush();
                flushes++;

                // Counted before the reconcile sweep below, which recomputes everything
                // on purpose and would drown the number it is meant to measure.
                drainComputes += _bridge.Stats().Computes - computesBefore;

                var after = _bridge.ReadAllValues();
                AssertNotificationsMatch(before, after, recorder, i);

                Assert.That(_bridge.Reconcile(), Is.Zero,
                    "iteration " + i + ": a cached value disagrees with a fresh recomputation");

                if (random.Next(120) != 0)
                {
                    continue;
                }

                // A hot update in the middle of the storm, in both directions.
                recorder.Calls.Clear();
                if (patched)
                {
                    _bridge.ReloadRules("return require('reddot.RedDotRules')");
                    patched = false;
                }
                else
                {
                    _bridge.ReloadRules(PatchSource());
                    patched = true;
                }

                reloads++;
                Assert.That(_bridge.Reconcile(), Is.Zero, "iteration " + i + ": the reload left the cache stale");
            }

            TestContext.WriteLine($"{eventCount} events, {flushes} flushes, {reloads} reloads");
            TestContext.WriteLine(_bridge.DumpState());

            Assert.That(flushes, Is.GreaterThan(500));
            Assert.That(reloads, Is.GreaterThan(3));
            var stats = _bridge.Stats();
            TestContext.WriteLine(
                drainComputes + " rule evaluations for " + stats.Queued + " queue entries across " +
                stats.Events + " events");

            // The batching guarantee, stated exactly: a dot on the pending set is computed
            // once when the frame drains it, however many events put it there. Fewer
            // computes than queue entries means some dots were destroyed before the drain
            // reached them, which is the keyed lifecycle doing its job.
            Assert.That(drainComputes, Is.LessThanOrEqualTo(stats.Queued),
                "a queued dot is evaluated at most once per drain");
        }

        private sealed class FuzzRecorder : IRedDotHandle
        {
            public readonly List<(string Key, bool Value)> Calls = new List<(string, bool)>();

            public void SetRedDot(string registryKey, bool value)
            {
                Calls.Add((registryKey, value));
            }
        }

        /// <summary>Opens and closes rows, so keyed dots are created and destroyed under load.</summary>
        private void ChurnSubscriptions(
            System.Random random,
            FuzzRecorder recorder,
            Dictionary<string, (string Type, object[] Keys)> live)
        {
            if (random.Next(4) == 0 && live.Count < 12)
            {
                var (type, keys) = random.Next(2) == 0
                    ? (MailItem, new object[] { random.Next(1, 8) })
                    : (QuestItem, new object[] { random.Next(1, 3), random.Next(1, 5) });

                var key = _bridge.BuildKey(type, keys);
                if (!live.ContainsKey(key))
                {
                    live[key] = (type, keys);
                    _bridge.Subscribe(recorder, type, keys);
                }
            }

            if (random.Next(5) == 0 && live.Count > 0)
            {
                var key = live.Keys.ElementAt(random.Next(live.Count));
                _bridge.Unsubscribe(key, recorder);
                live.Remove(key);
            }

            if (random.Next(400) == 0)
            {
                _bridge.ClearSubscriptions();
                live.Clear();
            }

        }

        private void MutateGameState(System.Random random, ref int offers, bool patched)
        {
            switch (random.Next(12))
            {
                case 0:
                case 1:
                    _mail.Receive();
                    break;
                case 2:
                    _mail.Open(random.Next(1, 8));
                    break;
                case 3:
                    _mail.ClaimAll();
                    break;
                case 4:
                    _mail.Delete(random.Next(1, 8));
                    break;
                case 5:
                    _quests.Complete(random.Next(1, 3), random.Next(1, 5));
                    break;
                case 6:
                    _quests.Claim(random.Next(1, 3), random.Next(1, 5));
                    break;
                case 7:
                    _quests.RollOverDay();
                    break;
                case 8:
                    _shop.AddFreeDeal();
                    break;
                case 9:
                    _shop.Purchase();
                    break;
                case 10:
                    // Time only ever moves forward, and crossing midnight is exactly the
                    // case the scheduled reset exists for.
                    _clock.Advance(random.Next(1, 40000));
                    break;
                default:
                    if (patched)
                    {
                        // The counter and its event always move together: changing the
                        // data without telling anybody is the bug the checker detects,
                        // not something to fuzz into every run.
                        offers++;
                        _context.SetCounter("shop.limitedOffer", offers);
                        _bridge.RaiseEvent("LimitedOfferStarted");
                    }

                    break;
            }
        }

        private static string RandomType(System.Random random, bool patched)
        {
            var types = patched
                ? new[] { Mail, Quests, Shop, LimitedOffer }
                : new[] { Mail, Quests, Shop };
            return types[random.Next(types.Length)];
        }

        /// <summary>
        /// The set of notified keys must equal the set whose value actually differs:
        /// nothing silent, nothing spurious, and nothing notified twice.
        /// </summary>
        private static void AssertNotificationsMatch(
            Dictionary<string, (bool Value, int Subscribers)> before,
            Dictionary<string, (bool Value, int Subscribers)> after,
            FuzzRecorder recorder,
            int iteration)
        {
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in before.Keys.Union(after.Keys))
            {
                // A dot that was destroyed cannot notify, and one that appeared did so
                // through Subscribe, which is outside this window.
                if (!before.TryGetValue(key, out var was) || !after.TryGetValue(key, out var now))
                {
                    continue;
                }

                if (was.Value != now.Value && now.Subscribers > 0)
                {
                    expected.Add(key);
                }
            }

            var notified = recorder.Calls.Select(call => call.Key).ToList();

            Assert.That(notified, Is.Unique, $"iteration {iteration}: a dot was notified twice for one change");
            Assert.That(new HashSet<string>(notified, StringComparer.Ordinal), Is.EquivalentTo(expected),
                $"iteration {iteration}: notifications do not match the actual value diff");
        }

        #endregion
    }
}
