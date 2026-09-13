---
kind: build_system
name: Build System — .NET WPF via SDK-style csproj
category: build_system
scope:
    - '**'
source_files:
    - Vindows/Vindows.csproj
    - Vindows/app.manifest
---

## What system/approach is used

The project is a Windows desktop application built with the **.NET SDK** using an **SDK-style `Microsoft.NET.Sdk` project file**. There are no custom build scripts, Makefiles, Dockerfiles, CI pipelines, or packaging tooling in this repository. The build is driven entirely by the standard `dotnet build` / `dotnet publish` workflow against the single project `Vindows/Vindows.csproj`.

## Key files and packages

- `Vindows/Vindows.csproj` — the only build definition. It declares:
  - `<OutputType>WinExe</OutputType>` (produces a standalone Windows executable)
  - `<TargetFramework>net10.0-windows</TargetFramework>` (targets .NET 10 on Windows)
  - `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</Nullable>` for C# language features
  - `<UseWPF>true</UseWPF>` to enable the WPF UI framework
  - `<ApplicationManifest>app.manifest</ApplicationManifest>` to apply a Win32 application manifest
- `Vindows/app.manifest` — Win32 application manifest consumed at link time (UAC/elevation settings, DPI awareness, etc.)
- `Vindows/obj/project.assets.json`, `*.nuget.*` — auto-generated NuGet asset cache produced by the SDK restore step; no hand-edited `packages.config` or `Directory.Packages.props` is present.
- `Vindows/bin/Debug/` — default output directory for builds; no custom `<OutputPath>` overrides.

## Architecture and conventions

- **Single-project layout**: there is one `.csproj` at the root of the `Vindows/` folder; no solution (`*.sln`) file is checked in, so builds are invoked directly against the project file.
- **No external dependencies**: the project contains no `<PackageReference>` elements and no `NuGet.config`; all references appear to be implicit or framework-only (WPF + Win32 interop via P/Invoke in `Core/Win32.cs`).
- **Publishing model**: because `<RuntimeIdentifier>` is not set, `dotnet publish` will produce a **framework-dependent deployment (FDD)** targeting the installed .NET 10 runtime on Windows. Self-contained publishing would require adding a RID such as `win-x64`.
- **Versioning**: no `<Version>`, `<AssemblyVersion>`, or `<FileVersion>` properties are defined in the project file; version numbers are therefore left to compiler defaults or post-build steps not present in this repo.

## Conventions and constraints

- Build is performed with the standard .NET CLI commands (`dotnet build`, `dotnet run`, `dotnet publish`) from the `Vindows/` directory.
- Target platform is constrained to Windows-only (`net10.0-windows`); cross-compilation to other OSes is not configured.
- No CI/CD configuration, Dockerfile, Makefile, shell build script, or release automation exists in this repository snapshot.
- The generated `obj/` contents (`project.assets.json`, `*.nuget.*`) indicate that NuGet restore runs automatically as part of the SDK build pipeline.
- Because no `<GeneratePackageOnBuild>` or pack targets are enabled, this project does not produce a NuGet package out of the box.