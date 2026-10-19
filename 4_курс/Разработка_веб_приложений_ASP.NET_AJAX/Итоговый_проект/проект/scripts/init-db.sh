#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "$0")/common.sh"
dotnet tool restore
project="src/MuzLoto.Web/MuzLoto.Web.csproj"
if ! compgen -G "src/MuzLoto.Web/Data/Migrations/*_InitialCreate.cs" > /dev/null; then
    (cd src/MuzLoto.Web && dotnet tool run dotnet-ef migrations add InitialCreate --project . --startup-project . --output-dir Data/Migrations)
fi
(cd src/MuzLoto.Web && dotnet tool run dotnet-ef database update --project . --startup-project .)
echo 'Database migrations applied. Commit generated Data/Migrations files for reproducible setup.'
