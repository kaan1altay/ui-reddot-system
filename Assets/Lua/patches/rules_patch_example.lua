--- A live-ops patch: adds a red dot TYPE the client never shipped, and changes
--- what an existing badge means.
---
--- This is the whole hot-update story in one file. Loading it through
--- `RedDotBridge.ReloadRules` introduces a new type, a new rule, a new global
--- dot, a new event subscription -- and rewrites a rule that shipped. No C# is
--- rebuilt, no scene is reloaded, no binding is lost.
---
--- The second half is the part worth reading. In a parent/child model the lobby
--- Shop button would light up for the new offer by aggregation, for free and
--- without anyone deciding it should. There is no aggregation here: every dot
--- answers its own question, so if the Shop button is to react to the offer,
--- the Shop *rule* has to say so. A patch owns the whole rule table, not just
--- the new entries, so it can say so -- which is a better demonstration than
--- aggregation was. Adding a badge is easy in any model; changing the meaning
--- of one that already shipped, on a Tuesday afternoon, is the thing worth
--- being able to do.

local RedDotType  = require("reddot.RedDotType")
local RedDotEvent = require("reddot.RedDotEvent")
local base        = require("reddot.RedDotRules")

--- The event the patch introduces. It is declared in the returned spec so boot
--- validation accepts it -- an event name that is neither known nor declared is
--- an error, because a typo there is otherwise invisible.
local LIMITED_OFFER_STARTED = "LimitedOfferStarted"

--- Where live-ops puts the offer. `Game:Counter` is the generic escape hatch: a
--- key/value store the server fills, which is the seam that lets a patch drive a
--- badge from a value nobody modelled at build time.
local OFFER_COUNTER = "shop.limitedOffer"

local function offerCount()
    return Game:Counter(OFFER_COUNTER)
end

local rules = {}
for typeName, rule in pairs(base) do
    rules[typeName] = rule
end

--------------------------------------------------------------------------------
-- The new type
--------------------------------------------------------------------------------

rules["LimitedOffer"] = {
    events     = { LIMITED_OFFER_STARTED },
    tracksSeen = true,

    -- A nil token while no offer is running means "there is nothing here", and
    -- keeps the dot off. It is the same mechanism a shipped rule uses to stay
    -- dark until its data has loaded.
    token = function()
        local offer = offerCount()
        if offer <= 0 then
            return nil
        end
        return "offer:" .. offer
    end,

    -- No condition: the whole behaviour lives in tracksSeen. The dot lights when
    -- an offer starts, clears when the player opens the shop, and lights again
    -- on the next one.
}

--------------------------------------------------------------------------------
-- The rewritten one
--------------------------------------------------------------------------------

local shop = base[RedDotType.Shop]

rules[RedDotType.Shop] = {
    -- The shipped events, plus the one the patch introduced. Without this the
    -- Shop dot would never be queued when an offer starts, and the button would
    -- be correct only by accident, on the next shop event that happened along.
    events = {
        RedDotEvent.ShopRefreshed,
        RedDotEvent.ShopPurchased,
        RedDotEvent.DayRollover,
        LIMITED_OFFER_STARTED,
    },

    tracksSeen = true,

    -- Unchanged: a free deal is still a free deal, and midnight is still
    -- midnight. A patch keeps what works.
    condition = shop.condition,
    resetAt   = shop.resetAt,

    -- Changed: the shop's content stamp now includes the running offer, so
    -- starting one makes the shop unseen and the lobby button lights. Opening
    -- the shop stores this token, which is what clears it; the next offer moves
    -- the token again.
    --
    -- With no offer running the stamp is byte-for-byte the shipped one, so
    -- applying the patch on a quiet shop changes nothing the player can see.
    -- A patch that lights a badge merely by being installed would be a patch
    -- nobody trusts.
    token = function()
        local day = Game.Shop:ResetToken()
        local offer = offerCount()
        if offer <= 0 then
            return day
        end
        return day .. "|offer:" .. offer
    end,
}

return {
    rules  = rules,
    events = { LIMITED_OFFER_STARTED },
}
