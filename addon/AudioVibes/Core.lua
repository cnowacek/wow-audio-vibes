-- Audio Vibes core: saved data, the signal queue (throttle / queue / dedup), playlist lookup,
-- slash commands and sound checks.
-- The channel is one-way: the addon only SENDS. It never knows what's playing or whether the
-- companion heard it, so nothing here pretends to.
-- Guardrail: this addon never reads combat-restricted data (unit health, auras, cooldowns).

AudioVibes = AudioVibes or {}
local M = AudioVibes

local THROTTLE = 0.25      -- seconds between frames
local QUEUE_MAX = 8
local DEDUP_WINDOW = 2.0   -- seconds; same EVT_* code is not resent inside this window
local LOG_MAX = 8

M.VERSION = "1.2"

local DEFAULTS = {
  debug = false,
  locations = {},          -- ["map:1429"] = { relaxed = { uri, name, shuffle }, locked = { … } }   (custom per location)
  types = {},              -- ["dungeon"]  = { relaxed = …, locked = … }   (every <type> without its own)
  modes = {},              -- ["map:1454"] = "relaxed" | "locked"   (this location ignores the overall mode)
  hype = nil,              -- { uri, name, shuffle }
  seen = {},               -- ["map:2548"] = { name, type }           (locations visited; names for the companion)
  pending = {},            -- [key] = true: saved since the last reload (companion hasn't got it yet)
  settings = {
    vibeMode = "auto", resume = true,
    autoSwitch = true, resumeAfterInstance = true, shuffle = false,
    queueDuck = true, deathDuck = true, levelFanfare = false, combatEvents = false, login = true,
    showBar = true, lockBar = false, fadeInCombat = true, toast = true,
  },
  bar = { collapsed = false },
  window = {},
}

local queue = {}
local lastSendAt = -math.huge
local draining = false
local lastEventSent = {}   -- code -> GetTime()

M.log = {}                 -- recently sent: { what, t } newest first (this session only)
M.listeners = {}

local function chat(msg)
  DEFAULT_CHAT_FRAME:AddMessage("|cffffd100Audio Vibes|r: " .. tostring(msg))
end
M.Print = chat

--- Tiny event bus for the UI: "SIGNAL", "LOCATION", "PLAYLISTS", "SETTINGS".
function M.On(event, fn)
  M.listeners[event] = M.listeners[event] or {}
  table.insert(M.listeners[event], fn)
end
function M.Fire(event, ...)
  for _, fn in ipairs(M.listeners[event] or {}) do
    local ok, err = pcall(fn, ...)
    if not ok and M.db and M.db.debug then chat("UI error: " .. tostring(err)) end
  end
end

local function copyDefaults(dst, src)
  for k, v in pairs(src) do
    if type(v) == "table" then
      if type(dst[k]) ~= "table" then dst[k] = {} end
      copyDefaults(dst[k], v)
    elseif dst[k] == nil then
      dst[k] = v
    end
  end
end

local function migrate(db)
  -- 1.0 → 1.1: triggers/showBar/locked/barPoint moved into settings/bar.
  if type(db.triggers) == "table" then
    local t, s = db.triggers, db.settings
    if t.zone == false then s.autoSwitch = false end
    if t.queue == false then s.queueDuck = false end
    if t.death == false then s.deathDuck = false end
    if t.combat == true then s.combatEvents = true end
    if t.levelup == true then s.levelFanfare = true end
    if t.login == false then s.login = false end
    db.triggers = nil
  end
  if db.showBar ~= nil then db.settings.showBar = db.showBar; db.showBar = nil end
  if db.locked ~= nil then db.settings.lockBar = db.locked; db.locked = nil end
  if db.barPoint then db.bar.point = db.barPoint; db.barPoint = nil end
  db.slotLabels = nil
  -- 1.1 → 1.2: one custom playlist per location/type became one per mode. Existing ones keep
  -- applying to both modes until the player sets a separate one.
  for _, tbl in ipairs({ db.locations, db.types }) do
    for k, v in pairs(tbl) do
      if type(v) == "table" and v.uri then
        tbl[k] = { relaxed = { uri = v.uri, name = v.name, shuffle = v.shuffle },
                   locked  = { uri = v.uri, name = v.name, shuffle = v.shuffle } }
      end
    end
  end
end

function M.GetCVar(name)
  if C_CVar and C_CVar.GetCVar then return C_CVar.GetCVar(name) end
  if GetCVar then return GetCVar(name) end
end
function M.SetCVar(name, value)
  if C_CVar and C_CVar.SetCVar then return C_CVar.SetCVar(name, value) end
  if SetCVar then return SetCVar(name, value) end
end

-- ------------------------------------------------------------------ --
-- Signal queue
-- ------------------------------------------------------------------ --
local function record(what)
  table.insert(M.log, 1, { what = what, t = GetTime() })
  while #M.log > LOG_MAX do table.remove(M.log) end
  M.Fire("SIGNAL", what)
end

-- Queue entries: { path = <sound file>, label = <debug text>, what = <user-facing text or nil> }
local function playEntry(e)
  local willPlay = PlaySoundFile(e.path, "Master")
  if e.what then record(e.what) end
  if M.db and M.db.debug then
    chat(("sent %s%s"):format(e.label, willPlay and "" or " |cffff6666[PlaySoundFile refused]|r"))
  end
  return willPlay
end

local function drain()
  if #queue == 0 then draining = false; return end
  local now = GetTime()
  local wait = (lastSendAt + THROTTLE) - now
  if wait > 0 then
    C_Timer.After(wait, drain)
    return
  end
  local entry = table.remove(queue, 1)
  lastSendAt = now
  playEntry(entry)
  if #queue > 0 then
    C_Timer.After(THROTTLE, drain)
  else
    draining = false
  end
end

local function enqueue(entry)
  if #queue >= QUEUE_MAX then table.remove(queue, 1) end
  queue[#queue + 1] = entry
  if not draining then
    draining = true
    drain()
  end
end

local FRIENDLY = {
  PLAY_PAUSE = "Play / pause", NEXT = "Next track", PREV = "Previous track",
  VOL_UP = "Volume up", VOL_DOWN = "Volume down", DUCK_ON = "Lower music", DUCK_OFF = "Restore music",
  LIKE_CURRENT = "Like track", PING = "Ping", EVT_HYPE = "Hype", EVT_QUEUE_POP = "Queue popped",
  EVT_PLAYER_DEAD = "Died", EVT_PLAYER_ALIVE = "Resurrected", EVT_LEVEL_UP = "Level up",
  EVT_COMBAT_START = "Combat start", EVT_COMBAT_END = "Combat end", EVT_LOGIN = "Login",
  MODE_RELAXED = "Mode: Relaxed", MODE_LOCKED = "Mode: Locked-in",
}

function M.NameOf(code)
  for name, c in pairs(AudioVibesCommands.codes) do
    if c == code then return name end
  end
end

--- Send a command by name, e.g. AudioVibes.Send("NEXT"). `what` overrides the "Signal sent" text;
--- pass false to keep it out of the log (e.g. the category event that follows a location).
function M.Send(name, what)
  local code = AudioVibesCommands.codes[name]
  if not code then
    chat("unknown command " .. tostring(name))
    return false
  end
  local now = GetTime()
  if name:sub(1, 4) == "EVT_" then
    local last = lastEventSent[code]
    if last and now - last < DEDUP_WINDOW then
      if M.db and M.db.debug then chat("dedup " .. name) end
      return false
    end
    lastEventSent[code] = now
  end
  if what == nil then what = FRIENDLY[name] or name end
  enqueue({ path = AudioVibesCommands.files[code], label = ("%s (%d)"):format(name, code), what = what or nil })
  return true
end

-- ------------------------------------------------------------------ --
-- Locations
-- ------------------------------------------------------------------ --
local INSTANCE_FLAG = AudioVibesCommands.zoneInstanceFlag or 32768

function M.KeyToZoneId(key)
  local kind, id = (key or ""):match("^(%a+):(%d+)$")
  id = tonumber(id)
  if not id then return nil end
  return kind == "instance" and (INSTANCE_FLAG + id) or id
end

--- Send a location id as two zone frames (high byte, then low byte).
function M.SendZone(zoneId, label, what)
  local pattern = AudioVibesCommands.zoneFilePattern
  if not pattern or not zoneId or zoneId < 0 or zoneId > 65535 then return false end
  local hi, lo = math.floor(zoneId / 256), zoneId % 256
  enqueue({ path = pattern:format(0, hi), label = ("zone %s hi"):format(label or zoneId) })
  enqueue({ path = pattern:format(1, lo), label = ("zone %s lo"):format(label or zoneId), what = what })
  return true
end

-- ------------------------------------------------------------------ --
-- Vibe modes. Every location has a Relaxed and a Locked-in playlist.
--   overall mode (settings.vibeMode): "auto" | "relaxed" | "locked"
--     auto = relaxed in cities and the open world, locked-in in dungeons, raids and PvP
--   per-location override (db.modes[key]): "relaxed" | "locked" | nil (follow overall)
-- The addon sends the effective mode (MODE_RELAXED / MODE_LOCKED) with every location and whenever it
-- changes, so the companion switches at once (no /reload needed for modes).
-- ------------------------------------------------------------------ --
M.MODES = { "relaxed", "locked" }
M.MODE_NAME = { relaxed = "Relaxed", locked = "Locked-in", auto = "Auto" }
local AUTO_LOCKED = { dungeon = true, raid = true, pvp = true }
local lastSentMode

--- Effective mode for a location: mode, why ("location" | "auto" | "overall").
function M.EffectiveMode(key, locType)
  local override = key and M.db.modes[key]
  if override == "relaxed" or override == "locked" then return override, "location" end
  local overall = M.db.settings.vibeMode or "auto"
  if overall == "relaxed" or overall == "locked" then return overall, "overall" end
  locType = locType or (key and M.LocationInfo(key).type) or "zone"
  return AUTO_LOCKED[locType] and "locked" or "relaxed", "auto"
end

--- Tell the companion the mode (only when it changed, unless force).
function M.SendMode(mode, force, what)
  if not force and mode == lastSentMode then return end
  lastSentMode = mode
  M.Send(mode == "locked" and "MODE_LOCKED" or "MODE_RELAXED", what == nil and ("Mode: " .. M.MODE_NAME[mode]) or what)
end

--- The current location's mode may have changed (overall mode, override, or Auto by type): resend it.
function M.ModeChanged()
  M.Fire("PLAYLISTS")
  if M.location and M.db.settings.autoSwitch then
    local mode = M.EffectiveMode(M.location.key, M.location.type)
    M.SendMode(mode)
  end
end

function M.SetOverallMode(mode)
  if mode ~= "auto" and mode ~= "relaxed" and mode ~= "locked" then return end
  M.db.settings.vibeMode = mode
  M.ModeChanged()
end

--- Auto → Relaxed → Locked-in → Auto (player bar pill, keybind, /vibes mode).
function M.CycleMode()
  local nextMode = { auto = "relaxed", relaxed = "locked", locked = "auto" }
  M.SetOverallMode(nextMode[M.db.settings.vibeMode or "auto"] or "auto")
  local m = M.location and M.EffectiveMode(M.location.key, M.location.type)
  M.Print(("vibe mode: %s%s"):format(M.MODE_NAME[M.db.settings.vibeMode],
    (M.db.settings.vibeMode == "auto" and m) and (" (" .. M.MODE_NAME[m] .. " here)") or ""))
end

function M.SetLocationMode(key, mode)
  if not key then return end
  M.db.modes[key] = (mode == "relaxed" or mode == "locked") and mode or nil
  if M.location and M.location.key == key then M.ModeChanged() else M.Fire("PLAYLISTS") end
end

--- "Play now": the location's id and its mode, then PLAY_LOCATION, so the companion starts that playlist at once.
function M.PlayLocation(key, what)
  local zoneId = M.KeyToZoneId(key)
  if not zoneId then return false end
  local info = M.LocationInfo(key)
  M.SendZone(zoneId, info.name, false)
  M.SendMode(M.EffectiveMode(key, info.type), true, false)
  enqueue({ path = AudioVibesCommands.files[AudioVibesCommands.codes.PLAY_LOCATION], label = "PLAY_LOCATION",
            what = what or ("Play: " .. info.name) })
  return true
end

--- Static info for a location key: { key, name, type, vibe, default = { relaxed, locked }, isNew }.
function M.LocationInfo(key)
  local def = AudioVibesLocations and AudioVibesLocations[key]
  local seen = M.db and M.db.seen[key]
  local kind, id = (key or ""):match("^(%a+):(%d+)$")
  return {
    key = key,
    name = (def and def.name) or (seen and seen.name) or ((kind == "instance" and "Instance " or "Zone ") .. tostring(id)),
    type = (def and def.type) or (seen and seen.type) or "zone",
    vibe = def and def.vibe,
    default = def and def.default,
    isNew = (def and def.isNew) or (def == nil),   -- not in the built-in list ⇒ new in Forever
  }
end

local TYPE_DEFAULT = {
  city = "EVT_ENTER_CITY", zone = "EVT_ENTER_OPEN_WORLD", dungeon = "EVT_ENTER_DUNGEON",
  raid = "EVT_ENTER_RAID", pvp = "EVT_ENTER_PVP",
}

--- Playlist the companion will use for a location in a mode (default: its effective mode):
--- { name, custom (bool), scope ("location"|"type"|"default"), uri, shuffle, mode }
function M.PlaylistFor(key, mode)
  local info = M.LocationInfo(key)
  mode = mode or M.EffectiveMode(key, info.type)
  local own = M.db.locations[key] and M.db.locations[key][mode]
  if own and own.uri then return { name = own.name, custom = true, scope = "location", uri = own.uri, shuffle = own.shuffle, mode = mode } end
  local byType = M.db.types[info.type] and M.db.types[info.type][mode]
  if byType and byType.uri then return { name = byType.name, custom = true, scope = "type", uri = byType.uri, shuffle = byType.shuffle, mode = mode } end
  local d = M.DefaultPlaylist(key, mode)
  return { name = d.name, custom = false, scope = "default", uri = d.uri, mode = mode }
end

--- The companion's choice for a location in a mode (from Resolved.lua, written by the companion app),
--- else the vibe's name for that mode from the built-in list. { name, uri (may be nil) }
function M.DefaultPlaylist(key, mode)
  local info = M.LocationInfo(key)
  mode = mode or M.EffectiveMode(key, info.type)
  local R = AudioVibesResolved or {}
  local function pick(t) return t and (t[mode] or (t.uri and t)) end   -- older files had no modes
  local r = pick(R.locations and R.locations[key]) or pick(R.types and R.types[info.type])
  local names = info.default
  if not names and AudioVibesLocationsCategories then names = AudioVibesLocationsCategories[TYPE_DEFAULT[info.type] or ""] end
  local vibeName = type(names) == "table" and names[mode] or names
  return { name = (r and r.name and r.name ~= "" and r.name) or vibeName or "Adventure", uri = r and r.uri }
end

--- Default playlist name for a location (ignores custom playlists).
function M.DefaultPlaylistName(key, mode) return M.DefaultPlaylist(key, mode).name end

--- spotify:playlist:ID → https://open.spotify.com/playlist/ID (nil if it isn't a Spotify URI).
function M.SpotifyUrl(uri)
  local kind, id = (uri or ""):match("^spotify:(%a+):(%w+)$")
  return kind and ("https://open.spotify.com/%s/%s"):format(kind, id) or nil
end

--- Something a person can open for any music source: the Spotify page, the radio stream URL, or the folder path.
function M.OpenUrl(uri)
  uri = uri or ""
  return M.SpotifyUrl(uri) or uri:match("^radio:(https?://.+)$") or uri:match("^folder:(.+)$")
end

--- "spotify" | "radio" | "folder" | nil
function M.SourceKind(uri)
  uri = uri or ""
  if uri:find("^spotify:") then return "spotify" end
  if uri:find("^radio:") then return "radio" end
  if uri:find("^folder:") then return "folder" end
end

--- Link to the playlist that applies to a location (in its effective mode, or `mode`), or nil if unknown.
function M.LinkFor(key, mode) return M.OpenUrl(M.PlaylistFor(key, mode).uri) end

--- Save a custom playlist.
---   scope "location": target = location key   · scope "type": target = type (every <type> without its own)
---   scope "hype": target ignored, mode ignored (one Hype playlist)
---   mode: "relaxed" | "locked"; uri nil = back to default for that mode
function M.SavePlaylist(scope, target, mode, uri, name, shuffle)
  if scope ~= "hype" and not target then return end
  local entry = uri and { uri = uri, name = (name and name ~= "") and name or "Custom playlist", shuffle = shuffle and true or false } or nil
  if scope == "hype" then
    M.db.hype = entry; M.db.pending.hype = true
  else
    if mode ~= "relaxed" and mode ~= "locked" then return end
    local tbl = scope == "type" and M.db.types or M.db.locations
    local cur = tbl[target] or {}
    cur[mode] = entry
    tbl[target] = (cur.relaxed or cur.locked) and cur or nil
    M.db.pending[(scope == "type" and "type:" or "") .. target] = true
  end
  M.Fire("PLAYLISTS")
end

function M.PendingCount()
  local n = 0
  for _ in pairs(M.db.pending) do n = n + 1 end
  return n
end

--- Accepts open.spotify.com playlist/album links (with ?si=… etc.) and spotify:playlist:/spotify:album: URIs.
--- Returns cleaned URI and kind ("playlist"|"album"), or nil.
function M.ParseSpotifyLink(s)
  s = strtrim(s or "")
  if s == "" then return nil end
  local kind, id = s:match("^spotify:(%a+):(%w+)$")
  if not kind then
    local path = s:match("^https?://open%.spotify%.com/(.+)$") or s:match("^open%.spotify%.com/(.+)$")
    if path then
      path = path:gsub("^intl%-[%w%-]+/", "")       -- localized links: /intl-de/playlist/…
      kind, id = path:match("^(%a+)/(%w+)")
    end
  end
  if (kind == "playlist" or kind == "album") and id then return ("spotify:%s:%s"):format(kind, id), kind end
  return nil
end

--- Anything the companion can play. Returns uri, kind, or nil plus a reason:
---   Spotify playlist/album links       → "spotify:playlist:ID", "playlist"|"album"
---   radio stream URLs (http/https)     → "radio:https://…", "radio"
---   folders on your PC (C:\… or \\…)   → "folder:C:\…", "folder"
function M.ParseMusicLink(s)
  s = strtrim(s or ""):gsub('^"(.*)"$', "%1")
  if s == "" then return nil end
  local uri, kind = M.ParseSpotifyLink(s)
  if uri then return uri, kind end
  local lower = s:lower()
  if lower:find("open%.spotify%.com") or lower:find("^spotify:") then
    return nil, "Spotify links need to be a playlist or album"
  end
  local url = s:match("^radio:(.+)$") or s
  if url:lower():find("^https?://[%w%-%.]+") then
    local host = url:lower():match("^https?://([^/:%?#]+)") or ""
    if host:find("youtube%.com$") or host:find("youtu%.be$") then
      return nil, "YouTube links can't be played; use Spotify, a radio stream or a folder"
    end
    return "radio:" .. url, "radio"
  end
  local path = s:match("^folder:(.+)$") or s
  if path:find("^%a:[\\/]") or path:find("^\\\\") then
    path = path:gsub("/", "\\"):gsub("\\+$", "")
    return "folder:" .. path, "folder"
  end
  return nil
end

-- ------------------------------------------------------------------ --
-- Sound check (Settings → Signal check, and a one-time chat warning)
-- ------------------------------------------------------------------ --
function M.SoundStatus()
  local mv = tonumber(M.GetCVar("Sound_MasterVolume") or "1") or 1
  return {
    soundOn = M.GetCVar("Sound_EnableAllSound") ~= "0" and mv > 0,
    background = M.GetCVar("Sound_EnableSoundWhenGameIsInBackground") ~= "0",
  }
end

local function checkSound()
  local s = M.SoundStatus()
  local problems = {}
  if not s.soundOn then problems[#problems + 1] = "game sound or master volume is off" end
  if not s.background then problems[#problems + 1] = "'Sound in Background' is off (signals stop while you're alt-tabbed)" end
  if #problems > 0 then
    chat("|cffffb35cwarning:|r " .. table.concat(problems, "; ") .. ". Audio Vibes talks to the companion app through game audio. /vibes settings to fix.")
  end
end

-- ------------------------------------------------------------------ --
-- Slash commands
-- ------------------------------------------------------------------ --
local simple = {
  next = "NEXT", prev = "PREV", play = "PLAY_PAUSE", pause = "PLAY_PAUSE",
  ["vol+"] = "VOL_UP", ["vol-"] = "VOL_DOWN", duck = "DUCK_ON", unduck = "DUCK_OFF",
  like = "LIKE_CURRENT", hype = "EVT_HYPE", ping = "PING",
}

local function help()
  chat("/vibes — open the window  ·  /vibes show — show/hide the player bar  ·  /vibes settings")
  chat("/vibes mode [auto|relaxed|locked] — switch the vibe mode (no argument cycles)")
  chat("/vibes next | prev | play | vol+ | vol- | duck | unduck | like | hype | slot <n> | zone | test | debug")
end

SLASH_AUDIOVIBES1 = "/vibes"
SLASH_AUDIOVIBES2 = "/audiovibes"
SlashCmdList["AUDIOVIBES"] = function(msg)
  msg = strtrim(msg or "")
  local cmd, rest = msg:match("^(%S*)%s*(.-)$")
  cmd = (cmd or ""):lower()
  if cmd == "" or cmd == "config" or cmd == "locations" then
    if M.ToggleConfig then M.ToggleConfig("locations") end
  elseif cmd == "settings" then
    if M.ToggleConfig then M.ToggleConfig("settings", true) end
  elseif cmd == "show" or cmd == "bar" then
    M.db.settings.showBar = not M.db.settings.showBar
    M.Fire("SETTINGS")
  elseif simple[cmd] then
    M.Send(simple[cmd])
  elseif cmd == "slot" then
    local n = tonumber(rest)
    if not n or n < 0 or n > 31 then chat("slot must be 0-31"); return end
    M.Send("PLAYLIST_SLOT_" .. math.floor(n), "Playlist slot " .. math.floor(n))
  elseif cmd == "mode" then
    local arg = strlower(rest or "")
    if arg == "" then M.CycleMode()
    elseif arg == "auto" or arg == "relaxed" or arg == "locked" then
      M.SetOverallMode(arg); chat("vibe mode: " .. M.MODE_NAME[arg])
    elseif arg == "locked-in" or arg == "lockedin" then
      M.SetOverallMode("locked"); chat("vibe mode: Locked-in")
    else chat("usage: /vibes mode [auto|relaxed|locked]") end
  elseif cmd == "zone" then
    if M.ZoneInfo then M.ZoneInfo() end
  elseif cmd == "test" then
    M.Send("PING", "Ping (test)")
    chat("sent a test signal; the companion's tray icon turns green when it hears it")
  elseif cmd == "debug" then
    M.db.debug = not M.db.debug
    chat("debug " .. (M.db.debug and "on" or "off"))
  else
    help()
  end
end

-- Key binding labels (Bindings.xml)
BINDING_HEADER_AUDIOVIBES = "Audio Vibes"
BINDING_NAME_AUDIOVIBES_PLAYPAUSE = "Play / Pause"
BINDING_NAME_AUDIOVIBES_NEXT = "Next track"
BINDING_NAME_AUDIOVIBES_PREV = "Previous track"
BINDING_NAME_AUDIOVIBES_HYPE = "Hype"
BINDING_NAME_AUDIOVIBES_MODE = "Switch vibe mode (Auto, Relaxed, Locked-in)"

--- Key text for a binding, e.g. "CTRL-H", or nil.
function M.KeyFor(binding)
  local k = GetBindingKey and GetBindingKey(binding)
  return k
end

-- ------------------------------------------------------------------ --
-- Lifecycle
-- ------------------------------------------------------------------ --
local f = CreateFrame("Frame")
f:RegisterEvent("ADDON_LOADED")
f:RegisterEvent("PLAYER_LOGIN")
f:SetScript("OnEvent", function(_, event, arg1)
  if event == "ADDON_LOADED" and arg1 == "AudioVibes" then
    AudioVibesDB = AudioVibesDB or {}
    copyDefaults(AudioVibesDB, DEFAULTS)
    migrate(AudioVibesDB)
    M.db = AudioVibesDB
    -- WoW wrote the saved-variables file on the reload/logout that got us here, so the companion
    -- now has every change saved before it: nothing is pending any more.
    M.db.pending = {}
  elseif event == "PLAYER_LOGIN" then
    M.Send("PING", "Ping (login)")
    checkSound()
    if M.OnLogin then M.OnLogin() end
    M.Fire("SETTINGS")
  end
end)
