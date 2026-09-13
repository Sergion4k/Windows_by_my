---
kind: error_handling
name: Silent-failure I/O and Win32 Interop Error Handling
category: error_handling
scope:
    - '**'
source_files:
    - Vindows/Core/LayoutStore.cs
    - Vindows/Core/WindowEnumerator.cs
    - Vindows/Core/LayoutApplier.cs
    - Vindows/MainWindow.xaml.cs
    - Vindows/App.xaml.cs
---

## Overview

The repository is a small WPF desktop app with no centralized error-handling framework, logging library, or custom exception types. Errors are handled locally at the call sites that interact with unreliable external resources (disk I/O and Win32 APIs) using bare `try`/`catch` blocks that swallow exceptions and fall back to safe defaults. There is no global exception handler in `App.xaml.cs`, no middleware, no sentinel errors, and no use of `throw` / `Exception` anywhere in the codebase.

## Key files and patterns

- **`Vindows/Core/LayoutStore.cs`** — The only file with explicit error handling. Both `Load()` and `Save()` wrap their body in `try`/`catch` blocks:
  - `Load()`: if the layout JSON file does not exist or deserialization fails, it returns an empty `LayoutFile` instead of throwing. This makes missing/corrupt configuration a non-fatal condition.
  - `Save()`: on any write failure (e.g. lack of permissions), the catch block is empty; the comment explicitly states the intent: *"Нет прав на запись — молча пропускаем, раскладка остаётся в памяти."* (No write permission — silently skip, layout stays in memory). The UI continues normally.

- **`Vindows/Core/WindowEnumerator.cs`** — Uses a per-call `try`/`catch` around `Process.GetProcessById(pid)` inside `TryGetWindowInfo`. If the process has already exited or cannot be queried, the method returns `false`, causing that window handle to be skipped from enumeration. No exception propagates outward.

- **`Vindows/Core/LayoutApplier.cs`**, **`Vindows/Core/Win32.cs`**, **`Vindows/Core/Models.cs`** — Contain no `try`/`catch`/`throw`/`Exception` usage. They assume callers tolerate failures via return values (`bool`, nullable references) or rely on Win32 P/Invoke returning zero/error codes rather than raising .NET exceptions.

- **`Vindows/MainWindow.xaml.cs`** — User-input validation uses `int.TryParse` and returns early by setting `StatusText.Text` to a user-facing message (e.g. *"Колонки: введите число ≥ 1"*). No exceptions are thrown for invalid input; the UI simply displays feedback in the status bar.

- **`Vindows/App.xaml.cs`** — Contains no `Application.DispatcherUnhandledException` handler or similar global error hook. Unhandled exceptions would therefore crash the process.

## Architecture and conventions

1. **Fail-safe defaults over propagation.** When a peripheral operation fails, the code prefers returning a neutral/default value (empty `LayoutFile`, empty window list, `false`) rather than bubbling an error up. Callers treat these as "nothing to do" and continue.

2. **Per-call try/catch, never rethrow.** Every `catch` block either returns a default value or does nothing. Exceptions are treated as implementation details of unreliable OS calls, not as control-flow signals.

3. **User-facing errors are strings on the UI.** Validation failures and status messages are written directly to `StatusText.Text` in `MainWindow.xaml.cs`. There is no shared error model, dialog, or toast notification system.

4. **No custom exception types or error codes.** The codebase does not define any `Exception` subclasses, error enums, or result types. All error signaling is done through return values (`bool`, nullables) and swallowed exceptions.

5. **Win32 interop relies on return codes.** Methods like `DwmGetWindowAttribute`, `GetWindowRect`, `IsIconic`, etc. are called and their success/failure is checked via return values or subsequent property checks, not via caught exceptions.

## Conventions and constraints

- Disk I/O failures are considered non-fatal; the application continues running with in-memory state when persistence fails.
- Process enumeration failures are ignored per-window; a crashed or inaccessible process simply does not appear in the window list.
- Invalid user input is validated inline in event handlers and reported via status text — no exceptions are raised.
- There is no global unhandled-exception policy; any unexpected exception will terminate the WPF process.
- The absence of `throw`, `Exception`, logging, or error-reporting infrastructure means this is a best-effort, resilient-by-design approach suited to a lightweight utility tool.