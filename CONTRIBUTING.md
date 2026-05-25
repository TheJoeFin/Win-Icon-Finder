# Contributing to WinIconFinder

Thank you for your interest in contributing! Here's everything you need to get up and running.

---

## Prerequisites

- Windows 10 version 1809 (build 17763) or later
- [Visual Studio 2022](https://visualstudio.microsoft.com/) with the **Windows application development** workload  
  *or* the [.NET 10 SDK](https://dotnet.microsoft.com/download)
- **Developer Mode** enabled — `Settings → System → For developers → Developer Mode`
- `git`

## Getting started

```powershell
git clone https://github.com/<your-username>/win-icon-finder.git
cd win-icon-finder
.\BuildAndRun.ps1
```

The first launch pre-renders all ~1,600 icons and caches them to disk. Subsequent builds skip this step and start in under a second.

## Project structure

```
Models/       – FluentIcon data model
Services/     – Icon loading, matching, clipboard export, similarity layout
ViewModels/   – MVVM view model (CommunityToolkit.Mvvm)
MainPage.*    – Main UI: search panel, draw canvas, similarity map
MainWindow.*  – Window host with Mica backdrop and custom title bar
Assets/       – Bundled icon font (TTF) and codepoint map (JSON)
```

## Making changes

1. Fork the repo and create a feature branch:
   ```powershell
   git checkout -b feature/my-improvement
   ```
2. Make your changes. Build to verify:
   ```powershell
   .\BuildAndRun.ps1 -SkipRun
   ```
3. Run a Release build before submitting to catch any trimming issues:
   ```powershell
   .\BuildAndRun.ps1 /p:Configuration=Release -SkipRun
   ```
4. Open a pull request against `main` with a clear description of what changed and why.

## Code style

- Follow the existing patterns — MVVM with `CommunityToolkit.Mvvm`, `x:Bind` compiled bindings in XAML, and `Win2D` for all 2D rendering.
- Keep code-behind minimal; business logic belongs in ViewModels or Services.
- Comment non-obvious algorithms (the cosine similarity pipeline, spiral layout maths, etc.) but don't over-comment straightforward code.
- Nullable reference types are enabled — keep everything null-safe.

## Third-party assets

The icon font (`Assets/FluentSystemIcons-Regular.ttf`) and codepoint map (`Assets/icons.json`) are sourced from Microsoft's [Fluent System Icons](https://github.com/microsoft/fluentui-system-icons) project, which is released under the **MIT License**.

When updating these assets, download the latest release from that repository and replace both files. Do not commit any asset that is not covered by a compatible open-source license.

## Reporting issues

Please include:
- Your Windows version (`winver`)
- Whether it's a Debug or Release build
- Steps to reproduce, and what you expected vs. what happened
- Any relevant output from the debug console

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
