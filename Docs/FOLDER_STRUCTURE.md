# Runtime folder structure

```text
PhotoBooth/Data
├── Sessions
├── Captures
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

`Temp` and `Preview` use hourly retention. `Sessions` uses configurable day retention. `Print` contains durable final images. `Public` is used only by the replaceable local upload provider.

For each Customer capture turn, the selected session output folder also contains a transient workspace:

```text
<configured output folder>
├── Session/                 # raw captures, retakes and composition previews
├── <accepted originals>_MP.jpg # Android Motion Photo 1.0 (JPEG + embedded MP4)
├── <final composite>.png
└── <final animation>.gif
```

`Session` is recreated at the start of a turn and removed after completion, cancellation, or shutdown. Only the accepted current captures, final composite, and generated GIF are promoted to the configured output folder.

Customer originals are stored as a single Motion Photo file. Live view is buffered for three seconds at 18 fps; the captured JPEG is the primary image and an H.264 MP4 is appended with Motion Photo 1.0 XMP metadata. Retakes are packaged under a new image ID before replacing a review position, so the previous Motion Photo remains intact if capture or encoding fails.
