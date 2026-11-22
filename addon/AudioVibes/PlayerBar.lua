-- Audio Vibes player bar (expanded 440 px / mini 300 px) and the "Now entering" toast.
-- Shows the location, its playlist and the last signal SENT. Never a track title or a
-- "connected" state: the addon can't know either.

local M = AudioVibes
local T = M.Theme
local C = T.C

local function keyText(binding)
  local k = M.KeyFor(binding)
  return k and ("Keybind: " .. k) or "No keybind set (Options › Keybindings › Audio Vibes)"
end

local function S() return M.db.settings end

-- Container: one movable anchor for both bar sizes, so collapsing keeps the position.
local holder = CreateFrame("Frame", "AudioVibesPlayer", UIParent)
holder:SetSize(440, 152)
holder:SetFrameStrata("MEDIUM")
holder:Hide()

-- ------------------------------------------------------------------ --
-- Expanded bar
-- ------------------------------------------------------------------ --
local bar = T.Panel(holder)
bar:SetAllPoints(holder)

local header = CreateFrame("Frame", nil, bar)
header:SetPoint("TOPLEFT"); header:SetPoint("TOPRIGHT"); header:SetHeight(38)
local wave = T.Icon(header, "wave", 18, C.gold)
wave:SetPoint("LEFT", 12, 0)
local collapse = T.IconButton(header, "up", nil, { size = 24, iconSize = 14, flat = true,
  tooltip = { "Collapse", "Shrink to the mini bar." } })
collapse:SetPoint("RIGHT", -8, 0)
local typePill = T.Pill(header, "Zone", "type")
typePill:SetPoint("RIGHT", collapse, "LEFT", -6, 0)

local function modeTooltip()
  local overall = S().vibeMode or "auto"
  local loc = M.location
  local mode, why = M.EffectiveMode(loc and loc.key, loc and loc.type)
  local here = loc and loc.name or "here"
  local desc
  if why == "location" then desc = ("%s is always %s. Overall mode: %s."):format(here, M.MODE_NAME[mode], M.MODE_NAME[overall])
  elseif why == "auto" then desc = ("Auto picks %s in %s (relaxed in cities and the open world, locked-in in dungeons, raids and PvP)."):format(M.MODE_NAME[mode], here)
  else desc = ("Overall mode: %s."):format(M.MODE_NAME[mode]) end
  return "Vibe mode: " .. M.MODE_NAME[mode], desc .. "\nClick to switch: Auto \226\128\186 Relaxed \226\128\186 Locked-in. " .. keyText("AUDIOVIBES_MODE"),
    "Fix one location's mode with Change on its row"
end
local modePill = T.Pill(header, "Relaxed", "relaxed", { onClick = function() M.CycleMode() end, tooltip = modeTooltip })
modePill:SetPoint("RIGHT", typePill, "LEFT", -6, 0)

local locName = T.Text(header, 16, C.gold, true)
locName:SetPoint("LEFT", wave, "RIGHT", 10, 0)
locName:SetPoint("RIGHT", modePill, "LEFT", -10, 0)
local hdrLine = T.Line(bar); hdrLine:SetPoint("TOPLEFT", 1, -38); hdrLine:SetPoint("TOPRIGHT", -1, -38)

local plRow = CreateFrame("Frame", nil, bar)
plRow:SetPoint("TOPLEFT", 0, -39); plRow:SetPoint("TOPRIGHT", 0, -39); plRow:SetHeight(30)
local note = T.Icon(plRow, "note", 16, C.muted2)
note:SetPoint("LEFT", 12, -2)
local plPill = T.Pill(plRow, "Default", "default")
plPill:SetPoint("RIGHT", -12, -2)
local plName = T.Text(plRow, 13, C.text)
plName:SetPoint("LEFT", note, "RIGHT", 10, 0)
plName:SetPoint("RIGHT", plPill, "LEFT", -10, 0)

local controls = CreateFrame("Frame", nil, bar)
controls:SetPoint("TOPLEFT", 0, -69); controls:SetPoint("TOPRIGHT", 0, -69); controls:SetHeight(52)

local function shiftHint() return "Shift-click: back to location playlist" end
local prev = T.IconButton(controls, "prev", function() M.Send("PREV") end,
  { tooltip = function() return "Previous track", keyText("AUDIOVIBES_PREV") end })
prev:SetPoint("LEFT", 12, 0)
local play = T.IconButton(controls, "play", function() M.Send("PLAY_PAUSE") end,
  { size = 40, iconSize = 18, large = true, tooltip = function() return "Play / pause", keyText("AUDIOVIBES_PLAYPAUSE") end })
