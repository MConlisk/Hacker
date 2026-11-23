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
| M2b Standard library (`Std.*`) | 🟡 In progress | `src/Ghostrig.CScript/Stdlib/Std.*.csd`, `Natives/Std` | 93 tests (`tests/Ghostrig.CScript.Tests/Std`); all 16 `Std.*` namespaces done; async iterators and Stryker remain |
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

Merged pull requests: #1 (docs, M0, M1, CScript front end), #2 (type checker), #3 (M2 bytecode compiler, VM, hot-swap). M2b work continues on branch `ccr-b7b99d06-09w97q`.

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
                          🟡 M2b Std.* library (Stdlib/*.csd declarations, Natives/Std implementations)
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

### M2b. Standard library — 🟡 In progress — *Doc 7 §13.2–13.14*
- One native declaration file per namespace (`Stdlib/Std.*.csd`, the contract players read in the IDE API browser, with published step costs) plus deterministic, step-charged host implementations (`Natives/Std`).
- ✅ Infrastructure: native classes in any module, `@Native("key")` for same-arity overloads, `NativeState` snapshots so faulted events roll back collections, caches and generators like fields and arrays; natives that rewrite script arrays journal them too; fail-fast iteration on concurrent modification; one `ValueComparer` (structural for values, `HashCode`/`Equatable`/`Comparable` for classes, identity otherwise) used by every collection.
- ✅ `Std.Collections`: `List`, `Map` (insertion-ordered), `SortedMap`, `Set`, `SortedSet`, `Queue`, `Stack`, `Deque`, `PriorityQueue` (stable), `RingBuffer`, `BitSet`, `Grid2D`, `Matrix`, `Multimap`, read-only views; caches `LruCache`, `LfuCache`, `TtlCache` (simulated clock) with hit/miss/eviction counters; `ObjectPool`, `BloomFilter`, `CountMinSketch`, `HyperLogLog`, `Trie`, `IntervalTree`, `SkipList`, `ConsistentHashRing`.
- ✅ `Std.Seq`: lazy, re-iterable `Sequence<T>` (fluent and functional forms), ordering with `ThenBy`, grouping, joins, windows, folds.
- ✅ `Std.Algo`: stable sorts, searches, partition, top-k, lazy combinatorics, edit distances, hashing helpers (FNV-1a, Murmur3, SplitMix).
- ✅ `Std.Random`: xoshiro256\*\* seeded via SplitMix64 (reference stream verified), unbiased bounded draws, sampling, weights, Gaussian, UUIDs, jump-based `Fork`.
- ✅ Deterministic software maths core (`SoftMath`, Doc 7 D7): fdlibm exp/log/trig/atan with exact Payne–Hanek reduction for huge arguments (2/π computed with Machin's formula at start-up), checked against the platform by property tests.
- ✅ `Std.Math`: int/float functions on `SoftMath`, number theory (deterministic Miller–Rabin, Pollard–Brent factorisation, modular arithmetic, `Checked*`), `BigInt`, `Rational`, `Complex`, `Fixed` (Q32.32), `Vector2`/`Vector3`, `Matrix3x2`/`Matrix4x4`, `Rect`, `Circle`, `Polygon`, segment/hull helpers. Native value types compare by value (`==`, `Equals`, collection keys) through `IStructuralState`.
- ✅ `Std.Stats`: mean/median/mode/variance/percentiles (compensated summation), `Describe`, histograms, correlation, least-squares regression, moving averages, z-score and IQR outliers, streaming `RunningStats`.
- ✅ `Std.Text`: `StringBuilder`, `Format` (bytes, durations, tables…), linear-time `Regex` (no catastrophic backtracking; captures and named groups), `Encoding` (UTF-8/ASCII/Latin-1, hex, Base64/Base64Url/Base32, URL, HTML, ROT13/Caesar, XOR), RFC 4180 `Csv` with typed columns, immutable `JsonValue` (strict parser with positions, path queries, `Serialize` of collections and `Serializable` objects), `Cfg`, `Markdown` text helpers, `Template`, Myers `Diff` with unified patch create/apply, `Tokenizer` + `TokenStream`, `Search` (glob, wildcard, fuzzy ranking, grep).
- ✅ `Std.Binary`: `ByteBuffer`, endian-aware `BinaryReader`/`BinaryWriter` (varints, length-prefixed strings; truncation raises `ProtocolError`), `Bits`, Python-style `Struct.Pack`/`Unpack`, `Checksum` (CRC-32/16, Adler-32, Fletcher-16, Internet), `HexDump`.
- ✅ `Std.Compress`: deterministic encoders written in-house — `Rle`, `Lz77`, `Deflate` (standard raw DEFLATE, verified against the platform inflater), canonical `Huffman`; decoders are bounded at 64 MB.
- ✅ `Std.Time`: `DateTime` on the world calendar (epoch Fri 17 Nov 2056 23:42), ISO parsing, whitelist formatting, `Time.*` instant/duration helpers, simulated-time `Stopwatch`, five-field `Cron` schedules.
- ✅ `Std.Path`: the kernel's VFS path rules (absolute, `..` never escapes the root) for normalise/join/relative/glob.
- ✅ `Std.Net` (pure): `IpAddress`, `Cidr` (lazy host enumeration, subnetting), `Port` service table (incl. 2121 and 8443), `Hostname`, `MacAddress`, `Url`, `HttpHeaders`, and IPv4/TCP/UDP/ICMP packet builders with correct checksums plus an IPv4 parser.
- ✅ `Std.Graph`: `Graph<T>`/`WeightedGraph<T>` with BFS/DFS, Dijkstra, A*, Floyd–Warshall, topological sort, weak and strong components, cycle enumeration, Kruskal MST, degree and Brandes betweenness centrality, DOT export.
- ✅ `Std.Data`: immutable `Table` with CSV/JSON import and export, `Where`/`Select`/`WithColumn`/`OrderBy`/`Join`/`LeftJoin`/`GroupBy`/`Pivot`/aggregates.
- ✅ `Std.Game` (simulation side): fixed-step `GameLoop`, `Easing`/`Tween`, `Color` and palettes, collision with penetration and swept AABB, `SpatialHash`, `Body`, `StateMachine`, grid `Pathfinding`, `Camera2D`, `TileMap`, seeded `ParticleSystem`, `InputMap`.
- ✅ `Std.Test`: `Assert` (raises `AssertionFailed`, code `EASSERT`), generators and `Property.ForAll` with shrinking, step-count `Bench`, `Debug` (trace log, breakpoints, `Inspect`, `Profile`); host-side `Testing.TestRunner` discovers `@Test`/`@TestCase`/`@Setup`/`@Teardown` and runs each test in a fresh VM.
- ⬜ Deferred into M2b from M2: async iterators; Stryker.NET mutation testing in CI.
- **Exit:** every declared native is implemented (link-checked in CI) and each namespace has behavioural tests run through the VM.
  🟡 Link check in place (`StdLibraryTests`: no missing and no orphan natives, library compiles without diagnostics); behavioural tests through the VM for all 16 namespaces.

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
| Doc 7 examples that use system libraries (`Net`, `CForms`, `Sys.Hardware`) are parsed in tests but not yet type-checked (the `Std`-only sample §16.3 now compiles and runs) | M2 | M3–M5 |
| `foreach` cannot deconstruct tuples (`foreach (var (k, v) in map)`); use `entry.Item1` / `entry.Item2` | M2b | Language polish, before M5 (IDE) |
| Generic arguments are erased at run time, so `Map<string, int>` and `Map<int, int>` share a runtime class (consistent with the M2 type-test limitation) | M2b | Documented limitation |
| `==` on *user* classes implementing `Equatable<T>` still compares references (Doc 7 §6 says it should call `Equals`); collections and native value types already honour equality | M2b | Language polish, before M5 |
| `Markdown` renders to plain text only; rendering to CForms rich text needs the CForms controls | M2b | M4 |
| `JsonValue` cannot deserialize into a typed object (`Deserialize<T>`): generic arguments are erased at run time | M2b | Revisit with runtime generics, or a schema-driven `Json.Bind(obj)` in M5 |
| `Std.Text` ships a `Tokenizer` + `TokenStream` for recursive-descent parsers instead of parser combinators (Doc 7 §13.4 names both) | M2b | Combinators if a puzzle needs them (M8–M9) |
| Regex numbering follows .NET (unnamed groups first, then named); backreferences and lookaround are rejected by design (linear-time matching) | M2b | Documented in the API |
| `Std.Game` drawing types (`Sprite`, `SpriteSheet`) and wiring `GameLoop` to `Gfx.OnFrame` need the Gfx library | M2b | M4 |
| `Assert.Throws<T>` is `Assert.Throws(action, "ClassName")`: generic arguments are erased at run time | M2b | Documented in the API |
| `Std.Net` is IPv4-only (every NY-56 network is IPv4) | M2b | Revisit only if a story beat needs IPv6 |
| `Std.Path` re-implements the kernel's `VfsPath` rules because the CScript assembly does not reference the kernel; the two must stay in step | M2b | Shared conformance test when M3 binds `Fs` |
| Rollback snapshots a whole native collection the first time an event mutates it (O(n) host time, never charged as steps); per-operation undo would be cheaper for very large collections | M2b | Revisit with M3 profiling |

## Testing strategy (applies to every milestone)

- **Unit + property-based** (xUnit + FsCheck): parser round-trips, ACL invariants, cost-model monotonicity (batching never costs more than unbatched), DNS consistency.
- **Determinism replay:** recorded input streams replayed in CI must reproduce the same state hash.
- **Mutation testing** (Stryker.NET) on Kernel and CScript to keep the test suite honest.
- **Content validation:** hardware catalog, world manifests, and narrative graph validated in CI.
- **Headless playthroughs:** scripted runs to each ending once M6+ lands.
