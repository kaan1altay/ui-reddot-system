--- The red dot engine.
---
--- Life cycle of a badge, end to end:
---
---   1. Something happens in the game and the host raises an event.
---   2. `dispatch` looks the event up in the trigger index and marks the leaves
---      that named it dirty. Nothing is evaluated yet.
---   3. Once per frame the host calls `flush`. Every dirty leaf is evaluated
---      exactly once, aggregates bubble up deepest-first, and only nodes whose
---      state actually changed are handed to the listeners.
---
--- There is no polling anywhere: a flush with an empty dirty set does no work
--- at all and notifies nobody.

local types    = require("reddot.types")
local tree_mod = require("reddot.tree")
local seen_mod = require("reddot.seen_store")

local M = {}

local Manager = {}
Manager.__index = Manager

local HIDDEN = { visible = false, count = 0 }

--------------------------------------------------------------------------------
-- Helpers
--------------------------------------------------------------------------------

--- Rules may return a count, a boolean, or nothing at all. Everything lands on
--- a non-negative integer so that a rule returning `true` and a rule returning
--- `1` are indistinguishable downstream.
local function normalizeCount(value)
    if value == nil or value == false then return 0 end
    if value == true then return 1 end

    local n = tonumber(value)
    if not n then return 0 end
    n = math.floor(n)
    if n < 0 then n = 0 end
    return n
end

local function sameState(a, b)
    return a.visible == b.visible and a.count == b.count
end

local function validateRules(rules)
    if type(rules) ~= "table" then
        error("reddot.manager: the rule table must be a table, got " .. type(rules), 0)
    end
    for path, rule in pairs(rules) do
        if type(path) ~= "string" then
            error("reddot.manager: rule keys must be node paths, got a " .. type(path), 0)
        end
        if type(rule) ~= "table" then
            error("reddot.manager: rule for " .. path .. " must be a table, got " .. type(rule), 0)
        end
        if not types.MODES[rule.mode] then
            error("reddot.manager: rule for " .. path .. " has unknown mode " .. tostring(rule.mode), 0)
        end
        if type(rule.evaluate) ~= "function" then
            error("reddot.manager: rule for " .. path .. " has no evaluate function", 0)
        end
        if rule.triggers ~= nil and type(rule.triggers) ~= "table" then
            error("reddot.manager: rule for " .. path .. " has a non-list triggers field", 0)
        end
    end
end

--------------------------------------------------------------------------------
-- Construction
--------------------------------------------------------------------------------

--- opts:
---   nodes       -- declared node list (defaults to types.nodes)
---   rules       -- rule table (defaults to an empty system)
---   bus         -- object with :Subscribe(event) / :Unsubscribe(event). The
---                  manager only registers interest; the host is responsible
---                  for calling `dispatch` when a subscribed event fires.
---   ctx         -- game data accessors handed to every evaluate()
---   seenBackend -- persistence backend for seen_store (optional)
---   log         -- function(message) used for rule failures (optional)
function M.new(opts)
    opts = opts or {}

    local self = setmetatable({
        bus          = opts.bus,
        ctx          = opts.ctx,
        log          = opts.log or function() end,
        baseNodes    = opts.nodes or types.nodes,
        seen         = seen_mod.new(opts.seenBackend),

        rules        = {},
        tree         = nil,
        state        = {},     -- path -> { visible, count }
        triggerIndex = {},     -- event name -> { path -> true }
        subscribed   = {},     -- event name -> true
        listeners    = {},     -- ordered list of function(path, state)

        dirty        = {},     -- path -> true, leaves awaiting evaluation
        dirtyCount   = 0,

        stats        = {
            flushes         = 0,
            leafEvaluations = 0,
            aggregations    = 0,
            notifications   = 0,
            dispatches      = 0,
        },
    }, Manager)

    self:_applyRules(opts.rules or {}, self.baseNodes)
    -- One bootstrap pass so the tree starts consistent with the game state.
    -- This is the only evaluation that is not caused by an event.
    self:_evaluateAll()

    return self
end

--------------------------------------------------------------------------------
-- Listeners
--------------------------------------------------------------------------------

function Manager:addListener(fn)
    table.insert(self.listeners, fn)
    return fn
end

function Manager:removeListener(fn)
    for i = #self.listeners, 1, -1 do
        if self.listeners[i] == fn then
            table.remove(self.listeners, i)
            return true
        end
    end
    return false
end

