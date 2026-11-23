# Hacker
A cyberpunk hacking simulator: **GHOSTRIG**.

Design documents live in [`documents/`](documents/README.md). Progress and the plan ahead are in [`documents/ROADMAP.md`](documents/ROADMAP.md) — milestones M0–M2 are done, M2b (standard library) is next.

## Build & test

Requires the .NET 8 SDK.

```bash
dotnet build Ghostrig.sln
dotnet test Ghostrig.sln
```

## Layout

| Path | Contents |
|---|---|
| `src/Ghostrig.Kernel` | Deterministic simulation kernel: clock, hardware bay, cost model, telemetry, thermals, VFS, network, events, save/load |
| `src/Ghostrig.CScript` | CScript toolchain: lexer, parser, type checker, bytecode compiler, step-metered VM, Core library (`Stdlib/*.csd` declarations + `Natives/`) |
| `tests/Ghostrig.Kernel.Tests` | Kernel unit, property-based (FsCheck) and determinism-replay tests |
| `tests/Ghostrig.CScript.Tests` | Parser, type-checker and end-to-end VM tests |
| `content/game/hardware.json` | Hardware catalog (single source of truth for component specs and cost constants) |
| `content/world/*.json` | World manifests: hosts, DNS, accounts, services and filesystems of the in-game internet |
