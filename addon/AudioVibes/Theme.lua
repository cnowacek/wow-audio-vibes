-- Audio Vibes theme: design tokens and small widget builders (BackdropTemplate frames, no custom art
-- except the icon textures in media/). Matches the visual style table in the addon UI spec.

AudioVibes = AudioVibes or {}
local A = AudioVibes
local T = {}
A.Theme = T

T.MEDIA = "Interface\\AddOns\\AudioVibes\\media\\"
T.WHITE = "Interface\\Buttons\\WHITE8X8"
T.BACKDROP_TEMPLATE = BackdropTemplateMixin and "BackdropTemplate" or nil
T.DISPLAY_FONT = "Fonts\\FRIZQT__.TTF"
T.BODY_FONT = (GameFontHighlight and GameFontHighlight:GetFont()) or STANDARD_TEXT_FONT or T.DISPLAY_FONT

local function hex(h, a)
  return { tonumber(h:sub(1, 2), 16) / 255, tonumber(h:sub(3, 4), 16) / 255, tonumber(h:sub(5, 6), 16) / 255, a or 1 }
end

T.C = {
  panel = hex("14110D", 0.96), section = hex("17130E"), border = hex("6B5A36"), inner = hex("2A2318"),
  btn = hex("221C13"), btnBorder = hex("5A4A2C"), chipBorder = hex("3D3322"), active = hex("3A2C12"),
  primary = hex("7A1A10"), primaryBorder = hex("C79A2E"),
  gold = hex("FFD100"), text = hex("EDE3CC"), muted = hex("B3A98F"), muted2 = hex("C9BFA6"), label = hex("9A907A"),
  customText = hex("6FE0C6"), customFill = hex("0F2A25"), customBorder = hex("2F6F60"),
  warnText = hex("FFB35C"), warnBorder = hex("7A4F1E"), warnFill = hex("24180B"),
  errText = hex("FF8A7D"), errBorder = hex("E0574A"),
  newText = hex("8FC7FF"), signal = hex("4FD1B5"),
  field = hex("0F0D0A"), card = hex("2A2210"), cardLabel = hex("E0C46A"), black = hex("000000"),
}
local C = T.C

-- ------------------------------------------------------------------ --
-- Basics
-- ------------------------------------------------------------------ --
function T.Skin(f, fill, border)
  if not f.SetBackdrop then Mixin(f, BackdropTemplateMixin) end
  f:SetBackdrop({ bgFile = T.WHITE, edgeFile = T.WHITE, edgeSize = 1 })
  f:SetBackdropColor(unpack(fill or C.panel))
  f:SetBackdropBorderColor(unpack(border or C.border))
end

function T.SetBorder(f, color) f:SetBackdropBorderColor(unpack(color)) end
function T.SetFill(f, color) f:SetBackdropColor(unpack(color)) end

--- A framed panel: fill, gold-brown border, 1 px black outer line.
function T.Panel(parent, name, fill, border)
  local f = CreateFrame("Frame", name, parent or UIParent, T.BACKDROP_TEMPLATE)
  T.Skin(f, fill or C.panel, border or C.border)
  local o = CreateFrame("Frame", nil, f, T.BACKDROP_TEMPLATE)
  o:SetPoint("TOPLEFT", -1, 1)
  o:SetPoint("BOTTOMRIGHT", 1, -1)
  o:SetFrameLevel(math.max(f:GetFrameLevel() - 1, 0))
  o:SetBackdrop({ edgeFile = T.WHITE, edgeSize = 1 })
  o:SetBackdropBorderColor(unpack(C.black))
  return f
end

function T.Line(parent, color, horizontal)
  local t = parent:CreateTexture(nil, "ARTWORK")
  t:SetTexture(T.WHITE)
  t:SetVertexColor(unpack(color or C.inner))
  if horizontal ~= false then t:SetHeight(1) else t:SetWidth(1) end
  return t
end

function T.Text(parent, size, color, display, layer)
  local fs = parent:CreateFontString(nil, layer or "OVERLAY")
  fs:SetFont(display and T.DISPLAY_FONT or T.BODY_FONT, size or 12, "")
  fs:SetTextColor(unpack(color or C.text))
  fs:SetJustifyH("LEFT")
  fs:SetWordWrap(false)
  return fs
