-- Audio Vibes config window (860 × 660): Locations · Settings · Help.

local M = AudioVibes
local T = M.Theme
local C = T.C

local W, H = 860, 660
local ROW_H = 40
local TYPE_ORDER = { city = 1, zone = 2, dungeon = 3, raid = 4, pvp = 5 }

local win = T.Panel(UIParent, "AudioVibesConfig", { C.panel[1], C.panel[2], C.panel[3], 0.97 })
win:SetSize(W, H)
win:SetFrameStrata("HIGH")
win:SetToplevel(true)
win:EnableMouse(true)
win:Hide()
tinsert(UISpecialFrames, "AudioVibesConfig")

-- ------------------------------------------------------------------ --
-- Header + tabs
-- ------------------------------------------------------------------ --
local header = CreateFrame("Frame", nil, win)
header:SetPoint("TOPLEFT"); header:SetPoint("TOPRIGHT"); header:SetHeight(48)
local hWave = T.Icon(header, "wave", 20, C.gold); hWave:SetPoint("LEFT", 14, 0)
local hTitle = T.Text(header, 19, C.gold, true); hTitle:SetPoint("LEFT", hWave, "RIGHT", 10, 0); hTitle:SetText("Audio Vibes")
local hTag = T.Text(header, 11, C.muted); hTag:SetPoint("LEFT", hTitle, "RIGHT", 10, -1); hTag:SetText("Music for every corner of Azeroth")
local hClose = T.IconButton(header, "close", function() win:Hide() end, { size = 28, iconSize = 14, tooltip = { "Close" } })
hClose:SetPoint("RIGHT", -14, 0)
T.MakeMovable(win, header, function() return M.db.window end)

local tabsLine = T.Line(win, C.border)
tabsLine:SetPoint("TOPLEFT", 1, -82); tabsLine:SetPoint("TOPRIGHT", -1, -82)

local pages, tabs = {}, {}
local current = "locations"
local function tabColors(b, on)
  T.SetFill(b, on and C.btn or C.section)
  T.SetBorder(b, on and C.border or C.chipBorder)
  b.label:SetTextColor(unpack(on and C.gold or C.muted))
end

local function showPage(name)
  current = name
  for k, p in pairs(pages) do p:SetShown(k == name) end
  for k, b in pairs(tabs) do tabColors(b, k == name) end
  if pages[name].Refresh then pages[name]:Refresh() end
end

local tabX = 14
for _, def in ipairs({ { "locations", "Locations" }, { "settings", "Settings" }, { "help", "Help" } }) do
  local key, label = def[1], def[2]
  local b = CreateFrame("Button", nil, win, T.BACKDROP_TEMPLATE)
  T.Skin(b, C.section, C.chipBorder)
  b.label = T.Text(b, 12); b.label:SetPoint("CENTER"); b.label:SetText(label)
  b:SetSize(b.label:GetStringWidth() + 32, 33)
  b:SetPoint("BOTTOMLEFT", win, "TOPLEFT", tabX, -82)
  b:SetScript("OnClick", function() showPage(key) end)
  b:SetScript("OnEnter", function(self) if current ~= key then self.label:SetTextColor(unpack(C.gold)) end end)
  b:SetScript("OnLeave", function(self) tabColors(self, current == key) end)
  tabX = tabX + b:GetWidth() + 4
  tabs[key] = b
  local p = CreateFrame("Frame", nil, win)
  p:SetPoint("TOPLEFT", 0, -83); p:SetPoint("BOTTOMRIGHT")
  p:Hide()
  pages[key] = p
end

local FOOTER_H = 44
local function footerBar(page)
  local f = CreateFrame("Frame", nil, page, T.BACKDROP_TEMPLATE)
  f:SetFrameLevel(page:GetFrameLevel() + 20)   -- always on top of anything that scrolls under it
  f:SetPoint("BOTTOMLEFT", 1, 1); f:SetPoint("BOTTOMRIGHT", -1, 1); f:SetHeight(44)
  f:SetBackdrop({ bgFile = T.WHITE }); f:SetBackdropColor(unpack(C.section))
  local l = T.Line(f, C.border); l:SetPoint("TOPLEFT"); l:SetPoint("TOPRIGHT")
  return f
