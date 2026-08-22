--- A live-ops patch: adds a badge that did not exist when the client shipped.
---
--- This is the whole hot-update story in one file. Loading it through
--- `RedDotBridge.ReloadRules` introduces a new node, a new rule and a new event
--- subscription. No C# is rebuilt, no scene is reloaded, no binding is lost:
---
---   * `Main.Shop.LimitedOffer` is a node the build has never heard of. The tree
---     module creates it under the existing `Main.Shop` parent automatically.
---   * `Main.Shop` aggregates it with the `any` policy, so the Shop tab on the
---     main screen lights up through a child whose name is not in any C# file.
---   * The event `LimitedOfferStarted` is subscribed on the C# bus as part of
---     the reload's subscription diff. Before this patch it was an event nobody
---     listened to and it cost a dictionary miss.
---
--- Everything the rules already shipped with is preserved: the patch copies the
--- base table rather than replacing it, which is what a real patch does when it
--- only means to add something.

local types = require("reddot.types")
local base  = require("reddot.rules")

local rules = {}
for path, rule in pairs(base) do
    rules[path] = rule
end

rules["Main.Shop.LimitedOffer"] = {
    mode     = types.MODE_TRANSIENT_UNTIL_SEEN,
    triggers = { "LimitedOfferStarted" },

    evaluate = function(ctx)
        -- The offer is content the client knows nothing about, so there is no
        -- typed accessor to read. `ctx:Counter` is the generic escape hatch:
        -- if live-ops starts sending a number for this key the badge shows it,
        -- and until then the badge is a plain "there is something new here".
        --
        -- Returning `true` rather than a count is deliberate. The whole
        -- behaviour of this badge lives in TransientUntilSeen: it lights up
        -- when the patch lands, clears when the player looks at the shop, and
        -- lights up again every time `LimitedOfferStarted` fires.
        local count = ctx:Counter("shop.limitedOffer")
        if count > 0 then
            return count
        end
        return true
    end,
}

return {
    -- Declared additively: the manager merges this with the shipped node list,
    -- so a patch can extend the tree but never delete a live branch.
    nodes = {
        { path = "Main.Shop.LimitedOffer" },
    },
    rules = rules,
}
