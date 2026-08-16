# AGENTS.md

Guidance for coding agents working in this repository. This is a **PhotoBooth** project: a Windows WPF desktop photo-booth app (Admin + Customer UIs, SQLite, multi-camera capture, printing) backed by a Cloudflare Worker + D1 API and two web portals. An in-progress **Android/Kotlin migration** lives under `/kotlin`.

## Repository layout (what matters)

- `PhotoBooth.sln` — **the active solution**. Contains `PhotoBooth.Admin.UI`, `PhotoBooth.Customer.UI`, `PhotoBooth.Core`, `PhotoBooth.Business`, `PhotoBooth.Infrastructure`, `PhotoBooth.Database`, `PhotoBooth.Shared`, `PhotoBooth.FrameEngine`, `PhotoBooth.UnitTests`, and `CameraControl.Devices`.
- `backend/PhotoBooth.Worker` — Cloudflare Worker (TypeScript) API for D1 + Cloudinary.
- `web-admin/` and `web-customer/` — Next.js/React portals built on **vinext** (Cloudflare Sites), with Drizzle + D1.
- `frontend/` — older standalone Vite static app (`myptbooth-frontend`).
- `kotlin/` — **Android/Kotlin migration**. `/kotlin/AGENTS.md` is the authoritative instruction file for Android work; `/kotlin/docs/MIGRATION_PLAN.md` is the migration roadmap.
- `kotlin/docs/` — analysis docs (design patterns, workflows, camera SDK feasibility, performance) that informed the migration plan.
- `template/digiCamControl/` and `.legacy-archive/` — **original digiCamControl source (MIT)**. Not part of the active solution. Do not build or modify casually.
- `Docs/` — authoritative architecture and setup docs (see References).
- `PhotoBooth.Native/Canon/` — vendor Canon EDSDK runtime binaries copied into desktop builds.

## Key documents (authoritative)

- `README.md` — active projects + build/test/backend commands.
- `PhotoBooth.ARCHITECTURE.md` — Clean Architecture, dependency direction, service boundaries, frame analysis.
- `Docs/ARCHITECTURE.md` — production flow, replaceable boundaries, runtime folders.
- `Docs/DEVELOPER_GUIDE.md` — how to add providers, logging channels.
- `Docs/FOLDER_STRUCTURE.md` — runtime data layout under `%LOCALAPPDATA%\PhotoBooth\Data`.
- `Docs/API.md` — replaceable extension boundaries (`IUploadService`, `IQrCodeService`, `IPhotoDeliveryService`, `IPrintQueueService`, etc.).
- `setup.backend.md` — Worker/D1/Cloudinary reconfiguration (contains real production URLs and test accounts; never echo secrets).
- `backend/PhotoBooth.Worker/README.md` — Worker secrets and API flow.
- `kotlin/AGENTS.md` — **read before any Android/Kotlin work**.
- `kotlin/docs/MIGRATION_PLAN.md` — the Windows→Android migration roadmap and gates.

## Technology stack & platform constraints

- Desktop targets **.NET Framework 4.8 (`net48`)**, C# `LangVersion=latest`, SDK-style projects.
- `PhotoBooth.Admin.UI` is the only executable (`WinExe`, `UseWPF`, `PlatformTarget=x86`, `Prefer32Bit`). `PhotoBooth.Customer.UI` is a **WPF class library** (not an exe) referenced by Admin.
- `CameraControl.Devices` uses `Sdk="Microsoft.NET.Sdk.WindowsDesktop"` (`UseWPF=true`) and supports `AnyCPU;x86;x64`; it references `refs/Interop.WIA.dll` and `Canon.Eos.Framework`, `PortableDeviceLib`.
- DI: `Microsoft.Extensions.DependencyInjection`; Logging: `Microsoft.Extensions.Logging`; Persistence: `Microsoft.Data.Sqlite` (7.0.20) with `e_sqlite3` native DLLs copied per RID.
- Worker/web require **Node.js >= 22** (Worker: `>=22.0.0`; portals: `>=22.13.0`).

## Desktop build, test, and validation

```powershell
dotnet restore PhotoBooth.sln
dotnet build PhotoBooth.sln --configuration Debug
dotnet build PhotoBooth.sln --configuration Debug --platform "Any CPU"
dotnet test PhotoBooth.UnitTests\PhotoBooth.UnitTests.csproj --configuration Debug
# or MSBuild (see Docs/DEVELOPER_GUIDE.md):
msbuild PhotoBooth.sln /t:Build /p:Configuration=Release /m
```

- Run the camera integration smoke test (hardware-independent): `PhotoBooth.Admin.UI.exe --camera-smoke`.
- Business and frame tests run without UI, camera hardware, or a real database.

## Backend and portals

