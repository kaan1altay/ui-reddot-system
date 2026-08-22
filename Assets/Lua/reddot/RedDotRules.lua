--- The rule table: the only file that knows what any particular badge means.
---
--- One entry per type. Each rule may declare:
---
---   keys        list of key *names*, in order. Absent (nil) means a global dot:
---               one instance, created at boot, alive for the whole session.
---   condition   (k1, ..., kn [, isUnseen]) -> boolean. The predicate. Receives
---               the dot's key values in the declared order, and `isUnseen` as a
---               final argument when the rule also tracks seen state.
---   tracksSeen  true when the player looking at the thing should turn it off.
---   token       (k1, ..., kn) -> string. A stamp of the current content. Seen
---               state stores the token that was current when the player looked,
---               so *new* content turns the dot back on by itself. A nil token
---               means "the data has not loaded yet" and the dot stays off.
---   resetAt     () -> unix seconds. A scheduled re-evaluation, for dots that
---               change on a clock rather than on an event.
---   events      the event names that dirty this type.
---
--- Value computation, in full:
---
---   no tracksSeen    ->  condition(keys...)
---   tracksSeen       ->  isUnseen = storedToken ~= currentToken
---                        no condition  ->  isUnseen
---                        condition     ->  condition(keys..., isUnseen)
---
--- `Game` is the global set by the bridge: the complete surface of game data a
--- rule is allowed to read. A rule cannot reach anything else, which is what
--- keeps the limit of hot updating visible -- a patch can invent any badge out
--- of these accessors, but it cannot invent an accessor.
---
--- Every condition and token call is wrapped in pcall. A rule that throws logs
--- once and reads as false; it never takes the rest of the UI down.

local RedDotType  = require("reddot.RedDotType")
local RedDotEvent = require("reddot.RedDotEvent")

--------------------------------------------------------------------------------
-- Shared event sets
--
-- Named once and referenced by every rule that cares, so that adding an event to
-- a domain reaches all of its dots instead of the one somebody remembered.
--------------------------------------------------------------------------------

local MAIL_EVENTS = {
    RedDotEvent.MailReceived,
    RedDotEvent.MailRead,
    RedDotEvent.MailClaimed,
    RedDotEvent.MailDeleted,
}

local QUEST_EVENTS = {
    RedDotEvent.QuestProgress,
    RedDotEvent.QuestClaimed,
    RedDotEvent.DayRollover,
}

local SHOP_EVENTS = {
    RedDotEvent.ShopRefreshed,
    RedDotEvent.ShopPurchased,
    RedDotEvent.DayRollover,
}

--------------------------------------------------------------------------------

return {

    --------------------------------------------------------------------------
    -- Global dots. Each has its own condition: the Mail button is not "the sum
    -- of what is inside Mail", it is its own question about the mailbox. That is
    -- what lets it be correct on the very first frame of the lobby, before the
    -- mail screen has ever been opened and before a single MailItem dot exists.
    --------------------------------------------------------------------------

    [RedDotType.Mail] = {
        events     = MAIL_EVENTS,
        tracksSeen = true,

        -- Stamp of the newest mail. When one arrives the stamp moves, the stored
        -- seen token no longer matches, and the button lights up again.
        token = function()
            return Game.Mail:InboxToken()
        end,

        condition = function(isUnseen)
            return isUnseen or Game.Mail:ActionableCount() > 0
        end,
    },

    [RedDotType.Quests] = {
        events     = QUEST_EVENTS,
        tracksSeen = true,

        token = function()
            return Game.Quests:BoardToken()
        end,

        condition = function(isUnseen)
            return isUnseen or Game.Quests:ClaimableCount() > 0
        end,
    },

    [RedDotType.Shop] = {
        events     = SHOP_EVENTS,
        tracksSeen = true,

        -- The shop's token is its reset stamp, so every daily rotation counts as
        -- new content even when the stock happens to look the same.
        token = function()
            return Game.Shop:ResetToken()
        end,

        condition = function(isUnseen)
            return isUnseen or Game.Shop:HasFreeDeal()
        end,

        -- Nothing raises an event at midnight, so the dot asks to be woken.
        resetAt = function()
            return Game.Clock:NextDayBoundary()
        end,
    },

    --------------------------------------------------------------------------
    -- Keyed dots. One live instance per row on screen, created when the row
    -- binds and destroyed when it goes away.
    --------------------------------------------------------------------------

    [RedDotType.MailItem] = {
        keys   = { "mailId" },
        events = MAIL_EVENTS,

        -- No seen tracking: whether a mail is unread is real game state, not a
        -- UI flag. Reading it raises mail.read and the dot goes off by itself.
        condition = function(mailId)
            return Game.Mail:IsActionable(mailId)
        end,
    },

    [RedDotType.QuestItem] = {
        keys   = { "chapterId", "questId" },
        events = QUEST_EVENTS,

        condition = function(chapterId, questId)
            return Game.Quests:IsClaimable(chapterId, questId)
        end,
    },
}
