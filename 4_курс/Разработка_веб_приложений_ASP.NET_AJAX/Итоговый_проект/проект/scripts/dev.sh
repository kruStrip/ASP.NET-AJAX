#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
dotnet run --project src/MuzLoto.Web/MuzLoto.Web.csproj --urls http://localhost:5080
