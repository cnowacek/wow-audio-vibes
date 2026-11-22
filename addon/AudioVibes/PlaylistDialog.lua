-- Audio Vibes "Change playlist" dialog (520 px). Every location has a Relaxed and a Locked-in
-- playlist; the dialog edits both (switch at the top) and the location's mode (row near the bottom).
-- Playlists save to AudioVibesDB, which WoW writes on /reload or logout; the companion app picks them
-- up from there. The location's mode is sent to the companion at once.

local M = AudioVibes
local T = M.Theme
local C = T.C

local TYPE_PLURAL = { city = "city", zone = "zone", dungeon = "dungeon", raid = "raid", pvp = "PvP map" }

local d = T.Panel(UIParent, "AudioVibesPlaylistDialog", C.panel, C.primaryBorder)
d:SetSize(520, 600)
d:SetPoint("CENTER", 0, 30)
d:SetFrameStrata("DIALOG")
d:SetToplevel(true)
d:EnableMouse(true)
d:Hide()
tinsert(UISpecialFrames, "AudioVibesPlaylistDialog")   -- Esc closes it

-- state = { scope = "location"|"hype", key, type, name, mode (tab being edited), locMode,
--           edits = { relaxed = { link, name, shuffle, applyType }, locked = { … } }, original = same shape }
local state = { edits = {}, original = {} }

-- Header
local title = T.Text(d, 18, C.gold, true)
title:SetPoint("TOPLEFT", 18, -16)
title:SetText("Change playlist")
local subtitle = T.Text(d, 12, C.muted2)
subtitle:SetPoint("TOPLEFT", title, "BOTTOMLEFT", 0, -6)
local close = T.Button(d, "", function() d:Hide() end, { width = 28, height = 28, tooltip = { "Close" } })
close:SetPoint("TOPRIGHT", -14, -14)
local x = T.Icon(close, "close", 14); x:SetPoint("CENTER"); close.icon = x; T.RefreshButton(close)
local hl = T.Line(d); hl:SetPoint("TOPLEFT", 1, -62); hl:SetPoint("TOPRIGHT", -1, -62)
T.MakeMovable(d, d, function() return {} end)

-- Which mode's playlist is being edited
local tabLabel = T.Text(d, 12, C.text); tabLabel:SetPoint("TOPLEFT", 18, -80); tabLabel:SetText("Playlist for")
local tabs = {}
local function chip(parent, text, onClick)
  local c = T.Button(parent, text, onClick, { height = 24, size = 11, padX = 22 })
  c.fill = { 0, 0, 0, 0 }; c.border = C.chipBorder; c.textColor = C.muted2
  T.RefreshButton(c)
  return c
end

-- Default line
local defBox = CreateFrame("Frame", nil, d, T.BACKDROP_TEMPLATE)
T.Skin(defBox, C.section, C.inner)
defBox:SetPoint("TOPLEFT", 18, -108); defBox:SetPoint("TOPRIGHT", -18, -108); defBox:SetHeight(38)
local defNote = T.Icon(defBox, "note", 16, C.muted2); defNote:SetPoint("LEFT", 12, 0)
local useDefault = T.Button(defBox, "Use default", nil, { height = 24, size = 11 })
useDefault:SetPoint("RIGHT", -8, 0)
local defText = T.Text(defBox, 12, C.text)
defText:SetPoint("LEFT", defNote, "RIGHT", 10, 0); defText:SetPoint("RIGHT", useDefault, "LEFT", -8, 0)

-- Link field
local linkLabel = T.Text(d, 12, C.text)
linkLabel:SetPoint("TOPLEFT", 18, -162); linkLabel:SetText("Spotify playlist link")
local link = T.Field(d, 484, "https://open.spotify.com/playlist/\226\128\166")
link:SetPoint("TOPLEFT", 18, -180)
local linkStateIcon = T.Icon(d, "check", 14, C.customText)
linkStateIcon:SetPoint("TOPLEFT", link, "BOTTOMLEFT", 0, -6)
local linkState = T.Text(d, 11, C.customText)
linkState:SetPoint("LEFT", linkStateIcon, "RIGHT", 6, 0)
local linkHint = T.Text(d, 11, C.muted)
linkHint:SetPoint("TOPLEFT", link, "BOTTOMLEFT", 0, -26)
linkHint:SetText("In Spotify: Share \226\128\186 Copy link to playlist, then paste here with Ctrl+V.")

-- Display name
local nameLabel = T.Text(d, 12, C.text)
nameLabel:SetPoint("TOPLEFT", 18, -264); nameLabel:SetText("Display name")
local nameField = T.Field(d, 484, "e.g. My Pirate Mix")
nameField:SetPoint("TOPLEFT", 18, -282)
nameField:SetMaxLetters(60)
local nameHint = T.Text(d, 11, C.muted)
nameHint:SetPoint("TOPLEFT", nameField, "BOTTOMLEFT", 0, -6)
nameHint:SetText("WoW can't read playlist names from Spotify. This is the name the player bar shows.")

