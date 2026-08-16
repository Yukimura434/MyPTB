# PhotoBooth Clean Architecture

## Dependency direction

```text
PhotoBooth.Admin.UI ----\
                        +--> PhotoBooth.Core --> PhotoBooth.Shared
PhotoBooth.Customer.UI -+--> PhotoBooth.Infrastructure --> PhotoBooth.Database
                                                   |----> CameraControl.Devices
                                                   `----> CameraControl.Devices
```

- `PhotoBooth.Core` owns models, use-case boundaries and all public service/repository interfaces.
- `PhotoBooth.Business` owns UI-independent business services, test repositories, `CapturePipeline` and `PrintPipeline`.
- `PhotoBooth.FrameEngine` detects transparent PNG regions and depends only on Core plus `System.Drawing`.
- `PhotoBooth.Infrastructure` implements services and is the composition module for camera, storage, printing, navigation and persistence adapters.
- `PhotoBooth.Database` implements Core repository interfaces with SQLite. It is never referenced by either UI project.
- `PhotoBooth.Shared` contains application bootstrap options that are not domain concepts.
- Both UI projects are executable composition roots only. They contain no Window, UserControl or XAML yet.
- `CameraControl.Devices` and SQLite are transitive implementation details hidden behind Core interfaces.

## Cross-cutting services

DI uses `Microsoft.Extensions.DependencyInjection`, logging uses `Microsoft.Extensions.Logging`, and persistence uses `Microsoft.Data.Sqlite`. `InitializePhotoBooth` applies the idempotent SQLite schema during startup.

## Core service boundaries

- `ICameraService`
- `IFrameService`
- `IPresetService`
- `IPrinterService`
- `ISessionService`
- `INavigationService`
- `IFileStorageService`

## Camera integration boundary

`ICameraService` exposes discovery, connection, normalized properties and capture results.
`ILiveViewService` exposes start/frame/focus/stop without leaking `LiveViewData`. Infrastructure
maps Canon, Nikon, Sony and other drivers through their common `ICameraDevice` contract. The
normalized property API covers ISO, shutter speed, aperture, white balance, focus mode,
metering, compression and exposure compensation. Vendor numeric codes remain inside
`CameraControl.Devices`; clients use stable display values and allowed-value lists.

Run the hardware-independent integration smoke test with:

```powershell
PhotoBooth.Admin.UI.exe --camera-smoke
```

## Frame analysis

`PngFrameAnalyzer` scans the PNG alpha channel using four-connected components. It filters
regions by alpha threshold, minimum pixel area, width and height. Border-connected transparency
can be ignored so the transparent canvas is not treated as a photo slot. Results are ordered
deterministically and hard-limited to eight `FrameSlot` records.

Business and frame tests run without UI, camera hardware or SQLite:

```powershell
dotnet test PhotoBooth.UnitTests/PhotoBooth.UnitTests.csproj
```

## Build

```powershell
msbuild PhotoBooth.sln /t:Restore,Build /p:Configuration=Debug /p:Platform="Any CPU"
msbuild PhotoBooth.sln /t:Build /p:Configuration=Release /p:Platform="Any CPU"
```

## Admin application

`PhotoBooth.Admin.UI` is a new dark-theme WPF MVVM application with exactly four cached page
view models: Home, Frame Manager, Preset Manager and Printer Manager. View models reference
only Core service interfaces. Runtime persistence for frames, presets, printer profiles and
settings is provided by SQLite repositories registered in Infrastructure. No DigiCamControl
window, control, style, converter or behavior is reused.
