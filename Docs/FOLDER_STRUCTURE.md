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
