#!/bin/sh
set -eu
# Railway provides PORT; local Docker defaults to 8000.
export ASPNETCORE_URLS="http://0.0.0.0:${PORT:-8000}"
exec dotnet /app/CarWise.Api.dll "$@"
