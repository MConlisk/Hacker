# GHOSTRIG — Development Roadmap

*Living document, updated with every milestone. Milestones are ordered by dependency; each ends with a concrete exit test. Design references point at Documents 1–7 in this folder.*

**Legend:** ✅ Done (exit criteria met and verified by tests in CI) · 🟡 In progress · ⏭ Next · ⬜ Not started

---

## Status at a glance

| Milestone | State | Where | Evidence |
|---|---|---|---|
| M0 Repository & toolchain | ✅ Done | `Ghostrig.sln`, `Directory.Build.props`, `.github/workflows/ci.yml` | CI green on every push and pull request |
| M1 Headless kernel core | ✅ Done | `src/Ghostrig.Kernel`, `content/` | 80 tests (`tests/Ghostrig.Kernel.Tests`) |
| M2 CScript language, compiler & VM | ✅ Done | `src/Ghostrig.CScript` | 201 tests (`tests/Ghostrig.CScript.Tests`) |
| M2b Standard library (`Std.*`) | ⏭ Next | `src/Ghostrig.CScript/Stdlib`, `Natives` | — |
| M3 Drivers & system libraries | ⬜ Not started | — | — |
| M4 MonoGame host shell | ⬜ Not started | — | — |
| M5 Core applications | ⬜ Not started | — | — |
| M6 Narrative runtime | ⬜ Not started | — | — |
| M7 Vertical slice: Act I | ⬜ Not started | — | — |
| M8 Act II | ⬜ Not started | — | — |
| M9 Act III | ⬜ Not started | — | — |
| M10 Act IV & endings | ⬜ Not started | — | — |
| M11 Game modes | ⬜ Not started | — | — |
| M12 CScript Portal | ⬜ Not started | — | — |
| M13 Polish & release | ⬜ Not started | — | — |

Merged pull requests: #1 (docs, M0, M1, CScript front end), #2 (type checker). Open: #3 (M2 bytecode compiler, VM, hot-swap).

## Guiding principles for the build

1. **Headless first.** Everything except presentation (kernel, CScript compiler/VM, VFS, network, narrative) is plain .NET 8 with no MonoGame dependency, so it can be unit-, property-, and replay-tested in CI (Doc 2 §5).
2. **Determinism is a test, not a hope.** Every milestone that touches simulation state adds replay tests: same seed + same inputs ⇒ bit-identical state hash (Doc 3 §2.3).
3. **Vertical slice early.** Act I playable end-to-end in Bare-Metal mode is the first major goal (M7); the other acts and modes are built on a proven core.
4. **Content is data.** Hardware, hosts, VFS trees, storylets, and dialogue live in `content/` and are validated at build time (Doc 4 §5).

## Solution layout

Entries marked ✅ exist today; the others are planned and named here so the layout is decided up front.

```text
src/
  Ghostrig.Kernel/        ✅ Simulation clock, cost model, hardware bay, thermals, VFS/ACL, network, DNS, event bus, save/load
  Ghostrig.CScript/       ✅ Lexer, parser, type checker, bytecode compiler, step-metered VM, Core natives, disassembler
  Ghostrig.SystemLibs/    ⬜ M3  Gfx, Input, Fs, Net, Proc, Log, Sys, Media, Shop, Account, CForms controls
  Ghostrig.Narrative/     ⬜ M6  Storylet engine, blackboard, listeners, dialogue player, graph validator
  Ghostrig.Portal/        ⬜ M12 CScript Portal sandbox + IProjectRepository (local store)
  Ghostrig.Host/          ⬜ M4  MonoGame DesktopGL shell: window manager, renderer, input, audio synthesis
content/
  game/hardware.json      ✅ Hardware catalog (Act I values tuned; later acts pending)
  world/                  ✅ Act I hosts (DNS resolver, Verbena & Rust); later acts pending
  system/                 ⬜ M3  Stock CScript apps (.csc) and drivers (.drv)
  narrative/              ⬜ M6  Storylets and dialogue beats (JSON)
tests/
  Ghostrig.Kernel.Tests/  ✅ xUnit + FsCheck
  Ghostrig.CScript.Tests/ ✅ xUnit + FsCheck, end-to-end VM tests
tools/
  Ghostrig.Validate/      ⬜ M6  Content + narrative graph validator (runs in CI)
```