end

-- ================================================================== --
-- Locations
-- ================================================================== --
local L = pages.locations
local filter, search = "all", ""

-- Filter chips + search
local chipDefs = { { "all", "All" }, { "city", "Cities" }, { "zone", "Zones" }, { "dungeon", "Dungeons" },
                   { "raid", "Raids" }, { "pvp", "PvP" }, { "custom", "Custom only" } }
local chips = {}
local chipX = 14
for _, def in ipairs(chipDefs) do
  local key = def[1]
  local c = T.Button(L, def[2], function() filter = key; L:Refresh() end, { height = 24, size = 11, padX = 22 })
  c.fill = { 0, 0, 0, 0 }; c.border = C.chipBorder; c.textColor = C.muted2
  c.baseLabel = def[2]
  c:SetPoint("TOPLEFT", chipX, -12)
  chips[key] = c
  chipX = chipX + c:GetWidth() + 6
end

local searchBox = T.Field(L, 220, "Search locations or playlists")
searchBox:SetPoint("TOPRIGHT", -14, -10)
searchBox:SetTextInsets(26, 8, 0, 0)
searchBox.placeholder:SetPoint("LEFT", 26, 0)
local sIcon = T.Icon(searchBox, "search", 14, C.muted); sIcon:SetPoint("LEFT", 8, 0)
searchBox:HookScript("OnTextChanged", function(self) search = strlower(strtrim(self:GetText() or "")); L:Refresh() end)

-- You are here
local card = CreateFrame("Frame", nil, L, T.BACKDROP_TEMPLATE)
T.Skin(card, C.card, C.primaryBorder)
card:SetPoint("TOPLEFT", 14, -48); card:SetPoint("TOPRIGHT", -14, -48); card:SetHeight(54)
local cPin = T.Icon(card, "pin", 18, C.gold); cPin:SetPoint("LEFT", 12, 0)
local cChange = T.Button(card, "Change playlist", function()
  if M.location then M.OpenPlaylistDialog({ scope = "location", key = M.location.key }) end
end, { height = 24, size = 11 })
cChange:SetPoint("RIGHT", -12, 0)
local cPlay = T.Button(card, "Play now", function()
  if M.location then M.PlayLocation(M.location.key) end
end, { height = 24, size = 11, tooltip = { "Play now", "Tells the companion to start this location's playlist now." } })
cPlay:SetPoint("RIGHT", cChange, "LEFT", -8, 0)
local cLink = T.Button(card, "Copy link", function()
  if M.location then M.CopyLinkFor(M.location.key) end
end, { height = 24, size = 11, tooltip = function() return M.LinkTooltip(M.location and M.location.key) end })
cLink:SetPoint("RIGHT", cPlay, "LEFT", -8, 0)
local cLabel = T.Text(card, 10, C.cardLabel); cLabel:SetPoint("TOPLEFT", cPin, "TOPRIGHT", 12, 6); cLabel:SetText("YOU ARE HERE")
local cLine = T.Text(card, 13, C.text)
cLine:SetPoint("TOPLEFT", cLabel, "BOTTOMLEFT", 0, -4); cLine:SetPoint("RIGHT", cLink, "LEFT", -10, 0)

-- Column headers
local COLS = { 24, 232, 300, 0 }   -- x of Location, Type, Playlist; actions (Link Play Change Reset) right-aligned
local PL_W = 130                    -- playlist name width before it's cut short; pills sit after it
local colHdr = CreateFrame("Frame", nil, L)
colHdr:SetPoint("TOPLEFT", 14, -112); colHdr:SetPoint("TOPRIGHT", -14, -112); colHdr:SetHeight(20)
for i, label in ipairs({ "LOCATION", "TYPE", "PLAYLIST" }) do
  local t = T.Text(colHdr, 10, C.label); t:SetPoint("LEFT", COLS[i] - 12, 0); t:SetText(label)
end
local actHdr = T.Text(colHdr, 10, C.label); actHdr:SetPoint("RIGHT", -24, 0); actHdr:SetText("ACTIONS")

