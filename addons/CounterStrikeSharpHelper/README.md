# CounterStrikeSharp Helper

Installs [Metamod:Source (2.x)](https://www.metamodsource.net/) and
[CounterStrikeSharp](https://docs.cssharp.dev/docs/guides/getting-started.html)
into a CS2 instance's game directory, following the steps in the official
[Getting Started guide](https://docs.cssharp.dev/docs/guides/getting-started.html).
Optionally also installs [PugSharp](https://github.com/Lan2Play/PugSharp), a
CounterStrikeSharp plugin for running pug/matchmaking-style servers.

**Status: early-stage.** First real test against a live CS2 server (Metamod
loaded, but `meta list` showed CounterStrikeSharp as `<ERROR>`) turned up a
real bug, now fixed: CounterStrikeSharp's `counterstrikesharp.so` ships with
its ELF stack marked executable, which this system's dynamic linker refused
("cannot enable executable stack as shared object requires: Invalid
argument") - see "Known limitations" below for the fix and how it's now
applied automatically. That fix has been verified to actually flip the ELF
flag correctly, but a full server restart with the fixed file and a
`meta list`/`css_plugins list` recheck is still pending - please confirm and
report back.

## What it does

On every server launch (`start`/`restart`) for an instance with this addon
enabled, it:

1. Downloads the latest Metamod:Source 2.x, CounterStrikeSharp (with-runtime,
   Linux), and, if enabled, PugSharp (Linux) releases from GitHub (cached
   locally by filename under
   `~/msm.d/cs2/addons/counterstrikesharp-helper/filecache`, so a given build
   is only downloaded once).
2. Copies their files into the instance's game directory
   (`<instance>/game/csgo/`), overwriting any existing Metamod/CSS/PugSharp
   files.
3. Adds `Game csgo/addons/metamod` beneath `Game_LowViolence csgo_lv` in that
   directory's `gameinfo.gi`, if not already present, so the Source 2 engine
   picks up Metamod (and, through it, CounterStrikeSharp).

There is no version pinning: both projects only ship rolling
"dev build"/prerelease binaries for the 2.x / CS2-compatible line, so this
always installs whatever is newest at launch time. If you need a stable,
reproducible version instead, edit the `test`/pattern arguments to
`CounterStrikeSharpHelper::latestAssetUrl` in `functions/hooks.sh` to pin an
exact GitHub release tag.

## Setup

1. Enable the addon globally by adding `CounterStrikeSharpHelper` to
   `MSM_ADDONS` in your MSM config (see `cs2-server setup`, or edit
   `~/msm.d/cfg/app-cs2.conf` directly).
2. Opt a specific instance in by setting `CSS_ENABLE=1` in that instance's
   `~/msm.d/cs2/cfg/inst-<name>/server.conf`. Instances without this set are
   left untouched (same convention as `SM_PLUGINS` in `SourcemodHelper`).
3. Optionally also set `CSS_PUGSHARP=1` in the same file to install PugSharp
   on top. It is pinned to `PUGSHARP_VERSION` (`functions/hooks.sh`) and its own
   DLLs are rebuilt from source with `PugSharp/knife-round.patch` (match config
   `"knife_round": true`: a knife round decides which team picks its side).
   Needs `dotnet` and `git`; if the build fails, the stock release runs.
4. Start (or restart) the instance. Check the launch log for this addon's
   output, then in the in-game console (`cs2-server @<name> console`) run
   `meta list` to confirm Metamod and CounterStrikeSharp both loaded (and
   `css_plugins list` for PugSharp, if enabled).

## Reporting match state to the boelk-lan frontend

The frontend's instance view no longer polls the server console for the
current map/players - PugSharp pushes that instead. To wire it up, point
your PugSharp match config's `cvars.get5_remote_log_url` (loaded via
`ps_loadconfig`/`ps_loadconfigfile`, see
[pugsharp.lan2play.de](https://pugsharp.lan2play.de/)) at:

```
http://<backend-host>:3001/api/instances/<instance-name>/pugsharp-events
```

(`<instance-name>` is the plain instance name, e.g. `test`, or `base` for the
base installation.) `backend-server` accepts PugSharp's Get5-API-compatible
event stream there and keeps an in-memory snapshot of the current map, match
ID, game state, and connected players (with team). This is match-scoped: the
snapshot only has data while/after a PugSharp match has actually reported
events - it does not reflect players connected outside of a PugSharp match.

## Known limitations / not handled

- **CounterStrikeSharp's executable-stack flag**: `counterstrikesharp.so`
  ships with its ELF `PT_GNU_STACK` header marked executable. Some systems'
  dynamic linker refuses to honor this, causing Metamod to fail loading it
  (`<ERROR>` in `meta list`, with
  `cannot enable executable stack as shared object requires: Invalid argument`
  in the server log - not shown by `meta list` itself). Root cause unclear
  (not an SELinux AVC denial on the system this was found on - `ausearch`/
  `journalctl` showed nothing), but the fix is unambiguous: clear the flag.
  This addon now does that automatically after every CounterStrikeSharp
  download/extraction (`CounterStrikeSharpHelper::clearExecStack` in
  `functions/utils.sh`, applied in `installCounterStrikeSharp`) using a
  small in-place ELF program-header patch (needs only `od`/`dd`, not the
  `execstack`/`patchelf` tools, which may not be installed). Verified to
  correctly flip `RWE` to `RW` on the real downloaded binary; not yet
  confirmed to resolve the actual `meta list` error end-to-end on a running
  server (see Status above).
- **Conflicts with `SourcemodHelper`**: both addons install their own copy of
  Metamod into the same `addons/metamod` path. Don't enable both on the same
  instance without reconciling this yourself.
- **Other CounterStrikeSharp plugins** besides PugSharp are not managed by
  this addon. Drop their files into
  `<instance>/game/csgo/addons/counterstrikesharp/plugins/` manually (or
  extend this addon's `installPugSharp`-style pattern in `functions/hooks.sh`
  if you want more of them automated).
- Requires `wget`, `jq`, `tar`, and `unzip` - all already required by
  `cs2-multiserver` itself.
