-- Minimal World of Warcraft API mock for smoke-testing the Audio Vibes addon outside the game.
-- Every frame/texture/fontstring is a "widget" whose unknown methods are no-ops returning the widget.
-- Scripts are recorded so tests can click buttons, type into edit boxes and fire events.

MOCK = { frames = {}, events = {}, played = {}, timers = {}, now = 1000, cvars = {}, bindings = {}, chat = {} }

local Widget = {}
local function widget(kind, name, parent)
  local w = setmetatable({ _kind = kind, _name = name, _parent = parent, _scripts = {}, _shown = true,
                           _text = "", _w = 100, _h = 20, _events = {}, _children = {} }, Widget)
  if parent and parent._children then table.insert(parent._children, w) end
  table.insert(MOCK.frames, w)
  if name then _G[name] = w end
  return w
end

local known = {
  SetScript = function(self, s, fn) self._scripts[s] = fn end,
  HookScript = function(self, s, fn)
    local old = self._scripts[s]
    self._scripts[s] = function(...) if old then old(...) end fn(...) end
  end,
  GetScript = function(self, s) return self._scripts[s] end,
  RegisterEvent = function(self, e) self._events[e] = true; MOCK.events[e] = MOCK.events[e] or {}; table.insert(MOCK.events[e], self) end,
  Show = function(self) self._shown = true end,
  Hide = function(self) self._shown = false; if self._scripts.OnHide then self._scripts.OnHide(self) end end,
  SetShown = function(self, v) self._shown = v and true or false end,
  IsShown = function(self) return self._shown end,
  IsVisible = function(self) return self._shown end,
  SetText = function(self, t) self._text = t and tostring(t) or ""; if self._kind == "EditBox" and self._scripts.OnTextChanged then self._scripts.OnTextChanged(self, true) end end,
  GetText = function(self) return self._text end,
  GetStringWidth = function(self) return #(self._text or "") * 6 end,
  SetWidth = function(self, v) self._w = v end, SetHeight = function(self, v) self._h = v end,
  SetSize = function(self, w, h) self._w = w; self._h = h end,
  GetWidth = function(self) return self._w end, GetHeight = function(self) return self._h end,
  GetPoint = function() return "CENTER", nil, "CENTER", 0, 0 end,
  GetFrameLevel = function() return 1 end,
  GetFont = function() return "Fonts\\FRIZQT__.TTF", 12, "" end,
  HasFocus = function() return false end,
  GetThumbTexture = function(self) self._thumb = self._thumb or widget("Texture", nil, self); return self._thumb end,
  CreateTexture = function(self) return widget("Texture", nil, self) end,
  CreateFontString = function(self) return widget("FontString", nil, self) end,
  SetValue = function(self, v)
    if self._min and v < self._min then v = self._min end
    if self._max and v > self._max then v = self._max end
    self._value = v
    if self._scripts.OnValueChanged then self._scripts.OnValueChanged(self, v) end
  end,
  SetMinMaxValues = function(self, a, b) self._min = a; self._max = b end,
  GetValue = function(self) return self._value or 0 end,
  GetName = function(self) return self._name end,
  GetParent = function(self) return self._parent end,
  SetBackdrop = function(self, b) self._backdrop = b end,
  SetVertexColor = function(self, ...) self._color = { ... } end,
  SetTextColor = function(self, ...) self._color = { ... } end,
}
-- WoW API methods are Capitalized; anything else (addon fields like .label) is nil, as on a real frame.
Widget.__index = function(_, k)
  if known[k] then return known[k] end
  if type(k) == "string" and k:match("^%u") then return function(self) return self end end
  return nil
end

function CreateFrame(kind, name, parent) return widget(kind or "Frame", name, parent) end
UIParent = widget("Frame", "UIParent")
GameTooltip = widget("GameTooltip", "GameTooltip")
DEFAULT_CHAT_FRAME = { AddMessage = function(_, m) table.insert(MOCK.chat, m) end }
GameFontHighlight = widget("Font", "GameFontHighlight")
STANDARD_TEXT_FONT = "Fonts\\FRIZQT__.TTF"
BackdropTemplateMixin = {}
function Mixin(o) return o end
UISpecialFrames = {}
SlashCmdList = {}
Enum = { UIMapType = { Cosmic = 0, World = 1, Continent = 2, Zone = 3, Dungeon = 4, Micro = 5, Orphan = 6 } }

