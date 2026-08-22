--- The red dot engine.
---
--- A dot is a **type plus its ordered key values**, and its registry key is those
--- joined with `|`: `"Shop"`, `"MailItem|42"`, `"QuestItem|3|17"`. Every dot
--- answers its own question through its rule's `condition`; no dot is defined as
--- the sum of other dots. That is the whole architectural bet, and it buys two
--- things a parent/child aggregation cannot:
---
---   * A lobby button is correct on the first frame, before the screen behind it
---     has ever been opened. Aggregation needs the children to exist first.
---   * Rows can come and go freely. A keyed dot is created when a row binds and
---     destroyed when the last one unbinds, so a mail list of 500 costs 500 dots
---     while it is open and none afterwards.
---
--- Two lifecycles:
---
---   Global (`keys = nil`)  created by CreateGlobalRedDots at boot, alive for
---                          the session, never destroyed.
---   Keyed  (`keys = {..}`) created on first Subscribe, destroyed with the last
---                          unsubscribe.
---
--- Events never compute anything. They queue the live dots of the affected types
--- into a pending set; one drain per frame computes each queued dot at most
--- once, compares against the cached value, and notifies only on a real change.
--- Fifty events in a frame cost one computation per dot.
---
--- Subscribe is the exception, and deliberately so: it computes synchronously
--- and hands the subscriber the value before returning. A row that binds
--- mid-frame must be right immediately, not after the next event.

local json     = require("reddot.json")
local seen_mod = require("reddot.seen_store")

local M = {}

local Manager = {}
Manager.__index = Manager

local unpack = table.unpack or unpack

--- How often the reconcile checker sweeps, in clock seconds.
M.RECONCILE_INTERVAL = 1

--------------------------------------------------------------------------------
-- Registry keys
--------------------------------------------------------------------------------

--- Integers must not pick up a decimal point on the way into a key: Lua 5.3
--- prints 42.0 for a float-typed 42, and "MailItem|42.0" would be a different
--- dot from "MailItem|42" forever after.
local function keyPart(value)
    if type(value) == "number" and value % 1 == 0 then
        return string.format("%d", value)
    end
    return tostring(value)
end

