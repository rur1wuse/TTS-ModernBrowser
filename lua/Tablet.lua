--[[ TTS Modern Browser - Tablet Object Script

Paste into the Object Script of a Tablet (right click tablet > Scripting >
Object). It draws a small address bar in front of the tablet and forwards the
typed URL to the Global Script's MB_setURL.

The input value is cached because Custom UI callbacks receive the field value on
change; the Go button then reads the cache, which avoids depending on how the
callback bundles `value` for InputField.
--]]

local PENDING_URL = ""

local UI_XML = [[
<Panel id="mbPanel" position="0 1.2 0" rotation="0 0 0" width="700" height="120" color="rgba(0,0,0,0.8)">
  <InputField id="mbUrl" position="-40 0 0" width="520" height="80" placeholder="https://..." lineType="SingleLine" onValueChanged="mbUrlChanged" />
  <Button id="mbGo" position="300 0 0" width="120" height="80" onClick="mbGo">Go</Button>
</Panel>
]]

function onLoad()
    self.UI.setXml(UI_XML)
end

function mbUrlChanged(player, value, id)
    PENDING_URL = value or ""
end

function mbGo(player, value, id)
    if PENDING_URL == "" then return end

    if MB_setURL then
        MB_setURL(self.getGUID(), PENDING_URL)
    else
        -- Fallback when Global functions are not visible from this script: write both
        -- channels, exactly like MB_setURL.
        self.Browser.url = "about:blank#mb=" .. (PENDING_URL:gsub("[^%w%-%._~]", function(char)
            return string.format("%%%02X", string.byte(char))
        end))
        self.setGMNotes("MB1:" .. PENDING_URL)
    end
end
