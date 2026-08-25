# Data identity and integrity

This document defines the identity model that management features must use. Paths and display names are attributes, never identifiers.

| Entity | Stable ID | Parent / ownership |
|---|---|---|
| Customer session | `CustomerSessions.Id` (`Guid`) | Account/device when enrolled |
| Physical camera result | `CapturedImages.Id` (`imageId`) | `SessionId` |
| Booth capture turn | `Captures.Id` (`Guid N`) | `SessionId` |
| Picture or Motion Photo | `CapturePhotos.Id` (`assetId`) | `CaptureId`, plus `CapturedImageId` |
| Composite | `CapturePhotos.Id` (`assetId`) | `CaptureId`; source assets in `CaptureAssetSources` |
| GIF | `CapturePhotos.Id` (`assetId`) | `CaptureId`; source assets in `CaptureAssetSources` |
| Share ZIP | `CapturePhotos.Id` (`assetId`) | `CaptureId`; packaged assets in `CaptureAssetSources` |
| Local share request | `LocalShareTicket.Id` | `SessionId`, `CaptureId`, `ArchiveAssetId` |
| Upload work item | `UploadQueue.Id` | `CaptureId`, `PhotoId` (asset ID) |
| Print history | `PrintJobs.Id` | `SessionId`, `CaptureId`, `PrinterProfileId` |
| Frame / frame slot | `Frames.Id` / `FrameSlots.Id` | Slot belongs to frame |
| Preset / LUT | `AdminPresets.Id` / `ColorLutAssets.Id` | `PresetColorSettings` links them |

## Asset rules

- Valid durable capture asset types are `Picture`, `MotionPhoto`, `Composite`, `Gif`, and `ShareArchive`.
- New booth originals are `MotionPhoto`; `Picture` is retained for legacy static captures.
- Every Motion Photo references exactly one physical `CapturedImages.Id` in the same session.
- Derived files store all input asset IDs in `CaptureAssetSources`. Cross-capture lineage is rejected by SQLite.
- Each asset stores MIME type, file length, SHA-256 content hash, UTC creation time, and `Ready`/`Missing` status. A management or repair operation should verify these values before moving, exporting, or deleting a file.
- A capture asset cannot be reassigned to another capture. Upload ownership cannot be changed after queue creation.
- `FinalImageId` and `CompositeImageId` are unique business identifiers; the corresponding file still has its own `CapturePhotos.Id` asset ID.

## Retake and commit rules

Retake creates a new physical image ID and a new Motion Photo file. The new file is packaged and persisted before it replaces the selected review position. The old file is deleted only after replacement succeeds. Capture creation happens after review, so only accepted physical image IDs become capture assets.

## Compatibility migration

Schema migration 4 adds asset metadata and lineage without changing existing primary keys. Legacy `Original` rows ending in `_MP.jpg` become `MotionPhoto`; other legacy originals become `Picture`. Existing creation timestamps are backfilled from their owning capture.
