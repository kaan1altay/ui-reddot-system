--- Connects node paths to whatever wants to draw them.
---
--- The binder is the reason hot updates are safe for the view layer: a binding
--- holds a path string, never a node object. `manager.reloadRules` can rebuild
--- the entire tree underneath and every binding still points at the same
--- badge, or at a badge that has just been introduced.
---
--- A handle is one of:
---
---   * a function  -- called as handle(path, visible, count)
---   * an object   -- called as handle:SetRedDot(path, visible, count)
---
--- The object form is what C# views use: only strings, booleans and numbers
--- cross the boundary, so no table marshalling is involved.

local M = {}

local Binder = {}
Binder.__index = Binder

--------------------------------------------------------------------------------
-- Handle invocation
--------------------------------------------------------------------------------

local function invoke(handle, path, visible, count)
    if type(handle) == "function" then
        return handle(path, visible, count)
    end

    -- `handle.SetRedDot` on a C# object goes through xLua's metatable, which
    -- raises rather than returning nil for an unknown member, hence the pcall.
    local ok, method = pcall(function() return handle.SetRedDot end)
    if ok and type(method) == "function" then
        return method(handle, path, visible, count)
    end

    error("reddot.binder: handle for " .. tostring(path) ..
          " is neither a function nor an object with SetRedDot", 0)
end

--------------------------------------------------------------------------------
-- Construction
--------------------------------------------------------------------------------

function M.new(manager, log)
    local self = setmetatable({
        manager  = manager,
        log      = log or function() end,
        byPath   = {},   -- path  -> array of { handle, owner }
        byOwner  = {},   -- owner -> array of { path, handle }
        count    = 0,
    }, Binder)

    self.listener = function(path, state)
        self:_publish(path, state.visible, state.count)
    end
    manager:addListener(self.listener)

    return self
end

function Binder:dispose()
    self.manager:removeListener(self.listener)
    self.byPath  = {}
    self.byOwner = {}
    self.count   = 0
end

--------------------------------------------------------------------------------
-- Binding
--------------------------------------------------------------------------------

--- Binds `handle` to `path`. `owner` is an optional grouping key (a screen, a
--- window, a list item) that `unbindAll` can release in one call; it defaults
--- to the handle itself.
---
--- The handle is pushed the current state immediately, so a view that binds
--- late is correct on its first frame instead of waiting for the next change.
function Binder:bind(path, handle, owner)
    if type(path) ~= "string" or path == "" then
        error("reddot.binder: bind requires a node path", 0)
    end
    if handle == nil then
        error("reddot.binder: bind requires a handle for " .. path, 0)
    end
    owner = owner or handle

    local bindings = self.byPath[path]
    if not bindings then
        bindings = {}
        self.byPath[path] = bindings
    end
    bindings[#bindings + 1] = { handle = handle, owner = owner }

    local owned = self.byOwner[owner]
    if not owned then
        owned = {}
        self.byOwner[owner] = owned
    end
    owned[#owned + 1] = { path = path, handle = handle }

    self.count = self.count + 1

    local visible, count = self.manager:getState(path)
    self:_invokeSafely(handle, path, visible, count)

    return handle
end

--- Removes one binding. Unbinding something that was never bound, or that a
--- previous unbind already removed, is a no-op rather than an error: view code
--- tears down in whatever order the UI framework feels like.
function Binder:unbind(path, handle)
    local bindings = self.byPath[path]
    if not bindings then
        return false
    end

    local removed = false
    for i = #bindings, 1, -1 do
        local binding = bindings[i]
        if binding.handle == handle then
            table.remove(bindings, i)
            self:_forgetOwner(binding.owner, path, handle)
            self.count = self.count - 1
            removed = true
        end
    end

    if #bindings == 0 then
        self.byPath[path] = nil
    end
    return removed
end

--- Releases everything registered under `owner`. This is the call a screen
--- makes in its teardown, and the reason views never have to remember which
--- paths they touched.
function Binder:unbindAll(owner)
    local owned = self.byOwner[owner]
    if not owned then
        return 0
    end
    self.byOwner[owner] = nil

    local removed = 0
    for _, entry in ipairs(owned) do
        local bindings = self.byPath[entry.path]
        if bindings then
            for i = #bindings, 1, -1 do
                local binding = bindings[i]
                if binding.handle == entry.handle and binding.owner == owner then
                    table.remove(bindings, i)
                    self.count = self.count - 1
                    removed = removed + 1
                    break
                end
            end
            if #bindings == 0 then
                self.byPath[entry.path] = nil
            end
        end
    end
    return removed
end

function Binder:_forgetOwner(owner, path, handle)
    local owned = self.byOwner[owner]
    if not owned then
        return
    end
    for i = #owned, 1, -1 do
        local entry = owned[i]
        if entry.path == path and entry.handle == handle then
            table.remove(owned, i)
            break
        end
    end
    if #owned == 0 then
        self.byOwner[owner] = nil
    end
end

--------------------------------------------------------------------------------
-- Delivery
--------------------------------------------------------------------------------

--- Iterates over a copy, because a handle is allowed to unbind itself while it
--- is being notified -- a badge that hides the widget it lives on is a normal
--- thing for UI code to do.
function Binder:_publish(path, visible, count)
    local bindings = self.byPath[path]
    if not bindings or #bindings == 0 then
        return 0
    end

    local snapshot = {}
    for i = 1, #bindings do
        snapshot[i] = bindings[i].handle
    end

    for _, handle in ipairs(snapshot) do
        self:_invokeSafely(handle, path, visible, count)
    end
    return #snapshot
end

--- A view that throws must not stop the other views from updating.
function Binder:_invokeSafely(handle, path, visible, count)
    local ok, err = pcall(invoke, handle, path, visible, count)
    if not ok then
        self.log("reddot: binding for " .. tostring(path) .. " failed: " .. tostring(err))
    end
end

--------------------------------------------------------------------------------
-- Queries
--------------------------------------------------------------------------------

function Binder:bindingCount(path)
    if path == nil then
        return self.count
    end
    local bindings = self.byPath[path]
    return bindings and #bindings or 0
end

function Binder:boundPaths()
    local paths = {}
    for path, bindings in pairs(self.byPath) do
        if #bindings > 0 then
            paths[#paths + 1] = path
        end
    end
    table.sort(paths)
    return paths
end

return M
