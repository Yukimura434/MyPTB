# MiuPhotoBooth 3.0.0

Windows release channel: https://github.com/Yukimura434/MyPTB/releases . The legacy MiuCamezaPTB channel remains unchanged.

This release consolidates the current PC implementation into one executable with Customer/Manager live-view ownership, responsive waiting/login/customer layouts, separate portrait waiting customization, frame/preset library improvements, background capture delivery and independent GIF/video settings.

Login is process-local. There is no remember-login option; startup removes old saved login and exit clears employee/Event/SelfBooth/PhotoBooth authority. Device enrollment, configuration, local media and durable capture delivery grants remain intact. Remote employee logout is best-effort; local clearing does not depend on connectivity.

About offers three explicit actions: check, download/verify, and install. Startup never checks, downloads or installs automatically. The Inno installer is accepted only from MyPTB stable release metadata with matching asset name, size and SHA-256 digest. Releases use vX.Y.Z tags and MiuPhotoBooth-X.Y.Z-Setup.exe assets. Keep DesktopApplicationDefaults.DataVersion and installer MyDataVersion stable when changing app version.

Build: dotnet build PhotoBooth.Admin.UI/PhotoBooth.Admin.UI.csproj -c Release . Tests: dotnet test PhotoBooth.UnitTests/PhotoBooth.UnitTests.csproj -c Release . Native UI checks: Scripts/Validation/Run-ManualUpdateCheck.ps1 and Run-UnifiedUxCheck.ps1 (synthetic camera/data). Publish into a clean directory and compile Installer/PhotoBooth.iss with MyAppSource pointing at that directory. Never include validation executables, logs or local databases in release assets. Use the same Inno AppId to preserve installation identity.

The historical .github/workflows/photobooth-release.yml is for the old photobooth-v* Velopack channel. Do not trigger it for this release; v3.0.0 uses the new Inno/manual channel.

Validation: full PC suite 316 tests, manual-update native UI checks, camera/live-view lifecycle checks and isolated camera/UI smoke. Physical camera/printer qualification remains device-specific.
