#! /bin/bash
## vim: noet:sw=0:sts=0:ts=4

::registerHook before~App::buildLaunchCommand CounterStrikeSharpHelper::initVars
::registerHook after~App::buildLaunchCommand  CounterStrikeSharpHelper::updateInstance

# Set CSS_ENABLE=1 in the instance's server.conf (or MSM's environment) to opt
# an instance into this addon. Leaving it unset is a no-op, same as SM_PLUGINS
# in SourcemodHelper.
CounterStrikeSharpHelper::updateInstance () {
	[[ $CSS_ENABLE ]] || {
		rmdir "$CSS_TMP_DIR" 2>/dev/null
		return
	}

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
