# GHOSTRIG — Development Roadmap

*Living document. Milestones are ordered by dependency; each ends with a concrete exit test. Design references point at Documents 1–6 in this folder.*

---

## Status

| Milestone | State | Notes |
|---|---|---|
| M0 Repository & toolchain | ✅ Done | .NET 8 solution, strict build settings, xUnit + FsCheck, GitHub Actions CI. |
| M1 Headless kernel core | ✅ Done | `src/Ghostrig.Kernel`, 80 passing tests. Carried forward: per-process step budgets and the scheduler move into M2 with the VM; the `Net`/`Fs` libraries billing transfers through drivers is M3; Stryker.NET mutation runs are not yet wired into CI. |
| M2 CScript language | ⏭ Next | Starts with the CScript Language Specification (Document 7). |

## Guiding principles for the build

1. **Headless first.** Everything except presentation (kernel, CScript compiler/VM, VFS, network, narrative) is plain .NET 8 with no MonoGame dependency, so it can be unit-, property-, and replay-tested in CI (Doc 2 §5).
2. **Determinism is a test, not a hope.** Every milestone that touches simulation state adds replay tests: same seed + same inputs ⇒ bit-identical state hash (Doc 3 §2.3).
3. **Vertical slice early.** Act I playable end-to-end in Bare-Metal mode is the first major goal (M7); the other acts and modes are built on a proven core.
4. **Content is data.** Hardware, hosts, VFS trees, storylets, and dialogue live in `content/` and are validated at build time (Doc 4 §5).

## Solution layout

```text
src/
  Ghostrig.Kernel/        Simulation clock, scheduler, cost model, hardware bay, VFS/ACL, network, DNS, event bus
  Ghostrig.CScript/       Lexer, parser, type checker, bytecode compiler, step-metered VM
  Ghostrig.SystemLibs/    Gfx, Input, Fs, Net, Proc, Log, Sys, Media, Shop, Account, CForms controls
  Ghostrig.Narrative/     Storylet engine, blackboard, listeners, dialogue player, graph validator
  Ghostrig.Portal/        CScript Portal sandbox + IProjectRepository (local store)
  Ghostrig.Host/          MonoGame DesktopGL shell: window manager, renderer, input, audio synthesis
content/
  game/hardware.json      Hardware catalog
  world/                  Hosts, DNS zones, VFS manifests, file contents
  system/                 Stock CScript apps (.csc) and drivers (.drv)
  narrative/              Storylets and dialogue beats (JSON)
tests/
  *.Tests                 xUnit + FsCheck (property-based); Stryker.NET mutation runs on core libraries
tools/
  Ghostrig.Validate/      Content + narrative graph validator (runs in CI)
```

---

## Phase 1 — Foundations

### M0. Repository & toolchain
- .NET 8 solution, project skeletons, `Directory.Build.props` (nullable, warnings-as-errors, analyzers).
- Test projects (xUnit, FsCheck), GitHub Actions CI: build + test on every push.
- **Exit:** CI green on an empty-but-wired solution.

### M1. Headless kernel core — *Doc 3 §1, §2.2–2.3, §3.2–3.3, §4, §5*
- Deterministic simulation clock (cycles/steps) decoupled from frame time; seeded deterministic RNG.
- Hardware catalog loader and validation (`hardware.json`); hardware bay with slots, power-down/reboot.
- Cost model implementing the $C_{total}$ formula; `DriverStats` telemetry (command, interrupt, cache efficiency); thermal model and thermal-cascade fault.
- VFS per `NetworkNode`: typed files (six types), ACL tiers (Guest/User/Operator/Root), `403` errors, stock-signing and restore.
- Network simulator: nodes, IP binding, DNS zones and `Resolve`, port daemons (HTTP 80, FTP 2121, mgmt 8443), sessions/credentials, structured access logs.
- Kernel event bus (`Fs.*`, `Net.*`, `Proc.*`, `Sys.*` events) for the narrative listeners.
- State hashing and save/load serialization.
- **Exit:** headless test suite proves that batched vs. unbatched submissions produce the expected cost ratios; DNS → connect → login → ACL denial/escalation works against a Verbena & Rust fixture; replay determinism holds.

## Phase 2 — The programmable machine

### M2. CScript language — *Doc 3 §2.1–2.2*
- **Prerequisite:** write the CScript Language Specification (grammar, types, modules, attributes like `@Driver`, imports, syscall surface). *This document does not exist yet and is the first gap to close.*
- Lexer, parser, AST, static type and capability checks, structured diagnostics.
- Bytecode compiler and step-metered VM; per-event step budgets; process suspension.
- Copy-on-write rollback snapshots; atomic hot-swap with automatic revert.
- **Exit:** compile and run CScript programs under step budgets; infinite loops are suspended, faults roll back, and hot-swap reverts on failed initialization; fuzz/property tests on the parser.

### M3. Drivers & system libraries — *Doc 3 §3.1, Doc 5 §2*
- `IKernelDriver` + device-class interfaces; `Sys.Hardware` binding metered by the cost model.
- Stock flawed drivers for Act I (`LegacyGPUDriver.drv`, `NetCardDriver.drv`, `StorageController.drv`) and their reference fixes (used by tests and Street Ghost patches).
- System libraries `Fs`, `Net`, `Proc`, `Log`, `Sys`, `Media`, `Shop`, `Account` as capability-gated proxies.
- **Exit:** swapping a stock driver for its fixed version measurably changes cost, telemetry, and acoustics inputs, with identical functional output.

## Phase 3 — The desktop

