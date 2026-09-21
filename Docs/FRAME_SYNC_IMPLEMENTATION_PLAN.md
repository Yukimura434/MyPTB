# Frame Sync Implementation Plan

Status: in progress

## Goal

Implement reliable immutable frame-version synchronization from Customer Web to Windows and Android, including thumbnails, original assets, and ordered slot geometry.

## Contract invariants

- frameId identifies the logical frame.
- frameVersionId identifies one immutable asset and slot-metadata version.
- contentSha256 verifies the PNG; it is not a version identity.
- Slots use original-canvas pixel coordinates and preserve explicit contiguous indexes.
- A configuration activates only after all required frames verify locally.
- Server acknowledgement is retryable and separate from local activation.

## Delivery order

### 1. Worker/API

- [ ] Persist thumbnail metadata/assets.
- [ ] Include frameVersionId, thumbnail fields, and ordered slots in payloads.
- [ ] Validate slot bounds/indexes and metadata integrity.
- [ ] Add a device-readable frame catalog.
- [ ] Add tests and run Worker validation.

### 2. Customer Web

- [ ] Generate/upload thumbnails.
- [ ] Add slot-order preview/editor.
- [ ] Expose publish, assignment, and device-apply states.
- [ ] Run portal validation.

### 3. Windows app

- [ ] Persist remote version provenance and sync state.
- [ ] Stage and verify downloads.
- [ ] Preserve slot indexes and refresh UI after activation.
- [ ] Separate local apply from acknowledgement retry.
- [ ] Add thumbnail/catalog UI and tests.

### 4. Android app

- [ ] Add durable frame-version storage.
- [ ] Implement configuration pull, downloads, verification, and ACK retry.
- [ ] Preserve offline last-known-good configuration.
- [ ] Add UI states and tests.

### 5. Cross-platform verification

- [ ] Verify geometry/order parity.
- [ ] Verify same PNG with changed slots applies as a new version.
- [ ] Verify corrupt/interrupted downloads preserve active configuration.
- [ ] Verify ACK retry is idempotent.

## Work log

- 2026-09-21: Audit confirmed Worker version storage and Windows pull exist; Windows lacks durable version provenance/UI notification, and Android has no configuration sync.
