--- The known event names.
---
--- Rules name the events that dirty them, and boot validation rejects any name
--- that is not in this list. That check is the whole reason the file exists: a
--- typo in a rule's `events` is otherwise invisible -- the dot simply never
--- refreshes, and the bug surfaces weeks later as "the badge is sometimes
--- wrong".
---
--- A hot patch may introduce new names; `manager.reloadRules` accepts an extra
--- set of names for exactly that case.

return {
    -- Mail
    MailReceived = "mail.received",
    MailRead     = "mail.read",
    MailClaimed  = "mail.claimed",
    MailDeleted  = "mail.deleted",

    -- Quests
    QuestProgress = "quest.progress",
    QuestClaimed  = "quest.claimed",

    -- Shop
    ShopRefreshed = "shop.refreshed",
    ShopPurchased = "shop.purchased",

    -- Global
    DayRollover = "day.rollover",
}
