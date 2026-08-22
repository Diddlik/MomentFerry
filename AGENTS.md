# Shared Agent Instructions

This file is the canonical instruction source for Codex, Claude Code, and GitHub Copilot CLI.

## Maintenance

- Keep this file synchronized with the repository's verified behavior.
- Update this file in the same change when build commands, validation steps, architecture constraints, workflows, or conventions change.
- Remove obsolete instructions instead of appending corrections.
- Record only durable facts that are not obvious from the codebase.
- Tool-specific instruction files may contain only an import of this file and genuine tool-specific exceptions.

## First-use onboarding

If any `[TO FILL]` entry remains, complete this onboarding before implementing the user's first task:

1. Inspect the repository and determine every project fact that can be verified from existing files and commands.
2. Do not ask the user for information that can be discovered reliably from the repository.
3. Briefly present the discovered facts, then ask guided questions only for the remaining decisions or unknowns.
4. Ask one focused question or one closely related group of no more than three questions at a time. Explain why each answer matters and offer a recommended option when useful while allowing a free-form answer.
5. Cover the remaining topics in this order: purpose and scope, runtimes and platforms, build and validation commands, environment requirements, then architecture and generated-file constraints.
6. After the user answers, replace the applicable placeholders with concise verified facts, remove entries that do not apply, and record unresolved decisions explicitly as `OPEN` rather than inventing an answer.
7. Summarize what was written to this file, then continue with the user's original task.

## Implementation

- Implement only what the current requirement needs.
- Prefer editing existing code over adding files, layers, helpers, or abstractions.
- Use the standard library and existing dependencies before writing custom implementations.
- Do not add speculative extension points, configuration, parameters, or abstractions.
- Add a dependency only when it provides a concrete benefit and does not duplicate existing functionality.
- Preserve validation, security, accessibility, error handling, and data-integrity safeguards.

## Language and comments

- Use English for source code, identifiers, comments, tests, documentation, logs, and commit messages.
- Comments explain why a decision, constraint, workaround, or non-obvious trade-off exists.
- Do not comment what readable code already expresses.
- Prefer clear naming and small functions over explanatory comments.

## Working method

- Inspect the relevant implementation and existing conventions before editing.
- Search for an existing implementation before creating a new one.
- Make the smallest coherent change that fully satisfies the request.
- Preserve unrelated user changes.
- Do not perform unrelated refactoring during a focused change.
- Ask before destructive, irreversible, security-sensitive, or materially out-of-scope actions.

## Verification

- Run the narrowest relevant test, build, lint, format, or executable check after the last change.
- Add or update tests for non-trivial behavior changes and bug fixes.
- Do not claim success without current verification evidence.
- Report what was verified and what could not be verified.
- Distinguish product failures from environment, permission, network, and tooling failures.

## Security

- Never commit, print, store, or document secrets, tokens, credentials, or private keys.
- Validate data at user, file, environment, process, and network boundaries.
- Preserve authentication, authorization, escaping, permission checks, and safe defaults.
- Do not weaken security controls to make tests or local execution pass.

## Project facts

