using System;
using System.Collections.Generic;

namespace RedDot
{
    /// <summary>Mail data a red dot rule may read.</summary>
    public interface IMailService
    {
        /// <summary>Mails that still want something from the player: unread or unclaimed.</summary>
        int ActionableCount();

        /// <summary>Is this particular mail still actionable?</summary>
        bool IsActionable(int mailId);

        /// <summary>
        /// A stamp of the inbox's contents. It moves when mail arrives, which is what
        /// makes the Mail button light up again after the player has already looked.
        /// </summary>
        string InboxToken();
    }

    /// <summary>Quest data a red dot rule may read.</summary>
    public interface IQuestService
    {
        int ClaimableCount();

        bool IsClaimable(int chapterId, int questId);

        /// <summary>A stamp of the quest board, moved by any progress or claim.</summary>
        string BoardToken();
    }

    /// <summary>Shop data a red dot rule may read.</summary>
    public interface IShopService
    {
        bool HasFreeDeal();

        /// <summary>
        /// A stamp of the current rotation. Every daily reset counts as new stock even
        /// when the deals happen to look the same.
        /// </summary>
        string ResetToken();
    }

    /// <summary>
    /// Game time, for the dots that change on a clock rather than on an event.
    /// </summary>
    /// <remarks>
    /// A rule's <c>resetAt</c> returns a moment from here and the manager keeps one
    /// timer for the soonest of them, so a daily reset costs a number comparison per
    /// frame instead of a rule that re-evaluates hoping to notice midnight.
    /// </remarks>
    public interface IClockService
    {
        /// <summary>Unix seconds.</summary>
        long Now();

        /// <summary>The next daily boundary, always strictly in the future.</summary>
        long NextDayBoundary();
    }

    /// <summary>
    /// The <c>Game</c> global every rule reads through: the complete surface of game
    /// data the Lua side can see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the honest limit of hot updating. A patch can invent any badge out of
    /// the accessors listed here with no C# change at all — but it cannot invent an
    /// accessor, because that needs a build. Keeping the surface explicit makes that
    /// boundary visible instead of surprising.
    /// </para>
    /// <para>
    /// <see cref="Counter"/> is the deliberate escape hatch: a generic key/value
    /// counter live-ops can fill from the server, so a patch can drive a badge from a
    /// value nobody modelled at build time.
    /// </para>
    /// </remarks>
    public sealed class RedDotContext
    {
        private readonly Dictionary<string, int> _counters = new Dictionary<string, int>(StringComparer.Ordinal);

        public RedDotContext(IMailService mail, IQuestService quests, IShopService shop, IClockService clock)
        {
            Mail = mail ?? throw new ArgumentNullException(nameof(mail));
            Quests = quests ?? throw new ArgumentNullException(nameof(quests));
            Shop = shop ?? throw new ArgumentNullException(nameof(shop));
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public IMailService Mail { get; }

        public IQuestService Quests { get; }

        public IShopService Shop { get; }

        public IClockService Clock { get; }

        /// <summary>
        /// Reads a generic named counter. Unknown keys read as zero, so a patch written
        /// against a counter the server has not started sending yet stays dark instead
        /// of erroring on every evaluation.
        /// </summary>
        public int Counter(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return 0;
            }

            return _counters.TryGetValue(key, out var value) ? value : 0;
        }

        public void SetCounter(string key, int value)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Counter key must not be empty.", nameof(key));
            }

            _counters[key] = value;
        }

        public void ClearCounters()
        {
            _counters.Clear();
        }
    }
}
