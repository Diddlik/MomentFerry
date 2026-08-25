FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG VERSION=0.0.0-dev

COPY . .
RUN dotnet restore MomentFerry.sln
RUN dotnet publish src/MomentFerry.Web/MomentFerry.Web.csproj \
    --configuration Release \
    --no-restore \
    -p:Version="$VERSION" \
    -p:InformationalVersion="$VERSION" \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# tzdata is not in the aspnet image, and without it .NET cannot resolve a named zone: TZ=Europe/Berlin
# is silently ignored, TimeZoneInfo.Local becomes UTC, and every photo whose EXIF states no offset is
# read as if it had been taken in UTC. A share's configured zone could not be resolved either.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libimage-exiftool-perl curl tzdata \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/health >/dev/null || exit 1

ENTRYPOINT ["dotnet", "MomentFerry.Web.dll"]