play:SetPoint("LEFT", prev, "RIGHT", 6, 0)
local nxt = T.IconButton(controls, "next", function() M.Send("NEXT") end,
  { tooltip = function() return "Next track", keyText("AUDIOVIBES_NEXT") end })
nxt:SetPoint("LEFT", play, "RIGHT", 6, 0)
local div = T.Line(controls, C.chipBorder, false)
div:SetHeight(24); div:SetPoint("LEFT", nxt, "RIGHT", 10, 0)
local vdown = T.IconButton(controls, "voldown", function() M.Send("VOL_DOWN") end,
  { tooltip = { "Volume down", "Turns Spotify down 10% (Spotify only, not the game)." } })
vdown:SetPoint("LEFT", nxt, "RIGHT", 21, 0)
local vup = T.IconButton(controls, "volup", function() M.Send("VOL_UP") end,
  { tooltip = { "Volume up", "Turns Spotify up 10%." } })
vup:SetPoint("LEFT", vdown, "RIGHT", 6, 0)
local like = T.IconButton(controls, "heart", function() M.Send("LIKE_CURRENT") end,
  { tooltip = { "Like track", "Saves the playing track to your Spotify library.", "Needs Spotify connected in the companion app" } })
like:SetPoint("LEFT", vup, "RIGHT", 6, 0)
local hype = T.IconButton(controls, "bolt", function()
  if IsShiftKeyDown() and M.location then M.PlayLocation(M.location.key, "Back to location playlist")
  else M.Send("EVT_HYPE") end
end, { tooltip = function()
  local h = M.db.hype
  return "Hype track", ("Plays your Hype playlist%s. %s"):format(h and (" (" .. h.name .. ")") or "", keyText("AUDIOVIBES_HYPE")), shiftHint()
end })
hype:SetPoint("LEFT", like, "RIGHT", 6, 0)
local gear = T.IconButton(controls, "gear", function() if M.ToggleConfig then M.ToggleConfig("locations") end end,
  { tooltip = { "Audio Vibes settings", "Location playlists, settings and help.", "/vibes" } })
gear:SetPoint("RIGHT", -12, 0)

local footer = CreateFrame("Frame", nil, bar)
footer:SetPoint("BOTTOMLEFT"); footer:SetPoint("BOTTOMRIGHT"); footer:SetHeight(31)
local ftLine = T.Line(footer); ftLine:SetPoint("TOPLEFT", 1, 0); ftLine:SetPoint("TOPRIGHT", -1, 0)
local dot = T.Icon(footer, "dot", 8, C.signal)
dot:SetPoint("LEFT", 12, 0)
local auto = T.Check(footer, "Auto-switch", nil,
  function() return S().autoSwitch end,
  function(v) S().autoSwitch = v; M.Fire("SETTINGS") end, 96)
auto:SetPoint("RIGHT", -6, -1)
local sent = T.Text(footer, 11, C.muted)
sent:SetPoint("LEFT", dot, "RIGHT", 8, 0)
sent:SetPoint("RIGHT", auto, "LEFT", -8, 0)

-- ------------------------------------------------------------------ --
-- Mini bar
-- ------------------------------------------------------------------ --
local mini = T.Panel(holder)
mini:SetPoint("TOPLEFT")
mini:SetSize(300, 40)
local mWave = T.Icon(mini, "wave", 16, C.gold)
mWave:SetPoint("LEFT", 10, 0)
local expand = T.IconButton(mini, "down", nil, { size = 28, iconSize = 14, flat = true,
  tooltip = { "Expand", "Show the full player bar." } })
expand:SetPoint("RIGHT", -6, 0)
local mNext = T.IconButton(mini, "next", function() M.Send("NEXT") end,
  { size = 28, iconSize = 14, tooltip = function() return "Next track", keyText("AUDIOVIBES_NEXT") end })
mNext:SetPoint("RIGHT", expand, "LEFT", -6, 0)
local mPlay = T.IconButton(mini, "play", function() M.Send("PLAY_PAUSE") end,
  { size = 28, iconSize = 14, tooltip = function() return "Play / pause", keyText("AUDIOVIBES_PLAYPAUSE") end })
mPlay.border = C.gold; mPlay.textColor = C.gold; T.RefreshButton(mPlay)
mPlay:SetPoint("RIGHT", mNext, "LEFT", -6, 0)
local mPrev = T.IconButton(mini, "prev", function() M.Send("PREV") end,
  { size = 28, iconSize = 14, tooltip = function() return "Previous track", keyText("AUDIOVIBES_PREV") end })
