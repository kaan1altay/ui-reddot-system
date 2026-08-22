--- A minimal JSON encoder/decoder, sized for the seen store and nothing else.
---
--- It handles null, booleans, numbers, strings, arrays and objects -- which is
--- the whole of what the save blob contains. It is not a general-purpose
--- library: there is no pretty printing, no scientific-notation coaxing and no
--- surrogate-pair handling, because the data is machine-written and
--- machine-read.
---
--- Arrays and objects are both Lua tables, so encoding has to be told which is
--- which. `json.array(t)` tags a table as an array; anything untagged with no
--- positive integer keys encodes as an object. That distinction matters here:
--- an empty seen list must come back as `[]`, not `{}`.

local json = {}

local ARRAY_MT = { __jsonarray = true }

--- Tags `t` as an array, so it encodes as `[...]` even when empty.
function json.array(t)
    return setmetatable(t or {}, ARRAY_MT)
end

function json.isArray(t)
    if getmetatable(t) == ARRAY_MT then
        return true
    end
    return next(t) ~= nil and t[1] ~= nil
end

--------------------------------------------------------------------------------
-- Encoding
--------------------------------------------------------------------------------

local ESCAPES = {
    ['"']    = '\\"',
    ['\\']   = '\\\\',
    ['\b']   = '\\b',
    ['\f']   = '\\f',
    ['\n']   = '\\n',
    ['\r']   = '\\r',
    ['\t']   = '\\t',
}

local function encodeString(value)
    local escaped = string.gsub(value, '[%c"\\]', function(char)
        return ESCAPES[char] or string.format('\\u%04x', string.byte(char))
    end)
    return '"' .. escaped .. '"'
end

local function encodeNumber(value)
    if value ~= value or value == math.huge or value == -math.huge then
        -- JSON has no way to say these; null is the least surprising answer.
        return "null"
    end
    if value % 1 == 0 then
        return string.format("%d", value)
    end
    return string.format("%.14g", value)
end

local encodeValue

