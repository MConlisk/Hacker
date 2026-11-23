# GHOSTRIG — Game Description & Product Vision

*Product & Systems Design Specification — Document 1 of 7*

---

## 1. High-Level Concept

**GHOSTRIG** is a narrative-driven, bare-metal systems-hacking and cyberpunk thriller simulation. Set in a flooded, mid-21st-century New York City, players assume the role of **Kael**, a sub-level technician operating a salvaged black-market terminal running **GHOSTRIG** — the same name covers the game, Kael's terminal, and the operating system it runs — to track down his missing sister, **Wren**.

Unlike traditional hacking games that rely on abstract mini-games or canned puzzle scripts, GHOSTRIG simulates a deterministic, end-to-end computer architecture. The core kernel, desktop shell, and system libraries are engineered in C#, while every user-space application, WinForms-style CForms interface, website, and hardware driver is written in **CScript** — an inspectable, modifiable scripting language. Survival and progression depend on diagnosing hardware bottlenecks, optimizing components, infiltrating tiered server filesystems, and securing leverage under mounting faction and federal pressure.

## 2. Setting & Narrative Premise

### 2.1 The World

In 2056, rising sea levels have submerged lower Manhattan behind massive concrete seawalls. The wealthy reside in climate-controlled corporate enclaves above the waterline, while flood-zone residents survive in damp, sub-level apartments powered by unstable grids and salvaged silicon.

### 2.2 The Hook

Kael's 22-year-old sister, Wren — a street-level social engineer — vanishes after taking what was supposed to be a routine data-extraction job for the Crimson Lotus syndicate. Her final clue leads Kael through a shell storefront (**Verbena & Rust**) and into the dark subnets of **Aegis Logistics**, an orbital-to-surface freight subsidiary of the **OmniCorp** conglomerate, operating an off-the-books neural-harvesting ("wetware") pipeline beneath Battery Park. Aegis's Battery Park facility is run by **Director Marcus Hale**.

### 2.3 Factions, Leverage & Endings

Across four acts, Kael is caught between competing powers:

- **The Crimson Lotus (Vex):** Manhattan waterfront smugglers who originally contracted Wren.
- **The Iron Syndicate (Silas):** Brooklyn-based industrial rivals offering heavy exploit suites in exchange for intelligence.
- **The FCCHO (Special Agent Vance):** Federal Cyber-Crimes & Homeland Oversight authorities running a long-term sting on Aegis Logistics.

**Mandatory Leverage & Mutually Assured Destruction.** Siding with any power is never safe based on trust alone. To survive a deal, the player must infiltrate the private network of whoever they are dealing with and extract irrefutable audiovisual proof (**kompromat**) that the party would kill to keep hidden. Kompromat is never handed to the player and never scripted into a cut-scene: in Bare-Metal and Silicon & Bus modes the player must discover on their own that it exists, where it lives, and how to get it. Only Street Ghost mode surfaces it as a guided objective.

- **Single-Faction Alliance (Crimson Lotus or Iron Syndicate):** Finishing the game for a syndicate without holding that syndicate's kompromat ends in **betrayal**: the faction kills Kael, and Wren remains Aegis cargo. Holding it forces the faction to honor Wren's extraction and leave Kael alive.
- **Lone Wolf / Faction War:** To ignite an open waterfront war between the Crimson Lotus and the Iron Syndicate, Kael must breach both syndicates' networks and use their kompromat to frame them against each other.
- **Special Agent Vance (every path):** Whether Kael complies with Vance or defies him, the final reckoning checks for kompromat on Vance himself. Without it, Kael is **arrested and imprisoned** — though Wren is still saved. With it, Kael walks away on the terms of his chosen ending.

**Ending set.** Player allegiances, acquired leverage, and rig stability determine which of **six branch endings** Kael reaches, or which of **three fail-state endings** cuts the story short:

| | Comply with Vance | Defy Vance |
|---|---|---|
| **Crimson Lotus** | A1 — *The Gilded Leash* | A2 — *Silk Over Acid* |
| **Iron Syndicate** | B1 — *Cold Iron, Clean Slate* | B2 — *Heavy Metal* |
| **Lone Wolf** | C1 — *Controlled Burn* | C2 — *The Ghost in the Flood* |

| Fail-state | Trigger | Outcome |
|---|---|---|
| E — *Paid in Full* (Betrayal) | Defy Vance on a Lotus or Iron path without the allied syndicate's kompromat. | The syndicate kills Kael; Wren remains Aegis cargo. |
| F — *Concrete and Glass* (Federal Capture) | Reach any ending without kompromat on Vance. | Kael is arrested and imprisoned; Wren is saved. |
| D — *Kernel Panic* (Thermal Cascade) | Enter the Pier 44 breach on an unstable rig. | Thermal cascade mid-breach; Wren's fate unknown. |

Endings range from indentured federal servitude to inciting a harbor war, escaping completely off-grid, or suffering a mid-breach thermal cascade. The order in which these checks are resolved is defined in Document 4, §4.3.

## 3. Player Demographics & Tailored Game Modes

GHOSTRIG is designed for three distinct player archetypes. Rather than altering the core C# kernel or stripping out the underlying CScript architecture, each mode tailors the mechanical friction, tooling, and progression focus to match how that demographic engages with the simulation.

