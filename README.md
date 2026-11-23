# Hacker
A cyberpunk hacking simulator: **GHOSTRIG**.

Design documents live in [`documents/`](documents/README.md). Progress and the plan ahead are in [`documents/ROADMAP.md`](documents/ROADMAP.md) — milestones M0–M2b are done (kernel core, CScript compiler and VM, the `Std.*` standard library); M3 (drivers and system libraries) is next.

## Build & test

Requires the .NET 8 SDK.

```bash
dotnet build Ghostrig.sln
dotnet test Ghostrig.sln

# mutation testing (Stryker.NET, pinned local tool)
dotnet tool restore
cd tests/Ghostrig.Kernel.Tests && dotnet stryker
```

## Layout

| Path | Contents |
|---|---|
| `src/Ghostrig.Kernel` | Deterministic simulation kernel: clock, hardware bay, cost model, telemetry, thermals, VFS, network, events, save/load |
| `src/Ghostrig.CScript` | CScript toolchain: lexer, parser, type checker, bytecode compiler, step-metered VM, Core and `Std.*` libraries (`Stdlib/*.csd` declarations + `Natives/`), test runner (`Testing/`) |
| `tests/Ghostrig.Kernel.Tests` | Kernel unit, property-based (FsCheck) and determinism-replay tests |
| `tests/Ghostrig.CScript.Tests` | Parser, type-checker, end-to-end VM and standard-library tests |
| `content/game/hardware.json` | Hardware catalog (single source of truth for component specs and cost constants) |
| `content/world/*.json` | World manifests: hosts, DNS, accounts, services and filesystems of the in-game internet |
