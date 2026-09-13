---
kind: configuration_system
name: Per-User JSON Layout Store in %APPDATA% with In-Memory Defaults
category: configuration_system
scope:
    - '**'
source_files:
    - Vindows/Core/LayoutStore.cs
    - Vindows/Core/Models.cs
    - Vindows/MainWindow.xaml.cs
    - Vindows/Vindows.csproj
    - Vindows/App.xaml.cs
---

## What system/approach is used

The application uses a minimal, file-based configuration system centered on a single `LayoutStore` class that serializes per-monitor window layouts to a JSON file. There is no external configuration framework (no YAML/TOML/INI parser, no `appsettings.json`, no environment-variable-driven config). The only runtime configuration source is the JSON layout file; defaults are hard-coded in-memory.

## Key files and packages

- `Vindows/Core/LayoutStore.cs` — the sole configuration persistence layer. It defines `FilePath` as `%APPDATA%\Vindows\layout.json`, provides `Load()` and `Save(LayoutFile)` using `System.Text.Json`, and exposes a static `Options` with indented output.
- `Vindows/Core/Models.cs` — defines the `LayoutFile` / `MonitorLayout` / `Zone` / `WindowInfo` types that are serialized to/from the JSON file.
- `Vindows/MainWindow.xaml.cs` — consumes `LayoutStore.Load()` at startup and calls `LayoutStore.Save(...)` when the user applies or explicitly saves a layout.
- `Vindows/Vindows.csproj` — targets `net10.0-windows` WPF (`UseWPF=true`) with `Nullable=enable`; no NuGet packages for configuration are referenced.
- `Vindows/App.xaml.cs` — empty `Application` subclass; no app-level config initialization.

## Architecture and conventions

- **Single canonical store**: All layout state flows through `LayoutStore`. The UI reads via `LayoutStore.Load()`, edits in-memory models, then persists by calling `LayoutStore.Save(...)`. There is no separate settings dialog or config editor.
- **Location**: Configuration lives under the per-user roaming profile directory: `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vindows", "layout.json")`. This means each Windows user gets an independent layout file without requiring admin privileges.
- **Default-on-miss**: `Load()` returns a fresh `new LayoutFile()` if the file does not exist or deserialization fails. Default monitor grids (e.g., 2×1) are constructed lazily in `MainWindow.Initialize()` / `GetLayoutFor` when a monitor has no stored layout.
- **Error-tolerant persistence**: `Save()` wraps write logic in `try/catch` and silently ignores failures (e.g., permission denied); the in-memory layout remains valid even if disk write fails. `Directory.CreateDirectory` ensures the `Vindows` folder exists before writing.
- **Human-readable format**: `JsonSerializerOptions { WriteIndented = true }` produces pretty-printed JSON so users can inspect/edit the file manually.
- **No layered config**: There is no merging of multiple sources (e.g., no env vars overriding JSON, no user vs. machine-wide config). The JSON file is the only persistent source of truth.
- **Runtime-only flags**: Feature toggles such as whether to apply layouts on startup are not exposed as configuration; they are controlled by code paths invoked from UI buttons (`ApplyBtn_Click`, `SaveBtn_Click`, `LoadBtn_Click`).

## Conventions and constraints

- The layout file schema is implicitly defined by the C# `LayoutFile` model in `Models.cs`; any change to that type changes the persisted format.
- The file path is computed once at process start via `Environment.GetFolderPath(SpecialFolder.ApplicationData)` and cached in the static `FilePath` field — it cannot be overridden at runtime.
- Missing or corrupt layout files are treated as empty layouts rather than fatal errors; the application always starts usable.
- Because `Save` swallows exceptions, there is no user-facing feedback when persistence fails beyond the in-memory state remaining unchanged; callers do not check a return value.
- No validation is performed on the JSON content during load; malformed data falls through the `catch` block and results in a default empty layout.