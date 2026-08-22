--- Remembers the content token that was current when the player last looked at
--- a dot.
---
--- Seen state is not a boolean. Storing "the player has seen Mail" would mean a
--- badge that never comes back; storing *what* they saw means new content turns
--- it on again by itself, with no extra bookkeeping in the rule. A dot is unseen
--- exactly when its stored token differs from the token its rule reports now.
---
--- Persistence is one string in, one string out, through a host callback:
---
---     backend:Load()      -> string | nil
---     backend:Save(blob)  -> void
---
--- so the same store works against PlayerPrefs, a save file, or a table in a
--- test, and nothing but a string crosses the Lua/C# boundary.

local json = require("reddot.json")

local M = {}

--- Bumped whenever the blob's shape changes. An older or newer version is
--- discarded rather than guessed at: seen state is cosmetic, and a wrong guess
--- would show the player badges they already dismissed.
M.SAVE_VERSION = 1

local SeenStore = {}
SeenStore.__index = SeenStore

--- `backend` may be nil, in which case seen state lives only for this session.
function M.new(backend, log)
    local self = setmetatable({
        backend = backend,
        log     = log or function() end,
        tokens  = {},    -- registry key -> token string
        dirty   = false,
        writes  = 0,     -- how often the blob has actually been handed to the host
    }, SeenStore)

    self:Reload()
    return self
end

--------------------------------------------------------------------------------
-- Loading
--------------------------------------------------------------------------------

function SeenStore:Reload()
    self.tokens = {}
    self.dirty = false

    if not self.backend then
        return
    end

    local ok, blob = pcall(function() return self.backend:Load() end)
    if not ok then
        self.log("reddot.seen: could not read the save (" .. tostring(blob) .. "); starting clean")
        return
    end

    if type(blob) ~= "string" or blob == "" then
        return
    end

    local decoded
    ok, decoded = pcall(json.decode, blob)
    if not ok or type(decoded) ~= "table" then
        self.log("reddot.seen: the save is corrupt (" .. tostring(decoded) .. "); starting clean")
        return
    end

    if decoded.version ~= M.SAVE_VERSION then
        self.log("reddot.seen: save version " .. tostring(decoded.version) .. " is not " ..
                 M.SAVE_VERSION .. "; starting clean")
        return
    end

    -- Entries are an array of [key, token] pairs rather than an object keyed by
    -- registry key. Round-tripping ids through JSON object keys turns numbers
    -- into strings, and a set that comes back with different key types is the
    -- kind of bug that only shows up on a device.
    local entries = decoded.entries
    if type(entries) ~= "table" then
        return
    end

    local restored = 0
    for _, entry in pairs(entries) do
        if type(entry) == "table" and type(entry[1]) == "string" and type(entry[2]) == "string" then
            self.tokens[entry[1]] = entry[2]
            restored = restored + 1
        end
    end

    if restored > 0 then
        self.log("reddot.seen: restored " .. restored .. " seen entries")
    end
end

--------------------------------------------------------------------------------
-- Saving
--------------------------------------------------------------------------------

--- Writes the blob if anything changed since the last write. Called once from
--- the frame tick, so a burst of MarkSeen calls in one frame costs one write.
function SeenStore:SaveIfChanged()
    if not self.dirty then
        return false
    end

    self.dirty = false

    if not self.backend then
        return false
    end

    local keys = {}
    for key in pairs(self.tokens) do
        keys[#keys + 1] = key
    end
    table.sort(keys)

    local entries = json.array({})
    for i = 1, #keys do
        entries[i] = json.array({ keys[i], self.tokens[keys[i]] })
    end

    local blob = json.encode({ version = M.SAVE_VERSION, entries = entries })

    local ok, err = pcall(function() self.backend:Save(blob) end)
    if not ok then
        self.log("reddot.seen: could not write the save (" .. tostring(err) .. ")")
        return false
    end

    self.writes = self.writes + 1
    return true
end

--------------------------------------------------------------------------------
-- Queries and mutation
--------------------------------------------------------------------------------

function SeenStore:Get(registryKey)
    return self.tokens[registryKey]
end

--- Stores the token that was current when the player looked. Returns true when
--- the value actually moved, so callers can skip the requeue.
function SeenStore:Set(registryKey, token)
    if self.tokens[registryKey] == token then
        return false
    end

    self.tokens[registryKey] = token
    self.dirty = true
    return true
end

function SeenStore:Forget(registryKey)
    if self.tokens[registryKey] == nil then
        return false
    end

    self.tokens[registryKey] = nil
    self.dirty = true
    return true
end

function SeenStore:Clear()
    if next(self.tokens) == nil then
        return false
    end

    self.tokens = {}
    self.dirty = true
    return true
end

--- Registry keys with stored tokens, sorted. Used by DumpState and by tests.
function SeenStore:Keys()
    local keys = {}
    for key in pairs(self.tokens) do
        keys[#keys + 1] = key
    end
    table.sort(keys)
    return keys
end

return M
