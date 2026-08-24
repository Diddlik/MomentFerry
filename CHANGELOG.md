# Changelog

All notable changes to MomentFerry are documented here.

The project follows semantic versioning for tagged releases. Until v1.0, breaking changes may occur in minor releases when documented explicitly.

## Unreleased

## [1.9.1] - 2026-08-24

### Fixed

- updating from inside the app could end with "MomentFerry did not return with version x.y.z within three minutes" even though the container had updated correctly. Publishing a release ran two container builds, one for the branch and one for the tag, and both claimed the `latest` tag; whichever finished last won it. When the branch build won, `latest` carried a `0.0.0-dev.<run>` version stamp, so the running container never reported the released version and the updater kept offering the same update forever. `latest` now comes from release tags only, and `main` publishes `edge` instead.

## [1.9.0] - 2026-08-24

### Added

- **Route again** for a whole event, next to *Sort existing media* on the event card. Sorting existing media into an event could not touch files the event had already finished: the backfill lifts the per-cycle file limit, but every file still passes the finished check, so a share full of already-routed media produced a run that matched hundreds of files and moved none. Route again clears that mark for the event in one step and then runs the normal backfill under the current naming rules. Items waiting in **Needs your decision** are left alone, so this cannot bury a decision you still owe, and copies already at the destination stay where they are.

### Changed

- the activity log no longer drowns in one line per already-routed file. A sorted share produced hundreds of identical entries every cycle, which flushed everything else out of the log within minutes; they are now summarised as a single count per share and cycle. Every other reason a file was skipped is still reported individually.

## [1.8.0] - 2026-08-24

### Added

- **Route again** on a finished operation in the Operations view. A file that already completed is never routed a second time — MomentFerry identifies it by share and path and remembers that the pair was done — so changing a naming template or a destination layout could not be applied to media that had already moved, short of inventing a new event or renaming the source. Route again supersedes the earlier operation and runs a normal transfer under the current rules. The copy the earlier run wrote is left where it is, so nothing is removed behind your back.

### Fixed

- MomentFerry could delete a file it had produced itself. When a routed file finds its way back onto a source share — a sync task mirroring the destination folder, a shared album, a phone subscribing to the destination — it was indexed as new media, matched the same event, found an identical file at the destination and, under the Safe Move to existing duplicate policy, deleted the source without a word. Where source and destination are mirrored, that deletion travels back onto the destination copy. Such a file is now held in **Needs your decision**, naming the destination its content was already routed to, and the source is kept. A genuine duplicate contributed by a second device is held the same way: the content hash cannot tell the two cases apart, and holding a file can be undone while deleting it cannot.

## [1.7.2] - 2026-08-24

### Fixed

- copying a file was up to 160 times slower than the storage allows. The destination was opened with write-through, which on Linux makes every 128 KB block wait for the disk to confirm it; the same 100 MiB measured 1.3 MB/s that way against 208 MB/s when flushed once at the end. MomentFerry now writes normally and forces the file to disk once when it is complete, before it reads the copy back to verify it, so nothing about the safety of a transfer changes. On the installation this was found on, routing 3.1 GiB took 1 hour 42 minutes.

## [1.7.1] - 2026-08-24

### Fixed

- a routed file carried the time it was copied as its modification date, which reordered every gallery that sorts by file date rather than by embedded metadata. The destination is now stamped with the capture time once it is verified. Linux offers no way to set a file's creation date, so the container stamps the modification date only; a filesystem that refuses the stamp no longer costs you the verified copy. Files routed by earlier versions keep their copy time and can be corrected with `exiftool "-FileModifyDate<DateTimeOriginal" <folder>`.

## [1.7.0] - 2026-08-24

### Added

- an Activity log on the Operations view shows why MomentFerry did what it did, filtered by everything, warnings or errors only. It mirrors the application's own log records into memory and serves them over `GET /api/v1/logs`, so a stuck file can be diagnosed in the browser instead of in the container log. The ring holds 500 entries by default and is cleared by a restart; the durable record remains the operation history and the audit CSV;
- every automation cycle now reports its trigger, duration and tally: `full reconcile` versus the number of paths the filesystem watcher reported. If a share only ever produces full reconciles, its changes are not reaching the watcher, which is what makes routing appear slow.

### Fixed

- an operation left in `RetryPending` was invisible and self-blocking. Interrupting a transfer before the destination is committed puts it there by design, but the state kept its media file unroutable on every following cycle while the "Needs your decision" card listed only quarantined items, so the file silently never moved again. Both states are now listed together and offer Retry; Dismiss stays limited to quarantined items;
- the routing worker no longer swallows the reason a matched file was not routed, and startup recovery names every operation it leaves in a non-terminal state instead of only counting them.

## [1.6.1] - 2026-08-23

### Fixed

- the language picker in the sidebar drew a second green box inside its row when focused and pushed its option list to the right edge. The row itself now carries the focus ring and the list reads left-aligned like every other menu.

## [1.6.0] - 2026-08-23

### Added

- the Web UI speaks German, Russian, Polish, Italian, French and Ukrainian besides English. The language is picked from the browser on first use and can be changed at the bottom of the sidebar; the choice is stored per browser, so nothing about the installation itself changes;
- adding a further language means dropping one file into `wwwroot/i18n/` and naming it in the language list. Translation keys are the English source strings, so an untranslated entry falls back to English instead of showing a broken label, and a test fails the build when a language file drifts from the reference catalog.

## [1.5.1] - 2026-08-23

### Fixed

- the File naming preview stayed empty until a template was typed. It now renders as soon as the view opens, and samples real filenames straight from the source shares when nothing has been indexed yet, so a fresh installation still shows how its own files would be named;
- the share form's sync-tool dropdown stopped being filled in 1.5.0, because the new preset list defined a second `renderPresets` function that shadowed the existing one;
- preview samples are numbered sequentially instead of every row showing `0001`, matching what a real run produces.

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
