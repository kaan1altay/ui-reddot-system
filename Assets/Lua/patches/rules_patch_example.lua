--- A live-ops patch: adds a red dot TYPE that did not exist when the client
--- shipped.
---
--- This is the whole hot-update story in one file. Loading it through
--- `RedDotBridge.ReloadRules` introduces a new type, a new rule, a new global
--- dot and a new event subscription. No C# is rebuilt, no scene is reloaded, no
--- binding is lost:
---
---   * `LimitedOffer` is a type the build has never heard of. It is keyless, so
---     the reload's `CreateGlobalRedDots` pass gives it a dot immediately.
---   * The Shop screen already has a button bound to it. Binding a type with no
---     rule is legal -- the dot exists and reads false -- so the view has simply
---     been waiting for content that did not exist yet.
---   * `LimitedOfferStarted` is subscribed on the C# bus as part of the reload's
---     subscription diff. Before this patch it was an event nobody listened to,
---     and it cost a dictionary miss.
---
--- Everything the client shipped with is preserved: the patch copies the base
--- table rather than replacing it, which is what a real patch does when it only
--- means to add something.

local base = require("reddot.RedDotRules")

--- The event the patch introduces. It is declared in the returned spec so boot
--- validation accepts it -- an event name that is neither known nor declared is
--- an error, because a typo there is otherwise invisible.
local LIMITED_OFFER_STARTED = "LimitedOfferStarted"

local rules = {}
for typeName, rule in pairs(base) do
    rules[typeName] = rule
end

rules["LimitedOffer"] = {
    events     = { LIMITED_OFFER_STARTED },
    tracksSeen = true,

    -- The offer is content the client knows nothing about, so there is no typed
    -- accessor to read it. `Game:Counter` is the generic escape hatch: a
    -- key/value store live-ops fills, which is exactly the seam that lets a
    -- patch drive a badge from a value nobody modelled at build time.
    --
    -- Returning nil while the counter is zero means "there is no offer", and a
    -- nil token keeps the dot off. That is the same mechanism a real rule uses
    -- to stay dark until its data has loaded.
    token = function()
        local offer = Game:Counter("shop.limitedOffer")
        if offer <= 0 then
            return nil
        end
        return "offer:" .. offer
    end,

    -- No condition: the whole behaviour lives in tracksSeen. The dot lights up
    -- when a new offer starts, clears when the player opens the shop, and lights
    -- up again on the next one -- with no bookkeeping anywhere.
}

return {
    rules  = rules,
    events = { LIMITED_OFFER_STARTED },
}
