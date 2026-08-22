using System;
using System.Collections.Generic;

namespace RedDot
{
    /// <summary>Mail data a red dot rule is allowed to look at.</summary>
    public interface IMailService
    {
        int UnreadCount();

        bool HasSystemNotice();
    }

    /// <summary>Quest and achievement data a red dot rule is allowed to look at.</summary>
    public interface IQuestService
    {
        int CompletableDailyCount();

        int UnclaimedAchievementCount();
    }

    /// <summary>Shop data a red dot rule is allowed to look at.</summary>
    public interface IShopService
    {
        int NewDealCount();
    }

    /// <summary>
    /// The <c>ctx</c> every rule's <c>evaluate</c> receives: the complete surface of game
    /// data the Lua side can read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the real boundary of the hot-update story. A Lua patch can invent any
    /// badge it likes out of the accessors listed here, with no C# change at all — but
    /// it cannot invent a new accessor, because that would need a new build. Keeping
    /// the surface explicit is what makes that limit visible instead of surprising.
    /// </para>
    /// <para>
    /// <see cref="Counter"/> is the deliberate escape hatch: a generic key/value counter
    /// that live-ops can fill from the server, so a patch can drive a badge from a value
    /// nobody modelled at build time.
    /// </para>
    /// </remarks>
    public sealed class RedDotContext
    {
        private readonly Dictionary<string, int> _counters = new Dictionary<string, int>(StringComparer.Ordinal);

        public RedDotContext(IMailService mail, IQuestService quests, IShopService shop)
        {
            Mail = mail ?? throw new ArgumentNullException(nameof(mail));
            Quests = quests ?? throw new ArgumentNullException(nameof(quests));
            Shop = shop ?? throw new ArgumentNullException(nameof(shop));
        }

        public IMailService Mail { get; }

        public IQuestService Quests { get; }

        public IShopService Shop { get; }

        /// <summary>
        /// Reads a generic named counter. Unknown keys read as zero, so a patch written
        /// against a counter the server has not started sending yet simply stays dark
        /// instead of erroring on every flush.
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
