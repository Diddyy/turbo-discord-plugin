#!/usr/bin/env sh
# Builds the plugin folder a Turbo server loads: dist/discord/{TurboDiscord.dll,TurboDiscord.xml,manifest.json}.
# TURBO_CLOUD: the turbo-cloud checkout to build against (default ../turbo-cloud), already built.
set -e
cd "$(dirname "$0")/.."
TURBO_CLOUD="${TURBO_CLOUD:-$(pwd)/../turbo-cloud}"
CONFIGURATION="${TURBO_CLOUD_CONFIGURATION:-Debug}"
rm -rf dist/discord
dotnet build TurboDiscord/TurboDiscord.csproj -c Release -nologo -v q \
  -p:TurboCloudPath="$TURBO_CLOUD" -p:TurboCloudConfiguration="$CONFIGURATION" \
  -p:DesignTime=false -o dist/discord
# Only the plugin's own files: everything else is the host's. The XML documentation is the
# settings' summaries on the admin panel's Settings page.
find dist/discord -type f ! -name 'TurboDiscord.dll' ! -name 'TurboDiscord.pdb' ! -name 'TurboDiscord.xml' ! -name 'manifest.json' -delete
find dist/discord -mindepth 1 -type d -empty -delete
ls dist/discord