```powershell
# Worker (run in backend\PhotoBooth.Worker)
npm install
npm run typecheck                # tsc --noEmit
npm run dev                      # wrangler dev
npm run db:migrate:local         # wrangler d1 migrations apply DB --local
npm run db:migrate:remote        # apply to production D1
npm run deploy                   # wrangler deploy

# Portals (web-admin / web-customer)
npm install
npm run dev                      # customer uses --port 3001
npm run build
npm run test                     # build + node --test rendered-html.test.mjs
npm run lint                     # web-admin only
npm run db:generate              # drizzle-kit generate
```

## Architecture rules — preserve these

Dependency flow is `UI -> Core contracts <- Business/Infrastructure/Database`:

- `PhotoBooth.Core` owns models and **all** public interfaces. UI projects never reference `PhotoBooth.Database` or camera SDK types.
- `PhotoBooth.Business` owns UI-independent services, test repositories, `CapturePipeline`, `PrintPipeline`.
- `PhotoBooth.Infrastructure` is the composition module (DI registration, camera/storage/printing/navigation/persistence adapters).
- `PhotoBooth.Database` implements Core repository interfaces with SQLite.
- `PhotoBooth.FrameEngine` depends only on Core + `System.Drawing` (`PngFrameAnalyzer`, hard limit 8 `FrameSlot`s).
- Implement providers behind Core interfaces; register **exactly one** default provider in `PhotoBooth.Infrastructure.DependencyInjection`.
- Image effects (`IImageEffectProcessor`) must be stateless, cancellation-aware, and return a **new** file. Add a processing stage by registering another processor, not by editing the UI workflow.
- Camera SDK numeric property codes stay inside `CameraControl.Devices`; clients use normalized display values.

## Working rules

- Make **minimal, task-scoped** changes; do not refactor unrelated code.
- Do not change architecture, public contracts, project structure, dependencies, build config, or platform targets unless the task requires it.
- Preserve existing behavior; reuse existing abstractions/patterns rather than adding new services, helpers, or interfaces that duplicate what already exists.
- Inspect the relevant subsystem and its docs before modifying it.
- Run the appropriate build/tests above to validate your change.

## Security, secrets, licensing, generated code

- Never commit secrets. `.env`, `.dev.vars`, `*.private`, real API keys, and the RSA `LICENSE_PRIVATE_KEY_PKCS8` stay out of the repo; commit only `.example` placeholders.
- Worker secrets are set via `wrangler secret put`, never in `wrangler.jsonc`. Do not leak the production URLs/accounts in `setup.backend.md` into code or logs.
- The RSA private key must exist only on the Worker; the matching public modulus/exponent is embedded in `PhotoBooth.Shared/LicensePublicKey.cs` (used for offline license signature verification). Changing the private key breaks existing desktop licenses. The Android client must ship only the public modulus/exponent, never a private key.
- Desktop license verification against the Worker and client-provided ownership identifiers must never be trusted for authorization.
- `template/` and `.legacy-archive/` contain MIT-licensed digiCamControl code. Preserve the `LICENSE` and existing source-file copyright notices; vendor SDK binaries may have separate redistribution terms.
- Do not expose Canon native runtime binaries (`PhotoBooth.Native/Canon`, `EDSDK.dll`) through the Worker, web frontend, or Android app. EDSDK is Windows-only and not packaged on Android.

## Android migration (Kotlin)

- The Android app is a **migration of the existing Windows implementation**, which remains the behavioral reference. Read `/kotlin/AGENTS.md` and `/kotlin/docs/MIGRATION_PLAN.md` before any Android task.
- Preserve business rules, workflows, data semantics and backend contracts; do **not** port WPF/XAML, `PrintDocument`, WPD/EDSDK/WIA interop, or Windows filesystem/lifecycle assumptions. Use Android-native equivalents.
- Android/Kotlin production code belongs under `/kotlin`. Do **not** put Android source under the desktop projects.
- Do not modify the Windows implementation unless explicitly requested; the Windows source is the read-only behavioral reference during the migration.

## Notes / discrepancies

- README uses `dotnet build`; `Docs/DEVELOPER_GUIDE.md` and `PhotoBooth.ARCHITECTURE.md` use `msbuild`. Both build the same `PhotoBooth.sln`. Use whichever tooling is available; prefer the README's `dotnet` commands when on a dev machine with the .NET SDK.
- `.gitignore` is the legacy digiCamControl one; several paths reference the archived project. It is not authoritative for the new PhotoBooth projects' outputs. It has not yet been extended for `/kotlin` Gradle outputs.
- `web-admin`/`web-customer` share the "vinext-starter" README template; production naming is `myptbooth-admin` / `myptbooth-customer`.
- Repository analysis found several behavior gaps versus the earlier `kotlin/docs` blueprints (e.g. upload is done synchronously in `CloudPhotoDeliveryService`, not via a background queue). Record such conflicts in `kotlin/docs/MIGRATION_PLAN.md` rather than silently inventing behavior.