--- Joins a type and its key values into the registry key.
function M.BuildKey(typeName, keys, count)
    count = count or (keys and #keys) or 0
    if count == 0 then
        return typeName
    end

    local parts = { typeName }
    for i = 1, count do
        parts[i + 1] = keyPart(keys[i])
    end
    return table.concat(parts, "|")
end

--------------------------------------------------------------------------------
-- Defensive list iteration
--------------------------------------------------------------------------------

--- Collects a list's values through `pairs`, sorted by index.
---
--- `#list` stops at the first hole, so a rule table with a nil in the middle of
--- its `events` would silently lose everything after it -- and the dot would
--- quietly stop refreshing. Reading every numeric key instead means a hole costs
--- nothing.
local function entriesOf(list)
    if type(list) ~= "table" then
        return {}
    end

    local indices = {}
    for index in pairs(list) do
        if type(index) == "number" then
            indices[#indices + 1] = index
        end
    end
    table.sort(indices)

    local out = {}
    for i = 1, #indices do
        out[#out + 1] = list[indices[i]]
    end
    return out
end

M.entriesOf = entriesOf

--------------------------------------------------------------------------------
-- Rule validation
--------------------------------------------------------------------------------

--- Checks the rule table against the four ways it can be wrong on its own terms,
--- and returns a list of `{ level, message }`.
---
--- This runs at boot and on every reload. All four failures are silent at
--- runtime -- the dot is simply always off, or never refreshes -- so catching
--- them at load is the difference between a five-second fix and a bug report
--- three weeks later.
function M.ValidateRules(rules, knownEvents)
    local problems = {}

    local function report(level, message)
        problems[#problems + 1] = { level = level, message = message }
    end

    if type(rules) ~= "table" then
        report("error", "the rule table must be a table, got " .. type(rules))
        return problems
    end

    for typeName, rule in pairs(rules) do
        if type(typeName) ~= "string" then
            report("error", "rule keys must be type names, got a " .. type(typeName))
        elseif type(rule) ~= "table" then
            report("error", "rule '" .. typeName .. "' must be a table, got " .. type(rule))
        else
            if rule.token ~= nil and not rule.tracksSeen then
                report("error", "rule '" .. typeName ..
                    "' has a token but does not set tracksSeen, so the token is never read")
            end

            if rule.condition == nil and not rule.tracksSeen then
                report("error", "rule '" .. typeName ..
                    "' has neither a condition nor tracksSeen, so it can only ever be false")
            end

            if rule.condition ~= nil and type(rule.condition) ~= "function" then
                report("error", "rule '" .. typeName .. "' has a non-function condition")
            end

            if rule.token ~= nil and type(rule.token) ~= "function" then
                report("error", "rule '" .. typeName .. "' has a non-function token")
            end

            local events = entriesOf(rule.events)
            if #events == 0 and rule.resetAt == nil then
                report("warning", "rule '" .. typeName ..
                    "' has neither events nor resetAt, so it never refreshes by itself")
            end

            if knownEvents then
                for _, name in ipairs(events) do
                    if not knownEvents[name] then
                        report("error", "rule '" .. typeName .. "' names the unknown event '" ..
                            tostring(name) .. "'")
                    end
                end
            end
        end
    end

    return problems
end

--------------------------------------------------------------------------------
-- Construction
--------------------------------------------------------------------------------

--- opts:
---   rules        the rule table
---   bus          object with :Subscribe(event) / :Unsubscribe(event). The
---                manager only registers interest; the host calls QueueEvent.
---   clock        object with :Now() in unix seconds, for scheduled resets
---   knownEvents  map of valid event name -> true, for validation
---   seenBackend  persistence backend for the seen store
---   log          function(message)
function M.new(opts)
    opts = opts or {}

    local self = setmetatable({
        bus         = opts.bus,
        clock       = opts.clock,
        log         = opts.log or function() end,
        knownEvents = opts.knownEvents,

        rules      = {},
        eventIndex = {},   -- event name -> { type -> true }
        subscribed = {},   -- event name -> true

        dots   = {},       -- registry key -> dot
        byType = {},       -- type -> { registry key -> true }
        count  = 0,

        pending      = {},
        pendingCount = 0,

        deadlines = {},    -- type -> unix seconds
        deadline  = nil,   -- the soonest of them

        failures = {},     -- type|phase -> true, so a broken rule logs once

        reconcileEnabled = false,
        nextReconcile    = 0,

        stats = {
            events        = 0,
            queued        = 0,
            computes      = 0,
            notifications = 0,
            drains        = 0,
            mismatches    = 0,
        },
    }, Manager)

    self.seen = seen_mod.new(opts.seenBackend, self.log)

    self:_ApplyRules(opts.rules or {})
    self:_RecomputeDeadlines(self:_Now())

    return self
end

--------------------------------------------------------------------------------
-- Rules and subscriptions
--------------------------------------------------------------------------------

function Manager:_KnownEvents(extra)
    if not self.knownEvents and not extra then
        return nil
    end

    local known = {}
    if self.knownEvents then
        for _, name in pairs(self.knownEvents) do
            known[name] = true
        end
    end
    for _, name in pairs(entriesOf(extra)) do
        known[name] = true
    end
    return known
end

function Manager:_ApplyRules(rules, extraEvents)
    local known = self:_KnownEvents(extraEvents)

    for _, problem in ipairs(M.ValidateRules(rules, known)) do
        self.log("reddot.rules " .. problem.level .. ": " .. problem.message)
    end

    self.rules = rules
    self.failures = {}

    local index = {}
    for typeName, rule in pairs(rules) do
        if type(rule) == "table" then
            for _, name in ipairs(entriesOf(rule.events)) do
                index[name] = index[name] or {}
                index[name][typeName] = true
            end
        end
    end
    self.eventIndex = index

    -- A dot whose type a reload removed stops being global, so it is cleaned up
    -- with its last subscriber instead of lingering for the session.
    for _, dot in pairs(self.dots) do
        local rule = rules[dot.type]
        dot.global = rule ~= nil and rule.keys == nil
    end

    self:_SyncSubscriptions()
end

--- Subscribes to events that gained their first rule and unsubscribes from
--- events that lost their last one. Events that survive a reload are never
--- touched, so a patch that edits one rule does not churn the bus.
function Manager:_SyncSubscriptions()
    if not self.bus then
        return 0, 0
    end

    local added, removed = 0, 0

    for name in pairs(self.subscribed) do
        if not self.eventIndex[name] then
            self.bus:Unsubscribe(name)
            self.subscribed[name] = nil
            removed = removed + 1
        end
    end

    for name in pairs(self.eventIndex) do
        if not self.subscribed[name] then
            self.bus:Subscribe(name)
            self.subscribed[name] = true
            added = added + 1
        end
    end

    return added, removed
end

--- Swaps the rule table and re-evaluates everything, reporting only real
--- changes. `spec` is either a bare rule table or
--- `{ rules = {...}, events = {...} }`, where `events` declares the event names
--- the patch introduces so validation accepts them.
function Manager:ReloadRules(spec)
    local rules, extraEvents = spec, nil
    if type(spec) == "table" and spec.rules ~= nil then
        rules = spec.rules
        extraEvents = spec.events
    end

    self:_ApplyRules(rules, extraEvents)
    self:CreateGlobalRedDots()

    for registryKey in pairs(self.dots) do
        self:_Queue(registryKey)
    end

    local changed = self:_Drain()
    self.seen:SaveIfChanged()
    self:_RecomputeDeadlines(self:_Now())
    return changed
end

--------------------------------------------------------------------------------
-- Dots
--------------------------------------------------------------------------------

function Manager:_CreateDot(typeName, keys, keyCount, registryKey)
    local rule = self.rules[typeName]

    if rule then
        local declared = rule.keys and #rule.keys or 0
        if declared ~= keyCount then
            self:_LogOnce("keys:" .. typeName,
                "reddot: type '" .. typeName .. "' declares " .. declared .. " key(s) but was asked for " ..
                keyCount .. " ('" .. registryKey .. "')")
        end
    else
        -- Legal: a view may bind a type a hot patch has not introduced yet. The
        -- dot exists, reads false, and lights up when the rule arrives.
        self:_LogOnce("norule:" .. typeName,
            "reddot: no rule for type '" .. typeName .. "' yet; '" .. registryKey .. "' stays off")
    end

    local dot = {
        key      = registryKey,
        type     = typeName,
        keys     = keys,
        keyCount = keyCount,
        value    = false,
        subs     = {},
        global   = rule ~= nil and rule.keys == nil,
    }

    self.dots[registryKey] = dot
    self.byType[typeName] = self.byType[typeName] or {}
    self.byType[typeName][registryKey] = true
    self.count = self.count + 1

    return dot
end

function Manager:_DestroyDot(dot)
    if not self.dots[dot.key] then
        return false
    end

    self.dots[dot.key] = nil
    self.count = self.count - 1

    local live = self.byType[dot.type]
    if live then
        live[dot.key] = nil
        if next(live) == nil then
            self.byType[dot.type] = nil
        end
    end

    if self.pending[dot.key] then
        self.pending[dot.key] = nil
        self.pendingCount = self.pendingCount - 1
    end

    return true
end

--- Creates the keyless dots. Called at boot and again after every reload, so a
--- patch that introduces a global type gets its dot without a restart.
function Manager:CreateGlobalRedDots()
    local created = 0

    for typeName, rule in pairs(self.rules) do
        if type(rule) == "table" and rule.keys == nil then
            local registryKey = M.BuildKey(typeName, nil, 0)
            local dot = self.dots[registryKey]
            if dot then
                -- Something bound to it before the rule existed.
                dot.global = true
                self:_Queue(registryKey)
            else
                dot = self:_CreateDot(typeName, {}, 0, registryKey)
                dot.value = self:_Compute(dot)
                created = created + 1
            end
        end
    end

    return created
end

--------------------------------------------------------------------------------
-- Subscription
--------------------------------------------------------------------------------

--- Binds `handle` to a dot, creating it if this is the first subscriber, and
--- pushes the current value before returning. Returns the registry key, which is
--- what Unsubscribe wants back.
function Manager:Subscribe(handle, typeName, ...)
    if type(typeName) ~= "string" or typeName == "" then
        error("reddot: Subscribe needs a type name", 0)
    end

    local keyCount = select("#", ...)
    local keys = { ... }
    local registryKey = M.BuildKey(typeName, keys, keyCount)

    local dot = self.dots[registryKey]
    if not dot then
        dot = self:_CreateDot(typeName, keys, keyCount, registryKey)

        -- Synchronous, on purpose. A component that binds halfway through a
        -- frame has to be correct on that frame; waiting for the next event
        -- would leave a freshly re-entered screen blank.
        dot.value = self:_Compute(dot)
    end

    dot.subs[#dot.subs + 1] = handle
    self:_Push(handle, dot)

    return registryKey
end

function Manager:Unsubscribe(registryKey, handle)
    local dot = self.dots[registryKey]
    if not dot then
        return false
    end

    local removed = false
    for i = #dot.subs, 1, -1 do
        if dot.subs[i] == handle then
            table.remove(dot.subs, i)
            removed = true
            break
        end
    end

    if #dot.subs == 0 and not dot.global then
        self:_DestroyDot(dot)
    end

    return removed
end

--- Drops every subscriber and destroys every keyed dot. The call a screen stack
--- makes at a state-change boundary, so a screen that was torn down without
--- unbinding cannot keep a dot alive for the rest of the session.
function Manager:ClearSubscriptions()
    local doomed = {}

    for _, dot in pairs(self.dots) do
        dot.subs = {}
        if not dot.global then
            doomed[#doomed + 1] = dot
        end
    end

    for _, dot in ipairs(doomed) do
        self:_DestroyDot(dot)
    end

    return #doomed
end

function Manager:SubscriberCount(registryKey)
    local dot = self.dots[registryKey]
    return dot and #dot.subs or 0
end

--------------------------------------------------------------------------------
-- Events and the pending queue
--------------------------------------------------------------------------------

--- Queues the live dots of every type that names this event. Computes nothing.
function Manager:QueueEvent(eventName, payload) -- luacheck: ignore payload
    self.stats.events = self.stats.events + 1

    local types = self.eventIndex[eventName]
    if not types then
        return 0
    end

    local queued = 0
    for typeName in pairs(types) do
        local live = self.byType[typeName]
        if live then
            for registryKey in pairs(live) do
                if self:_Queue(registryKey) then
                    queued = queued + 1
                end
            end
        end
    end

    return queued
end

function Manager:_Queue(registryKey)
    if self.pending[registryKey] then
        return false
    end

    self.pending[registryKey] = true
    self.pendingCount = self.pendingCount + 1
    self.stats.queued = self.stats.queued + 1
    return true
end

--------------------------------------------------------------------------------
-- Seen tracking
--------------------------------------------------------------------------------

--- Records that the player has looked, by storing the token that is current
--- right now. New content moves the token and the dot comes back on by itself.
function Manager:MarkSeen(typeName, ...)
    local rule = self.rules[typeName]
    if not rule then
        return false
    end

    if not rule.tracksSeen then
        -- Not a mistake worth shouting about: a screen can mark everything it
        -- shows, and the dots that track real state simply ignore it.
        return false
    end

    local keyCount = select("#", ...)
    local keys = { ... }
    local registryKey = M.BuildKey(typeName, keys, keyCount)

    local token = self:_SafeToken(typeName, rule, keys, keyCount)
    if token == nil then
        -- The data has not loaded; storing "seen nothing" would hide the badge
        -- for content the player has not been shown.
        return false
    end

    if not self.seen:Set(registryKey, token) then
        return false
    end

    if self.dots[registryKey] then
        self:_Queue(registryKey)
    end

    return true
end

function Manager:IsSeen(typeName, ...)
    local keyCount = select("#", ...)
    local registryKey = M.BuildKey(typeName, { ... }, keyCount)
    return self.seen:Get(registryKey) ~= nil
end

--------------------------------------------------------------------------------
-- Computation
--------------------------------------------------------------------------------

function Manager:_LogOnce(tag, message)
    if self.failures[tag] then
        return
    end

    self.failures[tag] = true
    self.log(message)
end

function Manager:_SafeToken(typeName, rule, keys, keyCount)
    if not rule.token then
        -- tracksSeen without a token: the dot is seen once and stays seen.
        return ""
    end

    local ok, token = pcall(rule.token, unpack(keys, 1, keyCount))
    if not ok then
        self:_LogOnce("token:" .. typeName,
            "reddot: token for '" .. typeName .. "' failed: " .. tostring(token))
        return nil
    end

    if token == nil then
        return nil
    end

    return tostring(token)
end

function Manager:_SafeCondition(typeName, rule, keys, keyCount, isUnseen)
    local ok, result

    if isUnseen == nil then
        ok, result = pcall(rule.condition, unpack(keys, 1, keyCount))
    else
        local args = {}
        for i = 1, keyCount do
            args[i] = keys[i]
        end
        args[keyCount + 1] = isUnseen
        ok, result = pcall(rule.condition, unpack(args, 1, keyCount + 1))
    end

    if not ok then
        self:_LogOnce("condition:" .. typeName,
            "reddot: condition for '" .. typeName .. "' failed: " .. tostring(result))
        return false
    end

    return result and true or false
end

function Manager:_Compute(dot)
    self.stats.computes = self.stats.computes + 1

    local rule = self.rules[dot.type]
    if not rule then
        return false
    end

    if rule.tracksSeen then
        local current = self:_SafeToken(dot.type, rule, dot.keys, dot.keyCount)
        if current == nil then
            -- Data not loaded yet: stay off rather than guess.
            return false
        end

        local isUnseen = self.seen:Get(dot.key) ~= current

        if rule.condition then
            return self:_SafeCondition(dot.type, rule, dot.keys, dot.keyCount, isUnseen)
        end

        return isUnseen
    end

    if not rule.condition then
        return false
    end

    return self:_SafeCondition(dot.type, rule, dot.keys, dot.keyCount, nil)
end

--------------------------------------------------------------------------------
-- Notification
--------------------------------------------------------------------------------

local function invoke(handle, registryKey, value)
    if type(handle) == "function" then
        return handle(registryKey, value)
    end

    -- On a C# object this goes through xLua's metatable, which raises rather
    -- than returning nil for an unknown member, hence the pcall.
    local ok, method = pcall(function() return handle.SetRedDot end)
    if ok and type(method) == "function" then
        return method(handle, registryKey, value)
    end

    error("reddot: handle for " .. tostring(registryKey) ..
          " is neither a function nor an object with SetRedDot", 0)
end

function Manager:_Push(handle, dot)
    self.stats.notifications = self.stats.notifications + 1

    local ok, err = pcall(invoke, handle, dot.key, dot.value)
    if not ok then
        self.log("reddot: subscriber for '" .. dot.key .. "' failed: " .. tostring(err))
    end
end

--- Iterates a copy: a handle is allowed to unbind itself while it is told, which
--- is what a badge that hides the widget it lives on does.
function Manager:_Notify(dot)
    local snapshot = {}
    for i = 1, #dot.subs do
        snapshot[i] = dot.subs[i]
    end

    for i = 1, #snapshot do
        self:_Push(snapshot[i], dot)
    end
end

--------------------------------------------------------------------------------
-- The frame tick
--------------------------------------------------------------------------------

function Manager:_Now()
    if not self.clock then
        return 0
    end

    local ok, now = pcall(function() return self.clock:Now() end)
    if not ok or type(now) ~= "number" then
        self:_LogOnce("clock", "reddot: the clock failed: " .. tostring(now))
        return 0
    end

    return now
end

--- Computes every queued dot exactly once and notifies the ones that moved.
function Manager:_Drain()
    if self.pendingCount == 0 then
        return 0
    end

    self.stats.drains = self.stats.drains + 1

    local pending = self.pending
    self.pending = {}
    self.pendingCount = 0

    local changed = 0
    for registryKey in pairs(pending) do
        local dot = self.dots[registryKey]
        if dot then
            local value = self:_Compute(dot)
            if value ~= dot.value then
                dot.value = value
                self:_Notify(dot)
                changed = changed + 1
            end
        end
    end

    return changed
end

--- One frame's work: fire anything the clock made due, compute the queue,
--- persist at most once, then keep the deadlines and the debug sweep honest.
---
--- The idle path is two number comparisons and a dirty flag. Nothing polls.
function Manager:Tick(now)
    now = now or self:_Now()

    local fired = 0
    if self.deadline and now >= self.deadline then
        fired = self:_FireDueResets(now)
    end

    local changed = self:_Drain()

    self.seen:SaveIfChanged()

    -- Fresh data can move a boundary -- a shop that just reset has a new next
    -- reset -- so deadlines are recomputed after work, not on a timer.
    if changed > 0 or fired > 0 then
        self:_RecomputeDeadlines(now)
    end

    if self.reconcileEnabled and now >= self.nextReconcile then
        self.nextReconcile = now + M.RECONCILE_INTERVAL
        self:_Reconcile()
    end

    return changed
end

--------------------------------------------------------------------------------
-- Scheduled resets
--------------------------------------------------------------------------------

function Manager:_RecomputeDeadlines(now) -- luacheck: ignore now
    local deadlines = {}
    local soonest = nil

    for typeName, rule in pairs(self.rules) do
        if type(rule) == "table" and rule.resetAt then
            local ok, at = pcall(rule.resetAt)
            if not ok then
                self:_LogOnce("resetAt:" .. typeName,
                    "reddot: resetAt for '" .. typeName .. "' failed: " .. tostring(at))
            elseif type(at) == "number" then
                deadlines[typeName] = at
                if not soonest or at < soonest then
                    soonest = at
                end
            end
        end
    end

    self.deadlines = deadlines
    self.deadline = soonest
end

--- Queues the types whose deadline has passed, then recomputes the next one.
function Manager:_FireDueResets(now)
    local fired = 0

    for typeName, at in pairs(self.deadlines) do
        if now >= at then
            local live = self.byType[typeName]
            if live then
                for registryKey in pairs(live) do
                    self:_Queue(registryKey)
                end
            end
            fired = fired + 1
        end
    end

    self:_RecomputeDeadlines(now)

    if self.deadline and self.deadline <= now then
        -- A resetAt that keeps pointing at the past would re-fire every frame.
        -- Stand it down until the next real flush recomputes it.
        self:_LogOnce("resetSpin",
            "reddot: a resetAt keeps returning a time in the past; scheduled resets are paused")
        self.deadline = nil
    end

    return fired
end

function Manager:NextDeadline()
    return self.deadline
end

--------------------------------------------------------------------------------
-- Debug tools
--------------------------------------------------------------------------------

--- Turns on a once-a-second sweep that recomputes every live dot and logs the
--- ones whose cached value disagrees.
---
--- It fixes nothing on purpose. A MISMATCH is not a glitch to paper over, it is
--- a rule missing an event -- the cache is right about what it was told, and
--- what it was told was incomplete.
function Manager:SetReconcileEnabled(enabled)
    self.reconcileEnabled = enabled and true or false
    self.nextReconcile = self:_Now()
    return self.reconcileEnabled
end

function Manager:_Reconcile()
    local mismatches = 0

    for registryKey, dot in pairs(self.dots) do
        local fresh = self:_Compute(dot)
        if fresh ~= dot.value then
            mismatches = mismatches + 1
            self.log("reddot MISMATCH " .. registryKey ..
                ": cached=" .. tostring(dot.value) .. " fresh=" .. tostring(fresh))
        end
    end

    self.stats.mismatches = self.stats.mismatches + mismatches
    return mismatches
end

--- Recomputes everything and reports the disagreement count without waiting for
--- the timer. Used by the tests and by the demo's debug button.
function Manager:Reconcile()
    return self:_Reconcile()
end

function Manager:GetValue(typeName, ...)
    local registryKey = M.BuildKey(typeName, { ... }, select("#", ...))
    local dot = self.dots[registryKey]
    return dot ~= nil and dot.value or false
end

function Manager:GetValueByKey(registryKey)
    local dot = self.dots[registryKey]
    return dot ~= nil and dot.value or false
end

function Manager:HasDot(registryKey)
    return self.dots[registryKey] ~= nil
end

--- The types whose rules track seen state. A badge of one of these can only be
--- cleared by somebody marking it seen, so a host can check at boot that every one
--- of them actually has a screen that does.
function Manager:SeenTrackingTypes()
    local types = {}
    for typeName, rule in pairs(self.rules) do
        if type(rule) == "table" and rule.tracksSeen then
            types[#types + 1] = typeName
        end
    end
    table.sort(types)
    return types
end

function Manager:GetRedDotCount()
    return self.count
end

function Manager:GetKeyedCount()
    local keyed = 0
    for _, dot in pairs(self.dots) do
        if not dot.global then
            keyed = keyed + 1
        end
    end
    return keyed
end

function Manager:Keys()
    local keys = {}
    for registryKey in pairs(self.dots) do
        keys[#keys + 1] = registryKey
    end
    table.sort(keys)
    return keys
end

function Manager:ResetStats()
    for name in pairs(self.stats) do
        self.stats[name] = 0
    end
end

--- Machine-readable snapshot: `key=0|1:subscriberCount;...`, sorted. One call,
--- no tables across the boundary.
function Manager:DumpValues()
    local parts = {}
    for _, registryKey in ipairs(self:Keys()) do
        local dot = self.dots[registryKey]
        parts[#parts + 1] = registryKey .. "=" .. (dot.value and "1" or "0") .. ":" .. #dot.subs
    end
    return table.concat(parts, ";")
end

--- Human-readable snapshot for the console and the demo's debug panel.
function Manager:DumpState()
    local keys = self:Keys()
    local keyed = self:GetKeyedCount()

    local lines = {
        string.format("red dots: %d live (%d global, %d keyed)", self.count, self.count - keyed, keyed),
    }

    for _, registryKey in ipairs(keys) do
        local dot = self.dots[registryKey]
        lines[#lines + 1] = string.format("  %-28s %-4s subs=%d%s",
            registryKey,
            dot.value and "on" or "off",
            #dot.subs,
            dot.global and "  (global)" or "")
    end

    local seenKeys = self.seen:Keys()
    lines[#lines + 1] = "seen: " .. (#seenKeys > 0 and table.concat(seenKeys, ", ") or "(none)")

    lines[#lines + 1] = string.format("pending=%d deadline=%s reconcile=%s",
        self.pendingCount,
        self.deadline and string.format("%d", self.deadline) or "none",
        self.reconcileEnabled and "on" or "off")

    lines[#lines + 1] = string.format(
        "stats: events=%d queued=%d computes=%d notifications=%d drains=%d mismatches=%d saves=%d",
        self.stats.events, self.stats.queued, self.stats.computes,
        self.stats.notifications, self.stats.drains, self.stats.mismatches, self.seen.writes)

    return table.concat(lines, "\n")
end

M.json = json

return M
