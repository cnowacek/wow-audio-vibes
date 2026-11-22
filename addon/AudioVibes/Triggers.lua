-- Audio Vibes triggers: only unrestricted, non-combat-data events.
-- Never read unit health, auras, cooldowns or any other combat-restricted value here.

local M = AudioVibes
local f = CreateFrame("Frame")

local function safeRegister(event)
  if C_EventUtils and C_EventUtils.IsEventValid and not C_EventUtils.IsEventValid(event) then
    return false
  end
  return pcall(f.RegisterEvent, f, event)
end

local function S() return M.db and M.db.settings or {} end

-- ------------------------------------------------------------------ --
-- Location identity. Each location gets its own id so the companion can give it its own playlist:
--   instances: instanceID from GetInstanceInfo() with the instance flag set
--   world:     the zone-level uiMapID (sub-zone/micro maps walk up to their zone)
-- Ids are numbers, so this works for Forever-only zones and any client language.
-- ------------------------------------------------------------------ --
local ZONE_MAP_TYPE = (Enum and Enum.UIMapType and Enum.UIMapType.Zone) or 3

local function zoneMap()
  if not (C_Map and C_Map.GetBestMapForUnit and C_Map.GetMapInfo) then return nil end
  local id = C_Map.GetBestMapForUnit("player")
  for _ = 1, 10 do
    if not id then return nil end
    local info = C_Map.GetMapInfo(id)
    if not info then return id end
    if info.mapType <= ZONE_MAP_TYPE or not info.parentMapID or info.parentMapID == 0 then
      return id, info.name
    end
    id = info.parentMapID
  end
  return id
end

local INSTANCE_TYPE = { party = "dungeon", raid = "raid", pvp = "pvp", arena = "pvp" }

--- Current location: { key, zoneId, name, type, instance (bool) } or nil.
function M.CurrentLocation()
  local inInstance, instanceType = IsInInstance()
  if inInstance then
    local name, _, _, _, _, _, _, instanceID = GetInstanceInfo()
    if instanceID and instanceID > 0 and instanceID < 32768 then
      return { key = "instance:" .. instanceID, zoneId = 32768 + instanceID, name = name,
               type = INSTANCE_TYPE[instanceType] or "dungeon", instance = true }
    end
  end
  local id, name = zoneMap()
  if id and id > 0 and id < 32768 then
    local key = "map:" .. id
    local def = AudioVibesLocations and AudioVibesLocations[key]
    return { key = key, zoneId = id, name = (def and def.name) or name or GetRealZoneText(),
             type = (def and def.type) or (IsResting() and "city" or "zone"), instance = false }
  end
end

local function category()
  local inInstance, instanceType = IsInInstance()
  if inInstance and instanceType == "party" then return "EVT_ENTER_DUNGEON"
  elseif inInstance and instanceType == "raid" then return "EVT_ENTER_RAID"
  elseif inInstance and (instanceType == "pvp" or instanceType == "arena") then return "EVT_ENTER_PVP"
  elseif IsResting() then return "EVT_ENTER_CITY" end
  return "EVT_ENTER_OPEN_WORLD"
end

M.location = nil          -- what the player bar shows
local lastSentKey, lastArea, wasInInstance

