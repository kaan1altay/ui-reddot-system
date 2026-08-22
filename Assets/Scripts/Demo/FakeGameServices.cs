using System;
using System.Collections.Generic;
using RedDot.Events;

namespace RedDot.Demo
{
    /// <summary>
    /// Game time the demo and the tests can move on purpose.
    /// </summary>
    /// <remarks>
    /// Nothing here reads the wall clock. The demo advances it from the frame driver so
    /// time passes at the usual rate, and the "Advance time +1 day" button jumps it;
    /// tests set it by hand. Either way a scheduled reset fires because the clock said
    /// so, not because a test slept.
    /// </remarks>
    public sealed class FakeClock : IClockService
    {
        public const long DayLengthSeconds = 86400;

        /// <summary>An arbitrary but fixed epoch, so runs are comparable.</summary>
        public const long DefaultStart = 1_700_000_000;

        private double _now;

        public FakeClock(long start = DefaultStart)
        {
            _now = start;
        }

        public long Now() => (long)_now;

        /// <summary>The next midnight. Always strictly ahead, so a timer cannot re-fire.</summary>
        public long NextDayBoundary() => (Now() / DayLengthSeconds + 1) * DayLengthSeconds;

        /// <summary>Which day the clock is on. The shop's reset token is built from this.</summary>
        public long Day => Now() / DayLengthSeconds;

        public void Advance(double seconds)
        {
            if (seconds > 0)
            {
                _now += seconds;
            }
        }

        public void AdvanceDays(int days) => Advance(days * (double)DayLengthSeconds);

        public void SetNow(long now) => _now = now;
    }

    /// <summary>One mail in the fake inbox.</summary>
    public readonly struct FakeMail
    {
        public FakeMail(int id, string subject, bool actionable)
        {
            Id = id;
            Subject = subject;
            Actionable = actionable;
        }

        public int Id { get; }

        public string Subject { get; }

        /// <summary>Unread, or read but with an unclaimed attachment.</summary>
        public bool Actionable { get; }
    }

    /// <summary>
    /// A stand-in mail manager.
    /// </summary>
    /// <remarks>
    /// The demo is about red dots, not about a mail client, so this holds a list and
    /// raises the events a real manager would. It is what the EditMode tests drive and
    /// what the demo's buttons poke — one implementation for both, so the tests
    /// exercise the same object the demo does.
    /// </remarks>
    public sealed class FakeMailService : IMailService
    {
        private readonly EventBus _bus;
        private readonly List<FakeMail> _mails = new List<FakeMail>();
        private int _nextId = 1;

        public FakeMailService(EventBus bus = null)
        {
            _bus = bus;
        }

        public IReadOnlyList<FakeMail> Mails => _mails;

        public int ActionableCount()
        {
            var count = 0;
            foreach (var mail in _mails)
            {
                if (mail.Actionable)
                {
                    count++;
                }
            }

            return count;
        }

        public bool IsActionable(int mailId)
        {
            foreach (var mail in _mails)
            {
                if (mail.Id == mailId)
                {
                    return mail.Actionable;
                }
            }

            return false;
        }

        /// <summary>
        /// The highest id issued so far. A new mail moves it, which is exactly the
        /// "there is something new in here" signal the Mail button's seen state wants.
        /// </summary>
        /// <returns>
        /// Null while the inbox is empty. A nil token means "no content", and the rule
        /// keeps the dot off -- otherwise a fresh install would light up the Mail button
        /// for mail that does not exist.
        /// </returns>
        public string InboxToken() => _mails.Count == 0 ? null : "inbox:" + (_nextId - 1);

        public int Receive(string subject = null)
        {
            var id = _nextId++;
            _mails.Add(new FakeMail(id, subject ?? ("Mail #" + id), true));
            Publish("mail.received");
            return id;
        }

        public bool Open(int mailId)
        {
            for (var i = 0; i < _mails.Count; i++)
            {
                if (_mails[i].Id != mailId || !_mails[i].Actionable)
                {
                    continue;
                }

                _mails[i] = new FakeMail(mailId, _mails[i].Subject, false);
                Publish("mail.read");
                return true;
            }

            return false;
        }

        public int ClaimAll()
        {
            var claimed = 0;
            for (var i = 0; i < _mails.Count; i++)
            {
                if (!_mails[i].Actionable)
                {
                    continue;
                }

                _mails[i] = new FakeMail(_mails[i].Id, _mails[i].Subject, false);
                claimed++;
            }

            if (claimed > 0)
            {
                Publish("mail.claimed");
            }

            return claimed;
        }

        public bool Delete(int mailId)
        {
            for (var i = 0; i < _mails.Count; i++)
            {
                if (_mails[i].Id != mailId)
                {
                    continue;
                }

                _mails.RemoveAt(i);
                Publish("mail.deleted");
                return true;
            }

            return false;
        }

        private void Publish(string eventName) => _bus?.Publish(eventName);
    }

    public sealed class FakeQuestService : IQuestService
    {
        private readonly EventBus _bus;
        private readonly HashSet<long> _claimable = new HashSet<long>();
        private int _version;

        public FakeQuestService(EventBus bus = null)
        {
            _bus = bus;
        }

        public int ClaimableCount() => _claimable.Count;

        public bool IsClaimable(int chapterId, int questId) => _claimable.Contains(Pack(chapterId, questId));

        /// <summary>
        /// Moves on any change, so the Quests button re-arms after new progress. Null
        /// until something has actually happened, for the same reason as the inbox.
        /// </summary>
        public string BoardToken() => _version == 0 ? null : "board:" + _version;

        public void Complete(int chapterId, int questId)
        {
            if (!_claimable.Add(Pack(chapterId, questId)))
            {
                return;
            }

            _version++;
            Publish("quest.progress");
        }

        public void Claim(int chapterId, int questId)
        {
            if (!_claimable.Remove(Pack(chapterId, questId)))
            {
                return;
            }

            _version++;
            Publish("quest.claimed");
        }

        /// <summary>Midnight: the board resets and everything claimable is gone.</summary>
        public void RollOverDay()
        {
            _claimable.Clear();
            _version++;
            Publish("day.rollover");
        }

        private static long Pack(int chapterId, int questId) => ((long)chapterId << 32) | (uint)questId;

        private void Publish(string eventName) => _bus?.Publish(eventName);
    }

    public sealed class FakeShopService : IShopService
    {
        private readonly EventBus _bus;
        private readonly FakeClock _clock;

        public FakeShopService(EventBus bus = null, FakeClock clock = null)
        {
            _bus = bus;
            _clock = clock;
        }

        public int FreeDeals { get; private set; }

        public bool HasFreeDeal() => FreeDeals > 0;

        /// <summary>
        /// The day the stock belongs to. Crossing a day boundary changes it, so the
        /// scheduled reset and the seen token move together with no extra bookkeeping.
        /// </summary>
        public string ResetToken() => "day:" + (_clock?.Day ?? 0);

        public void AddFreeDeal()
        {
            FreeDeals++;
            Publish("shop.refreshed");
        }

        public void Purchase()
        {
            if (FreeDeals == 0)
            {
                return;
            }

            FreeDeals--;
            Publish("shop.purchased");
        }

        private void Publish(string eventName) => _bus?.Publish(eventName);
    }
}
