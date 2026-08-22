--- Remembers which TransientUntilSeen nodes the player has already looked at.
---
--- The store owns the in-memory table and delegates durability to a backend
--- supplied by the host. The backend contract is deliberately one string in,
--- one string out, so the same interface can be implemented by a C# object
--- (PlayerPrefs, a save file, a server call) or by a plain Lua table in tests
--- without any table marshalling across the boundary:
---
---     backend:Load()      -> string | nil
---     backend:Save(blob)  -> void

local M = {}

local SEPARATOR = "|"

local SeenStore = {}
SeenStore.__index = SeenStore

--- `backend` may be nil, in which case seen state lives only for this session.
function M.new(backend)
    local self = setmetatable({
        backend = backend,
        seen    = {},
    }, SeenStore)
    self:reload()
    return self
end

--------------------------------------------------------------------------------
-- Persistence
--------------------------------------------------------------------------------

function SeenStore:reload()
    self.seen = {}
    if not self.backend then
        return
    end

    local ok, blob = pcall(function() return self.backend:Load() end)
    if not ok then
        -- A broken save must never take the UI down with it: start clean.
        self.lastError = tostring(blob)
        return
    end
    if type(blob) ~= "string" or blob == "" then
        return
    end

    for path in string.gmatch(blob, "[^%" .. SEPARATOR .. "]+") do
        self.seen[path] = true
    end
end

function SeenStore:flushToBackend()
    if not self.backend then
        return
    end

    -- Sorted so that the persisted blob is stable, which makes it comparable in
    -- tests and diffable when someone inspects a save file.
    local paths = {}
    for path, isSeen in pairs(self.seen) do
        if isSeen then
            paths[#paths + 1] = path
        end
    end
    table.sort(paths)

    local blob = table.concat(paths, SEPARATOR)
    local ok, err = pcall(function() self.backend:Save(blob) end)
    if not ok then
        self.lastError = tostring(err)
    end
end

--------------------------------------------------------------------------------
-- Queries and mutation
--------------------------------------------------------------------------------

function SeenStore:isSeen(path)
    return self.seen[path] == true
end

--- Returns true when the value actually changed, so callers can skip the
--- persistence round trip and the re-evaluation that would follow it.
function SeenStore:set(path, value)
    value = value and true or false
    if (self.seen[path] == true) == value then
        return false
    end
    self.seen[path] = value or nil
    self:flushToBackend()
    return true
end

function SeenStore:clear()
    if next(self.seen) == nil then
        return false
    end
    self.seen = {}
    self:flushToBackend()
    return true
end

--- Snapshot of the seen paths, sorted. Used by debugDump and by tests.
function SeenStore:paths()
    local paths = {}
    for path, isSeen in pairs(self.seen) do
        if isSeen then
            paths[#paths + 1] = path
        end
    end
    table.sort(paths)
    return paths
end

return M