-- List (virtual rows)
-- Page area is H - 83 (header + tabs). The list sits between the column headers and the footer,
-- and shows only as many rows as fit, so nothing runs under the footer.
local LIST_TOP, LIST_GAP = 134, 6
local LIST_H = (H - 83) - LIST_TOP - FOOTER_H - LIST_GAP
local VISIBLE = math.floor((LIST_H - 2) / ROW_H)
local list = CreateFrame("Frame", nil, L)
list:SetPoint("TOPLEFT", 14, -LIST_TOP); list:SetPoint("TOPRIGHT", -14, -LIST_TOP)
list:SetHeight(LIST_H)
local listTop = T.Line(list); listTop:SetPoint("TOPLEFT"); listTop:SetPoint("TOPRIGHT")
M._layout = { listHeight = LIST_H, visibleRows = VISIBLE, rowHeight = ROW_H }
local rows, data, offset = {}, {}, 0

local scroll = CreateFrame("Slider", nil, list, T.BACKDROP_TEMPLATE)
scroll:SetOrientation("VERTICAL")
scroll:SetPoint("TOPRIGHT", -2, -4); scroll:SetPoint("BOTTOMRIGHT", -2, 4); scroll:SetWidth(6)
scroll:SetBackdrop({ bgFile = T.WHITE }); scroll:SetBackdropColor(unpack(C.section))
scroll:SetThumbTexture(T.WHITE)
scroll:GetThumbTexture():SetVertexColor(unpack(C.btnBorder))
scroll:GetThumbTexture():SetSize(6, 40)
scroll:SetValueStep(1)
if scroll.SetObeyStepOnDrag then scroll:SetObeyStepOnDrag(true) end

local function makeRow(i)
  local r = CreateFrame("Frame", nil, list, T.BACKDROP_TEMPLATE)
  r:SetPoint("TOPLEFT", 0, -1 - (i - 1) * ROW_H); r:SetPoint("RIGHT", -10, 0); r:SetHeight(ROW_H)
  r:SetBackdrop({ bgFile = T.WHITE })
  local line = T.Line(r, C.btn); line:SetPoint("BOTTOMLEFT"); line:SetPoint("BOTTOMRIGHT")
  r.name = T.Text(r, 12, C.text); r.name:SetPoint("LEFT", COLS[1] - 12, 0); r.name:SetWidth(COLS[2] - COLS[1] - 10)
  r.new = T.Text(r, 10, C.newText); r.new:SetText("New in Forever")
  r.type = T.Text(r, 11, C.muted2); r.type:SetPoint("LEFT", COLS[2] - 12, 0)
  r.pl = T.Text(r, 12, C.text); r.pl:SetPoint("TOPLEFT", COLS[3] - 12, -7); r.pl:SetWidth(PL_W)
  r.mode = T.Text(r, 10, C.newText); r.mode:SetPoint("TOPLEFT", r.pl, "BOTTOMLEFT", 0, -3)
  r.custom = T.Pill(r, "Custom", "custom")
  r.pending = T.Pill(r, "Pending reload", "pending")
  r.reset = T.Button(r, "Reset", function()
    local mode = M.EffectiveMode(r.key)
    M.SavePlaylist("location", r.key, mode, nil)
  end, { height = 22, size = 11, tooltip = function()
    local mode = r.key and M.EffectiveMode(r.key) or "relaxed"
    return "Reset", ("Back to the default %s playlist. The other mode's playlist is kept."):format(M.MODE_NAME[mode])
  end })
  r.reset:SetPoint("RIGHT", -8, 0)
  r.change = T.Button(r, "Change", function() M.OpenPlaylistDialog({ scope = "location", key = r.key }) end, { height = 22, size = 11 })
  r.play = T.Button(r, "Play", function() M.PlayLocation(r.key) end,
    { height = 22, size = 11, tooltip = function() return "Play", "Start this location's playlist now." end })
  r.link = T.Button(r, "Link", function() M.CopyLinkFor(r.key) end,
    { height = 22, size = 11, tooltip = function() return M.LinkTooltip(r.key) end })
  r:EnableMouseWheel(true)
  r:SetScript("OnMouseWheel", function(_, delta) scroll:SetValue(offset - delta * 3) end)
  return r