- Purpose: Self-hosted service that discovers synchronized photos and videos, matches capture timestamps to events, and safely routes media from source shares to shared destinations.
- Primary languages and runtimes: C# on .NET 10 / ASP.NET Core; browser UI in plain JavaScript, HTML, and CSS; SQLite persistence.
- Important entry points: `src/MomentFerry.Web/Program.cs` composes the API and workers; `src/MomentFerry.Web/wwwroot/app.js` drives the Web UI; `src/MomentFerry.Web/Background/MediaRoutingWorker.cs` runs automated routing.
- Build command: `dotnet build MomentFerry.sln -c Release --no-restore -m:1` after `dotnet restore MomentFerry.sln`.
- Test command: `dotnet test MomentFerry.sln -c Release --no-build -m:1` after a successful Release build.
- Single test command: `dotnet test tests/MomentFerry.Tests/MomentFerry.Tests.csproj -c Release --no-build -m:1 --filter FullyQualifiedName~SafeTransferServiceTests`.
- Lint and format command: `dotnet format MomentFerry.sln --verify-no-changes --no-restore`.
- Local run command: `dotnet run --project src/MomentFerry.Web/MomentFerry.Web.csproj -c Release --no-build --urls http://127.0.0.1:5080`.
- Required environment: .NET 10 SDK for development; ExifTool for local metadata extraction; Docker with Compose for the supported container deployment. Mounted source and destination paths must be readable or writable according to their configured roles.
- Module map: `MomentFerry.Core/Domain` holds entities and enums only; `MomentFerry.Application/Services` holds the routing and transfer use cases (`SafeTransferService`, `TransferCoordinator`, `RoutingPreviewService`, `OperationRecoveryService`); `MomentFerry.Infrastructure` holds SQLite repositories, ExifTool metadata, and the filesystem gateway; `MomentFerry.Web/Api` holds minimal-API endpoint groups and `MomentFerry.Web/Background` the workers.
- Architecture constraints: Dependencies flow from Core to Application to Infrastructure to Web. Core must not depend on ASP.NET, MQTT, ExifTool, or persistence infrastructure. Filesystem paths are the sync-tool-agnostic integration boundary. Safe Move may delete a source only after the destination is committed and its size and SHA-256 match. Bounded routing cycles use persisted `media_files.last_seen_at` values to prioritize unindexed, then least-recently evaluated files; creating, editing, or deleting an event requeues already-indexed media inside the affected capture window by resetting `last_seen_at`, so a late-defined event is applied on the next cycle instead of waiting for the sweep; Synology `@eaDir` metadata is excluded from discovery. The update UI treats the install-request connection drop as an expected restart, waits for the requested running version and reloads itself.
- Generated files: `bin/`, `obj/`, local SQLite databases, `data/runtime-settings.json`, and `data/automation-status.json` are build or runtime outputs.
- Files or directories not to edit manually: Do not edit `bin/`, `obj/`, SQLite database files, or persisted runtime settings by hand. Add schema changes through the versioned migration code documented in `docs/DATABASE-MIGRATIONS.md`; the current schema version is 3.
- Platform-specific constraints: The production Docker image is Linux-based and includes ExifTool. Development is supported on Windows; keep path handling platform-neutral and validate mounted container paths. Use serial .NET build/test commands on Windows to avoid intermittent file-lock contention.
- UI task behavior: Long-running browser actions use the global background-task tracker so in-app navigation does not hide progress or allow duplicate starts. Tasks do not persist across a manual browser reload or a closed tab.
- Share media rules: a source share stores its own image and video extension lists; an empty list falls back to `MediaExtensionDefaults`, so clearing the field cannot silently stop discovery. Excluding a media type entirely is done with the share's allowed media types, not by emptying its extensions. A destination share may set `ImageSubfolder`/`VideoSubfolder`, appended below the event folder by `DestinationPathResolver`; unset keeps photos and videos together. Subfolder segments are sanitized and validated as relative paths.
- Metadata processing: Routing reuses indexed capture metadata only when source size and last-write time still match. New or changed files use bounded ExifTool parallelism from runtime settings (default 2, range 1-8); SQLite matching and transfers remain controlled. Full-file SHA-256 reads remain mandatory for Safe Move verification.
- Wake types: `AutomationWakeSignal` distinguishes a targeted wake from a full reconcile. The filesystem watcher carries the changed path, so the routing worker evaluates just that file through `ShareDiscoveryService.Observe` and `RoutingPreviewService.EvaluateAsync` without walking the share. A full walk (`PreviewAsync`) runs only on the periodic schedule, on manual runs, on watcher errors, and when pending paths for one share exceed 1000 — targeted evaluation does not apply `MaxFilesPerSharePerCycle`, so overflow deliberately falls back to the bounded walk. The worker tracks the last full reconcile independently of its wait loop, stamping it after the cycle so the interval is a rest gap and steady watcher traffic cannot postpone the sweep.
- Event backfill: `POST /api/v1/events/{id}/backfill` queues a full pass over that event's source shares and routes every stable file whose capture time falls in the event window, reading metadata for files that are not indexed yet. It runs on the same single-reader worker, is not capped by `MaxFilesPerSharePerCycle`, and filters matches to the requested event so it cannot move media belonging to another event. Like the manual scan it requires automation enabled and an idle cycle.
- Manual automation: `POST /api/v1/automation/run` only queues the existing single-reader worker when automation is enabled and idle, and requests a full reconcile. The Running event card displays the periodic countdown and disables duplicate manual triggers.

Do not begin implementation while `[TO FILL]` entries remain. Follow the guided onboarding above instead.

## Definition of done

A change is complete when:

- The requested behavior is implemented.
- Relevant verification passes after the final edit.
- Appropriate error paths and edge cases are handled.
- Documentation and this file reflect changed behavior or workflows.
- No unrelated files, abstractions, or dependencies were introduced.
- Remaining limitations and unverified points are stated clearly.
