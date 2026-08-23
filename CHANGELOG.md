# Changelog

All notable changes to MomentFerry are documented here.

The project follows semantic versioning for tagged releases. Until v1.0, breaking changes may occur in minor releases when documented explicitly.

## Unreleased

## [1.5.0] - 2026-08-23

### Added

- files can be renamed on their way to the destination using templates such as `{captured:yyyyMMdd_HHmmss}_{camera}_{seq:0000}`. Presets are defined once under **File naming** and attached to individual sources and destinations: the source preset normalizes the incoming name and the destination preset then shapes the stored name, so the two chain instead of competing;
- a camera name mapping table rewrites the model a device reports into the name you want, for example CPH2581 to OnePlus12, which the `{camera}` token then uses;
- the File naming view previews templates live against real indexed media, so a template can be checked before it is attached to a share and starts naming files.

### Changed

- database schema version 4 adds the rename preset and camera mapping tables, a rename preset reference on shares, and the camera make and model on indexed media. Existing shares keep their filenames unchanged until a preset is attached. Back up `/app/data` before updating, because version 1.3.x and older refuse to open a version 4 database.

## [1.4.0] - 2026-08-23

### Changed

- the overview showed only one event when several were collecting at once. It now lists every active event as a compact row with its window, source group, destination and its own start/stop button, showing the first five and linking to the rest. A single active event keeps the previous detailed layout unchanged. Cycle counters stay below the list because they are automation-wide rather than per event.

## [1.3.1] - 2026-08-23

### Added

- the Updates view links the running version to its release page on GitHub, and to the latest release notes once an update check has run. The repository is derived from the configured release API URL rather than hardcoded in the browser.

## [1.3.0] - 2026-08-23

### Added

- source shares define which file extensions count as photos and videos, pre-filled with the built-in defaults so a device that only produces a subset can be narrowed without affecting other shares;
- destination shares can route photos and videos into their own subfolders below the event folder; leaving both empty keeps everything together as before;
- events can be applied retroactively: **Sort existing media** on an event runs a full pass over its source shares and routes everything captured inside the event window, including files that were never indexed. Use it to sort a past period, for example last month, into an event defined after the media arrived.

### Changed

- database schema version 3 adds four nullable `shares` columns for the per-share extension lists and destination subfolders. Existing shares keep their current behavior until edited. Back up `/app/data` before updating, because version 1.2.0 and older refuse to open a version 3 database.

## [1.2.0] - 2026-08-22

### Added

- creating, editing or deleting an event now re-matches already-indexed media inside the affected capture window instead of waiting for the least-recently-evaluated sweep to reach it, so an event defined after the photos arrived is applied on the next automation cycle.

### Changed

- filesystem-watcher notifications now carry the changed path, so a routing cycle triggered by new media evaluates only that file instead of walking and stat-ing the entire source share. Full share walks now run only on the periodic schedule, on manual scans, on watcher errors, and when more than 1000 paths are pending for one share;
- the periodic reconciliation interval default increases from 300 to 1800 seconds, because the watcher now covers new-file latency and the periodic walk only has to act as a correctness backstop. Existing installations keep their saved value in `data/runtime-settings.json`; change it in the Web UI to adopt the new default;
- the reconciliation interval is now a rest gap measured after a cycle finishes rather than a fixed period, so a share that takes longer than the interval to walk no longer runs walks back to back.

## [1.1.0] - 2026-08-22

### Changed

- the project is rebranded from MediaFlow to MomentFerry: solution/project/namespace names, `MediaFlow__*` configuration keys become `MomentFerry__*`, the container image moves to `ghcr.io/diddlik/momentferry`, and the default SQLite database filename becomes `momentferry.db`. Existing deployments must update their compose environment variables and rename the database file; see [RELEASING.md](docs/RELEASING.md) and [BACKUP-RESTORE.md](docs/BACKUP-RESTORE.md).

## [1.0.5] - 2026-08-22

### Fixed

- manual scans now use fast lifecycle polling, keep progress visible across in-app navigation and show a persistent completion summary even when the cycle finishes between regular status polls.

## [1.0.4] - 2026-08-22

### Added

- the running-event card shows a live countdown to the next scheduled automation cycle and provides a guarded **Scan now** action.

## [1.0.3] - 2026-08-22

### Added

- the running-event card now shows the active source, phase, processed count and percentage while an automation cycle is running;
- metadata extraction parallelism is configurable from 1 to 8, with a NAS-friendly default of 2.

### Changed

