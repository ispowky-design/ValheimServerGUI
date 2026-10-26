#!/usr/bin/env bash
# Write a ClientSecrets.Values.cs with empty values, which Release builds require (see
# SolutionResources/README.md). Builds made this way send no Runeberry API key. An existing file is kept.
set -euo pipefail
out="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/SolutionResources/ClientSecrets.Values.cs"
[ -f "$out" ] && { echo "Kept existing $out"; exit 0; }
cat > "$out" <<'CS'
namespace ValheimServerGUI.Properties
{
    public static partial class ClientSecrets
    {
        static ClientSecrets() { }
    }
}
CS
echo "Wrote $out"
