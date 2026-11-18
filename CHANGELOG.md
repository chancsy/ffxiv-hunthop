# Changelog

All notable changes to HuntHop. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions: `MAJOR.MINOR.PATCH`.

The plugin's release version is `ReleaseVersion` in `plugin/HuntHop/HuntHop.csproj`. Its 4th segment is a local build counter for Dalamud's dev reloads; it isn't tracked here or in git. The userscript has its own version, in its `@version` header.

## [Unreleased]

## [0.1.0] - 2026-11-19

First release. Faloop spawn alerts appear in game chat with a one-click world visit through Lifestream, or an automatic one.

Components: plugin `HuntHop` 0.1.0, userscript `faloop-hunthop.user.js` 0.2.0.

### Added

- **Dalamud plugin `HuntHop`** (API 15)
  - Localhost HTTP listener (`http://localhost:47821/`) that receives alerts from the userscript. It requires a random `X-HuntHop-Token`, caps bodies at 8 KB and only accepts slug-shaped ids.
  - Chat line per alert with a clickable **[Go]** link (`Sansheya (S) @ Aegis in Shaaloani [Go]`).
  - **Click** mode (default) and **Auto** mode. Auto travels only from the Limsa, Gridania or Ul'dah aetheryte, for alerts ≤ 60 s old, with a configurable cooldown (default 300 s).
  - **Auto-hop delay**: Auto mode waits before travelling (default 3 s, 0–30, 0 = immediate) and runs every guard again when the wait ends. The countdown chat line has a **[Cancel]** link; `/hunthop stop` cancels it too.
  - Guards for both modes:
    - Lifestream loaded and idle
    - not in combat, a duty, a cutscene, crafting or gathering
    - World Visit only, unless Data Center Travel is enabled
    - not in a normal party, unless allowed. A cross-world party never blocks: World Visit keeps you in it.
  - Rank (S / SS / hunt FATE) and expansion filters, using a mob table generated from Faloop's data. Defaults are S + SS in ShB / EW / DT.
  - Duplicate suppression (2 min). A chat warning appears when the userscript is silent for 5 min.
  - Commands: `/hunthop` (settings), `go`, `auto on|off`, `stop`, `test <world>`.
  - Settings window: status (listener, userscript contact, Lifestream, aetheryte, pending hop), travel, filters, token (show / copy / regenerate), test alert.
  - Automatic local build number (4th version segment) for Dalamud dev reloads.
- **Userscript `faloop-hunthop.user.js`** (Tampermonkey)
  - Reads Faloop's in-page spawn toasts, with no desktop notification or browser permission needed. Requires the English site UI.
  - Backup hook on Faloop's desktop notifications, deduplicated against toasts.
  - Health ping every 60 s. Menu commands for token, port, forwarding on/off, hiding Windows pop-ups, and a test alert.
- **Tools**
  - `tools/ListenerCheck`: runs the plugin's listener outside the game to check the browser side.
  - `tools/gen-mobtable.js`: regenerates `MobTable.cs` from Faloop's JS bundle.
- **Tests**
  - `tests/userscript`: jsdom tests for toast and desktop-notification parsing.
- **Docs**
  - `README.md`: disclaimer, what it does, requirements, build, setup, tests, commands, guards, security notes, limitations.

### Verified in game

- A real Faloop spawn toast reached chat, and [Go] completed the world visit.
- Auto mode hopped from a city aetheryte, the 3 s delay and [Cancel] worked, and the cooldown blocked the next hop.

[Unreleased]: #unreleased
[0.1.0]: #010---2026-11-19