tinsert = table.insert
wipe = function(t) for k in pairs(t) do t[k] = nil end return t end
strtrim = function(s) return (s:gsub("^%s+", ""):gsub("%s+$", "")) end
strlower = string.lower

function GetTime() return MOCK.now end
C_Timer = { After = function(sec, fn) table.insert(MOCK.timers, fn) end }
--- Run pending C_Timer callbacks, advancing the clock, until none are left (the send queue drains).
function MOCK.runTimers()
  for _ = 1, 200 do
    if #MOCK.timers == 0 then return end
    MOCK.now = MOCK.now + 0.3
    local t = MOCK.timers; MOCK.timers = {}
    for _, fn in ipairs(t) do fn() end
  end
end

function PlaySoundFile(path, channel) table.insert(MOCK.played, path); return true, 1 end
function ReloadUI() MOCK.reloaded = true end
function IsShiftKeyDown() return MOCK.shift end
function UnitIsDeadOrGhost() return false end
function GetMaxBattlefieldID() return 2 end
function GetBattlefieldStatus() return "none" end
function GetBindingKey(b) return MOCK.bindings[b] end

MOCK.loc = { instance = false, instanceType = "none", mapId = 1453, resting = true, instanceID = 0, instanceName = "" }
function IsInInstance() return MOCK.loc.instance, MOCK.loc.instanceType end
function GetInstanceInfo() return MOCK.loc.instanceName, "party", 1, "", 5, 0, false, MOCK.loc.instanceID end
function IsResting() return MOCK.loc.resting end
function GetRealZoneText() return "Somewhere" end
local maps = {
  [1453] = { mapID = 1453, name = "Stormwind City", mapType = 3, parentMapID = 1415 },
  [1429] = { mapID = 1429, name = "Elwynn Forest", mapType = 3, parentMapID = 1415 },
  [9001] = { mapID = 9001, name = "Goldshire", mapType = 5, parentMapID = 1429 },
  [2600] = { mapID = 2600, name = "Zephras Isle", mapType = 3, parentMapID = 1415 },
  [1415] = { mapID = 1415, name = "Eastern Kingdoms", mapType = 2, parentMapID = 947 },
}
C_Map = {
  GetBestMapForUnit = function() return MOCK.loc.mapId end,
  GetMapInfo = function(id) return maps[id] end,
}
C_CVar = {
  GetCVar = function(n) return MOCK.cvars[n] end,
  SetCVar = function(n, v) MOCK.cvars[n] = tostring(v) end,
}

function MOCK.fire(event, ...)
  for _, f in ipairs(MOCK.events[event] or {}) do
    local h = f._scripts.OnEvent
    if h then h(f, event, ...) end
  end
end

--- Click every visible-or-not button that has an OnClick, collecting errors.
function MOCK.clickAll(filter)
  local errors, n = {}, 0
  for _, f in ipairs(MOCK.frames) do
    local fn = f._scripts.OnClick
    if fn and (not filter or filter(f)) then
      n = n + 1
      local ok, err = pcall(fn, f, "LeftButton")
      if not ok then table.insert(errors, tostring(err)) end
      for _, s in ipairs({ "OnEnter", "OnLeave" }) do
        if f._scripts[s] then
          local ok2, err2 = pcall(f._scripts[s], f)
          if not ok2 then table.insert(errors, s .. ": " .. tostring(err2)) end
        end
      end
    end
  end
  return n, errors
end

function MOCK.updateAll(dt)
  local errors = {}
  for _, f in ipairs(MOCK.frames) do
    if f._scripts.OnUpdate then
      local ok, err = pcall(f._scripts.OnUpdate, f, dt or 1.1)
      if not ok then table.insert(errors, tostring(err)) end
    end
  end
  return errors
end
