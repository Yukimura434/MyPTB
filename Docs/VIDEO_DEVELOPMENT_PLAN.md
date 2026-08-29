# Video Development Plan

## Product contract

Each shutter result is one aggregate identified by `CapturedImageId`:

- `Picture`: JPG still with the configured LUT.
- `Video`: standalone H.264 MP4 without LUT, 18 fps, covering the three seconds before shutter.

After frame arrangement the workflow creates:

- `Composite`: static framed image sourced from `Picture` assets.
- `CompositeVideo`: standalone framed MP4 sourced only from videos assigned to frame slots.
- `Gif`: animation sourced from accepted `Picture` assets.
- Local Share gallery: one tokenized HTML page with per-asset downloads; no ZIP is created.

For `N` accepted shots, a complete video capture contains `N Picture`, `N Video`, one `Composite`, one `CompositeVideo`, and optionally one `Gif`. Local Share is a transient delivery view, not a capture asset. See `Docs/LOCAL_SHARE.md`.

## Integrity rules

- `Picture` and `Video` in a pair share one `CapturedImageId` and one capture owner.
- Retake replaces the JPG/MP4 pair atomically; old and new IDs or paths cannot be mixed.
- `Composite.SourceAssetIds` references Picture assets.
- `CompositeVideo.SourceAssetIds` is a non-empty subset of Video assets actually assigned to slots.
- Assets store stable ID, MIME type, file length, SHA-256, UTC creation time and status.
- Integrity validation runs before Local Share publishes the token-scoped asset list.

## Encoding and composition

- Output is MP4 with H.264 video; there is no JPEG container or metadata packaging.
- Encoder dimensions are aligned to multiples of four for the legacy native codec.
- Composite encoding runs in two phases: decode/render 54 managed frames, close decoders, then encode.
- Repeated slot assignment reuses one decoder per source video.
- Slot rendering uses cover crop and the PNG frame overlay supplies masks for circular or irregular shapes.
- Native codec work runs in a child process so a codec crash cannot terminate the customer workflow.

## Workflow and feature control

- Feature flags are `Video` and `VideoNativeEncoder`.
- Customer flow is `FrameSelection → VideoSelection → Printing → Complete` when video is enabled.
- When video is disabled, the Picture/Composite flow remains available and no placeholder video asset is created.
- UI preview uses the standalone MP4 directly.
- QR Local Share serves a thumbnail gallery and streams original assets individually over the LAN; it does not create a ZIP.

## Runtime files

```text
<session output>/
├── <imageId>.jpg
├── <imageId>.mp4
├── <composite>.png
├── <composite>.mp4
├── <capture>.gif
└── LocalShare/<token>/*.jpg    # transient thumbnails only
```

## Verification gate

```powershell
dotnet build PhotoBooth.sln --configuration Debug
dotnet test PhotoBooth.UnitTests\PhotoBooth.UnitTests.csproj --configuration Debug
PhotoBooth.Admin.UI.exe --camera-smoke
```

Hardware acceptance verifies timing, playback, LUT isolation, retake pairing, frame composition, MP4 playback on Android/iOS, and individual Local Share downloads over the target Wi-Fi network.
