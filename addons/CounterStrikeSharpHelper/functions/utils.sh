#! /bin/bash
## vim: noet:sw=0:sts=0:ts=4

# Finds the newest GitHub release asset for repo $1 ("owner/name") whose
# filename matches the extended regex $2, and prints its download URL.
#
# NOTE: unlike SourcemodHelper's downloadFile, this has no checksum to verify
# against, since we deliberately track a rolling "latest" build (both
# Metamod:Source's 2.x branch and CounterStrikeSharp ship this way, with no
# stable pinned version). Files are still cached locally by filename, so a
# given build is only downloaded once - see CounterStrikeSharpHelper::downloadFile.
CounterStrikeSharpHelper::latestAssetUrl () {
	local repo="$1" pattern="$2"
	wget -qO- "https://api.github.com/repos/$repo/releases?per_page=20" | jq -r --arg pattern "$pattern" '
		[.[] | .assets[] | select(.name | test($pattern))][0].browser_download_url // empty
	'
}

# Downloads $1 into the file cache (skipping the download if already cached
# under that filename) and prints the local path.
CounterStrikeSharpHelper::downloadFile () {
	local url="$1"
	[[ $url ]] || return 1
	local cachefile="$CSS_FILECACHE_DIR/${url##*/}"
	if [[ ! -r $cachefile ]]; then
		wget -q -O "$cachefile.part" "$url" || return
		mv "$cachefile.part" "$cachefile"
	fi
	echo "$cachefile"
}

CounterStrikeSharpHelper::unpackTar () {
	local file="$(CounterStrikeSharpHelper::downloadFile "$1")"
	[[ -r $file ]] && tar xzf "$file"
}

CounterStrikeSharpHelper::unpackZip () {
	local file="$(CounterStrikeSharpHelper::downloadFile "$1")"
	[[ -r $file ]] && unzip -q -o "$file"
}