local function encodeArray(value, out)
    out[#out + 1] = "["
    for i = 1, #value do
        if i > 1 then
            out[#out + 1] = ","
        end
        encodeValue(value[i], out)
    end
    out[#out + 1] = "]"
end

local function encodeObject(value, out)
    -- Sorted, so the same state always produces the same blob: comparable in
    -- tests, and diffable when someone opens a save file.
    local names = {}
    for name in pairs(value) do
        names[#names + 1] = tostring(name)
    end
    table.sort(names)

    out[#out + 1] = "{"
    for i = 1, #names do
        if i > 1 then
            out[#out + 1] = ","
        end
        out[#out + 1] = encodeString(names[i])
        out[#out + 1] = ":"
        encodeValue(value[names[i]], out)
    end
    out[#out + 1] = "}"
end

encodeValue = function(value, out)
    local kind = type(value)
    if value == nil then
        out[#out + 1] = "null"
    elseif kind == "boolean" then
        out[#out + 1] = value and "true" or "false"
    elseif kind == "number" then
        out[#out + 1] = encodeNumber(value)
    elseif kind == "string" then
        out[#out + 1] = encodeString(value)
    elseif kind == "table" then
        if json.isArray(value) then
            encodeArray(value, out)
        else
            encodeObject(value, out)
        end
    else
        error("json: cannot encode a " .. kind, 0)
    end
end

function json.encode(value)
    local out = {}
    encodeValue(value, out)
    return table.concat(out)
end

--------------------------------------------------------------------------------
-- Decoding
--------------------------------------------------------------------------------

local UNESCAPES = {
    ['"']  = '"',
    ['\\'] = '\\',
    ['/']  = '/',
    ['b']  = '\b',
    ['f']  = '\f',
    ['n']  = '\n',
    ['r']  = '\r',
    ['t']  = '\t',
}

local function skipSpace(text, pos)
    local _, stop = string.find(text, "^[ \t\r\n]*", pos)
    return stop + 1
end

local parseValue

local function fail(pos, message)
    error("json: " .. message .. " at position " .. pos, 0)
end

local function parseString(text, pos)
    pos = pos + 1 -- opening quote
    local out = {}
    while true do
        local char = string.sub(text, pos, pos)
        if char == "" then
            fail(pos, "unterminated string")
        elseif char == '"' then
            return table.concat(out), pos + 1
        elseif char == "\\" then
            local escape = string.sub(text, pos + 1, pos + 1)
            if escape == "u" then
                local hex = string.sub(text, pos + 2, pos + 5)
                local code = tonumber(hex, 16)
                if not code then
                    fail(pos, "bad unicode escape")
                end
                -- The save blob is ASCII in practice; anything above it is kept
                -- as a replacement rather than pretending to do UTF-16.
                out[#out + 1] = code < 128 and string.char(code) or "?"
                pos = pos + 6
            else
                local replacement = UNESCAPES[escape]
                if not replacement then
                    fail(pos, "bad escape '\\" .. escape .. "'")
                end
                out[#out + 1] = replacement
                pos = pos + 2
            end
        else
            out[#out + 1] = char
            pos = pos + 1
        end
    end
end

local function parseNumber(text, pos)
    local literal = string.match(text, "^-?%d+%.?%d*[eE]?[-+]?%d*", pos)
    local value = literal and tonumber(literal)
    if not value then
        fail(pos, "bad number")
    end
    return value, pos + #literal
end

local function parseArray(text, pos)
    local out = json.array({})
    pos = skipSpace(text, pos + 1)
    if string.sub(text, pos, pos) == "]" then
        return out, pos + 1
    end

    while true do
        local value
        value, pos = parseValue(text, pos)
        out[#out + 1] = value
        pos = skipSpace(text, pos)

        local char = string.sub(text, pos, pos)
        if char == "]" then
            return out, pos + 1
        end
        if char ~= "," then
            fail(pos, "expected ',' or ']'")
        end
        pos = skipSpace(text, pos + 1)
    end
end

local function parseObject(text, pos)
    local out = {}
    pos = skipSpace(text, pos + 1)
    if string.sub(text, pos, pos) == "}" then
        return out, pos + 1
    end

    while true do
        if string.sub(text, pos, pos) ~= '"' then
            fail(pos, "expected a key")
        end

        local name
        name, pos = parseString(text, pos)
        pos = skipSpace(text, pos)
        if string.sub(text, pos, pos) ~= ":" then
            fail(pos, "expected ':'")
        end

        pos = skipSpace(text, pos + 1)
        out[name], pos = parseValue(text, pos)
        pos = skipSpace(text, pos)

        local char = string.sub(text, pos, pos)
        if char == "}" then
            return out, pos + 1
        end
        if char ~= "," then
            fail(pos, "expected ',' or '}'")
        end
        pos = skipSpace(text, pos + 1)
    end
end

parseValue = function(text, pos)
    pos = skipSpace(text, pos)
    local char = string.sub(text, pos, pos)

    if char == "{" then return parseObject(text, pos) end
    if char == "[" then return parseArray(text, pos) end
    if char == '"' then return parseString(text, pos) end
    if string.sub(text, pos, pos + 3) == "true" then return true, pos + 4 end
    if string.sub(text, pos, pos + 4) == "false" then return false, pos + 5 end
    if string.sub(text, pos, pos + 3) == "null" then return nil, pos + 4 end
    if string.find(char, "[%-%d]") then return parseNumber(text, pos) end

    fail(pos, "unexpected character '" .. char .. "'")
end

--- Returns the decoded value, or raises. Callers are expected to pcall: a
--- corrupted save is a normal thing to find, not an exceptional one.
function json.decode(text)
    if type(text) ~= "string" or text == "" then
        error("json: nothing to decode", 0)
    end

    local value, pos = parseValue(text, 1)
    pos = skipSpace(text, pos)
    if pos <= #text then
        fail(pos, "trailing content")
    end
    return value
end

return json
