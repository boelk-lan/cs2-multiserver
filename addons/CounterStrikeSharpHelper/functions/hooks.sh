#! /bin/bash
## vim: noet:sw=0:sts=0:ts=4

::registerHook before~App::buildLaunchCommand CounterStrikeSharpHelper::initVars
::registerHook after~App::buildLaunchCommand  CounterStrikeSharpHelper::shareWorkshop
::registerHook after~App::buildLaunchCommand  CounterStrikeSharpHelper::updateInstance

# Workshop maps (host_workshop_map <id>) are downloaded by each server into its own
# game/bin/linuxsteamrt64/steamapps/workshop - so every instance would download every map
# again. Link that steamapps directory to one shared directory instead, for all instances
# (not only CSS_ENABLE ones). Maps an instance already downloaded are moved over once.
# ponytail: two servers downloading the same new map at the same moment share one manifest
# (appworkshop_730.acf); fine at a LAN, per-instance manifests if that ever breaks.
CounterStrikeSharpHelper::shareWorkshop () {
	local shared="$USER_DIR/$APP/workshop/steamapps"
	local link="$INSTANCE_DIR/game/bin/linuxsteamrt64/steamapps"
	[[ -L $link ]] && return
	mkdir -p "$shared"
	if [[ -d $link ]]; then
		cp -rn "$link"/. "$shared"/ && rm -rf "$link" || return
	fi
	ln -s "$shared" "$link"
}