--------------------------------------------------------------------------------
-- Rules and subscriptions
--------------------------------------------------------------------------------

--- Rebuilds the tree and the trigger index, then reconciles the event
--- subscriptions with the bus. Existing state is left untouched: the caller
--- decides when to re-evaluate, which is what lets `reloadRules` report only
--- the badges that genuinely moved.
function Manager:_applyRules(rules, nodeDefs)
    validateRules(rules)

    local tree = tree_mod.build(nodeDefs or self.baseNodes)
    tree:attachRules(rules)

    local triggerIndex = {}
    for path, rule in pairs(rules) do
        for _, event in ipairs(rule.triggers or {}) do
            local paths = triggerIndex[event]
            if not paths then
                paths = {}
                triggerIndex[event] = paths
            end
            paths[path] = true
        end
    end

    self.rules        = rules
    self.tree         = tree
    self.triggerIndex = triggerIndex

    self:_syncSubscriptions()
    self:_pruneDirty()
end

--- Subscribes to events that gained their first rule and unsubscribes from
--- events that lost their last one. Events that survive a reload are never
--- touched, so a patch that only edits one rule does not churn the bus.
function Manager:_syncSubscriptions()
    if not self.bus then
        return 0, 0
    end

    local added, removed = 0, 0

    for event in pairs(self.subscribed) do
        if not self.triggerIndex[event] then
            self.bus:Unsubscribe(event)
            self.subscribed[event] = nil
            removed = removed + 1
        end
    end

    for event in pairs(self.triggerIndex) do
        if not self.subscribed[event] then
            self.bus:Subscribe(event)
            self.subscribed[event] = true
            added = added + 1
        end
    end

    return added, removed
end

--- Drops pending work for nodes a reload removed.
function Manager:_pruneDirty()
    for path in pairs(self.dirty) do
        if not self.tree:get(path) then
            self.dirty[path] = nil
            self.dirtyCount = self.dirtyCount - 1
        end
    end
end