---

## Phase 1 — Foundations

### M0. Repository & toolchain — ✅ Done
- .NET 8 solution, project skeletons, `Directory.Build.props` (nullable, warnings-as-errors, analyzers).
- Test projects (xUnit, FsCheck), GitHub Actions CI: build + test on every push.
- **Exit:** CI green on an empty-but-wired solution. ✅ Met — GitHub Actions builds and tests every push and pull request.

### M1. Headless kernel core — ✅ Done — *Doc 3 §1, §2.2–2.3, §3.2–3.3, §4, §5*
- Deterministic simulation clock (cycles/steps) decoupled from frame time; seeded deterministic RNG.
- Hardware catalog loader and validation (`hardware.json`); hardware bay with slots, power-down/reboot.
- Cost model implementing the $C_{total}$ formula; `DriverStats` telemetry (command, interrupt, cache efficiency); thermal model and thermal-cascade fault.
- VFS per `NetworkNode`: typed files (six types), ACL tiers (Guest/User/Operator/Root), `403` errors, stock-signing and restore.
- Network simulator: nodes, IP binding, DNS zones and `Resolve`, port daemons (HTTP 80, FTP 2121, mgmt 8443), sessions/credentials, structured access logs.
- Kernel event bus (`Fs.*`, `Net.*`, `Proc.*`, `Sys.*` events) for the narrative listeners.
- State hashing and save/load serialization.
- **Exit:** headless test suite proves that batched vs. unbatched submissions produce the expected cost ratios; DNS → connect → login → ACL denial/escalation works against a Verbena & Rust fixture; replay determinism holds.
  ✅ Met — `CostModelTests` (incl. the story's 4-hour vs. seconds dictionary read), `NetworkTests.Act_one_escalation_path_works_end_to_end`, `DeterminismTests` (state-hash replay and save/load round-trip).

## Phase 2 — The programmable machine

### M2. CScript language, compiler & VM — ✅ Done — *Doc 7, Doc 3 §2.1–2.2*
- ✅ Language specification: [Document 7](07-cscript-language.md), design decisions D1–D7 approved.
- ✅ Lexer, parser and AST with error recovery and stable `CSC1xxx`/`CSC2xxx` diagnostics.
- ✅ Type checker: symbols, generics with constraints and inference, overload resolution, lambdas and closures, `async`/`await`, iterators, patterns, null-safety with flow narrowing, capability rules (Doc 7 §10), `CSC3xxx`/`CSC4xxx` diagnostics.
- ✅ Bytecode compiler and linker (runtime classes, virtual dispatch, module initializers).
- ✅ Step-metered VM: fibers for `async`/`await` on simulated time, generators, exceptions with stack traces, per-event step budgets with an uncatchable `StepBudgetExceeded` fault and journal-based rollback.
- ✅ Hot-swap between events with `@Persist` globals and automatic revert; disassembler.
- ✅ `Core` library (Doc 7 §13.1): every native declared in `Stdlib/Core.csd` is implemented (link-checked by a test).
- **Exit:** compile and run CScript programs under step budgets; infinite loops are suspended, faults roll back, and hot-swap reverts on failed initialization; fuzz/property tests on the parser.
  ✅ Met — `VmTests` (budgets, rollback, async timing, determinism), `HotSwapAndDisassemblyTests`, `ParserTests` (FsCheck fuzzing), and a test that parses every module sample in Documents 5 and 7.
- Moved out of M2: the `Std.*` catalog (Doc 7 §13.2–13.14) → **M2b**; system libraries (§13.15–13.16) → **M3–M5**.

### M2b. Standard library — ⏭ Next — *Doc 7 §13.2–13.14*
- One native declaration file per namespace (`Std.Collections`, `Std.Seq`, `Std.Algo`, `Std.Text`, `Std.Math`, `Std.Stats`, `Std.Random`, `Std.Binary`, `Std.Compress`, `Std.Time`, `Std.Path`, `Std.Net`, `Std.Graph`, `Std.Data`, `Std.Game`, `Std.Test`) plus deterministic, step-charged host implementations.
- **Exit:** every declared native is implemented (link-checked in CI) and each namespace has behavioural tests run through the VM.

### M3. Drivers & system libraries — ⬜ Not started — *Doc 3 §3.1, Doc 5 §2*
- `IKernelDriver` + device-class interfaces; `Sys.Hardware` binding metered by the cost model.
- Stock flawed drivers for Act I (`LegacyGPUDriver.drv`, `NetCardDriver.drv`, `StorageController.drv`) and their reference fixes (used by tests and Street Ghost patches).
- System libraries `Fs`, `Net`, `Proc`, `Log`, `Sys`, `Media`, `Shop`, `Account` as capability-gated proxies.
- **Exit:** swapping a stock driver for its fixed version measurably changes cost, telemetry, and acoustics inputs, with identical functional output.

## Phase 3 — The desktop

### M4. MonoGame host shell — ⬜ Not started — *Doc 2 §1, Doc 3 §6*
- DesktopGL host, fixed frame loop, kernel ticking on its own clock.
- CForms controls (`Form`, `Panel`, `Label`, `Button`, `TextBox`, `ListView`, `Canvas`, `MediaView`), retained draw groups, `Gfx.MeasureText`.
- Window manager, taskbar, launcher; input routing.
- Procedural audio: fan, coil whine, platter chatter, and audio-artifact synthesis from `DriverStats` (Doc 3 §3.3 table).
- **Exit:** a CScript app opens a window, renders controls, reacts to input, and a flawed driver is audibly and visibly worse than its fix.

### M5. Core applications in CScript — ⬜ Not started — *Doc 2 §1*
- Notepad, Calc, Browser (+ search portal, `.cform` sandbox), Port Sniffer, Password Cracker, FTP Browser, Crawler, CScript IDE, System Profiler, My Rig, CommLink, Image Viewer, Audio Player, Video Player.
- **Exit:** every Act I investigation step can be performed manually with the real tools.

## Phase 4 — The story

### M6. Narrative runtime — ⬜ Not started — *Doc 4*
- Blackboard, storylet engine, armed listeners on the kernel event bus, atomic effects, dialogue player (CommLink video, dead-drop chat, scanner feed).
- Narrative Graph Validator (schema, action signatures vs. world manifests, reachability to the nine endings, mode parity) in CI.
- **Exit:** validator passes on the full Act I–IV skeleton graph; scripted test playthroughs reach each ending headlessly.

### M7. Vertical slice: Act I (Bare-Metal) — ⬜ Not started — *Doc 6 Act I*
- Verbena & Rust world content, Rook call, receipt freeze-frame, three driver bottlenecks, privilege escalation, BIO-ASSET-09 reveal.
- First mini-game (original vector asteroid shooter) running on `LegacyGPUDriver.drv`.
- **Exit:** a new player can complete Act I from boot without developer tools; playtest feedback round 1.

### M8. Act II — ⬜ Not started — *Doc 6 Act II*
- Vex and Silas calls, drop-node credentials, Silicon Alley Surplus shop, hardware upgrades (OptiLink NIC, DDR9), `OptiLinkDriver.drv`, Aegis gateway (8443), Crawler and dark subnet, faction fork listeners.
- Syndicate private networks (kompromat reachable organically).
- **Exit:** all three fork edges trigger from real actions; kompromat for both syndicates is obtainable without hints.

### M9. Act III (three paths) — ⬜ Not started — *Doc 6 Act III*
- Lotus, Iron, and Lone Wolf storylets; `AudioMixerDriver.drv`, `CryptoDriver.drv`, `ProxyRouterDriver.drv`; IronClad repository; additional mini-games; FCCHO oversight relay; live Vance trace with comply/defy/trace-complete listeners.
- **Exit:** every path reaches Act IV with every combination of leverage flags.

### M10. Act IV & endings — ⬜ Not started — *Doc 6 Act IV*
- Hardware Exchange (QuantumBus, Cryo-NVMe), `BusController.drv`/`NVMeQueue.drv`, Pier 44 breach under live load, thermal cascade, resolution order, all nine endings.
- **Exit:** full campaign playable start to finish in Bare-Metal mode.

## Phase 5 — Breadth

### M11. Game modes — ⬜ Not started — *Doc 1 §3, Doc 3 §3.4, Doc 4 §2*
- Silicon & Bus: expanded My Rig tuning, code-diff patches, driver synthesis.
- Street Ghost: reference-rig billing, CS-Vault drop-in patches, guided CommLink choices, Notepad objectives (including kompromat), no Kernel Panic.
- **Exit:** mode-parity validator passes; full playthrough in each mode.

### M12. CScript Portal — ⬜ Not started — *Doc 1 §5, Doc 3 §2.4*
- Main-menu sandbox kernel seeded from a progression snapshot; local `IProjectRepository`; in-game pull via Kael's personal CS-Vault repository.
- **Exit:** a driver written in the Portal can be pulled, compiled, and hot-swapped in the campaign on the live clock.

## Phase 6 — Ship

### M13. Polish & release — ⬜ Not started
- Visual direction pass (shaders: scanlines, federal crest override), sound design, accessibility (text scaling, colour-blind-safe profiler, subtitles for calls), performance budget, save robustness, packaging for Windows/Linux/macOS.
- **Exit:** release candidate.

### Post-launch
- Hosted cloud backend for the Portal repository (accounts, sharing community solutions).
- Additional mini-games and community driver challenges.

---

## Known gaps to close along the way

| Gap | Needed by | State |
|---|---|---|
| CScript Language Specification (Document 7) | M2 | ✅ Written and approved |
| Concrete `hardware.json` numbers and cost constants ($C_{arb}$, $C_{irq}$, $C_{mem}$, transfer multipliers) | M1, M7 | 🟡 Act I hardware tuned so the story's timings hold (tested); Act II–IV parts need tuning in M8–M10 |
| Full world manifests (every host, file, and credential) | M7–M10 | 🟡 Act I: DNS resolver and Verbena & Rust done; Aegis, syndicate and FCCHO hosts pending |
| Mini-game designs (asteroid shooter, circuit routing, signal decoding, grid defense) — original designs | M7–M9 | ⬜ |
| Visual and audio direction | M4, M13 | ⬜ |
| Save-file format and versioning | M1, M6 | 🟡 Kernel snapshot format v1 done; narrative state added in M6 |

## Deferred items and known limitations

Items consciously postponed while building; each has a target milestone so nothing is lost.

| Item | Found in | Target |
|---|---|---|
| Async iterators (`async Iterable<T>` with `yield`) report "not supported yet" at runtime | M2 | M2b |
| Runtime type tests ignore generic arguments (`List<int>` and `List<string>` test the same) | M2 | Documented limitation; revisit if gameplay needs it |
| VM step charges go to a stand-in clock; billing CScript steps as CPU cycles through the kernel (heat, simulated time) | M2 | M3 |
| Per-module hot-swap of a single driver (today the whole program image is swapped) | M2 | M3 |
| Rollback covers writes by the faulting fiber only; writes by async calls it started are not undone | M2 | M3 |
| Faults of tasks nobody awaits are reported as soon as they fail, rather than when the task is collected | M2 | M3 |
| `Net`/`Fs` transfers billed through CScript drivers | M1 | M3 |
| Stryker.NET mutation testing wired into CI | M1 | M2b |
| Doc 7 examples that use system libraries (`Net`, `CForms`) are parsed in tests but not yet type-checked | M2 | M3–M5 |

## Testing strategy (applies to every milestone)

- **Unit + property-based** (xUnit + FsCheck): parser round-trips, ACL invariants, cost-model monotonicity (batching never costs more than unbatched), DNS consistency.
- **Determinism replay:** recorded input streams replayed in CI must reproduce the same state hash.
- **Mutation testing** (Stryker.NET) on Kernel and CScript to keep the test suite honest.
- **Content validation:** hardware catalog, world manifests, and narrative graph validated in CI.
- **Headless playthroughs:** scripted runs to each ending once M6+ lands.