- unchanged indexed media reuse their capture metadata based on source size and last-write time instead of invoking ExifTool again;
- database schema version 2 adds `media_files.source_last_write_at_utc`; back up `/app/data` before updating because older MomentFerry versions refuse this newer schema.

### Fixed

- completed automation counters survive container restarts;
- Dry Run now reports the number of files that would move instead of displaying the always-zero executed count.

## [1.0.2] - 2026-08-22

### Added

- long-running path checks, scans, metadata reads, routing previews, transfers, retries and image updates remain active during in-app navigation and report their result in a global background-task panel;

### Fixed

- the update UI now treats the updater-triggered connection drop as an expected restart, waits for the requested version and reloads automatically;
- persisted update status now reports the version of the running application immediately after restart instead of briefly showing the previous version.

## [1.0.1] - 2026-08-22

### Fixed

- bounded routing cycles now persistently rotate through large source shares instead of repeatedly evaluating the same first batch, preventing newer event media from starving;
- Synology `@eaDir` metadata thumbnails are excluded from media discovery regardless of the selected sync-tool preset.

## [1.0.0] - 2026-08-22

### Added

- .NET 10 layered application structure.
- SQLite persistence for Shares, Source Groups, Events, media index and operation state.
- transactional versioned SQLite schema migrations with `schema_migrations` history.
- legacy database baselining that preserves existing data.
- downgrade guard that refuses startup when the database schema is newer than the application supports.
- sync-tool-agnostic filesystem Share model and presets.
- ExifTool image/video metadata extraction with timezone-aware capture timestamps.
- capture-time Event matching including late synchronization after an Event is closed.
- routing preview and persistent media indexing.
- Safe Move (`copy → verify → commit → delete`) and Copy operation modes.
- SHA-256 duplicate verification and configurable filename conflict handling.
- persistent operation state machine, restart recovery and explicit retry.
- Dry Run default and explicit Live-mode confirmation.
- Web UI for Shares, Source Groups, Events, routing preview, operations and runtime safety settings.
- periodic reconciliation worker plus FileSystemWatcher wake-ups.
- per-media transfer serialization to avoid concurrent duplicate execution.
- automated Safe Move tests covering verification, persistence failures and source-delete failures.
- destination free-space detection and 512 MiB reserve before real staging copies.
- `/api/v1/storage` destination capacity status.
- optional MQTT event control and Home Assistant REST/MQTT examples.
- Docker image with ExifTool and healthcheck.
- CI Release build, automated tests and Docker build validation.
- GHCR publishing with `latest`, commit SHA and SemVer tags.
- backup/restore, security, contributing, database migration and release documentation.
- Dependabot configuration for NuGet, Docker and GitHub Actions.
- quarantine review UI with audit-preserving manual dismissal.
- OpenAPI 3.1 document and Swagger UI at `/docs/`.
- configurable destination free-space reserve in runtime settings.
- CSV operation audit export and Prometheus-compatible `/metrics` endpoint.
- guided first-run onboarding.
- stable image update checks, changelog display, opt-in automatic updates and confirmed manual update triggering through an isolated Watchtower companion.
- release-tag version injection and persisted healthy-restart/update-failure reporting.
- deterministic Compose image pinning and an operator-triggered rollback runbook without adding another Docker-socket service.
- mounted-folder browser for Share paths, backed by `GET /api/v1/folders`.
- sidebar Web UI with one view per task, an overview of the running event, destination headroom and held files, a light theme and a phone layout.
- first-run setup wizard and a typed confirmation dialog for leaving Dry Run.

### Changed

- every SQLite connection now enables foreign-key enforcement and a 5-second busy timeout.

### Fixed

- routing preview now evaluates up to 2,000 files instead of stopping after the first 50 displayed by the console.
- share scan now counts every media file in the share instead of stopping at the sampled page, so large shares no longer report a flat `500 media files`.
- the console now shows the resolved destination folder (`/destinations/family/Sommerurlaub`) instead of the raw `{event.name}` template.
- the selected row in the mounted-folder browser no longer clips its folder name and path.
- the reported running version now keeps its prerelease suffix (`0.0.0-dev.42` instead of `0.0.0`), read from `InformationalVersion`.

### Security

- source deletion is guarded by persisted destination commit plus size/SHA-256 verification.
- ambiguous/recoverable states preserve the source.
- destination path resolution is restricted to the configured destination Share.
- Live mode is opt-in and requires explicit confirmation.
- older application builds refuse to mutate database schemas created by newer MomentFerry versions.