--- Replaces the rule table wholesale and re-evaluates everything.
---
--- `spec` is either a bare rule table or `{ nodes = {...}, rules = {...} }`,
--- which is what a hot-update patch returns when it also needs to introduce
--- interior nodes or override an aggregation policy.
---
--- Returns the number of nodes whose state changed.
function Manager:reloadRules(spec)
    local rules, nodeDefs = spec, nil
    if type(spec) == "table" and spec.rules ~= nil then
        rules    = spec.rules
        nodeDefs = spec.nodes
    end

    if nodeDefs then
        -- Declared nodes are additive: a patch extends the shipped tree rather
        -- than replacing it, so it cannot accidentally delete a live branch.
        local merged = {}
        for _, def in ipairs(self.baseNodes) do merged[#merged + 1] = def end
        for _, def in ipairs(nodeDefs) do merged[#merged + 1] = def end
        nodeDefs = merged
    end

    self:_applyRules(rules, nodeDefs)
    return self:_evaluateAll()
end

--------------------------------------------------------------------------------
-- Events
--------------------------------------------------------------------------------

--- Called by the host for every subscribed event. Marks the interested leaves
--- dirty and returns how many were marked; does not evaluate anything.
---
--- `payload` is accepted and ignored on purpose -- see the note in rules.lua
--- about events being signals rather than data.
function Manager:dispatch(eventName, payload) -- luacheck: ignore payload
    self.stats.dispatches = self.stats.dispatches + 1

    local paths = self.triggerIndex[eventName]
    if not paths then
        return 0
    end

    local marked = 0
    for path in pairs(paths) do
        local rule = self.rules[path]
        if rule and rule.mode == types.MODE_TRANSIENT_UNTIL_SEEN then
            -- A trigger means "something new happened here", which is exactly
            -- what un-sees a transient node.
            self.seen:set(path, false)
        end
        if self:markDirty(path) then
            marked = marked + 1
        end
    end
    return marked
end

--- Returns true when the node was not already dirty. Marking the same node
--- from a hundred events in one frame still yields one evaluation.
function Manager:markDirty(path)
    if not self.tree:get(path) then
        return false
    end
    if self.dirty[path] then
        return false
    end
    self.dirty[path] = true
    self.dirtyCount = self.dirtyCount + 1
    return true
end

--- Records that the player has looked at `path`. Marking an interior node
--- marks its whole subtree, because opening the Mail tab does clear the
--- transient badges inside it.
---
--- Persistent nodes ignore this entirely; that is the difference between the
--- two modes.
function Manager:markSeen(path)
    local node = self.tree:get(path)
    if not node then
        return 0
    end

    local marked = 0
    local stack = { path }
    while #stack > 0 do
        local current = table.remove(stack)
        local rule = self.rules[current]
        if rule and rule.mode == types.MODE_TRANSIENT_UNTIL_SEEN then
            if self.seen:set(current, true) then
                self:markDirty(current)
                marked = marked + 1
            end
        end
        for _, child in ipairs(self.tree:get(current).children) do
            stack[#stack + 1] = child
        end
    end
    return marked
end

--------------------------------------------------------------------------------
-- Evaluation
--------------------------------------------------------------------------------

function Manager:_evaluateLeaf(path)
    self.stats.leafEvaluations = self.stats.leafEvaluations + 1

    local rule = self.rules[path]
    if not rule then
        -- A node whose rule a reload removed: it has nothing to say any more.
        return HIDDEN
    end

    local ok, raw = pcall(rule.evaluate, self.ctx)
    if not ok then
        -- One broken rule must not take the rest of the UI down with it.
        self.log("reddot: rule for " .. path .. " failed: " .. tostring(raw))
        return HIDDEN
    end

    local count = normalizeCount(raw)

    if rule.mode == types.MODE_TRANSIENT_UNTIL_SEEN and self.seen:isSeen(path) then
        return HIDDEN
    end

    return { visible = count > 0, count = count }
end

--- A parent is visible whenever any child is visible; the policy only decides
--- the number it shows.
function Manager:_aggregate(path)
    self.stats.aggregations = self.stats.aggregations + 1

    local node    = self.tree:get(path)
    local policy  = node.policy
    local visible = false
    local count   = 0

    for _, child in ipairs(node.children) do
        local childState = self.state[child] or HIDDEN
        if childState.visible then
            visible = true
            if policy == types.POLICY_SUM then
                count = count + childState.count
            elseif policy == types.POLICY_MAX then
                if childState.count > count then
                    count = childState.count
                end
            else
                -- POLICY_ANY: a dot, never a number.
                break
            end
        end
    end

    if not visible then
        return HIDDEN
    end
    return { visible = true, count = count }
end

--- Writes `newState` and records the change. Returns true when it differed.
function Manager:_commit(path, newState, changed)
    local previous = self.state[path] or HIDDEN
    if sameState(previous, newState) then
        return false
    end
    self.state[path] = newState
    changed[path] = newState
    return true
end

--- Recomputes ancestors, deepest first, stopping on any branch whose aggregate
--- did not move. `pending` is a set of paths to reconsider.
function Manager:_bubble(pending, changed)
    local byDepth, deepest = {}, -1

    local function schedule(path)
        local node = self.tree:get(path)
        if not node then return end
        local level = byDepth[node.depth]
        if not level then
            level = {}
            byDepth[node.depth] = level
        end
        level[path] = true
        if node.depth > deepest then
            deepest = node.depth
        end
    end

    for path in pairs(pending) do
        schedule(path)
    end

    for depth = deepest, 0, -1 do
        local level = byDepth[depth]
        if level then
            for path in pairs(level) do
                if self:_commit(path, self:_aggregate(path), changed) then
                    local parent = self.tree:get(path).parent
                    if parent then
                        schedule(parent) -- strictly shallower, so still ahead of us
                    end
                end
            end
        end
    end
end

--- Notifies listeners about the nodes in `changed`. Deepest first and then
--- alphabetical, so a view that reacts to a leaf has already been told about
--- it by the time its container updates, and so test output is stable.
function Manager:_notify(changed)
    local paths = {}
    for path in pairs(changed) do
        paths[#paths + 1] = path
    end
    if #paths == 0 then
        return 0
    end

    local tree = self.tree
    table.sort(paths, function(a, b)
        local na, nb = tree:get(a), tree:get(b)
        local da = na and na.depth or -1
        local db = nb and nb.depth or -1
        if da ~= db then return da > db end
        return a < b
    end)

    for _, path in ipairs(paths) do
        local state = changed[path]
        self.stats.notifications = self.stats.notifications + 1
        for _, listener in ipairs(self.listeners) do
            local ok, err = pcall(listener, path, state)
            if not ok then
                self.log("reddot: listener for " .. path .. " failed: " .. tostring(err))
            end
        end
    end

    return #paths
end

--- Evaluates every dirty leaf once, bubbles the aggregates and notifies the
--- nodes that actually moved. Returns the number of changed nodes.
---
--- Doing nothing is free: an empty dirty set returns immediately without
--- touching a single rule.
function Manager:flush()
    if self.dirtyCount == 0 then
        return 0
    end

    self.stats.flushes = self.stats.flushes + 1

    local dirty = self.dirty
    self.dirty      = {}
    self.dirtyCount = 0

    local changed, pending = {}, {}
    for path in pairs(dirty) do
        if self:_commit(path, self:_evaluateLeaf(path), changed) then
            local parent = self.tree:get(path).parent
            if parent then
                pending[parent] = true
            end
        end
    end

    self:_bubble(pending, changed)
    self:_notify(changed)

    local total = 0
    for _ in pairs(changed) do total = total + 1 end
    return total
end

--- Full re-evaluation. Used once at startup and again after a rule reload,
--- where the whole tree has to be reconsidered but only genuine differences
--- should reach the views.
function Manager:_evaluateAll()
    local changed = {}

    -- Nodes the current tree no longer contains: clear them so bound views can
    -- reset instead of keeping a badge that nothing owns any more.
    for path, state in pairs(self.state) do
        if not self.tree:get(path) and state.visible then
            self.state[path] = HIDDEN
            changed[path] = HIDDEN
        end
    end

    for _, path in ipairs(self.tree:pathsByDepthDesc()) do
        if self.tree:isLeaf(path) then
            self:_commit(path, self:_evaluateLeaf(path), changed)
        else
            self:_commit(path, self:_aggregate(path), changed)
        end
    end

    self.dirty      = {}
    self.dirtyCount = 0

    self:_notify(changed)

    local total = 0
    for _ in pairs(changed) do total = total + 1 end
    return total
end

--------------------------------------------------------------------------------
-- Queries
--------------------------------------------------------------------------------

function Manager:getState(path)
    local state = self.state[path]
    if not state then
        return false, 0
    end
    return state.visible, state.count
end

function Manager:isVisible(path)
    local visible = self:getState(path)
    return visible
end

function Manager:subscribedEvents()
    local events = {}
    for event in pairs(self.subscribed) do
        events[#events + 1] = event
    end
    table.sort(events)
    return events
end

function Manager:paths()
    local paths = {}
    for path in pairs(self.tree.nodes) do
        paths[#paths + 1] = path
    end
    table.sort(paths)
    return paths
end

function Manager:resetStats()
    for key in pairs(self.stats) do
        self.stats[key] = 0
    end
end

--- Machine-readable snapshot: "path=visible:count;..." sorted by path.
--- Deliberately a flat string so the host can read the whole tree in one call
--- without marshalling tables across the Lua/C# boundary.
function Manager:dumpStates()
    local parts = {}
    for _, path in ipairs(self:paths()) do
        local visible, count = self:getState(path)
        parts[#parts + 1] = path .. "=" .. (visible and "1" or "0") .. ":" .. count
    end
    return table.concat(parts, ";")
end

--- Human-readable snapshot for the console and for the demo's debug panel.
function Manager:debugDump()
    local lines = { "red dot tree (" .. self.tree.count .. " nodes)" }

    local function walk(path, indent)
        local node = self.tree:get(path)
        local visible, count = self:getState(path)
        local rule = self.rules[path]

        local kind
        if rule then
            kind = "rule " .. rule.mode
        else
            kind = "aggregate " .. node.policy
        end

        local badge
        if not visible then
            badge = "-"
        elseif count > 0 then
            badge = "* " .. count
        else
            badge = "*"
        end

        lines[#lines + 1] = string.format("%s%-22s %-30s %s",
            string.rep("  ", indent), node.name, "[" .. kind .. "]", badge)

        for _, child in ipairs(node.children) do
            walk(child, indent + 1)
        end
    end

    for _, root in ipairs(self.tree.roots) do
        walk(root, 1)
    end

    local seen = self.seen:paths()
    lines[#lines + 1] = "seen: " .. (#seen > 0 and table.concat(seen, ", ") or "(none)")
    lines[#lines + 1] = string.format(
        "stats: flushes=%d leafEvaluations=%d aggregations=%d notifications=%d dispatches=%d",
        self.stats.flushes, self.stats.leafEvaluations, self.stats.aggregations,
        self.stats.notifications, self.stats.dispatches)

    return table.concat(lines, "\n")
end

return M
