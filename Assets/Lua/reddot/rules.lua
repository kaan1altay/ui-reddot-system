--- The rule table: the only place that knows what any particular badge means.
---
--- This file is pure data. Each entry maps a leaf path to:
---
---   mode      -- types.MODE_PERSISTENT or types.MODE_TRANSIENT_UNTIL_SEEN
---   triggers  -- event names that make the node dirty. The manager subscribes
---               to exactly this set, so an event nobody names costs nothing.
---   evaluate  -- (ctx) -> number | boolean. Called at most once per flush per
---               node, and only after one of its triggers fired.
---
--- `evaluate` must read the authoritative state from `ctx` rather than from
--- the event: events are signals, not payloads. That is what makes the system
--- safe to batch -- ten "mail.received" events collapse into one read of the
--- real unread count instead of ten increments that can drift.
---
--- Replacing this file (or shadowing it from a patch folder) is the whole
--- hot-update story. No C# needs to change.

local types = require("reddot.types")

return {

    -- Unread mail. The dot is a symptom of the mailbox, so it stays up for
    -- exactly as long as unread mail exists -- opening the tab is not enough.
    [types.MAIL_INBOX] = {
        mode     = types.MODE_PERSISTENT,
        triggers = { "mail.received", "mail.read", "mail.deleted" },
        evaluate = function(ctx)
            return ctx.Mail:UnreadCount()
        end,
    },

    -- Operational notices ("servers down at 04:00"). There is nothing to
    -- clear, so seeing it once is the whole interaction.
    [types.MAIL_SYSTEM] = {
        mode     = types.MODE_TRANSIENT_UNTIL_SEEN,
        triggers = { "mail.systemNoticePosted" },
        evaluate = function(ctx)
            return ctx.Mail:HasSystemNotice()
        end,
    },

    -- Dailies the player can hand in right now. Persistent: the reward is
    -- still waiting after the player has looked at the list.
    [types.QUESTS_DAILY] = {
        mode     = types.MODE_PERSISTENT,
        triggers = { "quest.progress", "quest.claimed", "day.rollover" },
        evaluate = function(ctx)
            return ctx.Quests:CompletableDailyCount()
        end,
    },

    -- Achievements unlocked since the player last opened the tab. Claiming is
    -- optional and never expires, so nagging past the first look is noise.
    [types.QUESTS_ACHIEVEMENTS] = {
        mode     = types.MODE_TRANSIENT_UNTIL_SEEN,
        triggers = { "achievement.unlocked", "day.rollover" },
        evaluate = function(ctx)
            return ctx.Quests:UnclaimedAchievementCount()
        end,
    },

    -- The daily deal rotation. Transient, and the daily refresh trigger makes
    -- the node unseen again, which is exactly the "new stock" behaviour.
    [types.SHOP_DAILY_DEALS] = {
        mode     = types.MODE_TRANSIENT_UNTIL_SEEN,
        triggers = { "shop.dailyDealsRefreshed" },
        evaluate = function(ctx)
            return ctx.Shop:NewDealCount()
        end,
    },
}
