# PhotoBooth business terminology

These names are canonical in code, UI, reporting and future server contracts.

| Term | Vietnamese UI | Meaning |
|---|---|---|
| `PhotoEvent` / Event | Sự kiện | Operator-managed grouping and default preset context. One Event contains many customer turns. |
| `BoothSession` | Lượt khách | One customer's complete journey from start through capture, selection, print/share and completion. |
| `CaptureAttempt` | Lần thử chụp | One request sent to the camera. It may end as accepted, failed or unknown. |
| `CapturedShot` | Ảnh chụp | One accepted shutter result, optionally containing a JPG/MP4 pair. |
| `Deliverable` | Bộ thành phẩm | The finalized set offered to the customer and referenced by print/share/delivery. |
| `MediaAsset` | Tệp media | One immutable physical JPG, PNG, MP4, GIF or archive identified by GUID. |
| `OutputJob` | Tác vụ đầu ra | A durable print, upload or delivery side effect with an idempotency key. |

## Terms that must not be overloaded

- Event is not a Session. Selecting an Event never reuses one customer turn for another customer.
- Capture means the camera/shutter operation. It does not mean the complete customer gallery or final output package.
- A filename is not an asset identity, customer identity or business sequence.
- A folder is not the source of truth for a BoothSession. SQLite owns lifecycle and relationships.
- An Event name is a display/search label and may be duplicated. `PhotoEvent.Id` is the unique identity used by relationships and reporting.

## Compatibility storage

Existing installations retain the physical SQLite names `CustomerSessions`, `Captures`, `CapturePhotos` and `CaptureAssetSources`. Renaming those tables in place would add migration and rollback risk without changing business behavior.

Migration 10 exposes canonical read models:

- `Events`
- `BoothSessions`
- `Deliverables`
- `DeliverableAssets`
- `DeliverableAssetSources`

Migration 11 restricts `Deliverables` and their assets to records owned by a real
`BoothSession`. Ambiguous legacy Event-linked output remains preserved in the
compatibility tables but is not counted as commercial customer activity.

New application contracts and UI use canonical terms. The old names are restricted to compatibility repositories and legacy public contracts until a later, separately tested storage migration removes them.

## Ownership

```text
PhotoEvent
└── BoothSession
    ├── CaptureAttempt -> CapturedShot -> MediaAsset(s)
    └── Deliverable -> MediaAsset(s)
        └── OutputJob(s)
```