mPrev:SetPoint("RIGHT", mPlay, "LEFT", -6, 0)
local mName = T.Text(mini, 12, C.text)
mName:SetPoint("LEFT", mWave, "RIGHT", 8, 0)
mName:SetPoint("RIGHT", mPrev, "LEFT", -8, 0)

T.MakeMovable(holder, header, function() return M.db.bar end, function() return S().lockBar end)
T.MakeMovable(holder, mini, function() return M.db.bar end, function() return S().lockBar end)

-- ------------------------------------------------------------------ --
-- State
-- ------------------------------------------------------------------ --
local function layout()
  local collapsed = M.db.bar.collapsed
  bar:SetShown(not collapsed)
  mini:SetShown(collapsed)
  holder:SetSize(collapsed and 300 or 440, collapsed and 40 or 152)
end

local function refreshSignal()
  local last = M.log[1]
  if last then
    sent:SetText(("Signal sent: |cffede3cc%s · %s|r"):format(last.what, T.Ago(last.t)))
  else
    sent:SetText("Signal sent: |cffede3ccnothing yet|r")
  end
end

local function refresh()
  if not M.db then return end
  local loc = M.location
  if loc then
    locName:SetText(loc.name or loc.key)
    typePill:SetLabel(T.TYPE_LABEL[loc.type] or "Zone")
    typePill:Show()
    local pl = M.PlaylistFor(loc.key)
    local mode, why = M.EffectiveMode(loc.key, loc.type)
    modePill:SetStyle(mode)
    modePill:SetLabel(M.MODE_NAME[mode] .. (why == "location" and " \194\183 here" or ""))
    modePill:Show()
    plName:SetText(pl.name)
    mName:SetText(pl.name)
    plPill:SetStyle(pl.custom and "custom" or "default")
    plPill:SetLabel(pl.custom and "Custom" or "Default")
    plPill:Show()
  else
    locName:SetText("Audio Vibes")
    typePill:Hide()
    modePill:Hide()
    plName:SetText("Waiting for your location…")
    mName:SetText("Audio Vibes")
    plPill:Hide()
  end
  auto:Refresh()
  refreshSignal()
  layout()
  holder:SetShown(S().showBar)
  holder:SetAlpha((M.inCombat and S().fadeInCombat) and 0.3 or 1)
end
M.RefreshBar = refresh

collapse:SetScript("OnClick", function() M.db.bar.collapsed = true; GameTooltip:Hide(); refresh() end)
expand:SetScript("OnClick", function() M.db.bar.collapsed = false; GameTooltip:Hide(); refresh() end)

-- keep "· 4s ago" ticking
local tick = 0
holder:SetScript("OnUpdate", function(_, dt)
  tick = tick + dt
  if tick >= 1 then tick = 0; refreshSignal() end
end)

M.On("SIGNAL", refreshSignal)
M.On("LOCATION", refresh)
M.On("PLAYLISTS", refresh)
M.On("SETTINGS", refresh)
M.On("COMBAT", refresh)

local loader = CreateFrame("Frame")
loader:RegisterEvent("PLAYER_LOGIN")
loader:SetScript("OnEvent", function()
  T.RestorePosition(holder, M.db.bar, { "CENTER", UIParent, "CENTER", 0, -220 })
  refresh()
end)

-- ------------------------------------------------------------------ --
-- "Now entering" toast (3 s)
-- ------------------------------------------------------------------ --
local toast = T.Panel(UIParent, "AudioVibesToast")
toast:SetSize(300, 50)
toast:SetPoint("TOP", UIParent, "TOP", 0, -170)
toast:SetFrameStrata("HIGH")
toast:Hide()
local tTitle = T.Text(toast, 14, C.gold, true)
tTitle:SetPoint("TOPLEFT", 12, -10); tTitle:SetPoint("RIGHT", -12, 0)
local tSub = T.Text(toast, 11, C.muted2)
tSub:SetPoint("TOPLEFT", tTitle, "BOTTOMLEFT", 0, -5); tSub:SetPoint("RIGHT", -12, 0)

local shownAt
toast:SetScript("OnUpdate", function(self)
  local age = GetTime() - shownAt
  if age > 3 then self:Hide()
  elseif age > 2.5 then self:SetAlpha((3 - age) / 0.5) end
end)

function M.ShowToast(location, playlist)
  tTitle:SetText("Now entering " .. (location or "?"))
  tSub:SetText(("Switching to \226\128\156%s\226\128\157"):format(playlist or "?"))
  shownAt = GetTime()
  toast:SetAlpha(1)
  toast:Show()
end
