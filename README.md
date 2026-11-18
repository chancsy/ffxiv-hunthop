# HuntHop: Faloop alerts → in-game chat → Lifestream world visit

## Disclaimer

**Use at your own risk.** HuntHop is a personal, unofficial project. It is not affiliated with or endorsed by Square Enix, [Faloop](https://faloop.app), NightmareXIV ([Lifestream](https://github.com/NightmareXIV/Lifestream)) or goatcorp ([Dalamud](https://github.com/goatcorp/Dalamud)).

- **It breaks Square Enix's rules.** All Dalamud plugins are third-party tools. In January 2025 the FFXIV producer wrote that *"The use of third-party tools is prohibited by the FINAL FANTASY XIV User Agreement"* ([official forum post](https://forum.square-enix.com/ffxiv/threads/515102-Regarding-the-Use-of-Third-Party-Programs-and-Player-Safety)). Square Enix has said that players found using them can have their accounts suspended, or banned for repeat offences ([2022 statement, as reported](https://automaton-media.com/en/news/20220511-12419/)). HuntHop also **automates travel**, which is more visible than a UI plugin. Auto mode is off by default, and the delay and cooldown are there to keep it low-key, but they don't make it allowed.
- **Faloop hasn't approved it.** Faloop publishes no terms of service; the only legal text on its site is a privacy policy for its Discord bot (checked 2026-11-18). HuntHop's userscript reads alerts from Faloop's page in your own logged-in tab. Faloop could change its site at any time, which would break HuntHop, or restrict accounts that use such tools.
- **Source only, no support.** This repo shares the code for reference; there are no prebuilt downloads and no plugin repository. Build it yourself, and keep it out of streams, screenshots and public chat.

---

This file covers what HuntHop does, setup and testing. Release notes are in [CHANGELOG.md](CHANGELOG.md).

## What it does

[Faloop](https://faloop.app) shows hunt spawns in a browser tab. HuntHop brings them into the game and gets you there.

- **Alerts in game chat.** Each S-rank (or SS / hunt FATE) spawn that Faloop alerts on shows up in chat as one line: mob, rank, instance, world and zone, e.g. `[HuntHop] Sansheya (S) @ Aegis in Shaaloani [Go]`. A single Faloop tab set to your whole region covers every data center there, so you don't need one tab per DC.
- **One-click travel.** Clicking **[Go]** makes [Lifestream](https://github.com/NightmareXIV/Lifestream) world-visit that world. If you aren't at a city aetheryte, Lifestream teleports you to one first. `/hunthop go` does the same for the latest alert, so you can put it on a macro or hotkey.
- **Optional automatic travel.** In Auto mode, HuntHop travels by itself, but only when:
  - you're standing at the Limsa, Gridania or Ul'dah aetheryte
  - the alert is fresh (≤ 60 s)
  - the cooldown since the last automatic hop has passed (default 5 min)

  It doesn't jump instantly. It posts `[HuntHop] Auto-hop to Aegis in 3s [Cancel]` and waits (default 3 s, adjustable). Click **[Cancel]** to stop it. When the wait ends, the safety checks run again.
- **Safety checks before every trip:**
  - Lifestream is loaded and idle.
  - You're not in combat, a duty, a cutscene, or crafting or gathering.
  - The world is reachable by World Visit; Data Center Travel only if you enable it.
  - You're not in a normal party, unless you allow it (Lifestream leaves the party first). A **cross-world party** is always fine: World Visit keeps you in it.

  If a check fails, chat says why instead of travelling.
- **Your filters, twice.** Faloop's own world and expansion filters apply first. HuntHop then filters by rank and expansion, both adjustable in `/hunthop` (out of the box: S + SS ranks, ShB / EW / DT), and drops duplicates.
- **No Faloop password needed.** It reuses the browser tab you're already logged in to. Nothing leaves your PC: the tab talks to the plugin over `localhost`, protected by a random token.
- **Tells you when it stops.** If the Faloop tab is closed or the script stops for 5 minutes, chat warns you.

What it doesn't do (yet):
- teleport to the hunt zone after arriving
- show the exact spawn spot
- send alerts to your phone
- cover A ranks (Faloop doesn't track them)

## How it works

```
faloop.app tab ──(userscript: reads Faloop's in-page spawn alerts)──► http://localhost:47821/hunt
                                                                         │
                                                       HuntHop plugin ◄──┘
                                                         ├─ chat: "Sansheya (S) @ Aegis in Shaaloani [Go]"
                                                         ├─ Click mode: [Go] or /hunthop go → Lifestream.ChangeWorld
                                                         └─ Auto mode: guards → wait (3 s, [Cancel]) → guards again → travel
```

## Layout

| Path | What |
|---|---|
| `plugin/HuntHop/` | [Dalamud](https://github.com/goatcorp/Dalamud) plugin (API 15, `Dalamud.NET.Sdk/15.0.0`) |
| `userscript/faloop-hunthop.user.js` | Tampermonkey userscript for faloop.app (Violentmonkey: untested) |
| `tools/gen-mobtable.js` | Regenerates `plugin/HuntHop/MobTable.cs` from Faloop's JS bundle |
| `tools/ListenerCheck/` | The plugin's listener as a console app, to check the browser side without the game |
| `tests/userscript/` | jsdom tests for the userscript (`npm install && npm test`) |
| `CHANGELOG.md` | Release notes (current: 0.1.0) |

## Requirements

| What | Version / notes |
|---|---|
| FFXIV + [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) | Dalamud enabled. Start the game through XIVLauncher **at least once**: that downloads the Dalamud dev files to `%AppData%\XIVLauncher\addon\Hooks\dev\`, which the build compiles against. |
| [Dalamud](https://github.com/goatcorp/Dalamud) | API **15**. It comes with XIVLauncher and updates itself; the csproj uses `Dalamud.NET.Sdk/15.0.0`. After a big game patch, the SDK version may need raising. |
| [.NET SDK](https://dotnet.microsoft.com/download) | **10.0** or newer. Check with `dotnet --list-sdks`. Visual Studio is optional. |
| [Lifestream](https://github.com/NightmareXIV/Lifestream) plugin | Installed and enabled in game (Setup step 1) |
| Chrome + [Tampermonkey](https://www.tampermonkey.net/) | On recent Chrome, also turn on **Allow User Scripts** in `chrome://extensions` → Tampermonkey → Details |
| [Node.js](https://nodejs.org/) | **18** or newer (jsdom 24 needs it). Check with `node --version`. Only for `tests/userscript` and `tools/gen-mobtable.js`. |

## Build

`<repo>` below means the folder this README is in, for example `D:\code\ffxiv-hunthop`. Replace it with your own path.

```powershell
cd <repo>
dotnet build plugin\HuntHop\HuntHop.csproj -c Debug
```

- Output: `plugin\HuntHop\bin\Debug\HuntHop.dll`, plus `HuntHop.json`, which Dalamud needs next to it.
- Version: the release version is `ReleaseVersion` in `HuntHop.csproj` (e.g. `0.1.0`). The 4th segment is a local build counter in `plugin\HuntHop\build.number`, which git ignores. Each successful build raises it, so Dalamud sees every dev build as new. The build prints e.g. `HuntHop 0.1.0.5 built (next local build: 6)`. To release, change `ReleaseVersion` and add a `CHANGELOG.md` entry.
- If the build says Dalamud assemblies are missing, start the game through XIVLauncher once (see Requirements).

A Release build (`-c Release`) should also package the plugin as `plugin\HuntHop\bin\Release\HuntHop\latest.zip`. Untested: no Release build has been made yet.

## Setup

1. **[Lifestream](https://github.com/NightmareXIV/Lifestream):** in game, `/xlsettings` → **Experimental** → **Custom Plugin Repositories**. Add `https://github.com/NightmareXIV/MyDalamudPlugins/raw/main/pluginmaster.json`, tick it and **Save**. Then `/xlplugins` → search **Lifestream** → Install. This is the official NightmareXIV repo, taken from [Lifestream's README](https://github.com/NightmareXIV/Lifestream#installation).
2. **HuntHop (dev plugin):** in game, `/xlsettings` → **Experimental** → **Dev Plugin Locations**.
   - Add `<repo>\plugin\HuntHop\bin\Debug\HuntHop.dll` (the full path, with `<repo>` replaced), then **Save and Close**.
   - Then `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** → enable **HuntHop**.

   **After a rebuild:** wait until `dotnet build` has finished, then reload HuntHop in `/xlplugins` → Dev Tools. Don't rely on Dalamud's auto-reload: it can fire while the build still locks `HuntHop.json` and fail.
3. `/hunthop` opens settings. The Status section should say *Listening on http://localhost:47821/* and *Lifestream: loaded*. Press **Copy token**.
4. **Userscript:** in [Tampermonkey](https://www.tampermonkey.net/), choose **Create a new script**, paste the contents of `userscript/faloop-hunthop.user.js`, and save.
5. Open [faloop.app](https://faloop.app) (logged in) and reload it once. Tampermonkey menu → **HuntHop: set token** → paste.
6. **Faloop settings:**
   - S-Ranks → World Filters = **Region**.
   - Expansion Filters: tick the expansions you hunt, e.g. ShB / EW / DT. Set the same ones in `/hunthop` → Filters, or the plugin drops what Faloop sends.
   - Notifications → **website toasts on**, which is Faloop's default.
   - Keep the site language **English**: mob names in the toast are mapped to IDs from the English names.
   - Desktop notifications are optional. If they're on, the script also hooks them as a backup, and the same spawn is sent only once. The Tampermonkey menu → **HuntHop: toggle Windows pop-ups** hides the Windows pop-up but still forwards it.
7. Within about a minute, `/hunthop` → Status shows *Userscript: last contact …*.

Keep the Faloop tab open. It can be in the background; the script still sees Faloop's alerts there.

## Test

| Step | Expected |
|---|---|
| `/hunthop test aegis` (or the Test button) | Chat: `[HuntHop] Sansheya (S) @ Aegis in Shaaloani [Go]` |
| Click **[Go]** on another world of your DC | [Lifestream](https://github.com/NightmareXIV/Lifestream) starts the world visit (teleports to a city aetheryte first if you aren't at one) |
| Tampermonkey menu → **HuntHop: send test (Sansheya @ a world you type)** → `aegis` | The same chat line, this time through the browser → localhost path |
| Browser DevTools console on faloop.app | `[HuntHop] hooked (toasts + desktop); forwarding ON → localhost:47821` |
| A real Faloop spawn alert on the page | Its chat line appears within a second or two |
| Close the Faloop tab for 5+ min | Chat: `[HuntHop] No contact from the Faloop userscript for 5 minutes. Is the faloop.app tab still open?` |
| `/hunthop auto on`, stand at the Limsa aetheryte, test alert for another world | `[HuntHop] Auto-hop to <World> in 3s [Cancel]`, then it travels |
| The same, but click **[Cancel]** within 3 s | `[HuntHop] Auto-hop to <World> cancelled.` and no travel |
| A second test alert within the cooldown, still at the aetheryte | `[HuntHop] Auto-hop skipped: cooldown, …s left` |

### Browser side without the game

`tools\ListenerCheck\run.ps1` runs the plugin's own `HuntListener` code as a console app on port 47821 and prints every alert and ping.
- Token: the plugin's saved token if the plugin has ever been loaded, otherwise `tools\ListenerCheck\token.txt`.
- Don't run it while the game has HuntHop loaded, because both want the port.
- After the plugin generates its own token, paste that one into the userscript instead.

## Commands

| Command | Does |
|---|---|
| `/hunthop` | Settings window |
| `/hunthop go` | Travel to the latest alert's world (bind it to a macro for a hotkey) |
| `/hunthop auto on` / `off` | Switch between automatic and click-to-go |
| `/hunthop stop` | Cancel a waiting auto-hop and abort a running [Lifestream](https://github.com/NightmareXIV/Lifestream) trip |
| `/hunthop test <world>` | Fake alert (Sansheya) for that world |

## Guards

**Both modes:**
- logged in
- [Lifestream](https://github.com/NightmareXIV/Lifestream) loaded and idle
- not in combat, a duty, a cutscene, zoning, crafting, gathering, casting or an event
- the target world isn't your current world and is reachable by World Visit (Data Center Travel is off unless enabled)
- not in a normal party, unless "allow while in a normal party" is on; Lifestream then `/leave`s it first
- a cross-world party never blocks. World Visit keeps you in it, and Lifestream only leaves normal parties. The exception is Data Center Travel (if enabled): Lifestream's "Leave party before switching data center" setting (on by default) leaves cross-world parties too.

**Auto mode also requires:**
- standing at the Limsa, Gridania or Ul'dah aetheryte (Lifestream's world-change aetherytes)
- the mob is in the plugin's table
- the alert is younger than 60 s
- the **cooldown** has passed: default 300 s between automatic hops, 0 = off
- no other auto-hop is already waiting

Auto mode then waits the **auto-hop delay** (default 3 s, 0–30, 0 = immediate) and runs all guards again before travelling. The cooldown and delay are the plugin's own settings, not game limits. Click mode ignores both.

## Filters

Faloop filters first: world method, expansions, and desktop notifications per event type. The plugin then filters by rank and expansion, using `MobTable.cs`. Both are checkboxes in `/hunthop` → Filters. Out of the box, S / SS are on, hunt FATEs off, and ShB / EW / DT on; match them to your Faloop expansion filters. Turn on *Show filtered-out alerts in grey* to see what was dropped.

## Security notes

- The listener binds to `localhost` only and rejects any request without the random `X-HuntHop-Token` (fixed-time compare). It also caps bodies at 8 KB and only accepts slug-shaped world/mob ids.
- A request is rejected unless its Host header is `localhost`, so `127.0.0.1` URLs fail by design.
- The token is stored in plain text in the plugin config, `%AppData%\XIVLauncher\pluginConfigs\HuntHop.json`, and in Tampermonkey's storage. **New token** in `/hunthop` replaces it; paste the new one into the userscript.
- The userscript reads only Faloop's spawn alerts (in-page toasts, and desktop notifications if enabled) and talks only to `localhost`.
- Git ignores `bin/`, `obj/`, `node_modules/` and `tools/ListenerCheck/token.txt` (ListenerCheck's own test token).

## Limitations

- **English Faloop UI only.** The toast shows translated mob names. In another language, alerts show as "unknown mob" and never auto-hop.
- **Breaks if Faloop changes its alert layout.** Chat lines stop arriving even though the userscript still pings. Update the toast markup in `tests/userscript`, then the selectors in the userscript, and rerun `npm test`.
- **New mobs or expansions** need `tools/gen-mobtable.js` and a rebuild; until then they show as "unknown mob".
- **After a big game patch**, wait for a Dalamud update, then rebuild (raise the SDK version in `HuntHop.csproj` if needed).
- **Japan same-region matching:** whether Lifestream treats the other Japanese data centers as World Visit targets is unconfirmed. If it doesn't, HuntHop reports them as needing Data Center Travel.
- The Faloop tab must stay open; HuntHop has no data source of its own.
