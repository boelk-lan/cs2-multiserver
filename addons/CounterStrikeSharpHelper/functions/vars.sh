#! /bin/bash
## vim: noet:sw=0:sts=0:ts=4

CounterStrikeSharpHelper::initVars () {
	CSS_HOME="$USER_DIR/$APP/addons/counterstrikesharp-helper"
	CSS_TMP_DIR="$CSS_HOME/tmp"
	# NOTE: hardcoded to match cs2/app/functions/instance.sh's own layout -
	# there's no shared variable for this in cs2-multiserver itself either.
	CSS_TARGET_DIR="$INSTANCE_DIR/game/csgo"
	CSS_FILECACHE_DIR="$CSS_HOME/filecache"
	mkdir -p "$CSS_TMP_DIR"
	mkdir -p "$CSS_FILECACHE_DIR"
	CSS_TMP_DIR="$(mktemp -d -p "$CSS_TMP_DIR")"
}
