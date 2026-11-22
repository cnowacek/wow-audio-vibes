"""Smoke-test the Audio Vibes addon in Lua 5.1 (WoW's version) against a mocked WoW API.

This catches runtime errors (nil fields, wrong calls, typos) that a syntax check can't. It doesn't
check how things look; that still needs a look in game.
"""
import re
from pathlib import Path

import pytest

lua51 = pytest.importorskip("lupa.lua51")

ADDON = Path(__file__).resolve().parents[2] / "addon" / "AudioVibes"
MOCK = Path(__file__).resolve().parent / "wow_mock.lua"


def toc_files():
    lines = (ADDON / "AudioVibes.toc").read_text(encoding="utf-8").splitlines()
    return [l.strip() for l in lines if l.strip() and not l.startswith("#")]


@pytest.fixture()
def wow():
    rt = lua51.LuaRuntime(unpack_returned_tuples=True)
    rt.execute(MOCK.read_text(encoding="utf-8"))
    G = rt.globals()
    for f in toc_files():
        r = G.loadstring((ADDON / f).read_text(encoding="utf-8"), "@" + f)
        chunk = r[0] if isinstance(r, tuple) else r
        assert chunk is not None, r
        chunk()
    G.MOCK.fire("ADDON_LOADED", "AudioVibes")
    G.MOCK.fire("PLAYER_LOGIN")
    G.MOCK.fire("PLAYER_ENTERING_WORLD", True, False)
    G.MOCK.runTimers()
    return rt, G


def lua_list(t):
    return [t[i] for i in range(1, len(t) + 1)] if t else []


def test_loads_and_sends_login_location(wow):
    rt, G = wow
    played = lua_list(G.MOCK.played)
    assert any("cmd_09.ogg" in p for p in played), "PING on login"
    zone = [p for p in played if "zone_" in p]
    assert zone[:2] == [r"Interface\AddOns\AudioVibes\sounds\zone_0_05.ogg",
                        r"Interface\AddOns\AudioVibes\sounds\zone_1_ad.ogg"], "map 1453 = 0x05AD"
    assert G.AudioVibes.location.key == "map:1453"
    assert G.AudioVibes.location.type == "city"


def test_every_button_and_screen_runs_without_errors(wow):
    rt, G = wow
    AV = G.AudioVibes
    errors = []
    for page in ("locations", "settings", "help", "locations"):
        AV.ToggleConfig(page, True)
        n, errs = G.MOCK.clickAll()
        errors += lua_list(errs)
    AV.OpenPlaylistDialog(rt.eval('{ scope = "location", key = "instance:36" }'))
    AV.OpenPlaylistDialog(rt.eval('{ scope = "hype" }'))
    errors += lua_list(G.MOCK.updateAll(3.1))
    n, errs = G.MOCK.clickAll()
    errors += lua_list(errs)
    assert n > 60, f"only {n} clickable controls found"
    assert errors == []


def test_slash_commands(wow):
    rt, G = wow
    for cmd in ("", "settings", "show", "show", "next", "slot 3", "zone", "test", "debug", "help", "bogus"):
        G.SlashCmdList["AUDIOVIBES"](cmd)
    assert G.AudioVibes.db.settings.showBar is True


def test_change_playlist_dialog_saves_and_marks_pending(wow):
    rt, G = wow
    AV = G.AudioVibes
    AV.OpenPlaylistDialog(rt.eval('{ scope = "location", key = "instance:36" }'))
    ui = AV.dialogWidgets
    link, name, save = ui.link, ui.name, ui.save
    link.SetText(link, "https://youtube.com/watch?v=pirates")
    assert save.disabled is True, "invalid link disables Save"
    link.SetText(link, "https://open.spotify.com/playlist/5xP1r4teMix0Defias?si=abc")
    name.SetText(name, "My Pirate Mix")
    assert not save.disabled
    save._scripts.OnClick(save, "LeftButton")
    entry = AV.db.locations["instance:36"].locked   # Deadmines is Locked-in under Auto
    assert entry.uri == "spotify:playlist:5xP1r4teMix0Defias"
    assert entry.name == "My Pirate Mix"
    assert AV.db.pending["instance:36"] is True
    assert AV.PendingCount() == 1
    pl = AV.PlaylistFor("instance:36")
    assert pl.custom is True and pl.name == "My Pirate Mix"
    # after a reload the pending marker is cleared (WoW wrote the file)
    G.MOCK.fire("ADDON_LOADED", "AudioVibes")
    assert AV.PendingCount() == 0


def test_type_scope_and_reset(wow):
    rt, G = wow
    AV = G.AudioVibes
    AV.SavePlaylist("type", "raid", "locked", "spotify:album:2noRn2Aes5aoNVsU6iWThc", "Raid Night Metal", True)
    assert AV.PlaylistFor("instance:409").name == "Raid Night Metal"          # Molten Core
    assert AV.PlaylistFor("instance:409").scope == "type"
    AV.SavePlaylist("location", "instance:409", "locked", "spotify:playlist:MC", "MC", False)
    assert AV.PlaylistFor("instance:409").name == "MC"
    AV.SavePlaylist("location", "instance:409", "locked", None)
    assert AV.PlaylistFor("instance:409").name == "Raid Night Metal"


