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

# Clears the PF_X (executable) flag on an ELF64 LE file's PT_GNU_STACK
# program header, in place - equivalent to `execstack -c <file>` (which isn't
# guaranteed to be installed; this needs only od/dd, already assumed present).
#
# CounterStrikeSharp's own counterstrikesharp.so ships with this flag set,
# which some systems' dynamic linker refuses to honor ("cannot enable
# executable stack as shared object requires: Invalid argument"), causing
# `meta list` to show it as <ERROR> and the plugin to never load. Verified:
# only this one file in the whole Metamod/CounterStrikeSharp/PugSharp bundle
# has the flag set - see CounterStrikeSharpHelper::installCounterStrikeSharp.
CounterStrikeSharpHelper::clearExecStack () {
	local file="$1"
	[[ -f $file ]] || return 0

	local magic
	magic="$(od -An -tx1 -j0 -N4 "$file" | tr -d ' \n')"
	[[ $magic == 7f454c46 ]] || return 0
	local ei_class ei_data
	ei_class="$(od -An -tu1 -j4 -N1 "$file" | tr -d ' ')"
	ei_data="$(od -An -tu1 -j5 -N1 "$file" | tr -d ' ')"
	[[ $ei_class == 2 && $ei_data == 1 ]] || return 0  # only ELF64 LE supported

	local e_phoff e_phentsize e_phnum
	e_phoff="$(od -An -tu8 -j$((0x20)) -N8 --endian=little "$file" | tr -d ' ')"
	e_phentsize="$(od -An -tu2 -j$((0x36)) -N2 --endian=little "$file" | tr -d ' ')"
	e_phnum="$(od -An -tu2 -j$((0x38)) -N2 --endian=little "$file" | tr -d ' ')"

	local i off p_type p_flags_byte new_byte
	for (( i = 0; i < e_phnum; i++ )); do
		off=$(( e_phoff + i * e_phentsize ))
		p_type="$(od -An -tu4 -j"$off" -N4 --endian=little "$file" | tr -d ' ')"
		[[ $p_type == 1685382481 ]] || continue  # PT_GNU_STACK = 0x6474e551
		# p_flags is a 4-byte LE field, but only bits 0-2 (R/W/X) are ever set,
		# so the executable bit (PF_X=1) lives entirely in its first byte.
		p_flags_byte="$(od -An -tu1 -j$((off + 4)) -N1 "$file" | tr -d ' ')"
		(( p_flags_byte & 1 )) || continue  # PF_X already clear
		new_byte=$(( p_flags_byte & ~1 & 0xff ))
		printf "$(printf '\\%03o' "$new_byte")" |
			dd of="$file" bs=1 seek=$((off + 4)) count=1 conv=notrunc status=none
		debug <<< "Cleared executable-stack flag on **$file**"
	done
}