end

function T.Icon(parent, name, size, color, layer)
  local t = parent:CreateTexture(nil, layer or "ARTWORK")
  t:SetTexture(T.MEDIA .. name)
  t:SetSize(size or 16, size or 16)
  t:SetVertexColor(unpack(color or C.text))
  return t
end

-- ------------------------------------------------------------------ --
-- Tooltips: WoW's standard dark tooltip; title white, description, grey hint.
-- ------------------------------------------------------------------ --
function T.ShowTooltip(owner, title, desc, hint)
  if not title then return end
  GameTooltip:SetOwner(owner, "ANCHOR_TOP")
  GameTooltip:SetText(title, 1, 1, 1)
  if desc and desc ~= "" then GameTooltip:AddLine(desc, 0.85, 0.85, 0.85, true) end
  if hint and hint ~= "" then GameTooltip:AddLine(hint, 0.66, 0.69, 0.75, true) end
  GameTooltip:Show()
end

local function tooltipOf(self)
  local t = self.tooltip
  if type(t) == "function" then return t(self) end
  if type(t) == "table" then return t[1], t[2], t[3] end
end

-- ------------------------------------------------------------------ --
-- Buttons
-- ------------------------------------------------------------------ --
local function buttonColors(b, hover)
  if b.primary then
    T.SetFill(b, C.primary); T.SetBorder(b, hover and C.gold or C.primaryBorder)
    if b.label then b.label:SetTextColor(unpack(C.gold)) end
  elseif b.active then
    T.SetFill(b, C.active); T.SetBorder(b, C.gold)
    if b.label then b.label:SetTextColor(unpack(C.gold)) end
    if b.icon then b.icon:SetVertexColor(unpack(C.gold)) end
  else
    if not b.flat then T.SetFill(b, b.fill or C.btn); T.SetBorder(b, hover and C.gold or (b.border or C.btnBorder)) end
    local c = hover and C.gold or (b.textColor or C.text)
    if b.label then b.label:SetTextColor(unpack(c)) end
    if b.icon then b.icon:SetVertexColor(unpack(c)) end
  end
  if b.disabled then
    if b.label then b.label:SetTextColor(unpack(C.label)) end
    if b.icon then b.icon:SetVertexColor(unpack(C.label)) end
  end
end
T.RefreshButton = function(b) buttonColors(b, b.hovered) end

local function wireButton(b, onClick)
  b:RegisterForClicks("LeftButtonUp")
  b:SetScript("OnEnter", function(self)
    self.hovered = true
    buttonColors(self, not self.disabled)
    T.ShowTooltip(self, tooltipOf(self))
  end)
  b:SetScript("OnLeave", function(self)
    self.hovered = false
    buttonColors(self, false)
    GameTooltip:Hide()
  end)
  b:SetScript("OnClick", function(self, mouse)
    if self.disabled then return end
    if onClick then onClick(self, mouse) end
    if self.hovered then T.ShowTooltip(self, tooltipOf(self)) end
  end)
  buttonColors(b, false)
end

--- Text button. opts: primary, width, height, size, tooltip
function T.Button(parent, text, onClick, opts)
  opts = opts or {}
  local b = CreateFrame("Button", nil, parent, T.BACKDROP_TEMPLATE)
  T.Skin(b, C.btn, C.btnBorder)
  b.primary = opts.primary
  b.tooltip = opts.tooltip
  b.label = T.Text(b, opts.size or 12)
  b.label:SetPoint("CENTER")
  b.label:SetJustifyH("CENTER")
  function b:SetLabel(s)
    self.label:SetText(s)
    if not opts.width then self:SetWidth(math.ceil(self.label:GetStringWidth()) + (opts.padX or 20)) end
  end
  b:SetHeight(opts.height or 24)
  b:SetLabel(text)
  if opts.width then b:SetWidth(opts.width) end
  function b:SetEnabledState(on) self.disabled = not on; buttonColors(self, false) end
  wireButton(b, onClick)
  return b
