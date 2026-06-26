#!/usr/bin/env bash
# Run this ON the Linux machine, from the deployed app directory (where AgentScraper.dll lives),
# after copying the linux-x64 publish output across.
set -euo pipefail

# 1) The Playwright node driver loses its execute bit when copied from Windows — restore it.
if [ -f ./.playwright/node/linux-x64/node ]; then
  chmod +x ./.playwright/node/linux-x64/node
else
  echo "WARNING: ./.playwright/node/linux-x64/node not found — publish was not built for linux-x64." >&2
fi

# 2) Download the Chromium browser binaries (the driver is bundled, browsers are not).
#    Add --with-deps (and run with sudo) on a bare server to also install the required OS libraries.
if [ "${1:-}" = "--with-deps" ]; then
  dotnet ./AgentScraper.dll --install-browsers --with-deps
else
  dotnet ./AgentScraper.dll --install-browsers
fi

echo ""
echo "Setup complete. Ensure \"Headless\": true in appsettings.json on this server (no display)."
