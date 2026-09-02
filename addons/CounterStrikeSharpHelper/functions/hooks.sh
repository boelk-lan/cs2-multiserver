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