| Game Mode | Demographic | Primary Focus | Architectural & Mechanical Implementation |
|---|---|---|---|
| **Bare-Metal Mode** (Default) | The Programmer / Hacker | Deep systems programming, writing custom CScript drivers and exploit payloads from scratch, and maximizing efficiency on constrained silicon. | Full unassisted CScript IDE access with minimal hand-holding. Players manually diagnose System Profiler warnings, write batching loops, implement DMA and LRU caching structs, author custom Crawler filters, script packet-reflection payloads, and discover kompromat organically. |
| **Silicon & Bus Mode** (Hardware) | The Hardware Engineer | Physical rig architecture, component procurement, thermal/power budgets, bus bandwidth balancing, and hardware topology optimization. | Expands My Rig and the black-market vendors (Silicon Alley Surplus, Hardware Exchange) with deeper hardware tuning (bus lanes, clock multipliers, DMA channels, thermal dissipation). The CScript IDE supplies structured code-diff patches and semi-automated driver synthesis once the hardware topology is configured properly. Kompromat is discovered organically. |
| **Street Ghost Mode** (Narrative) | The Cyberpunk Fan | Atmospheric immersion, investigative deduction, faction politics, social engineering, and narrative branching without technical roadblocks. | Preserves the full CScript ecosystem, DNS/log investigation, and desktop apps while removing hard failure locks from unoptimized drivers (no Kernel Panic). Kernel costs bill against a high-end reference rig, and CS-Vault supplies plug-and-play community driver patches and hardware upgrades. Story choices and kompromat targets are surfaced as guided prompts. |

## 4. Core Gameplay Loop

1. **Investigate & Infiltrate:** Resolve target domains via DNS and use simulated CScript desktop applications — Browser, Port Sniffer, Password Cracker, FTP Browser, Crawler, Notepad, and Calc — to enumerate ports, escalate tiered filesystem permissions, audit login IP logs, and map hidden subnets.
2. **Diagnose Acoustic & Telemetry Cues:** The simulated rig communicates its state physically and audibly. Inefficient drivers cause mechanical drives to chatter, cooling fans to shriek, and circuitry to emit coil whine. Opening the System Profiler displays deterministic command, interrupt, and cache efficiency telemetry.
3. **Resolve Bottlenecks (Code, Silicon, or Drop-In Patch):** Depending on the active game mode, players refactor CScript drivers in `/sys/drivers/`, reconfigure bus and DMA hardware topologies in My Rig, or pull verified community patches from CS-Vault and hot-swap them at runtime. **The in-game clock never pauses** — all of this happens in live game time.
4. **Upgrade Hardware:** Spend earned credits at black-market vendors to acquire NICs, RAM modules, I/O controllers, and NVMe arrays, slotting them into the simulated hardware bay in My Rig.
5. **Secure Leverage & Execute Breaches:** Respond to forced encrypted video calls, infiltrate faction and federal networks to extract audiovisual kompromat, review it in the Image Viewer, Audio Player, and Video Player apps, fabricate or split intelligence packages, and execute multi-stage breaches under live trace timers.

## 5. The CScript Portal (Out-of-Clock Development)

Because the game clock never pauses, deliberate, unhurried coding happens in the **CScript Portal**, reachable from the main menu:

- **Separate sandbox:** The Portal runs its own isolated GHOSTRIG kernel instance. Nothing done in the Portal advances the game clock or alters the save game directly.
- **Progression-gated:** The sandbox mirrors the furthest progress the player has reached in the campaign — the drivers, hardware, CS-Vault libraries, and API surface the player has unlocked so far — so Portal solutions are built against the same constraints as the live game.
- **Cloud repository:** Portal projects and solutions are saved to the player's personal cloud repository. Inside the game, that repository appears as Kael's personal **CS-Vault** repository: pulling a project into the live game (via Browser or the in-game CScript IDE), compiling it, and hot-swapping it all happen in live game time and are billed against the rig like any other transfer.
- **Storage backend:** The first implementation stores the repository locally behind a repository interface; a hosted online backend (accounts, sharing) can replace it later without changing game code.

## 6. Key Product Pillars & Technical Differentiators

- **Authentic OS & Userland Architecture:** The kernel, desktop shell, and system libraries are built in C#, while all applications, WinForms-like CForms UIs, websites, and device drivers execute in CScript. Every library call incurs deterministic hardware step delays derived directly from active driver settings.
- **Unified Network, DNS & Permissioned Filesystems:** Every server and website is genuinely hackable through CScript tools, backed by DNS resolution, multi-level filesystem permissions requiring progressive privilege escalation, and persistent authentication IP logs that expose deeper network pivots.
- **Deterministic Dual-Clock Simulation:** Simulation time (deterministic step budgets and driver delays) is decoupled from real-world rendering frame time, so low-end virtual hardware feels sluggish in-universe without degrading host frame rates. The simulation clock never pauses during play.
- **Non-Destructive Safety & Moddability:** Per-event execution rollbacks, strict step budgets, and signed stock driver baselines allow players to freely modify live drivers and applications without permanently bricking their host runtime.
- **Host Technology:** The C# host (kernel, desktop shell, system libraries) runs on .NET 8 using **MonoGame** (DesktopGL) for windowing, rendering, input, and real-time audio synthesis.
