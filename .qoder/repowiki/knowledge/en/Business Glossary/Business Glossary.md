---
kind: business_term
name: Business Glossary
category: business_term
scope:
    - '**'
---

### область (зона)
- Definition：A user-defined rectangular region on a monitor's work area into which windows are snapped; zones are created by a grid of columns × rows with a configurable pixel gap, one independent set per monitor.
- Aliases：zone、grid cell

### раскладка
- Definition：The persisted layout configuration for all monitors, including each monitor's grid (columns, rows, gap), the list of zones, and the process-name-to-zone assignments; stored as JSON under `%APPDATA%\Vindows\layout.json`.
- Aliases：layout、layout file

### рабочая область
- Definition：The usable screen rectangle returned by `GetMonitorInfo`'s `rcWork` field, explicitly excluding the taskbar; all zone calculations and window placements are relative to this rectangle rather than the full monitor bounds.
- Aliases：work area、rcWork

### Vindows
- Definition：The name of this custom window-manager application (project and assembly namespace), distinct from the existing Microsoft PowerToys FancyZones feature it aims to replicate.
