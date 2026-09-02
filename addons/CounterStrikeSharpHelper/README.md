# CounterStrikeSharp Helper

Installs [Metamod:Source (2.x)](https://www.metamodsource.net/) and
[CounterStrikeSharp](https://docs.cssharp.dev/docs/guides/getting-started.html)
into a CS2 instance's game directory, following the steps in the official
[Getting Started guide](https://docs.cssharp.dev/docs/guides/getting-started.html).

**Status: early-stage, unverified against a live CS2 server.** The download
step (fetching the newest GitHub release of both projects and unpacking them)
and the `gameinfo.gi` patch step were tested directly. What has **not** been
tested is actually launching a CS2 dedicated server with the result and
confirming `meta list` (in the server console) shows both Metamod and
CounterStrikeSharp loaded. Please verify this on your setup and report back
if anything is off - see the caveats below.

## What it does

On every server launch (`start`/`restart`) for an instance with this addon
enabled, it:

1. Downloads the latest Metamod:Source 2.x and CounterStrikeSharp
   (with-runtime, Linux) releases from GitHub (cached locally by filename
   under `~/msm.d/cs2/addons/counterstrikesharp-helper/filecache`, so a given
   build is only downloaded once).
2. Copies their `addons/` directories into the instance's game directory
   (`<instance>/cs2/game/csgo/`), overwriting any existing Metamod/CSS files.
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
3. Start (or restart) the instance. Check the launch log for this addon's
   output, then in the in-game console (`cs2-server @<name> console`) run
   `meta list` to confirm Metamod and CounterStrikeSharp both loaded.

## Known limitations / not handled

- **Conflicts with `SourcemodHelper`**: both addons install their own copy of
  Metamod into the same `addons/metamod` path. Don't enable both on the same
  instance without reconciling this yourself.
- **CounterStrikeSharp plugins** are not managed by this addon - it only
  installs the framework itself. Drop plugin `.dll`s into
  `<instance>/cs2/game/csgo/addons/counterstrikesharp/plugins/` manually (or
  extend this addon, following `SourcemodHelper`'s `SM_PLUGINS` pattern, if
  you want that automated).
- Requires `wget`, `jq`, `tar`, and `unzip` - all already required by
  `cs2-multiserver` itself.
