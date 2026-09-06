#!/bin/bash
set -e

# Cloud Run injects PORT at runtime (defaults to 8080); the app has to listen
# on whatever it's given, not a value baked in at build time.
export ASPNETCORE_URLS="http://+:${PORT:-8080}"

# Same reasoning as the GitHub Actions workflow: the target site hard-blocks
# headless Chrome at the network layer, so this needs a real headful browser
# with a virtual display rather than true headless mode.
exec xvfb-run --auto-servernum dotnet /app/NoonScraper.Api.dll
