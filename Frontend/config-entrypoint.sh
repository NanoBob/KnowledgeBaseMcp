#!/bin/sh
set -e

# The API is hosted separately, so the operator points this UI at it with API_BASE at startup
# instead of rebuilding the image.
escaped=$(printf '%s' "${API_BASE:-}" | sed 's/\\/\\\\/g; s/"/\\"/g')
printf 'window.__APP_CONFIG__ = { apiBase: "%s" };\n' "$escaped" > /tmp/app-config.js
