# Runtime folder structure

```text
PhotoBooth/Data
├── Captures
│   └── yyyy/MM/dd/<session-guid>
│       ├── Work
│       ├── Originals
│       └── Final
├── Frames
├── Presets
├── Preview
├── Print
├── Logs
├── Temp
├── Backup
├── Public
└── photobooth.db
```

`Temp` and `Preview` use hourly retention. Managed booth-session roots under `Captures` use configurable day retention. `Print` contains print-queue state. `Public` is used only by the replaceable local upload provider.

Each Customer turn gets a new booth-session GUID and an isolated managed root:

```text
Captures/yyyy/MM/dd/<session-guid>/
├── Work/                    # partial captures and composition previews
├── Originals/
│   ├── <asset-guid>.jpg     # accepted still with configured LUT
│   └── <asset-guid>.mp4     # accepted standalone H.264 video
└── Final/
    ├── <asset-guid>.png     # final composite
    ├── <asset-guid>.mp4     # optional composite video
    └── <asset-guid>.gif     # optional animation
```

`Work` is removed after completion or cancellation. SQLite is the source of truth for asset identity and lifecycle; physical filenames are immutable GUIDs, not customer-facing names.

Retention deletes only an exact DB-registered, terminal booth-session root with no pending or ambiguous output job. Event folders and arbitrary operator-selected folders are not deletion targets.

Live view is buffered for three seconds at 18 fps and encoded as a standalone H.264 MP4. The camera still is stored separately as JPG. Retakes create a new JPG/MP4 pair before replacing a review position, so the previous pair remains intact if capture or encoding fails.

See `Docs/LOCAL_BUSINESS_DATA_MODEL.md` for lifecycle, crash recovery and server-sync invariants.
