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
├── <imageId>.jpg            # still picture with configured LUT
├── <imageId>.mp4            # standalone H.264 video without LUT
├── <final composite>.png
└── <final animation>.gif
```

`Session` is recreated at the start of a turn and removed after completion, cancellation, or shutdown. Only the accepted current captures, final composite, and generated GIF are promoted to the configured output folder.

Live view is buffered for three seconds at 18 fps and encoded as a standalone H.264 MP4. The camera still is stored separately as JPG. Retakes create a new JPG/MP4 pair before replacing a review position, so the previous pair remains intact if capture or encoding fails.