end

--- Icon-only button. opts: size (frame), iconSize, large, flat, tooltip
function T.IconButton(parent, icon, onClick, opts)
  opts = opts or {}
  local size = opts.size or 32
  local b = CreateFrame("Button", nil, parent, T.BACKDROP_TEMPLATE)
  b:SetSize(size, size)
  b.flat = opts.flat
  if not opts.flat then T.Skin(b, C.btn, C.btnBorder) end
  if opts.large then b.fill = C.active; b.border = C.gold; b.textColor = C.gold end
  b.icon = T.Icon(b, icon, opts.iconSize or 16)
  b.icon:SetPoint("CENTER")
  b.tooltip = opts.tooltip
  function b:SetEnabledState(on) self.disabled = not on; buttonColors(self, self.hovered and on) end
  wireButton(b, onClick)
  return b
end

-- ------------------------------------------------------------------ --
-- Pills: rounded labels in the mock-up; square-cornered 1 px boxes here.
-- styles: default | custom | pending | type | new
-- ------------------------------------------------------------------ --
local PILL = {
  default = { C.muted2, nil, C.chipBorder },
  type    = { C.muted2, nil, C.btnBorder },
  custom  = { C.customText, C.customFill, C.customBorder },
  pending = { C.warnText, nil, C.warnBorder },
  relaxed = { C.newText, nil, C.chipBorder },
  locked  = { C.gold, C.active, C.primaryBorder },
}

--- opts (optional): { onClick = fn, tooltip = fn|table } makes the pill a clickable control.
function T.Pill(parent, text, style, opts)
  local p = CreateFrame("Button", nil, parent, T.BACKDROP_TEMPLATE)
  if opts and opts.onClick then
    p.tooltip = opts.tooltip
    p:RegisterForClicks("LeftButtonUp")
    p:SetScript("OnClick", function(self) opts.onClick(self); T.ShowTooltip(self, tooltipOf(self)) end)
    p:SetScript("OnEnter", function(self) T.SetBorder(self, C.gold); T.ShowTooltip(self, tooltipOf(self)) end)
    p:SetScript("OnLeave", function(self) self:SetStyle(self.style); GameTooltip:Hide() end)
  else
    p:EnableMouse(false)
  end
  p:SetHeight(16)
  p.label = T.Text(p, 10)
  p.label:SetPoint("CENTER")
  function p:SetStyle(s)
    self.style = s
    local st = PILL[s] or PILL.default
    self:SetBackdrop({ bgFile = T.WHITE, edgeFile = T.WHITE, edgeSize = 1 })
    self:SetBackdropColor(unpack(st[2] or { 0, 0, 0, 0 }))
    self:SetBackdropBorderColor(unpack(st[3]))
    self.label:SetTextColor(unpack(st[1]))
  end
  function p:SetLabel(s)
    self.label:SetText(s)
    self:SetWidth(math.ceil(self.label:GetStringWidth()) + 14)
  end
  p:SetStyle(style)
  p:SetLabel(text or "")
  return p
end

-- ------------------------------------------------------------------ --
-- Checkbox / radio with label and optional hint line.
-- ------------------------------------------------------------------ --
local function toggle(parent, kind, label, hint, get, set, width)
  local b = CreateFrame("Button", nil, parent)
  b:SetSize(width or 360, hint and 34 or 18)
  local box = CreateFrame("Frame", nil, b, T.BACKDROP_TEMPLATE)
  box:SetSize(16, 16)
  box:SetPoint("TOPLEFT", 0, -1)
  T.Skin(box, C.field, C.btnBorder)
  local mark = T.Icon(box, kind == "radio" and "radio" or "check", kind == "radio" and 14 or 14, C.gold)
  mark:SetPoint("CENTER")
  b.text = T.Text(b, 12, C.text)
  b.text:SetPoint("TOPLEFT", box, "TOPRIGHT", 8, -1)
  b.text:SetPoint("RIGHT", b, "RIGHT")
  b.text:SetText(label)
  if hint then
    b.hint = T.Text(b, 11, C.muted)
    b.hint:SetPoint("TOPLEFT", b.text, "BOTTOMLEFT", 0, -3)
    b.hint:SetPoint("RIGHT", b, "RIGHT")
    b.hint:SetText(hint)
  end
  function b:Refresh()
    local on = get()
    mark:SetShown(on and true or false)
    T.SetBorder(box, (on or self.hovered) and C.gold or C.btnBorder)
  end
  b:SetScript("OnClick", function(self)
    if kind == "radio" then set(true) else set(not get()) end
    if self.onChanged then self.onChanged() end
    self:Refresh()
  end)
  b:SetScript("OnEnter", function(self) self.hovered = true; self:Refresh() end)
  b:SetScript("OnLeave", function(self) self.hovered = false; self:Refresh() end)
  -- No Refresh here: widgets are built at file load, before saved variables exist.
  -- The owning screen refreshes them once AudioVibesDB is loaded.
  mark:Hide()
  return b