-- Apply to
local applyLabel = T.Text(d, 12, C.text)
applyLabel:SetPoint("TOPLEFT", 18, -344); applyLabel:SetText("Apply to")
local function edit() return state.edits[state.mode] or {} end
local onlyHere = T.Radio(d, "This location only", function() return not edit().applyType end,
  function() edit().applyType = false end, 480)
onlyHere:SetPoint("TOPLEFT", 18, -364)
local everyType = T.Radio(d, "Every type without a custom playlist", function() return edit().applyType end,
  function() edit().applyType = true end, 480)
everyType:SetPoint("TOPLEFT", 18, -386)
local function refreshRadios() onlyHere:Refresh(); everyType:Refresh() end
onlyHere.onChanged = refreshRadios
everyType.onChanged = refreshRadios

local shuffle = T.Check(d, "Shuffle when this playlist starts", nil,
  function() return edit().shuffle end, function(v) edit().shuffle = v end, 480)
shuffle:SetPoint("TOPLEFT", 18, -414)

-- This location's mode
local modeLabel = T.Text(d, 12, C.text); modeLabel:SetPoint("TOPLEFT", 18, -446)
local modeChips = {}
local modeHint = T.Text(d, 10, C.muted); modeHint:SetPoint("TOPLEFT", 18, -496); modeHint:SetPoint("RIGHT", -18, 0)
modeHint:SetText("Applies right away. Follow overall uses the bar's mode (Auto, Relaxed or Locked-in).")

-- Notice
local notice = CreateFrame("Frame", nil, d, T.BACKDROP_TEMPLATE)
T.Skin(notice, C.warnFill, C.warnBorder)
notice:SetPoint("TOPLEFT", 18, -514); notice:SetPoint("TOPRIGHT", -18, -514); notice:SetHeight(30)
local nIcon = T.Icon(notice, "warn", 14, C.warnText); nIcon:SetPoint("LEFT", 10, 0)
local nText = T.Text(notice, 11, C.warnText)
nText:SetPoint("LEFT", nIcon, "RIGHT", 8, 0); nText:SetPoint("RIGHT", -10, 0)
nText:SetText("The companion applies saved playlists after your next /reload or logout.")

-- Footer
local fl = T.Line(d); fl:SetPoint("BOTTOMLEFT", 1, 46); fl:SetPoint("BOTTOMRIGHT", -1, 46)
local resetBtn = T.Button(d, "Reset to default", nil, { height = 28 })
resetBtn:SetPoint("BOTTOMLEFT", 18, 10)
local saveBtn = T.Button(d, "Save", nil, { height = 28, primary = true, width = 72 })
saveBtn:SetPoint("BOTTOMRIGHT", -18, 10)
local cancelBtn = T.Button(d, "Cancel", function() d:Hide() end, { height = 28 })
cancelBtn:SetPoint("RIGHT", saveBtn, "LEFT", -8, 0)

-- ------------------------------------------------------------------ --
local function validate()
  local text = strtrim(link:GetText() or "")
  if text == "" then
    linkStateIcon:Hide(); linkState:SetText(""); link:SetError(false)
    saveBtn:SetEnabledState(true)
    return true
  end
  local uri, kind = M.ParseSpotifyLink(text)
  linkStateIcon:Show()
  if uri then
    linkStateIcon:SetTexture(T.MEDIA .. "check"); linkStateIcon:SetVertexColor(unpack(C.customText))
    linkState:SetTextColor(unpack(C.customText))
    linkState:SetText(kind == "album" and "Looks like a Spotify album" or "Looks like a Spotify playlist")
    link:SetError(false)
    saveBtn:SetEnabledState(true)
    return true
  end
  linkStateIcon:SetTexture(T.MEDIA .. "cross"); linkStateIcon:SetVertexColor(unpack(C.errText))
  linkState:SetTextColor(unpack(C.errText))
  linkState:SetText("That isn't a Spotify playlist or album link")
  link:SetError(true)
  saveBtn:SetEnabledState(false)
  return false
end
link:HookScript("OnTextChanged", function(self)
  if state.mode then edit().link = self:GetText() end
  validate()
end)
nameField:HookScript("OnTextChanged", function(self) if state.mode then edit().name = self:GetText() end end)
link:SetScript("OnTabPressed", function() nameField:SetFocus() end)
nameField:SetScript("OnTabPressed", function() link:SetFocus() end)

