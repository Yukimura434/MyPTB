# Local business data model

This document defines the local source of truth for a commercial PhotoBooth installation. It is intentionally independent from the future server schema: the booth must finish a customer turn when the Internet is unavailable.

The canonical vocabulary is defined in `Docs/BUSINESS_TERMINOLOGY.md` and must be used by UI labels, application contracts, reporting and future server APIs.

## Business identities

The application uses separate identities for separate concepts:

- An **Event** row is an operator-managed configuration and reporting scope. It may be selected as the default event, but it is never reused as a customer turn.
- A **Booth session** is one customer turn. It has its own GUID, state, timestamps, event link and storage root.
- A **Capture attempt** records intent before the camera shutter is requested. It distinguishes a definite failure from an outcome that became unknown because the process stopped.
- A **Media asset** is one immutable physical file. Its GUID is the filename; its kind, relationship, display label, hash and retention policy are database metadata.
- An **Output job** records an external side effect such as printing, upload or delivery. Its idempotency key prevents an accidental duplicate after a retry.

Display codes and original camera names are metadata only. They must never be used as primary keys or relied on to find files.

## Session lifecycle

```text
Active -> Finalizing -> Completed
   |           |
   +-----------+-> Abandoned / Failed
```

Terminal sessions have `CompletedAtUtc`. On startup, any booth session still in `Active` or `Finalizing` is marked `Failed`; an operator can then inspect it without the application pretending that the turn completed normally.

The selected event remains active across many customers. Starting Customer mode creates a new booth session linked by `EventId`.

## Capture commit protocol

For every shutter operation:

1. Generate the capture-attempt ID and all expected media-asset IDs.
2. Persist `CaptureAttempts.Status = IntentRecorded` before calling the camera.
3. Transfer to a `.partial` file in `Work`.
4. Finish image/video processing under GUID filenames.
5. Persist media metadata and the accepted shot.
6. Mark the attempt `Accepted`.

If a normal error occurs, the attempt becomes `Failed`. If the camera call may have fired but the application cannot prove the result, it becomes `Unknown`. Partial files are disposable and never become business records.

## Storage contract

Managed booth sessions use this layout:

```text
Captures/yyyy/MM/dd/<booth-session-guid>/
├── Work/       # partial files and composition previews
├── Originals/  # accepted JPG/MP4 assets
└── Final/      # final PNG/MP4/GIF deliverables
```

All persisted paths in `MediaAssets` are relative to the managed data root. `IStorageManager` rejects absolute paths and path traversal when resolving them.

`Work` may be removed as soon as a turn finishes. A BoothSession root may be deleted only when all of the following are true:

- the BoothSession is terminal;
- its terminal timestamp is older than the configured retention window;
- the directory exactly matches the path registered for that BoothSession in SQLite;
- no print/upload/delivery job is pending or has an ambiguous outcome.

Legacy event folders and arbitrary operator folders are never recursively deleted by managed BoothSession cleanup.

## SQLite durability

SQLite uses foreign keys, WAL journal mode, `synchronous=NORMAL` and a 5-second busy timeout. Schema migrations are versioned and idempotent.

`CustomerSessions`, `Captures` and `CapturePhotos` remain compatibility storage names. Canonical read models `Events`, `BoothSessions`, `Deliverables` and `DeliverableAssets` expose the business meaning without a destructive table rename. Commercial reporting counts Booth sessions and Deliverables explicitly, not event configuration rows or arbitrary files.

The local database contains the future synchronization boundary in `SyncOutbox`. Server synchronization must consume immutable outbox events and use stable local IDs/idempotency keys; it must not scan folders or derive identity from filenames.

## Operational rules

- Back up SQLite and the managed media root together.
- Never rename accepted media to make it human-readable. Export or download names may be generated at presentation time.
- Do not retry a print job in `UnknownOutcome` automatically. An operator must confirm whether paper was produced.
- Do not delete media referenced by a pending or ambiguous output job.
- Reconciliation and retention are correctness workflows, not UI cleanup helpers.