end

function T.Check(parent, label, hint, get, set, width) return toggle(parent, "check", label, hint, get, set, width) end
function T.Radio(parent, label, get, set, width) return toggle(parent, "radio", label, nil, get, set, width) end

-- ------------------------------------------------------------------ --
-- Text field with placeholder.
-- ------------------------------------------------------------------ --
function T.Field(parent, width, placeholder)
  local e = CreateFrame("EditBox", nil, parent, T.BACKDROP_TEMPLATE)
  e:SetSize(width or 300, 28)
  T.Skin(e, C.field, C.btnBorder)
  e:SetAutoFocus(false)
  e:SetFont(T.BODY_FONT, 12, "")
  e:SetTextColor(unpack(C.text))
  e:SetTextInsets(8, 8, 0, 0)
  e:SetMaxLetters(300)
  e.placeholder = T.Text(e, 12, C.label)
  e.placeholder:SetPoint("LEFT", 8, 0)
  e.placeholder:SetText(placeholder or "")
  local function upd(self) self.placeholder:SetShown(self:GetText() == "") end
  e:SetScript("OnEscapePressed", function(self) self:ClearFocus() end)
  e:SetScript("OnEnterPressed", function(self) self:ClearFocus() end)
  e:SetScript("OnEditFocusGained", function(self) if not self.error then T.SetBorder(self, C.gold) end end)
  e:SetScript("OnEditFocusLost", function(self) if not self.error then T.SetBorder(self, C.btnBorder) end end)
  e:HookScript("OnTextChanged", function(self) upd(self) end)
  function e:SetError(on)
    self.error = on
    T.SetBorder(self, on and C.errBorder or (self:HasFocus() and C.gold or C.btnBorder))
  end
  upd(e)
  return e
end

-- ------------------------------------------------------------------ --
-- Movable frames with saved position.
-- ------------------------------------------------------------------ --
function T.MakeMovable(frame, handle, getStore, isLocked)
  frame:SetMovable(true)
  frame:SetClampedToScreen(true)
  handle:EnableMouse(true)
  handle:RegisterForDrag("LeftButton")
  handle:SetScript("OnDragStart", function()
    if isLocked and isLocked() then return end
    frame:StartMoving()
  end)
  handle:SetScript("OnDragStop", function()
    frame:StopMovingOrSizing()
    local point, _, relPoint, x, y = frame:GetPoint()
    local store = getStore()
    store.point = { point, relPoint, x, y }
  end)
end

function T.RestorePosition(frame, store, default)
  frame:ClearAllPoints()
  local p = store and store.point
  if p then frame:SetPoint(p[1], UIParent, p[2], p[3], p[4])
  else frame:SetPoint(unpack(default)) end
end

--- "4s ago", "1m ago", "just now"
function T.Ago(t)
  if not t then return "" end
  local d = GetTime() - t
  if d < 2 then return "just now" end
  if d < 60 then return ("%ds ago"):format(d) end
  if d < 3600 then return ("%dm ago"):format(d / 60) end
  return ("%dh ago"):format(d / 3600)
end

T.TYPE_LABEL = { city = "City", zone = "Zone", dungeon = "Dungeon", raid = "Raid", pvp = "PvP" }
