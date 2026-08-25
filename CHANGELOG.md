# Changelog

All notable changes to MomentFerry are documented here.

The project follows semantic versioning for tagged releases. Until v1.0, breaking changes may occur in minor releases when documented explicitly.

## Unreleased

## [1.11.6] - 2026-08-25

### Added

- **Rename stored files**, per event. Naming happens on the way to the destination, so a rename preset or a camera mapping added afterwards never reached the media that was already stored — and *Route again* cannot repair those names either, because it needs a source to re-route and Safe Move released the sources once their copies were verified. This renames the stored files where they lie and moves the operation history with them, so each record keeps pointing at the copy it verified. Two things must hold before a file moves: the new name must be free, and the file must still hash to the checksum its operation recorded. The checksum is the point — an operation is the record that this content was verified, and carrying that record to a new name without re-proving the bytes would hand a file that was replaced or damaged in the meantime a verification it never earned. Files whose names already match are never read. Under Dry Run the button reports the plan, mismatches included, and touches nothing. Found while a camera reporting its marketing name (`OnePlus 12`) rather than its model code left 404 files stored under a name the mapping added later would have fixed.

## [1.11.5] - 2026-08-25

### Fixed

- a file whose destination copy no longer existed was never routed again. A finished operation blocked any further attempt on that file and event on the strength of the database row alone, so once the file it recorded was gone from the destination, the source sat in the share, matched its event on every cycle, and was counted as *already routed* with nothing at the other end — a state no *Needs your decision* card and no counter showed, only the Activity log. On the installation this was found on, 437 files were held that way and the Overview reported every file routed cleanly. A finished operation now blocks a re-route only while the file it committed is still at the destination, which is the same rule the duplicate check already followed: the record says where to look, the file on disk decides. Only existence is checked, never content, because re-hashing every routed file on every cycle would read the whole library — and a transfer still verifies bytes before it removes any source. *Route again* on the event remains the way to re-route media whose destination is present.

## [1.11.4] - 2026-08-25

### Fixed

- turning automatic updates on did nothing until the next six-hourly check came due, which is up to six hours of a toggle that looks dead. Saving the setting now wakes the update worker, so the check runs immediately — and only on the transition from off to on, so re-saving other settings does not hammer the release service.

## [1.11.3] - 2026-08-25

### Changed

- installing an update no longer asks you to type `INSTALL_UPDATE` first. The button only appears when a newer release exists, it names the version it installs, and a restart onto a verified image destroys nothing, so the dialog only stood between you and the thing you clicked. The API still requires the explicit token, so a stray request cannot restart the container.

## [1.11.2] - 2026-08-25

### Fixed

- automatic image updates never ran on a container that restarts regularly. The check sat on a bare six-hour timer whose first tick came six hours after start, so a restart reset it every time and turning the toggle on did nothing visible until that timer happened to fire. It now checks shortly after start and every six hours after that, and every pass says in the Activity log what it found: nothing to install, an available version with no updater companion configured, the install it requested, or the error — a release that silently never arrives was indistinguishable from a broken toggle.

## [1.11.1] - 2026-08-25

### Fixed

- routed media put back into a source share was stored a second time, under a different name, instead of being recognised as content the destination already holds. The check for it only ran when the returned file rendered to exactly the name it was stored under, and it ignored operations that a *Route again* had marked as superseded — the two conditions that hold precisely when an album is copied back to retest it. On the installation this was found on, one retest wrote 213 duplicate files into the destination and left 224 files in the source, each with a finished operation that made every later cycle skip them. The content is now looked up before the destination name is resolved and whatever became of the earlier operation, and the copy it points at is stat'ed and re-hashed on disk rather than trusted from the record: a source whose content is verifiably already stored follows the event's duplicate policy — *Safe move to existing* removes it — and a source whose stored copy is gone or changed is routed again.

## [1.11.0] - 2026-08-24

### Added

- a **Maintenance** view. *Read metadata again* clears the mark that makes routing reuse what it already knows about a file, so a corrected extractor reaches media that is already indexed — without it, fixes to capture time or camera only ever apply to new arrivals. *Forget missing files* drops index entries whose source is gone, keeping any entry an operation refers to so the record that a file was verified survives. *Compact database* hands reclaimed pages back to the disk. And the operation history can now expire: set a retention window in days and each full reconcile removes finished operations older than that, or leave it at zero to keep everything. Anything still waiting for a decision is never removed, however old it is.

## [1.10.0] - 2026-08-24

### Fixed

- every video's capture time was wrong by the container's UTC offset — one hour in winter, two in summer. QuickTime writes `MediaCreateDate` in UTC and without an offset, and the parser's `K` format specifier matches an empty offset and then silently attaches the machine's own time zone. Verified on two real files: exiftool reported `2026:04:21 12:00:28` and MomentFerry stored `10:00:28Z`. Worse, it recorded the result as certain rather than inferred, so nothing flagged the guess. These fields are now read as UTC, `CreationDate` is preferred when a recording carries its own offset, and Samsung's recorded offset re-expresses the same instant in the zone it was filmed in. Photos were never affected, and their timestamps still fall back to the share's zone and still say so.
- a camera mapping did not apply to videos that pad the model out. A OnePlus recording reports `OnePlus  CPH2581 23mm` — maker, model code and lens in one string — so a table keyed on `CPH2581` never matched and the filename carried the whole raw string. A mapping key is now also matched inside the reported model, and the focal length no longer needs an entry of its own. The longest key wins when several match.
- the `{camera}` token stayed empty for phone videos. A Galaxy S25 recording carries no Make or Model at all: the model code sits in a Samsung maker note and the device name "Galaxy S25" in the author field, and other Android phones use the `com.android` keys instead. On the installation this was found on, 483 of 497 indexed videos had no camera recorded while photos from the same phone were named correctly. All of these are now read, and a video is named like a photo from the same device. The author field is only trusted on a recording that identifies itself as Samsung, because elsewhere it is free text that could name a person.

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
