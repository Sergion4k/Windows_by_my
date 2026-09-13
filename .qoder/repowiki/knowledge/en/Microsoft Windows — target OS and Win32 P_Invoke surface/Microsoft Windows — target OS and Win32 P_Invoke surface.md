---
kind: external_dependency
name: Microsoft Windows — target OS and Win32 P/Invoke surface
slug: microsoft-windows
category: external_dependency
category_hints:
    - sdk_real_api
    - client_constraint
scope:
    - '**'
source_files:
    - Vindows/Core/Win32.cs
    - Vindows/app.manifest
    - Vindows/Vindows.csproj
---

The application is a Windows-only tiling/snap window manager built on .NET targeting `net10.0-windows` with WPF. All OS interaction goes through P/Invoke to the Win32 API:
- Monitor enumeration via `EnumDisplayMonitors` + `GetMonitorInfo` (uses `rcWork`, i.e. work area excluding taskbar).
- Window enumeration via `EnumWindows`, filtering hidden/minimized/UWP helper windows.
- Window placement via `SetWindowPos` against zone rectangles.
- Future drag-to-zone uses `SetWinEventHook` (`EVENT_SYSTEM_MOVESIZESTART`).
- Hotkeys via `RegisterHotKey`.
The app manifest declares PerMonitorV2 DPI awareness so coordinates are in physical pixels across mixed-DPI multi-monitor setups. There are no NuGet packages for OS access — this is a pure Win32 interop layer.