# Set CSS_ENABLE=1 in the instance's server.conf (or MSM's environment) to opt
# an instance into this addon. Leaving it unset is a no-op, same as SM_PLUGINS
# in SourcemodHelper.
CounterStrikeSharpHelper::updateInstance () {
	[[ $CSS_ENABLE ]] || {
		rmdir "$CSS_TMP_DIR" 2>/dev/null
		return
	}

	# CounterStrikeSharp's .NET runtime crashes ("stack smashing detected", SIGSEGV) while the
	# server process exits, after the game has already shut down cleanly. Harmless, but every stop
	# would leave a ~300 MB core dump in /var/lib/systemd/coredump - so don't write one.
	LAUNCH_CMD="prlimit --core=0 $LAUNCH_CMD"

	warning <<-EOF
		The CounterStrikeSharp helper addon is early-stage and UNTESTED against
		a live CS2 server. It will overwrite Metamod/CounterStrikeSharp files in
		**$CSS_TARGET_DIR** and patch its gameinfo.gi on every launch.
	EOF

	(
		cd "$CSS_TMP_DIR"
		CounterStrikeSharpHelper::installMetamod || return
		CounterStrikeSharpHelper::installCounterStrikeSharp || return
		CounterStrikeSharpHelper::installPugSharp || return
		CounterStrikeSharpHelper::installBoelkStatus || return
		CounterStrikeSharpHelper::installK4Arenas || return
		CounterStrikeSharpHelper::installBoelkArena || return
	) || return

	cp -r "$CSS_TMP_DIR"/* "$CSS_TARGET_DIR"
	CounterStrikeSharpHelper::patchGameinfo

	# Move to last_state directory for easier debugging (mirrors SourcemodHelper)
	rm -rf "$CSS_HOME/last_state"
	mv "$CSS_TMP_DIR" "$CSS_HOME/last_state"
}

CounterStrikeSharpHelper::installMetamod () {
	echo "Fetching latest Metamod:Source 2.x (Source2/CS2) Linux build ..."
	local url
	url="$(CounterStrikeSharpHelper::latestAssetUrl alliedmodders/metamod-source 'mmsource-2\..*-linux\.tar\.gz$')"
	[[ $url ]] || {
		error <<< "Could not find a Metamod:Source 2.x Linux release on GitHub."
		return 1
	}
	CounterStrikeSharpHelper::unpackTar "$url"
}

CounterStrikeSharpHelper::installCounterStrikeSharp () {
	echo "Fetching latest CounterStrikeSharp (with-runtime, Linux) build ..."
	local url
	url="$(CounterStrikeSharpHelper::latestAssetUrl roflmuffin/CounterStrikeSharp 'counterstrikesharp-with-runtime-linux-.*\.zip$')"
	[[ $url ]] || {
		error <<< "Could not find a CounterStrikeSharp Linux release on GitHub."
		return 1
	}
	CounterStrikeSharpHelper::unpackZip "$url"

	# See CounterStrikeSharpHelper::clearExecStack in functions/utils.sh - without
	# this, Metamod fails to load the plugin on systems that refuse an executable
	# stack, showing up as `<ERROR>` in `meta list` with no obvious cause.
	CounterStrikeSharpHelper::clearExecStack "addons/counterstrikesharp/bin/linuxsteamrt64/counterstrikesharp.so"
}

# Set CSS_PUGSHARP=1 (alongside CSS_ENABLE=1) in the instance's server.conf
# to also install https://github.com/Lan2Play/PugSharp, a CounterStrikeSharp
# plugin for running pug/matchmaking-style servers. Uses the plain
# "PugSharp_linux_*" release asset (not the "with_cssharp"-bundled ones),
# since CounterStrikeSharp is already installed by this addon separately.
CounterStrikeSharpHelper::installPugSharp () {
	[[ $CSS_PUGSHARP ]] || return 0
	echo "Fetching latest PugSharp (Linux) build ..."
	local url
	url="$(CounterStrikeSharpHelper::latestAssetUrl Lan2Play/PugSharp '^PugSharp_linux_[0-9].*\.zip$')"
	[[ $url ]] || {
		error <<< "Could not find a PugSharp Linux release on GitHub."
		return 1
	}
	CounterStrikeSharpHelper::unpackZip "$url"
}

# Set CSS_K4ARENAS=1 (alongside CSS_ENABLE=1) to install K4-Arenas
# (https://github.com/K4ryuu/K4-Arenas), a multi-arena 1v1 plugin. Needs an arena map
# (workshop, e.g. WORKSHOP_MAP_ID=3232857701). Runs without its MySQL database (weapon
# preferences then last until a player leaves).
CounterStrikeSharpHelper::installK4Arenas () {
	[[ $CSS_K4ARENAS ]] || return 0
	echo "Fetching K4-Arenas ..."
	# Pinned: the project was archived on 2026-07-31, v2.0.8 is its last release
	local file="$(CounterStrikeSharpHelper::downloadFile https://github.com/K4ryuu/K4-Arenas/releases/download/v2.0.8/K4-Arenas.zip)"
	[[ -r $file ]] || {
		error <<< "Could not download K4-Arenas."
		return 1
	}
	# The zip has a K4-Arenas/{plugins,shared} root (plus __MACOSX junk), meant for addons/counterstrikesharp
	unzip -q -o "$file" 'K4-Arenas/*' -d k4-unpack || return
	mkdir -p addons/counterstrikesharp
	cp -r k4-unpack/K4-Arenas/. addons/counterstrikesharp/
	rm -rf k4-unpack
}

# Set CSS_BOELK_ARENA_URL (alongside CSS_K4ARENAS=1) to build and install the BoelkArena
# plugin from this addon's BoelkArena/ directory: it POSTs every 1v1 arena duel (winner,
# loser) to that URL, for the backend's ELO. Built against the K4-ArenaSharedApi.dll from
# the K4-Arenas release unpacked just before. Like BoelkStatus, a failed build only skips it.
CounterStrikeSharpHelper::installBoelkArena () {
	[[ $CSS_K4ARENAS && $CSS_BOELK_ARENA_URL ]] || return 0
	local dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
	local src="$THIS_DIR/addons/CounterStrikeSharpHelper/BoelkArena"
	local out="$CSS_HOME/boelkarena-build"
	local api="$PWD/addons/counterstrikesharp/shared/K4-ArenaSharedApi/K4-ArenaSharedApi.dll"
	echo "Building BoelkArena plugin ..."
	"$dotnet" build "$src" -c Release -o "$out" -p:K4ApiPath="$api" --nologo -v quiet >/dev/null || {
		warning <<< "Could not build the BoelkArena plugin with **$dotnet** - skipping it (no ELO)."
		return 0
	}
	mkdir -p addons/counterstrikesharp/plugins/BoelkArena addons/counterstrikesharp/configs/plugins/BoelkArena
	cp "$out"/BoelkArena.{dll,deps.json} addons/counterstrikesharp/plugins/BoelkArena/
	jq -n --arg url "$CSS_BOELK_ARENA_URL" '{Url: $url, ConfigVersion: 1}' \
		> addons/counterstrikesharp/configs/plugins/BoelkArena/BoelkArena.json

	# BoelkArena sets the clan tag itself ("ARENA 1 | 1016"), so turn off K4-Arenas' own ("ARENA 1").
	# Its config only exists after the first start; until then BoelkArena overwrites K4's tag right
	# after each round start. (sed, not jq: the file starts with a // comment line.)
	local k4cfg="$CSS_TARGET_DIR/addons/counterstrikesharp/configs/plugins/K4-Arenas/K4-Arenas.json"
	[[ -f $k4cfg ]] && sed -i 's/"disable-clantags": false/"disable-clantags": true/' "$k4cfg"
	return 0
}

# Set CSS_BOELK_STATUS_URL (alongside CSS_ENABLE=1) to build and install the
# BoelkStatus plugin from this addon's BoelkStatus/ directory. It POSTs the
# player list to that URL on connect/disconnect/team switch and every 3 seconds,
# and the final scoreboard at match end. Optional, written to its config:
#   CSS_BOELK_EVERYONE_ADMIN=1      every player gets @css/root (e.g. for !rcon)
#   CSS_BOELK_MAP_COMMANDS="..."    server commands run on every map start
#   CSS_PUGSHARP_MATCH_CONFIG=path  PugSharp match config (JSON), copied to
#                                   PugSharp/Config/boelk-match.json and loaded
#                                   once after the server has started
# Needs the .NET SDK matching CounterStrikeSharp's runtime (currently 10):
# `dotnet` on PATH, in ~/.dotnet, or set DOTNET=/path/to/dotnet. If the build
# fails the server still starts, just without the plugin.
CounterStrikeSharpHelper::installBoelkStatus () {
	[[ $CSS_BOELK_STATUS_URL ]] || return 0
	local dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
	local src="$THIS_DIR/addons/CounterStrikeSharpHelper/BoelkStatus"
	local out="$CSS_HOME/boelkstatus-build"
	echo "Building BoelkStatus plugin ..."
	"$dotnet" build "$src" -c Release -o "$out" --nologo -v quiet >/dev/null || {
		warning <<< "Could not build the BoelkStatus plugin with **$dotnet** - skipping it."
		return 0
	}
	mkdir -p addons/counterstrikesharp/plugins/BoelkStatus addons/counterstrikesharp/configs/plugins/BoelkStatus
	cp "$out"/BoelkStatus.{dll,deps.json} addons/counterstrikesharp/plugins/BoelkStatus/

	local pugsharpFile=""
	if [[ $CSS_PUGSHARP && $CSS_PUGSHARP_MATCH_CONFIG ]]; then
		pugsharpFile="boelk-match.json"
		mkdir -p PugSharp/Config
		cp "$CSS_PUGSHARP_MATCH_CONFIG" "PugSharp/Config/$pugsharpFile"
	fi
	jq -n \
		--arg url "$CSS_BOELK_STATUS_URL" \
		--argjson admin "$([[ $CSS_BOELK_EVERYONE_ADMIN ]] && echo true || echo false)" \
		--arg commands "$CSS_BOELK_MAP_COMMANDS" \
		--arg pugsharp "$pugsharpFile" \
		'{Url: $url, EveryoneAdmin: $admin, MapStartCommands: $commands, PugSharpConfigFile: $pugsharp, ConfigVersion: 1}' \
		> addons/counterstrikesharp/configs/plugins/BoelkStatus/BoelkStatus.json
}

# CS2 (Source 2) does not auto-discover Metamod the way Source 1 games did -
# gameinfo.gi needs an explicit search path added beneath Game_LowViolence.
# Source: https://docs.cssharp.dev/docs/guides/getting-started.html
CounterStrikeSharpHelper::patchGameinfo () {
	local gameinfo="$CSS_TARGET_DIR/gameinfo.gi"
	[[ -f $gameinfo ]] || {
		warning <<< "**$gameinfo** not found - add \"Game csgo/addons/metamod\" to it manually."
		return
	}
	local patched_re='Game[[:space:]]+csgo/addons/metamod([[:space:]]|$)'
	grep -qE "$patched_re" "$gameinfo" && return
	[[ -w $gameinfo ]] || {
		warning <<< "**$gameinfo** is not writable - add \"Game csgo/addons/metamod\" to it manually."
		return
	}
	sed -i '/Game_LowViolence[[:space:]]\+csgo_lv/a\\t\t\tGame\tcsgo/addons/metamod' "$gameinfo"
	grep -qE "$patched_re" "$gameinfo" || warning <<-EOF
		Could not automatically patch **$gameinfo** (unexpected file format).
		Add "Game csgo/addons/metamod" on a line below "Game_LowViolence csgo_lv" manually.
	EOF
}