end
for i = 1, VISIBLE do rows[i] = makeRow(i) end
list:EnableMouseWheel(true)
list:SetScript("OnMouseWheel", function(_, delta) scroll:SetValue(offset - delta * 3) end)

local function fillRows()
  for i = 1, VISIBLE do
    local r, e = rows[i], data[offset + i]
    if not e then r:Hide() else
      r:Show()
      r.key = e.key
      r:SetBackdropColor(unpack((offset + i) % 2 == 0 and C.section or { 0, 0, 0, 0 }))
      r.name:SetText(e.name)
      r.name:ClearAllPoints()
      if e.isNew then
        r.name:SetPoint("TOPLEFT", COLS[1] - 12, -7)
        r.new:ClearAllPoints(); r.new:SetPoint("TOPLEFT", r.name, "BOTTOMLEFT", 0, -3); r.new:Show()
      else
        r.name:SetPoint("LEFT", COLS[1] - 12, 0); r.new:Hide()
      end
      r.type:SetText(T.TYPE_LABEL[e.type] or e.type)
      r.pl:SetText(e.pl.name)
      r.mode:SetText(M.MODE_NAME[e.mode] .. (e.modeWhy == "location" and " \194\183 always here" or ""))
      r.mode:SetTextColor(unpack(e.mode == "locked" and C.gold or C.newText))
      local w = math.min(r.pl:GetStringWidth(), PL_W)
      r.custom:ClearAllPoints(); r.custom:SetPoint("LEFT", r.pl, "LEFT", w + 8, 0)
      r.custom:SetShown(e.pl.custom)
      r.pending:ClearAllPoints()
      r.pending:SetPoint("LEFT", e.pl.custom and r.custom or r.pl, e.pl.custom and "RIGHT" or "LEFT", e.pl.custom and 6 or (w + 8), 0)
      r.pending:SetShown(e.pending)
      r.reset:SetShown(e.ownCustom)
      r.change:ClearAllPoints()
      if e.ownCustom then r.change:SetPoint("RIGHT", r.reset, "LEFT", -6, 0) else r.change:SetPoint("RIGHT", -8, 0) end
      r.play:ClearAllPoints(); r.play:SetPoint("RIGHT", r.change, "LEFT", -6, 0)
      r.link:ClearAllPoints(); r.link:SetPoint("RIGHT", r.play, "LEFT", -6, 0)
      r.link:SetEnabledState(e.link ~= nil)
    end
  end
end

scroll:SetScript("OnValueChanged", function(_, v) offset = math.floor(v + 0.5); fillRows() end)

-- Footer
local lf = footerBar(L)
local lCount = T.Text(lf, 11, C.muted); lCount:SetPoint("LEFT", 14, 0)
local reloadBtn = T.Button(lf, "Reload UI", function() ReloadUI() end, { primary = true, height = 26,
  tooltip = { "Reload UI", "Saves your changes to disk so the companion app can pick them up." } })
reloadBtn:SetPoint("RIGHT", -14, 0)
local lPending = T.Text(lf, 11, C.warnText); lPending:SetPoint("RIGHT", reloadBtn, "LEFT", -12, 0)

