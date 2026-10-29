# Production image for МузЛото. Built on the developer machine (the 1 GB server must not compile .NET).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/MuzLoto.Web/MuzLoto.Web.csproj src/MuzLoto.Web/
RUN dotnet restore src/MuzLoto.Web/MuzLoto.Web.csproj
COPY src/ src/
RUN dotnet publish src/MuzLoto.Web/MuzLoto.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
# /data holds Data Protection keys and uploaded covers; the volume inherits this ownership.
RUN mkdir -p /data/keys /data/images && chown -R app:app /data
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    DataProtection__KeysPath=/data/keys \
    Storage__ImagesPath=/data/images
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "MuzLoto.Web.dll"]
