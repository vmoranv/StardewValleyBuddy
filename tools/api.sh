#!/usr/bin/env bash
# Dump a game type's decompiled source from the local Stardew Valley install,
# so mod code can be written against the real API instead of guessed member names.
#
#   tools/api.sh StardewValley.Farmer              # full type
#   tools/api.sh StardewValley.Object health       # type + grep for a member
#   tools/api.sh -t StardewValley.Tools.MeleeWeapon DoDamage speed
#
# Output is cached under .cache/api/<Type>.cs.
set -euo pipefail

GAME_PATH="${GAME_PATH:-D:/game/steam/steamapps/common/Stardew Valley}"
ILSPY="${ILSPY:-$HOME/.dotnet/tools/ilspycmd.exe}"
CACHE="$(cd "$(dirname "$0")/.." && pwd)/.cache/api"
mkdir -p "$CACHE"

if [ ! -x "$ILSPY" ]; then
  echo "ilspycmd not found at $ILSPY — install with: dotnet tool install -g ilspycmd" >&2
  exit 1
fi

type_name="${1:?usage: tools/api.sh <Namespace.Type> [grep-pattern...]}"
shift || true
file="$CACHE/$type_name.cs"

if [ ! -s "$file" ]; then
  # Game types live in Stardew Valley.dll; GameData types in StardewValley.GameData.dll.
  if ! "$ILSPY" -t "$type_name" "$GAME_PATH/Stardew Valley.dll" > "$file" 2>/dev/null || [ ! -s "$file" ]; then
    "$ILSPY" -t "$type_name" "$GAME_PATH/StardewValley.GameData.dll" > "$file" 2>/dev/null || true
  fi
fi

if [ ! -s "$file" ]; then
  echo "Type '$type_name' not found. Check the namespace or try: $ILSPY -l c \"$GAME_PATH/Stardew Valley.dll\" | grep -i <fragment>" >&2
  exit 2
fi

if [ $# -eq 0 ]; then
  cat "$file"
else
  pattern="$(printf '%s|' "$@")"
  grep -nE "${pattern%|}" "$file" || { echo "(no match for: $*)"; exit 3; }
fi
