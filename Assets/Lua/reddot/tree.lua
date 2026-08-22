--- Builds the red dot node tree out of plain path strings.
---
--- The tree is deliberately dumb: it knows about parents, children, depth and
--- the aggregation policy of each node, and nothing at all about rules,
--- events or state. `manager.lua` owns everything that changes over time.

local types = require("reddot.types")

local M = {}

local MAX_DEPTH = 32 -- a red dot tree this deep is a bug, not a design

--------------------------------------------------------------------------------
-- Path helpers
--------------------------------------------------------------------------------

--- "Main.Mail.Inbox" -> "Main.Mail", "Main" -> nil
function M.parentOf(path)
    return string.match(path, "^(.*)%.[^%.]+$")
end

--- "Main.Mail.Inbox" -> "Inbox"
function M.nameOf(path)
    return string.match(path, "([^%.]+)$")
end

--- Depth of the root is 0.
function M.depthOf(path)
    local depth = 0
    for _ in string.gmatch(path, "%.") do
        depth = depth + 1
    end
    return depth
end

local function validatePath(path)
    if type(path) ~= "string" or path == "" then
        error("reddot.tree: node path must be a non-empty string, got " .. tostring(path), 0)
    end
    if string.match(path, "^%.") or string.match(path, "%.$") or string.match(path, "%.%.") then
        error("reddot.tree: malformed node path '" .. path .. "' (empty path segment)", 0)
    end
    if M.depthOf(path) > MAX_DEPTH then
        error("reddot.tree: node path '" .. path .. "' exceeds the maximum depth of " .. MAX_DEPTH, 0)
    end
end

--------------------------------------------------------------------------------
-- Construction
--------------------------------------------------------------------------------

local Tree = {}
Tree.__index = Tree

local function newNode(path, policy)
    return {
        path     = path,
        name     = M.nameOf(path),
        parent   = M.parentOf(path),
        children = {},
        depth    = M.depthOf(path),
        policy   = policy or types.DEFAULT_POLICY,
        hasRule  = false,
    }
end

--- Adds `path` and every ancestor it implies. Ancestors that were never
--- declared are created with the default policy rather than rejected: a hot
--- patch that introduces "Main.Guild.Requests" should not have to also declare
--- "Main.Guild".
function Tree:ensure(path, policy)
    validatePath(path)

    local existing = self.nodes[path]
    if existing then
        if policy then
            if not types.POLICIES[policy] then
                error("reddot.tree: unknown aggregation policy '" .. tostring(policy) ..
                      "' on node '" .. path .. "'", 0)
            end
            existing.policy = policy
        end
        return existing
    end

    if policy and not types.POLICIES[policy] then
        error("reddot.tree: unknown aggregation policy '" .. tostring(policy) ..
              "' on node '" .. path .. "'", 0)
    end

    local node = newNode(path, policy)
    self.nodes[path] = node
    self.count = self.count + 1
    if node.depth > self.maxDepth then
        self.maxDepth = node.depth
    end

    if node.parent then
        local parent = self:ensure(node.parent)
        table.insert(parent.children, node.path)
        table.sort(parent.children) -- stable iteration order keeps dumps diffable
    else
        table.insert(self.roots, node.path)
        table.sort(self.roots)
    end

    return node
end

function Tree:get(path)
    return self.nodes[path]
end

function Tree:isLeaf(path)
    local node = self.nodes[path]
    return node ~= nil and #node.children == 0
end

--- Every node, deepest first. Aggregates bubble along this order, so a parent
--- is only ever recomputed after all of its children are final.
function Tree:pathsByDepthDesc()
    local paths = {}
    for path in pairs(self.nodes) do
        paths[#paths + 1] = path
    end
    table.sort(paths, function(a, b)
        local da, db = self.nodes[a].depth, self.nodes[b].depth
        if da ~= db then return da > db end
        return a < b
    end)
    return paths
end

--- Walks parent links to make sure they terminate. Path-derived parents cannot
--- form a cycle, but the check is cheap and this is the one place that would
--- otherwise loop forever if a caller hand-built a malformed tree.
function Tree:assertAcyclic()
    for path, node in pairs(self.nodes) do
        local seen, cursor, steps = { [path] = true }, node.parent, 0
        while cursor do
            if seen[cursor] then
                error("reddot.tree: cycle detected in the ancestry of '" .. path .. "'", 0)
            end
            seen[cursor] = true
            steps = steps + 1
            if steps > MAX_DEPTH then
                error("reddot.tree: ancestry of '" .. path .. "' does not terminate", 0)
            end
            local parent = self.nodes[cursor]
            if not parent then
                error("reddot.tree: node '" .. path .. "' refers to missing parent '" .. cursor .. "'", 0)
            end
            cursor = parent.parent
        end
    end
end

--- Marks the nodes that rules target and rejects rules on interior nodes.
--- A rule on a parent would fight with the parent's aggregation policy, so the
--- system treats it as a configuration error instead of picking a winner.
function Tree:attachRules(rules)
    for path in pairs(rules) do
        local node = self:ensure(path)
        node.hasRule = true
    end
    for path, node in pairs(self.nodes) do
        if node.hasRule and #node.children > 0 then
            error("reddot.tree: node '" .. path .. "' has a rule but is not a leaf (children: " ..
                  table.concat(node.children, ", ") .. ")", 0)
        end
    end
    self:assertAcyclic()
end

--------------------------------------------------------------------------------
-- Entry point
--------------------------------------------------------------------------------

--- `nodeDefs` is a list of { path = "...", policy = "..." }. Anything the list
--- forgets is filled in from the paths themselves.
function M.build(nodeDefs)
    local self = setmetatable({
        nodes    = {},
        roots    = {},
        count    = 0,
        maxDepth = 0,
    }, Tree)

    for _, def in ipairs(nodeDefs or {}) do
        if type(def) == "string" then
            self:ensure(def)
        else
            self:ensure(def.path, def.policy)
        end
    end

    self:assertAcyclic()
    return self
end

return M
