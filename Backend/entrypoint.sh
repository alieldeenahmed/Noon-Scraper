#!/bin/bash
set -e

# The platform hosting this (Render, Cloud Run, etc.) injects PORT at runtime;
# the app has to listen on whatever it's given, not a value baked in at build time.
export ASPNETCORE_URLS="http://+:${PORT:-8080}"

exec dotnet /app/NoonScraper.Api.dll