def test_spotify_link_parsing(wow):
    rt, G = wow
    p = G.AudioVibes.ParseSpotifyLink
    assert p("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc")[0] == "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M"
    assert p("https://open.spotify.com/intl-de/album/2noRn2Aes5aoNVsU6iWThc")[0] == "spotify:album:2noRn2Aes5aoNVsU6iWThc"
    assert p("spotify:album:abc") == ("spotify:album:abc", "album")
    assert p("https://youtube.com/watch?v=x") is None
    assert p("spotify:track:abc") is None
    assert p("") is None


def test_play_now_sends_location_then_play_location(wow):
    rt, G = wow
    AV = G.AudioVibes
    G.MOCK.played = rt.eval("{}")
    AV.PlayLocation("instance:36")
    G.MOCK.now = G.MOCK.now + 5
    for _ in range(5):
        G.MOCK.runTimers()
        G.MOCK.now = G.MOCK.now + 1
    played = lua_list(G.MOCK.played)
    assert [Path(p.replace("\\", "/")).name for p in played] == ["zone_0_80.ogg", "zone_1_24.ogg", "cmd_57.ogg", "cmd_55.ogg"]   # id, its mode (Locked-in), play
    assert AV.log[1].what == "Play: The Deadmines"


def test_unknown_forever_zone_is_learned_and_listed(wow):
    rt, G = wow
    AV = G.AudioVibes
    G.MOCK.loc.mapId = 2600
    G.MOCK.loc.resting = False
    G.MOCK.fire("ZONE_CHANGED_NEW_AREA")
    assert AV.location.key == "map:2600"
    seen = AV.db.seen["map:2600"]
    assert seen.name == "Zephras Isle" and seen.type == "zone"
    assert AV.LocationInfo("map:2600").isNew is True


def test_layout_fits_above_the_footer(wow):
    rt, G = wow
    lay = G.AudioVibes._layout
    assert lay.visibleRows * lay.rowHeight <= lay.listHeight, "list rows run under the footer"
    assert lay.visibleRows >= 8
    assert lay.settingsBottom >= lay.footerTop, "settings boxes run under the footer"


def test_links_for_defaults_custom_and_unknown(wow):
    rt, G = wow
    AV = G.AudioVibes
    G.AudioVibesResolved = rt.eval("""{
        locations = { ["map:1453"] = { relaxed = { uri = "spotify:playlist:SWLANTERNS", name = "Stormwind Lanterns" } } },
        types = { dungeon = { locked = { uri = "spotify:playlist:DUNGEONSYNTH" } } },
    }""")
    # default from the companion: real name + link
    pl = AV.PlaylistFor("map:1453")
    assert pl.name == "Stormwind Lanterns" and pl.custom is False
    assert AV.LinkFor("map:1453") == "https://open.spotify.com/playlist/SWLANTERNS"
    # type-level default: link, but keeps the vibe name since none was sent
    assert AV.LinkFor("instance:36") == "https://open.spotify.com/playlist/DUNGEONSYNTH"
    assert AV.PlaylistFor("instance:36").name == "Dungeon run"   # Locked-in vibe name
    # nothing known yet
    assert AV.LinkFor("map:1429") is None
    assert AV.CopyLinkFor("map:1429") is False
    # custom playlist wins and its link is copyable
    AV.SavePlaylist("location", "map:1429", "relaxed", "spotify:album:2noRn2Aes5aoNVsU6iWThc", "Goldshire", False)
    assert AV.LinkFor("map:1429") == "https://open.spotify.com/album/2noRn2Aes5aoNVsU6iWThc"
    assert AV.CopyLinkFor("map:1429") is True
    ui = AV.dialogWidgets
    assert ui.copy._shown is True
    assert ui.copyField._text == "https://open.spotify.com/album/2noRn2Aes5aoNVsU6iWThc"
    # typing in the copy box can't change the link
    ui.copyField._scripts.OnTextChanged(ui.copyField, True)
    assert ui.copyField._text == "https://open.spotify.com/album/2noRn2Aes5aoNVsU6iWThc"


def test_link_buttons_run_without_errors(wow):
    rt, G = wow
    AV = G.AudioVibes
    G.AudioVibesResolved = rt.eval('{ locations = { ["map:1453"] = { relaxed = { uri = "spotify:playlist:SW", name = "SW" } } }, types = {} }')
    AV.ToggleConfig("locations", True)
    n, errs = G.MOCK.clickAll()
    assert lua_list(errs) == []


def names_played(G):
    return [Path(p.replace("\\", "/")).name for p in lua_list(G.MOCK.played)]


