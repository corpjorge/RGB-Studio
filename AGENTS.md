# Repository Guidelines

## Project Structure & Module Organization

`App/` contains the .NET 10 WPF application. UI markup and code-behind live in `App/*.xaml` and `App/*.xaml.cs`; hardware communication is isolated in `App/Rgb/`, software effects in `App/Effects/`, device drawing in `App/Rendering/`, and reusable UI controls in `App/Controls/`. `test-connection/` is a console utility for listing devices and mapping zones. `Iniciar.ps1` and `RGB Studio.cmd` launch OpenRGB and the desktop app. Build outputs under `bin/` and `obj/` are ignored and must not be committed.

## Build, Test, and Development Commands

- `dotnet restore App/App.csproj` restores NuGet dependencies.
- `dotnet build App/App.csproj -c Release` builds the WPF application for Windows x64.
- `dotnet run --project App/App.csproj` runs the UI during development.
- `dotnet build test-connection/test-connection.csproj -c Release` builds the hardware probe.
- `dotnet run --project test-connection -- list` lists OpenRGB devices, zones, and modes. Use `map` or `walk` only when hardware is connected.

OpenRGB's SDK server must be listening on port `6742`. PawnIO and administrator privileges are required for OpenRGB to expose SMBus devices such as RAM; the WPF app itself does not require elevation.

## Coding Style & Naming Conventions

Use four-space indentation and file-scoped namespaces. Follow standard C# naming: `PascalCase` for types, methods, properties, and public/internal members; `_camelCase` for private fields; `camelCase` for locals and parameters. Keep nullable reference types enabled. Preserve the existing concise XML comments around hardware constraints and concurrency. Keep XAML control names descriptive and aligned with their code-behind files. No formatter is configured, so match surrounding code and run `dotnet build` to catch analyzer/compiler issues.

## Testing Guidelines

There is currently no automated test project or coverage threshold. Validate every change by building both projects. For RGB behavior, document the OpenRGB version, device names, command used, and observed result. Never make tests depend on a specific device being present unless clearly marked as manual hardware validation.

## Commit & Pull Request Guidelines

History uses short, imperative Spanish subjects, for example `Aplanar la estructura del proyecto`. Keep each commit focused. Pull requests should explain what changed, why, user impact, and validation performed. Include screenshots for UI changes and hardware observations for device-control changes. Do not commit OpenRGB binaries, proprietary vendor SDK files, logs, credentials, or generated build artifacts.
