# Repository Guidelines

## Project Structure & Module Organization

`DL小工具.sln` contains the Windows x64 .NET 8 WPF application and tests. Retain the source directory and namespace `烧录上位机程序`.

- `Pages/` contains cached flashing, packaging, and offline authorization pages; `ViewModels/` holds observable state.
- `Models/Packaging/` defines project descriptors, options, and results. Firmware parsers remain independent of WPF.
- `Services/Packaging/` handles inspection, processes, recovery, installers, and profiles; `Services/JLink/` owns probe operations.
- `Services/Authorization/` computes six-digit responses and protects profiles/backups. Keep keys out of bindings, logs, fixtures, and clipboard; use public test keys only.
- `Resources/` contains shared styles and translations. `tests/DLTools.Tests/` uses xUnit; `tests/ProcessTestHost/` supplies process fixtures.
- `docs/` stores implementation, review, and acceptance records. The root feature log tracks checkpoints.

## Build, Test, and Development Commands

Run from the repository root on Windows x64:

```powershell
dotnet restore "DL小工具.sln"
dotnet build "DL小工具.sln" -c Release
dotnet run --project "烧录上位机程序/DL小工具.csproj"
dotnet test "tests/DLTools.Tests/DLTools.Tests.csproj" -c Release
```

These restore, build, launch, and test. Inspection tests also need Visual Studio MSBuild17.8+ and the Framework4.8 targeting pack. Set `DLTOOLS_INTEGRATION=1` for real publishing/Inno checks; additionally set `DLTOOLS_INSTALL_INTEGRATION=1` for temporary current-user installation tests. See README prerequisites. J-Link drivers are required only for flashing.

## Coding Style & Naming Conventions

Use four spaces, separate-line braces, file-scoped namespaces, nullable annotations, `PascalCase` members, and `_camelCase` fields. Prefer CommunityToolkit `[ObservableProperty]` and shared styles. Dispatch background UI updates. Bind computed read-only TextBox values with `Mode=OneWay`. No formatter or linter is configured.

## Testing Guidelines

Use behavior names such as `Rollback_RestoresOriginalAndPreservesNewOutput`. No coverage percentage is enforced. Exercise failures, file protection, and cancellation. Use unique temporary roots and AppIds; never install fixtures over real applications. Record UI, installation, and hardware results separately. Hardware checks cover connect, program, mandatory readback verification, reset, cancellation, and closing while busy.

## Commit & Pull Request Guidelines

Use focused subjects such as `feat(P06): ...`, `fix(hex): ...`, or `docs: ...`. PRs describe behavior, validation, linked issues, remaining field checks, and include UI screenshots. Update relevant records. Exclude build output, TestResults, licenses, and personal settings.

## Task & File Safety

Register background operations with `BackgroundTaskHost`; run tools through `ExternalProcessRunner`. Preserve solution mappings, original scripts, recovery journals, and stable AppIds. Keep `%APPDATA%/JLinkRTTViewer/` flashing preferences compatible; packaging and authorization use separate subtrees. Client throttling and provisioning are separate from the authorization calculator.