### M4. MonoGame host shell — *Doc 2 §1, Doc 3 §6*
- DesktopGL host, fixed frame loop, kernel ticking on its own clock.
- CForms controls (`Form`, `Panel`, `Label`, `Button`, `TextBox`, `ListView`, `Canvas`, `MediaView`), retained draw groups, `Gfx.MeasureText`.
- Window manager, taskbar, launcher; input routing.
- Procedural audio: fan, coil whine, platter chatter, and audio-artifact synthesis from `DriverStats` (Doc 3 §3.3 table).
- **Exit:** a CScript app opens a window, renders controls, reacts to input, and a flawed driver is audibly and visibly worse than its fix.

### M5. Core applications in CScript — *Doc 2 §1*
- Notepad, Calc, Browser (+ search portal, `.cform` sandbox), Port Sniffer, Password Cracker, FTP Browser, Crawler, CScript IDE, System Profiler, My Rig, CommLink, Image Viewer, Audio Player, Video Player.
- **Exit:** every Act I investigation step can be performed manually with the real tools.

## Phase 4 — The story

### M6. Narrative runtime — *Doc 4*
- Blackboard, storylet engine, armed listeners on the kernel event bus, atomic effects, dialogue player (CommLink video, dead-drop chat, scanner feed).
- Narrative Graph Validator (schema, action signatures vs. world manifests, reachability to the nine endings, mode parity) in CI.
- **Exit:** validator passes on the full Act I–IV skeleton graph; scripted test playthroughs reach each ending headlessly.

### M7. Vertical slice: Act I (Bare-Metal) — *Doc 6 Act I*
- Verbena & Rust world content, Rook call, receipt freeze-frame, three driver bottlenecks, privilege escalation, BIO-ASSET-09 reveal.
- First mini-game (original vector asteroid shooter) running on `LegacyGPUDriver.drv`.
- **Exit:** a new player can complete Act I from boot without developer tools; playtest feedback round 1.

### M8. Act II — *Doc 6 Act II*
- Vex and Silas calls, drop-node credentials, Silicon Alley Surplus shop, hardware upgrades (OptiLink NIC, DDR9), `OptiLinkDriver.drv`, Aegis gateway (8443), Crawler and dark subnet, faction fork listeners.
- Syndicate private networks (kompromat reachable organically).
- **Exit:** all three fork edges trigger from real actions; kompromat for both syndicates is obtainable without hints.

### M9. Act III (three paths) — *Doc 6 Act III*
- Lotus, Iron, and Lone Wolf storylets; `AudioMixerDriver.drv`, `CryptoDriver.drv`, `ProxyRouterDriver.drv`; IronClad repository; additional mini-games; FCCHO oversight relay; live Vance trace with comply/defy/trace-complete listeners.
- **Exit:** every path reaches Act IV with every combination of leverage flags.

### M10. Act IV & endings — *Doc 6 Act IV*
- Hardware Exchange (QuantumBus, Cryo-NVMe), `BusController.drv`/`NVMeQueue.drv`, Pier 44 breach under live load, thermal cascade, resolution order, all nine endings.
- **Exit:** full campaign playable start to finish in Bare-Metal mode.

## Phase 5 — Breadth

### M11. Game modes — *Doc 1 §3, Doc 3 §3.4, Doc 4 §2*
- Silicon & Bus: expanded My Rig tuning, code-diff patches, driver synthesis.
- Street Ghost: reference-rig billing, CS-Vault drop-in patches, guided CommLink choices, Notepad objectives (including kompromat), no Kernel Panic.
- **Exit:** mode-parity validator passes; full playthrough in each mode.

### M12. CScript Portal — *Doc 1 §5, Doc 3 §2.4*
- Main-menu sandbox kernel seeded from a progression snapshot; local `IProjectRepository`; in-game pull via Kael's personal CS-Vault repository.
- **Exit:** a driver written in the Portal can be pulled, compiled, and hot-swapped in the campaign on the live clock.

## Phase 6 — Ship

### M13. Polish & release
- Visual direction pass (shaders: scanlines, federal crest override), sound design, accessibility (text scaling, colour-blind-safe profiler, subtitles for calls), performance budget, save robustness, packaging for Windows/Linux/macOS.
- **Exit:** release candidate.

### Post-launch
- Hosted cloud backend for the Portal repository (accounts, sharing community solutions).
- Additional mini-games and community driver challenges.

---

## Known gaps to close along the way

| Gap | Needed by |
|---|---|
| CScript Language Specification (new Document 7) | M2 |
| Concrete `hardware.json` numbers and the cost constants ($C_{arb}$, $C_{irq}$, $C_{mem}$, transfer multipliers) tuned so the story's timings hold (e.g., the 4-hour vs. seconds crack) | M1 tuning, M7 |
| Full world manifests (every host, file, and credential) for Acts II–IV | M8–M10 |
| Mini-game designs (asteroid shooter, circuit routing, signal decoding, grid defense) — original designs | M7–M9 |
| Visual and audio direction | M4, M13 |
| Save-file format and versioning | M1 (format), M6 (narrative state) |

## Testing strategy (applies to every milestone)

- **Unit + property-based** (xUnit + FsCheck): parser round-trips, ACL invariants, cost-model monotonicity (batching never costs more than unbatched), DNS consistency.
- **Determinism replay:** recorded input streams replayed in CI must reproduce the same state hash.
- **Mutation testing** (Stryker.NET) on Kernel and CScript to keep the test suite honest.
- **Content validation:** hardware catalog, world manifests, and narrative graph validated in CI.
- **Headless playthroughs:** scripted runs to each ending once M6+ lands.
