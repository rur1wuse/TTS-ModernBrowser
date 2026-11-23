--[[ TTS Modern Browser - Global Script

Paste into the save's Global Script (Games > Scripting > Global), or into a
Workshop mod's Global Lua. Requires the TtsModernBrowser client plugin on every
client; clients without it simply keep the vanilla CEF tablet.

Two channels carry the URL:
  * Browser.url  - about:blank#mb=<percent-encoded-url>, so the legacy CEF view
                   only loads a blank page;
  * GMNotes      - the same marker as plain object state, which always
                   synchronises between clients (the plugin falls back to it when
                   Browser.url has no marker).
Unmarked tablets are left alone.
--]]

local MARKER = "about:blank#mb="

local function mb_encode(value)
    return (value:gsub("[^%w%-%._~]", function(char)
        return string.format("%%%02X", string.byte(char))
    end))
end

--- Point a marked tablet at a real URL. Callable from any script.
function MB_setURL(guid, url)
    local obj = getObjectFromGUID(guid)
    if not obj or not obj.Browser then return false end
    if type(url) ~= "string" or url == "" then return false end

    -- Channel 1: the tablet's own URL, so the legacy CEF view only loads a blank page.
    obj.Browser.url = MARKER .. mb_encode(url)
    -- Channel 2: plain object state, read as "MB1:<raw url>". It survives CEF
    -- normalising the marker above back to about:blank.
    obj.setGMNotes("MB1:" .. url)
    return true
end

--- Convert a plain Tablet into a modern browser and load `url`.
---
--- Order matters: the marker is written before setLock, because a locked object's
--- state change can be dropped. If the marker never lands, the tablet keeps loading
--- its CEF view of about:blank and the screen stays black.
function MB_mark(guid, url)
    local obj = getObjectFromGUID(guid)
    if not obj or not obj.Browser then return false end

    obj.setName("Modern Browser")
    obj.setDescription("Modern browser (WebView2). Requires the TtsModernBrowser client plugin.")
    MB_setURL(guid, url or "https://example.com/")
    obj.setLock(true)
    return true
end

--- Drop the marker so the tablet returns to vanilla CEF behaviour.
function MB_unmark(guid)
    local obj = getObjectFromGUID(guid)
    if not obj or not obj.Browser then return false end
    obj.Browser.url = "about:blank"
    obj.setGMNotes("")
    return true
end

--- Spawn a marked tablet at `position` (default above the table).
function MB_spawn(url, position)
    spawnObject({
        type = "Tablet",
        position = position or { 0, 3, 0 },
        callback_function = function(obj)
            MB_mark(obj.getGUID(), url)
        end,
    })
end