local function showTab(mode)
  state.mode = mode
  local e = edit()
  link:SetText(e.link or "")
  nameField:SetText(e.name or "")
  for m, c in pairs(tabs) do c.active = (m == mode); T.RefreshButton(c) end
  if state.scope == "location" then
    defText:SetText(("|cffb3a98fDefault (%s): |r%s"):format(M.MODE_NAME[mode], M.DefaultPlaylistName(state.key, mode)))
  end
  refreshRadios(); shuffle:Refresh()
  validate()
end

local x0 = 110
for _, mode in ipairs(M.MODES) do
  local c = chip(d, M.MODE_NAME[mode], function() showTab(mode) end)
  c:SetPoint("TOPLEFT", x0, -76)
  x0 = x0 + c:GetWidth() + 6
  tabs[mode] = c
end

local function refreshModeChips()
  for m, c in pairs(modeChips) do c.active = ((state.locMode or "follow") == m); T.RefreshButton(c) end
end
x0 = 18
for _, def in ipairs({ { "follow", "Follow overall" }, { "relaxed", "Always relaxed" }, { "locked", "Always locked-in" } }) do
  local c = chip(d, def[2], function()
    state.locMode = def[1]
    M.SetLocationMode(state.key, def[1] ~= "follow" and def[1] or nil)
    refreshModeChips()
  end)
  c:SetPoint("TOPLEFT", x0, -466)
  x0 = x0 + c:GetWidth() + 6
  modeChips[def[1]] = c
end

useDefault:SetScript("OnClick", function() link:SetText(""); nameField:SetText(""); validate() end)

local function saveTarget(e)
  if state.scope == "hype" then return "hype", nil end
  if e.applyType then return "type", state.type end
  return "location", state.key
end

saveBtn:SetScript("OnClick", function(self)
  if self.disabled then return end
  local modes = state.scope == "hype" and { "locked" } or M.MODES
  -- check every edited mode first, so a bad link on the other tab isn't silently dropped
  for _, mode in ipairs(modes) do
    local e = state.edits[mode]
    if e and strtrim(e.link or "") ~= "" and not M.ParseSpotifyLink(e.link) then showTab(mode); return end
  end
  for _, mode in ipairs(modes) do
    local e, o = state.edits[mode], state.original[mode]
    if not (e and o) then break end
    local changed = strtrim(e.link or "") ~= strtrim(o.link or "") or (e.name or "") ~= (o.name or "")
      or (e.shuffle or false) ~= (o.shuffle or false) or (e.applyType or false) ~= (o.applyType or false)
    if changed then
      local uri = M.ParseSpotifyLink(e.link or "")
      local scope, target = saveTarget(e)
      -- moving between "this location only" and "every type": clear it from where it was
      if state.scope == "location" and (o.applyType or false) ~= (e.applyType or false) and o.link and o.link ~= "" then
        local oldScope, oldTarget = saveTarget(o)
        M.SavePlaylist(oldScope, oldTarget, mode, nil)
      end
      M.SavePlaylist(scope, target, mode, uri, strtrim(e.name or ""), e.shuffle)
    end
  end
  d:Hide()
end)

resetBtn:SetScript("OnClick", function()
  if not state.mode then return end
  local e = edit()
  local scope, target = saveTarget(state.original[state.mode] or e)
  M.SavePlaylist(scope, target, state.mode, nil)
  d:Hide()
end)

local function loadEdit(entry, applyType)
  return { link = entry and entry.uri or "", name = entry and entry.name or "", shuffle = entry and entry.shuffle or false,
           applyType = applyType or false }
end

--- opts: { scope = "location", key = "map:1429", mode = "relaxed"|"locked" (tab to open) }  or  { scope = "hype" }
function M.OpenPlaylistDialog(opts)
  state = { scope = opts.scope or "location", key = opts.key, edits = {}, original = {} }
  local locationOnly = { tabLabel, applyLabel, onlyHere, everyType, modeLabel, modeHint }
  if state.scope == "hype" then
    subtitle:SetText("Hype \194\183 plays when you press the Hype button or key")
    defText:SetText("|cffb3a98fDefault: |rthe companion's Hype playlist")
    local e = loadEdit(M.db.hype)
    state.edits.locked, state.original.locked = e, loadEdit(M.db.hype)
    for _, w in ipairs(locationOnly) do w:Hide() end
    for _, c in pairs(tabs) do c:Hide() end
    for _, c in pairs(modeChips) do c:Hide() end
    showTab("locked")
  else
    local info = M.LocationInfo(state.key)
    state.type = info.type
    subtitle:SetText(("%s \194\183 %s"):format(info.name, T.TYPE_LABEL[info.type] or "Zone"))
    onlyHere.text:SetText(info.name .. " only")
    everyType.text:SetText(("Every %s without a custom playlist"):format(TYPE_PLURAL[info.type] or info.type))
    modeLabel:SetText("Mode in " .. info.name)
    for _, mode in ipairs(M.MODES) do
      local own = M.db.locations[state.key] and M.db.locations[state.key][mode]
      local byType = M.db.types[info.type] and M.db.types[info.type][mode]
      state.edits[mode] = own and loadEdit(own) or (byType and loadEdit(byType, true)) or loadEdit(nil)
      state.original[mode] = own and loadEdit(own) or (byType and loadEdit(byType, true)) or loadEdit(nil)
    end
    state.locMode = M.db.modes[state.key] or "follow"
    for _, w in ipairs(locationOnly) do w:Show() end
    for _, c in pairs(tabs) do c:Show() end
    for _, c in pairs(modeChips) do c:Show() end
    refreshModeChips()
    showTab(opts.mode or M.EffectiveMode(state.key, info.type))
  end
  d:Show()
  d:Raise()
