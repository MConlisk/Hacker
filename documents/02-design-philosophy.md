# GHOSTRIG — Design Philosophy & Engineering Consistencies

*Core Architecture & Runtime Specification — Document 2 of 7*

---

## 1. Architectural Boundary: C# Core vs. CScript Userland & Drivers

The codebase enforces a strict separation between the host operating-system core and the inspectable, modifiable software executing above it. Every layer has an unambiguous responsibility and language boundary.

### Kernel, Desktop Shell & System Libraries (C#)

- **Kernel (`Ghostrig.Kernel`):** The sole authority on simulated hardware state, capability enforcement, step scheduling, and deterministic resource billing.
- **Desktop Shell:** The window manager, taskbar, application launcher, audio synthesizer output, and host frame presenter are implemented in C# to ensure a responsive, stable host interface.
- **System Libraries & CForms Primitives:** Core OS services (`Gfx`, `Input`, `Fs`, `Net`, `Proc`, `Log`, `Sys`, `Media`, `Shop`, `Account`) and UI control primitives (`Form`, `Panel`, `Label`, `Button`, `TextBox`, `ListView`, `Canvas`, `MediaView`, plus the extended controls catalogued in Document 7 §13.16) are written in C#. They form the system-call boundary: CScript code invokes these libraries to render controls, access files, decode media, and transmit packets, and every call routes through the kernel to incur deterministic hardware costs.

### Applications, Websites & Drivers (CScript)

- **Applications (`*.csc`):** Every user-facing tool is written in CScript: Browser, Port Sniffer, Password Cracker, FTP Browser, Crawler, Notepad, Calc, Image Viewer, Audio Player, Video Player, CommLink, My Rig, CScript IDE, System Profiler, and the built-in mini-games.
- **Websites & CForms UI Trees (`*.cform`):** Every website and application interface is authored as CScript forms code using a WinForms-like control hierarchy (CForms), instantiating the standard C# UI controls rather than drawing custom widgets or bypassing the library layer. A `.cform` file is CScript source, comparable to a WinForms designer file.
- **Device Drivers (`/sys/drivers/**/*.drv`):** All hardware drivers are written in CScript and carry the `.drv` extension. Players can inspect, modify, compile, and hot-swap both applications and drivers at runtime.

The `.csc`, `.cform`, and `.drv` extensions keep in-game CScript visibly distinct from the real C# engine source (`.cs`).

## 2. Deterministic Execution & Authentic Hardware Constraints

- **Strict Determinism (No Stochastic Shortcuts):** Every simulation tick, instruction step, bus transfer, and network exchange must be 100% deterministic. Random number generators or arbitrary wall-clock timers must never be used to fake processing delays, packet jitter, or credential crack times. Where variation is desired, it comes from seeded, save-serialized deterministic generators.
- **Driver-Driven Step Delays (No Fake Timers):** Performance bottlenecks must never be implemented via `sleep()` calls, artificial timers, or hard-coded wait loops. When a CScript application invokes a C# system library, the kernel queries the active CScript driver's configuration and execution path to calculate the exact simulated step delay and resource cost (CPU cycles, bus arbitration overhead, DMA channel contention, cache misses, and RAM allocation).
- **Structural Driver Flaws Over Artificial Sabotage:** Stock or aftermarket drivers must perform their functional task accurately (packets arrive intact, polygons render, hashes compute, media decodes) while suffering from realistic systems-engineering anti-patterns:
  - Unbatched single-item submission loops (`DrawSingle`, `SendPacket`, single DMA descriptors) instead of ring-buffer or batch transfers.
  - CPU-polled byte-by-byte I/O (`TransferMode.CPU`) instead of block-level `TransferMode.DMA`.
  - Cache thrashing or premature buffer flushing (`FlushBuffer()`, `keyCache.Clear()`) instead of LRU caching or key-schedule reuse.
- **Hardware-Agnostic Application Code:** Applications and CForms UIs never query installed hardware specifications to scale their execution speed. A `TextBox` redraws slowly or a Port Sniffer crawls on low-tier silicon strictly because its C# library calls are billed against the player's active CPU, bus, and driver pipeline.
- **Dual-Clock Decoupling:** Simulated execution time (step budgets and driver delays) is strictly decoupled from the host machine's rendering frame rate. A sluggish simulated rig produces authentic in-universe latency, fan whine, and disk chatter without causing frame drops on the player's real PC.
- **The Clock Never Pauses:** During play, the simulation clock keeps running while the player codes, browses, or takes calls — including inside the in-game CScript IDE. Unhurried development happens in the out-of-world **CScript Portal** (main menu), which runs a separate sandbox kernel and never advances the campaign clock (see Document 1, §5).