local function allKeys()
  local keys, seen = {}, {}
  for k in pairs(AudioVibesLocations or {}) do keys[#keys + 1] = k; seen[k] = true end
  for k in pairs(M.db.seen) do if not seen[k] then keys[#keys + 1] = k end end
  return keys
end

function L:Refresh()
  if not M.db then return end
  local entries, counts, customCount = {}, { all = 0, city = 0, zone = 0, dungeon = 0, raid = 0, pvp = 0, custom = 0 }, 0
  for _, key in ipairs(allKeys()) do
    local info = M.LocationInfo(key)
    local mode, why = M.EffectiveMode(key, info.type)
    local pl = M.PlaylistFor(key, mode)
    local own = M.db.locations[key] ~= nil and M.db.locations[key][mode] ~= nil
    local e = { key = key, name = info.name, type = info.type, isNew = info.isNew, pl = pl, ownCustom = own,
                mode = mode, modeWhy = why,
                link = M.OpenUrl(pl.uri),
                pending = M.db.pending[key] or (pl.scope == "type" and M.db.pending["type:" .. info.type]) or false }
    counts.all = counts.all + 1
    counts[info.type] = (counts[info.type] or 0) + 1
    if pl.custom then counts.custom = counts.custom + 1 end
    if own then customCount = customCount + 1 end
    local pass = filter == "all" or filter == info.type or (filter == "custom" and pl.custom)
    if pass and search ~= "" then
      pass = strlower(info.name):find(search, 1, true) or strlower(pl.name or ""):find(search, 1, true)
    end
    if pass then entries[#entries + 1] = e end
  end
  table.sort(entries, function(a, b)
    local ta, tb = TYPE_ORDER[a.type] or 9, TYPE_ORDER[b.type] or 9
    if ta ~= tb then return ta < tb end
    return a.name < b.name
  end)
  data = entries

  local x = 14
  for _, def in ipairs(chipDefs) do
    local c = chips[def[1]]
    c:SetLabel(def[1] == "custom" and def[2] or (def[2] .. " " .. (counts[def[1]] or 0)))
    c.active = (filter == def[1])
    c:ClearAllPoints(); c:SetPoint("TOPLEFT", x, -12)
    x = x + c:GetWidth() + 6
    T.RefreshButton(c)
  end

  local maxOff = math.max(0, #data - VISIBLE)
  scroll:SetMinMaxValues(0, maxOff)
  scroll:SetShown(maxOff > 0)
  if offset > maxOff then offset = maxOff end
  scroll:SetValue(offset)
  fillRows()

  local loc = M.location
  if loc then
    local pl = M.PlaylistFor(loc.key)
    local mode = M.EffectiveMode(loc.key, loc.type)
    cLine:SetText(("|cffffd100%s|r|cffb3a98f \194\183 %s \194\183 |r%s|cffb3a98f \194\183 %s|r"):format(
      loc.name, T.TYPE_LABEL[loc.type] or "Zone", pl.name, M.MODE_NAME[mode]))
    cPlay:SetEnabledState(true); cChange:SetEnabledState(true)
    cLink:SetEnabledState(M.LinkFor(loc.key) ~= nil)
  else
    cLine:SetText("|cffb3a98fWaiting for your location…|r")
    cPlay:SetEnabledState(false); cChange:SetEnabledState(false); cLink:SetEnabledState(false)
  end

  lCount:SetText(("%d locations \194\183 %d custom"):format(counts.all, customCount))
  local pending = M.PendingCount()
  lPending:SetText(pending > 0 and ("%d change%s saved. Reload to send %s to the companion."):format(
    pending, pending == 1 and "" or "s", pending == 1 and "it" or "them") or "")
  reloadBtn:SetShown(pending > 0)
end

-- ================================================================== --
-- Settings
-- ================================================================== --
local Sg = pages.settings
local function S() return M.db.settings end
local COLW = (W - 14 * 3) / 2

local function box(title, x, y, h)
  local b = CreateFrame("Frame", nil, Sg, T.BACKDROP_TEMPLATE)
  T.Skin(b, C.section, C.inner)
  b:SetPoint("TOPLEFT", x, y); b:SetSize(COLW, h)
  local t = T.Text(b, 15, C.gold, true); t:SetPoint("TOPLEFT", 14, -12); t:SetText(title)
  b.y = -38
  return b
end

local checks = {}
local function addCheck(b, label, hint, key)
  local c = T.Check(b, label, hint, function() return S()[key] end,
    function(v) S()[key] = v; M.Fire("SETTINGS") end, COLW - 28)
  c:SetPoint("TOPLEFT", 14, b.y)
  b.y = b.y - 36
  checks[#checks + 1] = c
  return c
end

-- Three rows of boxes, all ending above the footer (page height H-83, footer FOOTER_H):
--   row 1 y=-14 h=150 · row 2 y=-176 h=186 · row 3 y=-374 h=150 → bottom at -524, footer top at -533.
local ROW1, ROW2, ROW3 = -14, -176, -374
local music = box("Music", 14, ROW1, 150)
addCheck(music, "Switch playlist when I change location", "Cities, zones, dungeons and raids each have their own playlist", "autoSwitch")
addCheck(music, "Resume the zone playlist after an instance", "Leaving a dungeon picks up the outside playlist again", "resumeAfterInstance")
addCheck(music, "Shuffle when a playlist starts", "Applies to every location unless it sets its own", "shuffle")

local events = box("Events", 28 + COLW, ROW1, 150)
addCheck(events, "Lower music when a queue pops", "Back to normal after 8 seconds", "queueDuck")
addCheck(events, "Lower music while I'm dead", "Back to normal when you resurrect", "deathDuck")
addCheck(events, "Play a fanfare on level up", "Then returns to the location playlist", "levelFanfare")

local barBox = box("Player bar", 14, ROW2, 186)
addCheck(barBox, "Show the player bar", "Or use /vibes show", "showBar")
addCheck(barBox, "Lock position", "Unlock to drag the bar by its header", "lockBar")
addCheck(barBox, "Fade the bar in combat", "Fades to 30% so it stays out of your way", "fadeInCombat")
addCheck(barBox, "Show \226\128\156Now entering\226\128\157 toast", "A short note when the playlist changes", "toast")

-- Signal check
local sig = box("Signal check", 28 + COLW, ROW2, 186)
local function statusLine(parent, y)
  local f = CreateFrame("Frame", nil, parent)
  f:SetPoint("TOPLEFT", 14, y); f:SetPoint("RIGHT", -14, 0); f:SetHeight(24)
  f.icon = T.Icon(f, "check", 14, C.customText); f.icon:SetPoint("LEFT")
  f.fix = T.Button(f, "Turn on", nil, { height = 22, size = 11 }); f.fix:SetPoint("RIGHT")
  f.text = T.Text(f, 11, C.text); f.text:SetPoint("LEFT", f.icon, "RIGHT", 8, 0); f.text:SetPoint("RIGHT", f.fix, "LEFT", -8, 0)
  f.text:SetWordWrap(true)
  return f
end
local sndLine = statusLine(sig, -38)
local bgLine = statusLine(sig, -64)
sndLine.fix:SetScript("OnClick", function()
  M.SetCVar("Sound_EnableAllSound", "1")
  if (tonumber(M.GetCVar("Sound_MasterVolume") or "1") or 1) <= 0 then M.SetCVar("Sound_MasterVolume", "0.5") end
  Sg:Refresh()
end)
bgLine.fix:SetScript("OnClick", function() M.SetCVar("Sound_EnableSoundWhenGameIsInBackground", "1"); Sg:Refresh() end)

local testBtn = T.Button(sig, "Send test signal", function() M.Send("PING", "Ping (test)") end, { primary = true, height = 24, size = 11 })
testBtn:SetPoint("TOPLEFT", 14, -94)
local testHint = T.Text(sig, 10, C.muted); testHint:SetPoint("LEFT", testBtn, "RIGHT", 8, 0); testHint:SetPoint("RIGHT", -10, 0)
testHint:SetText("The tray icon turns green when it's heard.")
local recent = T.Text(sig, 10, C.label); recent:SetPoint("TOPLEFT", 14, -126); recent:SetText("RECENTLY SENT")
local logLines = {}
local LOG_LINES = 3
for i = 1, LOG_LINES do
  local what = T.Text(sig, 11, C.text); what:SetPoint("TOPLEFT", 14, -126 - i * 16); what:SetWidth(COLW - 110)
  local when = T.Text(sig, 11, C.muted); when:SetPoint("TOPRIGHT", -14, -126 - i * 16); when:SetJustifyH("RIGHT")
  logLines[i] = { what = what, when = when }
end

-- Hype & keybinds
local hypeBox = box("Hype & keybinds", 14, ROW3, 150)
local hypeLine = T.Text(hypeBox, 12, C.text); hypeLine:SetPoint("TOPLEFT", 14, -42); hypeLine:SetWidth(COLW - 110)
local hypeChange = T.Button(hypeBox, "Change", function() M.OpenPlaylistDialog({ scope = "hype" }) end, { height = 22, size = 11 })
hypeChange:SetPoint("TOPRIGHT", -14, -38)
local binds = {}
for i, def in ipairs({ { "Play / pause", "AUDIOVIBES_PLAYPAUSE" }, { "Next track", "AUDIOVIBES_NEXT" },
                       { "Previous track", "AUDIOVIBES_PREV" }, { "Hype", "AUDIOVIBES_HYPE" },
                       { "Switch vibe mode", "AUDIOVIBES_MODE" } }) do
  local a = T.Text(hypeBox, 11, C.text); a:SetPoint("TOPLEFT", 14, -54 - i * 13); a:SetText(def[1])
  local k = T.Text(hypeBox, 11, C.muted2); k:SetPoint("TOPRIGHT", -14, -54 - i * 13); k:SetJustifyH("RIGHT")
  binds[i] = { key = k, binding = def[2] }
end

-- Vibe mode (row 3, right): overall mode + pick up where you left off
local modeBox = box("Vibe mode", 28 + COLW, ROW3, 150)
local modeRadios = {}
for i, def in ipairs({ { "auto", "Auto" }, { "relaxed", "Always relaxed" }, { "locked", "Always locked-in" } }) do
  local r = T.Radio(modeBox, def[2], function() return (S().vibeMode or "auto") == def[1] end,
    function() M.SetOverallMode(def[1]) end, COLW - 28)
  r:SetPoint("TOPLEFT", 14, -36 - (i - 1) * 20)
  r.onChanged = function() for _, x in ipairs(modeRadios) do x:Refresh() end end
  modeRadios[i] = r
end
local modeHint = T.Text(modeBox, 10, C.muted); modeHint:SetPoint("TOPLEFT", 14, -98); modeHint:SetPoint("RIGHT", -14, 0)
modeHint:SetText("Auto: relaxed in cities and the open world, locked-in in dungeons, raids and PvP.")
local resumeCheck = T.Check(modeBox, "Pick up where I left off", "Switching back to a playlist resumes its last track and position",
  function() return S().resume end, function(v) S().resume = v; M.Fire("SETTINGS") end, COLW - 28)
resumeCheck:SetPoint("TOPLEFT", 14, -114)
checks[#checks + 1] = resumeCheck
for _, r in ipairs(modeRadios) do checks[#checks + 1] = r end
local bindHint = T.Text(hypeBox, 10, C.muted); bindHint:SetPoint("BOTTOMLEFT", 14, 8)
bindHint:SetText("Change in Game Menu \226\128\186 Options \226\128\186 Keybindings \226\128\186 Audio Vibes.")
M._layout.settingsBottom = -(ROW3 * -1 + 150)            -- y of the lowest boxes' bottom edges (Hype, Vibe mode)
M._layout.footerTop = -((H - 83) - FOOTER_H)             -- y of the footer's top edge

local sf = footerBar(Sg)
local ver = T.Text(sf, 11, C.muted); ver:SetPoint("LEFT", 14, 0)
ver:SetText("Audio Vibes " .. M.VERSION .. " \194\183 /vibes for commands")
local resetAll = T.Button(sf, "Reset all settings", function()
  M.db.settings = {
    vibeMode = "auto", resume = true,
    autoSwitch = true, resumeAfterInstance = true, shuffle = false, queueDuck = true, deathDuck = true,
    levelFanfare = false, combatEvents = false, login = true, showBar = true, lockBar = false,
    fadeInCombat = true, toast = true,
  }
  M.ModeChanged()
  M.Fire("SETTINGS")
end, { height = 26, tooltip = { "Reset all settings", "Puts every option on this tab back to its default. Your playlists are kept." } })
resetAll:SetPoint("RIGHT", -14, 0)

function Sg:Refresh()
  if not M.db then return end
  for _, c in ipairs(checks) do c:Refresh() end
  local s = M.SoundStatus()
  local function setLine(l, ok, okText, badText)
    l.icon:SetTexture(T.MEDIA .. (ok and "check" or "warn"))
    l.icon:SetVertexColor(unpack(ok and C.customText or C.warnText))
    l.text:SetText(ok and okText or badText)
    l.fix:SetShown(not ok)
  end
  setLine(sndLine, s.soundOn, "Game sound and master volume are on", "Game sound or master volume is off. Signals can't be heard.")
  setLine(bgLine, s.background, "Sound in background is on", "Sound in background is off. Signals stop while you're alt-tabbed.")
  for i = 1, LOG_LINES do
    local e = M.log[i]
    logLines[i].what:SetText(e and e.what or (i == 1 and "|cffb3a98fNothing sent yet|r" or ""))
    logLines[i].when:SetText(e and T.Ago(e.t) or "")
  end
  local h = M.db.hype
  hypeLine:SetText("|cffb3a98fHype playlist: |r" .. (h and h.name or "the companion's default"))
  for _, b in ipairs(binds) do b.key:SetText(M.KeyFor(b.binding) or "not set") end
end

local tick = 0
Sg:SetScript("OnUpdate", function(self, dt)
  tick = tick + dt
  if tick > 1 then tick = 0; self:Refresh() end
end)

-- ================================================================== --
-- Help (not in the mock-ups: plain text in the same style)
-- ================================================================== --
local Hp = pages.help
local helpText = T.Text(Hp, 12, C.text)
helpText:SetPoint("TOPLEFT", 24, -20); helpText:SetPoint("RIGHT", -24, 0)
helpText:SetJustifyV("TOP"); helpText:SetWordWrap(true); helpText:SetSpacing(4)
helpText:SetText(table.concat({
  "|cffffd100How it works|r",
  "Audio Vibes plays tiny, near-silent sound signals. The WoW Audio Vibes companion app on your PC hears them in WoW's audio and plays the music: Spotify, internet radio, or music folders on your PC. Signals only go one way, so this window shows what was sent, not what's playing. The companion's tray icon is where you see that it's connected.",
  " ",
  "|cffffd100Location playlists|r",
  "Every city, zone, dungeon, raid and PvP map has its own playlist. Without Spotify the defaults are free internet radio stations. Change one with Change on the Locations tab and paste a Spotify playlist or album link, a radio stream URL (https://\226\128\166), or a folder on your PC (C:\\Music\\\226\128\166). WoW only saves to disk on /reload or logout, so press Reload UI after changing playlists; the companion picks the changes up from there.",
  " ",
  "|cffffd100Commands|r",
  "/vibes (this window) \194\183 /vibes show (player bar) \194\183 /vibes settings \194\183 /vibes zone (current location id) \194\183 /vibes test",
  "/vibes next \194\183 prev \194\183 play \194\183 vol+ \194\183 vol- \194\183 like \194\183 hype",
  " ",
  "|cffffd100Nothing happens?|r",
  "1. Is the companion app running, and is its tray icon green? Press Send test signal on the Settings tab.",
  "2. Check the Signal check box: game sound must be on, and Sound in background keeps it working while you're alt-tabbed.",
  "3. Spotify playlists and Like need Spotify connected in the companion (tray menu \226\128\186 Connect Spotify). Radio and folders don't.",
  "4. After installing or updating the addon, restart WoW completely; /reload doesn't load new sound files.",
}, "\n"))

-- ------------------------------------------------------------------ --
function M.ToggleConfig(page, forceShow)
  if not M.db then return end
  if win:IsShown() and not forceShow and (not page or page == current) then win:Hide() return end
  T.RestorePosition(win, M.db.window, { "CENTER", UIParent, "CENTER", 0, 40 })
  win:Show()
  win:Raise()
  showPage(page or current)
end

local function refreshVisible()
  if win:IsShown() and pages[current].Refresh then pages[current]:Refresh() end
end
M.On("LOCATION", refreshVisible)
M.On("PLAYLISTS", refreshVisible)
M.On("SETTINGS", refreshVisible)
M.On("SIGNAL", refreshVisible)
