#!/usr/bin/env bash
# Builds the loader and both game-version builds into out/.
# Needs lib/v111/ and lib/v113/, each with sts2.dll, 0Harmony.dll and GodotSharp.dll copied from that game version
# (Slay the Spire 2/data_sts2_windows_x86_64/).
set -e
cd "$(dirname "$0")"
for p in v111/WaitTracker.V111.csproj v113/WaitTracker.V113.csproj WaitTracker.csproj; do dotnet build "$p" -c Release -o out; done
echo "Install: copy out/WaitTracker.dll, out/WaitTracker.V111.dll, out/WaitTracker.V113.dll and WaitTracker.json into mods/WaitTracker/"
