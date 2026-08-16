# PhotoBooth

PhotoBooth is a Windows desktop photo booth application with separate Admin and Customer interfaces, a shared SQLite database, multi-camera support, frame composition, printing, and a Cloudflare/Cloudinary download backend.

## Active projects

- `PhotoBooth.Admin.UI` — configuration and operator application.
- `PhotoBooth.Customer.UI` — customer kiosk workflow.
- `PhotoBooth.Core`, `Business`, `Infrastructure`, `Database`, `FrameEngine`, `Shared` — application layers.
- `CameraControl.Devices` — camera device engine retained for Canon, Nikon, Sony and other supported transports.
- `Canon.Eos.Framework`, `PortableDeviceLib`, `refs` — camera runtime dependencies.
- `PhotoBooth.Native` — vendor native runtimes copied into desktop builds.
- `backend/PhotoBooth.Worker` — Cloudflare Worker API for D1 and Cloudinary.
- `PhotoBooth.UnitTests` — automated tests.

Open `PhotoBooth.sln` for desktop development. The original digiCamControl UI, plugins, workflows, setup tools, examples, and test applications are not part of the active solution.

## Build

```powershell
dotnet restore PhotoBooth.sln
dotnet build PhotoBooth.sln --configuration Debug
dotnet test PhotoBooth.UnitTests\PhotoBooth.UnitTests.csproj --configuration Debug
```

The desktop projects target .NET Framework 4.8. Canon runtime files under `PhotoBooth.Native/Canon` are copied to the Admin and Customer output directories.

## Desktop releases and automatic updates

PhotoBooth uses Velopack and the public [`Yukimura434/MiuCamezaPTB`](https://github.com/Yukimura434/MiuCamezaPTB) GitHub Releases as its update source. An installed copy checks for a stable update after startup, downloads it in the background, safely stops camera services, then applies the update and restarts. Builds started directly from `bin` are not Velopack installations and skip this check.

Create a release by pushing a semantic-version tag with the `photobooth-v` prefix:

```powershell
git tag photobooth-v1.1.0
git push origin photobooth-v1.1.0
```

The `PhotoBooth Release` GitHub Actions workflow builds the x86 application, packages it with Velopack, and publishes the installer and update feed to that release repository. Configure the `MIUCAMEZAPTB_RELEASE_TOKEN` Actions secret with write access to the release repository before using the workflow. It can also be run manually with a semantic version. Install `MiuCamezaPTB-win-Setup.exe` from the first release; subsequent published releases are installed automatically.

## Backend

```powershell
cd backend\PhotoBooth.Worker
npm install
npm run typecheck
npm run dev
```

Node.js 22 or later is required. Local Worker secrets belong in `.dev.vars` and must not be committed.

## Attribution

The camera device layer contains code derived from the MIT-licensed digiCamControl project. Preserve the repository `LICENSE` and existing source-file notices. Vendor SDK binaries may have separate redistribution terms.