## 3. Unified Network, DNS & Permissioned Filesystem Model

- **No Scripted "Fake" Targets:** Every website and remote host in the game world is a live, simulated node accessible through the exact same network stack and CScript hacking tools (Browser, Port Sniffer, FTP Browser, Crawler).
- **Domain Name Service (DNS) Resolution:** Applications never resolve domains via hard-coded string matching. All domain lookups (such as `verbena-and-rust.ny56`) must query a simulated DNS service to resolve hostnames into routable IP addresses before establishing connections.
- **Per-Server Filesystems & Tiered Permissions:** Every remote server and web host maintains its own virtual filesystem with explicit Access Control Lists (ACLs) and multi-level user permissions (Guest, User, Operator, Root). Players must harvest lower-tier credentials first, enumerate readable files and configurations, and escalate privileges before accessing restricted directories or kompromat archives.
- **Persistent Access Logs & Pivot Trails:** Web servers and internal gateways record structured authentication and access logs containing timestamps, usernames, and source IP addresses. Auditing compromised logs provides players with actionable investigative pivots — exposing internal admin IPs, relay nodes, reused credentials, and upstream corporate subnets.

## 4. Engineering Best Practices & Design Patterns

To keep the codebase maintainable, deterministic, and extensible across both C# and CScript layers, contributors must adhere to established software-engineering best practices and standardized design patterns.

| Pattern / Practice | Target Layer | Architectural Application in GHOSTRIG |
|---|---|---|
| Command & Queue | Kernel, C# Libraries & CScript Drivers | Hardware operations (GPU draws, NIC packet sends, disk block reads, media decodes) are encapsulated as discrete command descriptors queued for batch execution, directly modeling driver coalescing efficiency. |
| Strategy | CScript Drivers & Game Modes | Transfer modes (`TransferMode.CPU` vs. `TransferMode.DMA`), caching policies (unbuffered vs. LRU), and difficulty billing profiles swap behavior cleanly without conditional bloat in kernel code. |
| Composite & Observer | CForms UI & Desktop Shell | WinForms-like CForms interfaces compose nested control trees (Form → Panel → TextBox) with retained invalidation flags and typed event delegates (`Click`, `TextChanged`, `KeyDown`). |
| Proxy & Capability Gate | C# System Libraries | Every CScript system call passes through a capability-enforcing proxy that validates permissions, computes driver-dependent step costs, and bills the active process budget. |
| Memento / Snapshot Rollback | CScript Interpreter & Kernel | Per-event state snapshots enable atomic rollbacks when a player-modified driver or script exceeds step limits or throws a runtime fault, preventing host crashes. |
| Facade & Repository | Virtual Filesystem, DNS, Network Stack & Portal Cloud Repository | Unified C# storage and network facades abstract per-host ACLs, log appending, DNS record resolution, and the Portal's cloud project store behind clean, testable interfaces. |

*Table 1: Core software design patterns enforced across the GHOSTRIG runtime and userland.*

### 4.1 Code Quality & Discipline Consistencies

- **SOLID Principles & Single Source of Truth:** Kernel subsystems, hardware definitions (`content/game/hardware.json`), and network topology schemas maintain a single authoritative definition. Shared data structures are never duplicated across C# and CScript layers.
- **Complete, Production-Grade Implementations:** All C# kernel/library code and CScript game/driver files must be complete, self-contained implementations — never stubs, mock placeholders, or truncated logic.
- **Explicit Error Handling (No Silent Fallbacks):** Faults — including CScript syntax/runtime exceptions, ACL permission denials, unresolved DNS records, unsupported media, and step-budget overruns — must surface authentic OS exceptions, System Profiler warnings, or protocol error codes rather than silently falling back to a default success state.
- **Non-Destructive Recovery & Mode Parity:** Every stock driver and system script retains a signed baseline copy for instant restoration. All three game modes (Bare-Metal, Silicon & Bus, and Street Ghost) and the CScript Portal execute on the exact same C# kernel, CForms library layer, and CScript codebase.

## 5. Host Technology

- **Runtime:** .NET 8, C#.
- **Host framework:** MonoGame (DesktopGL) provides the window, frame loop, input, GPU presentation, and real-time audio output (used to synthesize fan, coil-whine, and platter-chatter acoustics from kernel telemetry).
- **Separation:** MonoGame is confined to the host presentation layer. The kernel, CScript compiler/VM, VFS, network simulator, and narrative runtime are plain .NET libraries with no MonoGame dependency, so they can be tested headlessly.