local function locationCheck(force)
  local loc = M.CurrentLocation()
  if not loc then return end
  local changed = not M.location or M.location.key ~= loc.key
  local previous = M.location
  M.location = loc

  -- Remember names of locations the built-in list doesn't know (Forever-only zones/dungeons):
  -- the companion reads these from the saved-variables file to label them.
  if not (AudioVibesLocations and AudioVibesLocations[loc.key]) and loc.name then
    M.db.seen[loc.key] = { name = loc.name, type = loc.type }
  end
  if changed then M.Fire("LOCATION", loc, previous) end

  local s = S()
  if not s.autoSwitch then return end
  -- Leaving an instance: only go back to the outside playlist if the player wants that.
  local leavingInstance = wasInInstance and not loc.instance
  wasInInstance = loc.instance
  if leavingInstance and not s.resumeAfterInstance and not force then lastSentKey = loc.key; return end

  if force or loc.key ~= lastSentKey then
    lastSentKey = loc.key
    M.SendZone(loc.zoneId, loc.name, "Location: " .. (loc.name or loc.key))
    -- The location's mode rides along (Auto may flip it, e.g. city → dungeon), so the companion picks
    -- the right one of its two playlists.
    M.SendMode(M.EffectiveMode(loc.key, loc.type), true, false)
    if changed and s.toast and M.ShowToast then
      local before = previous and M.PlaylistFor(previous.key).name
      local now = M.PlaylistFor(loc.key).name
      if force or before ~= now then M.ShowToast(loc.name, now) end
    end
  end
  -- The category event pairs with the zone id in the companion (fallback vibe for unknown places).
  local area = category()
  if force or area ~= lastArea then
    lastArea = area
    M.Send(area, false)
  end
end

local function enteringWorld()
  locationCheck()
  -- The map API can lag a moment behind the loading screen; check again shortly after.
  C_Timer.After(2, function() locationCheck() end)
end

function M.ZoneInfo()
  local loc = M.CurrentLocation()
  if not loc then
    M.Print("couldn't work out the current location id")
    return
  end
  M.Print(("location: %s  |cffffd100%s|r  (%s)"):format(loc.name or "?", loc.key, M.Theme.TYPE_LABEL[loc.type] or loc.type))
  if not S().autoSwitch then M.Print("Auto-switch is off, so it isn't sent automatically. Use Play in /vibes.") return end
  locationCheck(true)
end

local function battlefieldPopped()
  local max = (GetMaxBattlefieldID and GetMaxBattlefieldID()) or 3
  for i = 1, max do
    if GetBattlefieldStatus(i) == "confirm" then return true end
  end
  return false
end

local handlers = {
  PLAYER_ENTERING_WORLD = enteringWorld,
  ZONE_CHANGED_NEW_AREA = function() locationCheck() end,
  PLAYER_UPDATE_RESTING = function() locationCheck() end,
  LFG_PROPOSAL_SHOW = function() if S().queueDuck then M.Send("EVT_QUEUE_POP") end end,
  UPDATE_BATTLEFIELD_STATUS = function()
    if S().queueDuck and battlefieldPopped() then M.Send("EVT_QUEUE_POP") end
  end,
  PLAYER_DEAD = function() if S().deathDuck then M.Send("EVT_PLAYER_DEAD") end end,
  PLAYER_ALIVE = function()
    -- PLAYER_ALIVE also fires on release-to-ghost; only count a real resurrection.
    if S().deathDuck and not UnitIsDeadOrGhost("player") then M.Send("EVT_PLAYER_ALIVE") end
  end,
  PLAYER_UNGHOST = function() if S().deathDuck then M.Send("EVT_PLAYER_ALIVE") end end,
  -- VERIFY in Forever: these only say "entered/left combat"; no combat data is read.
  -- Used for the bar's combat fade, and optionally sent as signals.
  PLAYER_REGEN_DISABLED = function()
    M.inCombat = true
    M.Fire("COMBAT", true)
    if S().combatEvents then M.Send("EVT_COMBAT_START") end
  end,
  PLAYER_REGEN_ENABLED = function()
    M.inCombat = false
    M.Fire("COMBAT", false)
    if S().combatEvents then M.Send("EVT_COMBAT_END") end
  end,
  PLAYER_LEVEL_UP = function() if S().levelFanfare then M.Send("EVT_LEVEL_UP") end end,
}

M.registeredEvents = {}
for event in pairs(handlers) do
  if safeRegister(event) then M.registeredEvents[event] = true end
end

f:SetScript("OnEvent", function(_, event, ...)
  local h = handlers[event]
  if h then h(...) end
end)

function M.OnLogin()
  if S().login then M.Send("EVT_LOGIN", false) end
end