end

-- ------------------------------------------------------------------ --
-- Copy-link box. Addons can't write to the clipboard, so this shows the link already selected
-- in a read-only field: Ctrl+C copies it.
-- ------------------------------------------------------------------ --
local cp = T.Panel(UIParent, "AudioVibesCopyLink", C.panel, C.primaryBorder)
cp:SetSize(460, 150)
cp:SetPoint("CENTER", 0, 80)
cp:SetFrameStrata("DIALOG")
cp:SetToplevel(true)
cp:EnableMouse(true)
cp:Hide()
tinsert(UISpecialFrames, "AudioVibesCopyLink")
T.MakeMovable(cp, cp, function() return {} end)

local cpTitle = T.Text(cp, 16, C.gold, true)
cpTitle:SetPoint("TOPLEFT", 18, -16); cpTitle:SetText("Copy playlist link")
local cpSub = T.Text(cp, 11, C.muted2)
cpSub:SetPoint("TOPLEFT", cpTitle, "BOTTOMLEFT", 0, -6); cpSub:SetPoint("RIGHT", -60, 0)
local cpClose = T.IconButton(cp, "close", function() cp:Hide() end, { size = 28, iconSize = 14, tooltip = { "Close" } })
cpClose:SetPoint("TOPRIGHT", -14, -14)
local cpField = T.Field(cp, 424, "")
cpField:SetPoint("TOPLEFT", 18, -62)
local cpHint = T.Text(cp, 11, C.muted)
cpHint:SetPoint("TOPLEFT", cpField, "BOTTOMLEFT", 0, -8)
cpHint:SetText("Press Ctrl+C to copy, then paste it into your browser or Spotify.")
local cpDone = T.Button(cp, "Done", function() cp:Hide() end, { height = 24, size = 11, width = 64 })
cpDone:SetPoint("BOTTOMRIGHT", -18, 14)

local cpUrl = ""
local function selectAll() cpField:SetFocus(); cpField:HighlightText() end
cpField:HookScript("OnTextChanged", function(self, userInput)
  if userInput and self:GetText() ~= cpUrl then self:SetText(cpUrl); self:HighlightText() end   -- read-only
end)
cpField:SetScript("OnEditFocusGained", function(self) self:HighlightText() end)
cpField:SetScript("OnMouseUp", function(self) self:HighlightText() end)
cpField:SetScript("OnEscapePressed", function() cp:Hide() end)
cpField:SetScript("OnEnterPressed", function() cp:Hide() end)

--- Show a copyable link. subtitle e.g. "The Deadmines · My Pirate Mix".
function M.ShowCopyLink(url, subtitle)
  if not url then return end
  cpUrl = url
  cpSub:SetText(subtitle or "")
  cpField:SetText(url)
  cp:Show()
  cp:Raise()
  selectAll()
end

--- Copy-link for a location key; returns false when no link is known yet.
function M.CopyLinkFor(key)
  local url = M.LinkFor(key)
  if not url then return false end
  M.ShowCopyLink(url, ("%s \194\183 %s"):format(M.LocationInfo(key).name, M.PlaylistFor(key).name))
  return true
end

--- Tooltip for a Link button.
function M.LinkTooltip(key)
  if key and M.LinkFor(key) then
    return "Copy link", "Shows this location's playlist link so you can open it in your browser or Spotify."
  end
  return "Copy link", "No link yet. The companion app chooses default playlists; it fills in links once Spotify is connected, and you'll see them after a /reload.",
    "Or set your own with Change"
end

M.dialogWidgets = { frame = d, link = link, name = nameField, save = saveBtn, copy = cp, copyField = cpField,
                    tabs = tabs, modeChips = modeChips }   -- used by the smoke tests
