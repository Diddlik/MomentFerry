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

# tzdata is installed explicitly rather than relying on the base image to carry it. Without it .NET
# resolves no named zone, so `TZ` and a share's configured zone would both fall back to UTC — the
# capture time of a photo whose EXIF states no offset depends on that zone. Whether the base image
# already provided tzdata was never verified, and its absence was not the cause of the wrong
# filenames fixed in 1.11.12: that was a QuickTime instant being recorded as a stated offset of zero.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libimage-exiftool-perl curl tzdata \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/health >/dev/null || exit 1

ENTRYPOINT ["dotnet", "MomentFerry.Web.dll"]
