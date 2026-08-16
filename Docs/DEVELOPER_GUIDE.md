# Developer guide

Requirements: Visual Studio 2022, .NET Framework 4.8 developer pack, Windows camera SDK dependencies and x64 Windows.

Build with:

```powershell
msbuild PhotoBooth.sln /t:Build /p:Configuration=Release /m
```

Run tests with Visual Studio Test Explorer or `vstest.console.exe PhotoBooth.UnitTests\bin\Debug\net48\PhotoBooth.UnitTests.dll` after an MSBuild build.

Implement providers behind Core interfaces and register exactly one default provider in `PhotoBooth.Infrastructure.DependencyInjection`. Image effects must be stateless, cancellation-aware, and return a new file. Never add SDK/database references to a UI project.

Logs are split into Camera, Printer, Session, Application and Error channels and rotate at 5 MB with seven retained files.
