using System;

namespace RedDot.Demo
{
    /// <summary>
    /// Stand-in game managers.
    /// </summary>
    /// <remarks>
    /// The point of the sample is the red dot system, not a mail client, so these hold
    /// a couple of integers and raise the same events a real manager would. They are
    /// what the EditMode tests drive, and what the demo scene's buttons poke in later
    /// slices — one implementation for both, so the tests exercise the same objects the
    /// demo does.
    /// </remarks>
    public sealed class FakeMailService : IMailService
    {
        private readonly Events.EventBus _bus;

        public FakeMailService(Events.EventBus bus = null)
        {
            _bus = bus;
        }

        public int Unread { get; private set; }

        public bool SystemNotice { get; private set; }

        public int UnreadCount() => Unread;

        public bool HasSystemNotice() => SystemNotice;

        public void Receive(int count = 1)
        {
            if (count <= 0)
            {
                return;
            }

            Unread += count;
            Publish("mail.received");
        }

        public void Read(int count = 1)
        {
            if (count <= 0 || Unread == 0)
            {
                return;
            }

            Unread = Math.Max(0, Unread - count);
            Publish("mail.read");
        }

        public void ReadAll()
        {
            if (Unread == 0)
            {
                return;
            }

            Unread = 0;
            Publish("mail.read");
        }

        public void PostSystemNotice()
        {
            SystemNotice = true;
            Publish("mail.systemNoticePosted");
        }

        public void ClearSystemNotice()
        {
            SystemNotice = false;
            Publish("mail.systemNoticePosted");
        }

        private void Publish(string eventName) => _bus?.Publish(eventName);
    }

    public sealed class FakeQuestService : IQuestService
    {
        private readonly Events.EventBus _bus;

        public FakeQuestService(Events.EventBus bus = null)
        {
            _bus = bus;
        }

        public int CompletableDailies { get; private set; }

        public int UnclaimedAchievements { get; private set; }

        public int CompletableDailyCount() => CompletableDailies;

        public int UnclaimedAchievementCount() => UnclaimedAchievements;

        public void CompleteDaily(int count = 1)
        {
            if (count <= 0)
            {
                return;
            }

            CompletableDailies += count;
            Publish("quest.progress");
        }

        public void ClaimDaily(int count = 1)
        {
            if (count <= 0 || CompletableDailies == 0)
            {
                return;
            }

            CompletableDailies = Math.Max(0, CompletableDailies - count);
            Publish("quest.claimed");
        }

        public void UnlockAchievement(int count = 1)
        {
            if (count <= 0)
            {
                return;
            }

            UnclaimedAchievements += count;
            Publish("achievement.unlocked");
        }

        public void ClaimAchievement(int count = 1)
        {
            if (count <= 0 || UnclaimedAchievements == 0)
            {
                return;
            }

            UnclaimedAchievements = Math.Max(0, UnclaimedAchievements - count);
            Publish("achievement.unlocked");
        }

        /// <summary>Midnight rollover: dailies reset and the achievement badge gets another go.</summary>
        public void RollOverDay()
        {
            CompletableDailies = 0;
            Publish("day.rollover");
        }

        private void Publish(string eventName) => _bus?.Publish(eventName);
    }

    public sealed class FakeShopService : IShopService
    {
        private readonly Events.EventBus _bus;

        public FakeShopService(Events.EventBus bus = null)
        {
            _bus = bus;
        }

        public int NewDeals { get; private set; }

        public int NewDealCount() => NewDeals;

        public void RefreshDailyDeals(int newDeals)
        {
            NewDeals = Math.Max(0, newDeals);
            Publish("shop.dailyDealsRefreshed");
        }

        private void Publish(string eventName) => _bus?.Publish(eventName);
    }
}
