# Database migrations

MomentFerry uses versioned, forward-only SQLite migrations.

## Schema history

The database contains:

```sql
schema_migrations (
    version INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    applied_at_utc TEXT NOT NULL
)
```

`SqliteDatabaseInitializer.CurrentSchemaVersion` is the highest schema version supported by the running application.

On startup MomentFerry:

1. opens the database;
2. enables WAL mode;
3. ensures `schema_migrations` exists;
4. reads applied versions;
5. refuses startup if the database contains a version newer than the application supports;
6. applies missing migrations in ascending order;
7. executes each migration and its history record in one SQLite transaction.

This makes migration application idempotent and prevents a partially applied migration from being recorded as successful.

## Existing installations before schema history

The first migration is the baseline schema and uses idempotent `CREATE TABLE/INDEX IF NOT EXISTS` statements.

For an existing MomentFerry database created before `schema_migrations` existed, startup therefore:

- leaves existing tables and rows intact;
- creates any missing baseline objects;
- records migration version `1` after the baseline transaction succeeds.

Automated tests cover preservation of existing Share data during this baseline process.

## Schema version 2

Version 2 adds nullable `media_files.source_last_write_at_utc`. Existing rows are preserved and have their metadata refreshed once; subsequent cycles reuse indexed capture metadata while file size and last-write time remain unchanged.

Back up `/app/data` before upgrading. Version 1.0.2 and older refuse to open the version 2 database, so rollback requires restoring the pre-upgrade data backup.

## Schema version 3

Version 3 adds four nullable `shares` columns: `image_extensions_json`, `video_extensions_json`, `image_subfolder` and `video_subfolder`. Existing rows keep NULL, which means "use the built-in extension lists" and "no media subfolder", so behavior is unchanged until a share is edited.

Back up `/app/data` before upgrading. Version 1.2.0 and older refuse to open the version 3 database, so rollback requires restoring the pre-upgrade data backup.

## Schema version 4

Version 4 adds the `rename_presets` and `camera_mappings` tables, a nullable `shares.rename_preset_id`, and nullable `media_files.camera_make` / `media_files.camera_model`. Existing rows keep NULL, so filenames are unchanged until a preset is attached to a share.

Camera columns are written by the routing cycle from ExifTool output and merged with `COALESCE`, so a later cycle that reuses indexed metadata never clears a previously discovered camera. Files indexed before this version gain their camera the next time their size or last-write time changes.

Back up `/app/data` before upgrading. Version 1.3.x and older refuse to open the version 4 database, so rollback requires restoring the pre-upgrade data backup.

## Schema version 5

Version 5 adds a nullable `media_files.captured_at_offset_minutes`.

`captured_at_utc` stays the absolute instant: event matching and the capture-window requeue compare it as text, so a column holding mixed offsets would break both. Normalising to UTC discarded the offset the file reported, and a filename rendered from that carried a time the camera never showed — a photo taken at 13:52 with `OffsetTimeOriginal +02:00` was stored as `20260821_115253`. The offset is therefore kept beside the instant and used only when a name or a date folder is rendered.

Existing rows keep NULL. A name rendered for such a row falls back to the source share's time zone, which is the same assumption the extractor already makes for a photo that names no offset. *Rename stored files* reads the offset off the stored copy and records it, so the fallback is only used where the file itself never stated one.

Back up `/app/data` before upgrading. Version 1.11.7 and older refuse to open the version 5 database, so rollback requires restoring the pre-upgrade data backup.

## Adding a migration

Never edit the SQL of an already released migration to change the meaning of its version. Add a new migration instead.

For example:

```csharp
new SqliteMigration(
    2,
    "add-operation-audit-column",
    """
    ALTER TABLE operations ADD COLUMN audit_id TEXT NULL;
    """)
```

Then:

1. append the migration in ascending version order;
2. increment `CurrentSchemaVersion`;
3. add tests for both a fresh database and upgrade from the previous schema;
4. verify existing data is preserved;
5. document any operational impact in `CHANGELOG.md`;
6. back up `/app/data` before deploying the release.

## Downgrades

Automatic down-migrations are intentionally not supported.

If a database reports a schema version newer than the running application understands, MomentFerry refuses to start rather than allowing an older binary to mutate newer persistent state.

To roll back across an incompatible schema change, restore the `/app/data` backup created before the upgrade and deploy the corresponding older container image.

See [Backup and restore](BACKUP-RESTORE.md).

## Connection-level SQLite safety

Every MomentFerry SQLite connection enables:

```sql
PRAGMA foreign_keys=ON;
PRAGMA busy_timeout=5000;
```

Foreign-key enforcement is connection-scoped in SQLite, so setting it only during startup initialization is insufficient. The connection factory applies these settings to every opened connection.

WAL mode is enabled by the database initializer.
