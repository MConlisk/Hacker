# Hacker
A cyberpunk hacking simulator: **GHOSTRIG**.

Design documents live in [`documents/`](documents/README.md); the build plan is in [`documents/ROADMAP.md`](documents/ROADMAP.md).

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
| `tests/Ghostrig.Kernel.Tests` | Unit, property-based (FsCheck) and determinism-replay tests |
| `content/game/hardware.json` | Hardware catalog (single source of truth for component specs and cost constants) |
| `content/world/*.json` | World manifests: hosts, DNS, accounts, services and filesystems of the in-game internet |