def test_auto_mode_and_mode_signals(wow):
    rt, G = wow
    AV = G.AudioVibes
    # login in Stormwind (a city): Auto → Relaxed, sent right after the location id
    played = names_played(G)
    i = played.index("zone_1_ad.ogg")
    assert played[i + 1] == "cmd_56.ogg", "MODE_RELAXED follows the location"
    assert AV.EffectiveMode("map:1453")[0] == "relaxed"
    assert AV.EffectiveMode("instance:36")[0] == "locked"          # dungeon under Auto
    # switching the overall mode sends the new mode for where you are
    G.MOCK.played = rt.eval("{}")
    AV.SetOverallMode("locked")
    G.MOCK.runTimers()
    assert names_played(G) == ["cmd_57.ogg"]
    assert AV.EffectiveMode("map:1453")[0] == "locked"
    # a per-location override wins over the overall mode
    G.MOCK.played = rt.eval("{}")
    AV.SetLocationMode("map:1453", "relaxed")
    G.MOCK.runTimers()
    assert names_played(G) == ["cmd_56.ogg"]
    assert AV.EffectiveMode("map:1453") == ("relaxed", "location")
    # cycling: locked → auto → relaxed → locked (override still holds for Stormwind)
    AV.CycleMode(); assert AV.db.settings.vibeMode == "auto"
    AV.CycleMode(); assert AV.db.settings.vibeMode == "relaxed"
    G.SlashCmdList["AUDIOVIBES"]("mode locked")
    assert AV.db.settings.vibeMode == "locked"


def test_each_mode_has_its_own_playlist(wow):
    rt, G = wow
    AV = G.AudioVibes
    key = "map:1454"   # Orgrimmar
    assert AV.PlaylistFor(key, "locked").name == "War camp"
    assert AV.PlaylistFor(key, "relaxed").name == "Village fires"
    AV.SavePlaylist("location", key, "relaxed", "spotify:playlist:SLOWTRIBAL", "Slow tribal", False)
    assert AV.PlaylistFor(key, "relaxed").name == "Slow tribal"
    assert AV.PlaylistFor(key, "locked").name == "War camp"            # the other mode is untouched
    assert AV.LinkFor(key, "relaxed") == "https://open.spotify.com/playlist/SLOWTRIBAL"
    AV.SavePlaylist("location", key, "relaxed", None)
    assert AV.db.locations[key] is None                                 # nothing left: entry removed


def test_dialog_edits_both_modes_and_location_mode(wow):
    rt, G = wow
    AV = G.AudioVibes
    AV.OpenPlaylistDialog(rt.eval('{ scope = "location", key = "map:1454", mode = "relaxed" }'))
    ui = AV.dialogWidgets
    ui.link.SetText(ui.link, "https://open.spotify.com/playlist/RELAXEDORG")
    ui.name.SetText(ui.name, "Village fires (mine)")
    # switch to the Locked-in tab and set that one too
    tab = ui.tabs["locked"]
    tab._scripts.OnClick(tab, "LeftButton")
    assert ui.link._text == ""
    ui.link.SetText(ui.link, "spotify:album:WARALBUM")
    # fix the mode for Orgrimmar: Always relaxed (applies immediately)
    chip = ui.modeChips["relaxed"]
    chip._scripts.OnClick(chip, "LeftButton")
    assert AV.db.modes["map:1454"] == "relaxed"
    ui.save._scripts.OnClick(ui.save, "LeftButton")
    loc = AV.db.locations["map:1454"]
    assert loc.relaxed.uri == "spotify:playlist:RELAXEDORG" and loc.relaxed.name == "Village fires (mine)"
    assert loc.locked.uri == "spotify:album:WARALBUM"
    assert AV.db.pending["map:1454"] is True


def test_old_saved_playlists_apply_to_both_modes(wow):
    rt, G = wow
    AV = G.AudioVibes
    G.AudioVibesDB.locations = rt.eval('{ ["map:1429"] = { uri = "spotify:playlist:OLD", name = "Old", shuffle = true } }')
    G.AudioVibesDB.types = rt.eval('{ raid = { uri = "spotify:playlist:OLDRAID", name = "Old raid" } }')
    G.MOCK.fire("ADDON_LOADED", "AudioVibes")
    assert AV.PlaylistFor("map:1429", "relaxed").uri == "spotify:playlist:OLD"
    assert AV.PlaylistFor("map:1429", "locked").uri == "spotify:playlist:OLD"
    assert AV.PlaylistFor("instance:409", "relaxed").name == "Old raid"


def test_auto_switch_off_sends_nothing_on_zone_change(wow):
    rt, G = wow
    AV = G.AudioVibes
    AV.db.settings.autoSwitch = False
    G.MOCK.played = rt.eval("{}")
    G.MOCK.loc.mapId = 1429
    G.MOCK.loc.resting = False
    G.MOCK.fire("ZONE_CHANGED_NEW_AREA")
    for _ in range(4):
        G.MOCK.runTimers()
    assert lua_list(G.MOCK.played) == []
    assert AV.location.key == "map:1429"   # the bar still follows you
