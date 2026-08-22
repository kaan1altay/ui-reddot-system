--- Shared vocabulary of the red dot system: node paths, aggregation policies
--- and rule modes.
---
--- Everything else in the system refers to nodes by their path string, which is
--- what makes the whole thing hot-updatable: a Lua patch can introduce a new
--- path without any C# ever learning about it, and existing bindings keep
--- working because they hold a path, not an object.

local M = {}

--------------------------------------------------------------------------------
-- Node paths
--------------------------------------------------------------------------------

M.MAIN                = "Main"

M.MAIL                = "Main.Mail"
M.MAIL_INBOX          = "Main.Mail.Inbox"
M.MAIL_SYSTEM         = "Main.Mail.System"

M.QUESTS              = "Main.Quests"
M.QUESTS_DAILY        = "Main.Quests.Daily"
M.QUESTS_ACHIEVEMENTS = "Main.Quests.Achievements"

M.SHOP                = "Main.Shop"
M.SHOP_DAILY_DEALS    = "Main.Shop.DailyDeals"

--------------------------------------------------------------------------------
-- Aggregation policies
--
-- A parent node is visible whenever any of its children is visible, whatever
-- the policy. The policy only decides which *number* the parent shows:
--
--   sum -- add the visible children's counts (an inbox-style total)
--   max -- show the largest visible child count (one representative number)
--   any -- show no number at all, just the dot
--------------------------------------------------------------------------------

M.POLICY_SUM = "sum"
M.POLICY_MAX = "max"
M.POLICY_ANY = "any"

M.DEFAULT_POLICY = M.POLICY_SUM

M.POLICIES = {
    [M.POLICY_SUM] = true,
    [M.POLICY_MAX] = true,
    [M.POLICY_ANY] = true,
}

--------------------------------------------------------------------------------
-- Rule modes
--
--   Persistent         -- the badge is visible for exactly as long as the
--                         condition holds. Marking it seen does nothing; the
--                         player has to actually deal with the mails/quests.
--   TransientUntilSeen -- the badge is visible until the player looks at the
--                         node. A later trigger event makes it unseen again,
--                         so "new stuff arrived" lights it up a second time.
--------------------------------------------------------------------------------

M.MODE_PERSISTENT           = "Persistent"
M.MODE_TRANSIENT_UNTIL_SEEN = "TransientUntilSeen"

M.MODES = {
    [M.MODE_PERSISTENT]           = true,
    [M.MODE_TRANSIENT_UNTIL_SEEN] = true,
}

--------------------------------------------------------------------------------
-- The declared tree
--
-- Only the interior nodes really need to be declared, because leaves are
-- created implicitly by the rules that target them. They are listed anyway so
-- that the intended shape of the UI is readable in one place, and so that a
-- node can carry a non-default policy.
--------------------------------------------------------------------------------

M.nodes = {
    { path = M.MAIN,                policy = M.POLICY_SUM },

    { path = M.MAIL,                policy = M.POLICY_SUM },
    { path = M.MAIL_INBOX },
    { path = M.MAIL_SYSTEM },

    -- The quests tab shows the most urgent single number rather than a total,
    -- because "12" across two unrelated lists reads as noise.
    { path = M.QUESTS,              policy = M.POLICY_MAX },
    { path = M.QUESTS_DAILY },
    { path = M.QUESTS_ACHIEVEMENTS },

    -- The shop tab is a plain dot: "there is something new in here".
    { path = M.SHOP,                policy = M.POLICY_ANY },
    { path = M.SHOP_DAILY_DEALS },
}

return M
