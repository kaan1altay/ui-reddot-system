--- The dictionary of red dot type names.
---
--- A concrete dot is identified by a **type plus its ordered key values**, joined
--- with `|`:
---
---     "Shop"              -- a global dot: no keys, one instance, alive from boot
---     "MailItem|42"       -- one keyed dot per mail
---     "QuestItem|3|17"    -- two keys, in the order the rule declares them
---
--- That string is the registry key. It is the only identifier that crosses the
--- Lua/C# boundary, which is what lets a view bind to a dot whose rule does not
--- exist yet -- and lets a hot patch introduce a type the build never heard of.

return {
    -- Global types: keyless, created at boot, alive for the whole session.
    Mail   = "Mail",
    Quests = "Quests",
    Shop   = "Shop",

    -- Keyed types: created on first subscribe, destroyed with the last one.
    MailItem  = "MailItem",  -- keys: mailId
    QuestItem = "QuestItem", -- keys: chapterId, questId
}
