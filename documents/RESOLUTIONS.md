# GHOSTRIG — Document Consistency Resolutions

*Decision log for the 2026-11-23 consistency pass. The original PDFs are preserved in [`original/`](original/); the corrected Markdown documents in this folder supersede them.*

## Decisions made by the project owner

| # | Inconsistency | Resolution | Affected docs |
|---|---|---|---|
| 1 | The Vision promised "seven primary endings plus betrayal/capture fail-states", but the story had 6 branch endings + Kernel Panic, and the kompromat rules never produced an ending. The Dialog doc referenced an undefined "Clean Break". | **Six branch endings (A1–C2) + three fail-states:** D *Kernel Panic*, E *Betrayal* (finish for a syndicate without its kompromat → the faction kills Kael and Wren remains cargo), F *Federal Capture* (no kompromat on Vance → Kael is imprisoned, Wren is saved). "Clean Break" removed. Fixed resolution order defined. | 1, 4, 6 |
| 2 | When does the Vance-leverage check apply? | **Always, on both comply and defy paths.** | 1, 4, 6 |
| 3 | No story scene shows Kael pulling kompromat. | Kompromat is found **organically** in Bare-Metal and Silicon & Bus modes (never scripted or handed over); only Street Ghost mode surfaces it as a guided objective. The story references it only where the plot requires it (the Lone Wolf framing, and the endings). | 1, 4, 5, 6 |
| 4 | "Audiovisual" kompromat conflicted with the strict three-file-type rule. | The file taxonomy expands to **six types**: Plain Text, Encrypted, CScript, **Image, Audio, Video**. New apps added: **Image Viewer, Audio Player, Video Player**. Kompromat decrypts to `.vid`; freeze-frames are saved as `.png`. | 2, 3, 5 |
| 5 | The story paused the game clock in the IDE, while the docs called for live trace timers. | **The game clock never pauses.** Unhurried coding moves to the **CScript Portal** on the main menu. | 1, 2, 3, 4, 6 |
| 6 | What the CScript Portal can touch. | The Portal is a **separate sandbox** whose unlocks match the player's campaign progress, with a **cloud repository** for projects and solutions. In the game, that repository appears as Kael's personal CS-Vault repo; pulling from it happens in live game time and is billed against the rig. **Local storage first**; a hosted backend comes later. | 1, 2, 3, 5 |
| 7 | Driver extension: `.drv` (Content) vs `.cs` (other docs and the story). | **`.drv` drivers, `.csc` apps/libraries**, `.cform` pages. | 2, 3, 5, 6 |
| 8 | GHOSTRIG vs. AetherOS. | **GHOSTRIG** names the game, Kael's terminal, *and* the OS. AetherOS is removed. | 1, 6 |
| 9 | "Late-21st-century" vs. 2056. | Keep **2056**; the setting is described as **mid-21st-century**. | 1 |
| 10 | `.cform`: plain-text markup vs. CScript code. | `.cform` is **CScript forms code** (comparable to a WinForms designer file) and belongs to the CScript file type. | 2, 3, 5 |
| 11 | v.chen (User) opened `/.root_archive/` (Root-only) with no escalation. | An **escalation step** was added: `auth_audit.log` leaks a reused, weakly salted Root hash; Kael cracks it and reconnects as Root. | 5, 6 |
| 12 | "Director Marcus Vance" collided with Agent Vance; "OmniCorp" was undefined. | The director is renamed **Director Marcus Hale** in every ending; **OmniCorp** is defined as Aegis Logistics' parent conglomerate. | 1, 5, 6 |
| 13 | No host framework was named. | **.NET 8 + MonoGame (DesktopGL)**, confined to the presentation layer; kernel, VM, VFS and narrative stay engine-free and testable headlessly. | 1, 2, 3 |

## Editorial fixes (no decision needed)

- **PDF damage repaired:** the Kernel and Dialog PDFs had text running off the page, missing spaces ("theNarrativeRuntimeattaches…"), stray `[spanN](startspan)` markers, broken list numbering (1, 1, 1 / 1, 1, 2, 2), and a mode table squeezed to one word per line. The full sentences were recovered from the PDF text layer.
- **Typos:** Content Architecture's "DNS &and" heading; "GHOSTRIGand" / "GHOSTRIGlocks" in the story.
- **Document numbering:** the story is now Document 6 of 6.
- **NetCardDriver example:** its `@Driver` label said "OptiLink Optical NIC", which is the Act II upgrade NIC. It now names the stock Act I modem (Kestrel OM-1). Profiler names are tabulated for every driver.
- **IKernelDriver vs. the example driver:** the example driver's `SendQueue()` method didn't match the `DriverCommand` dispatch contract. Device-class interfaces (`INetDriver`, `IStorageDriver`, …) now sit on top of `IKernelDriver`.
- **Port 8443 vs. FTP:** the FTP Browser was connecting to a management port. Management gateways on 8443 now expose an authenticated file channel the FTP Browser can mount.
- **Drop-node credentials:** Act III referred to drop-node credentials Vex and Silas "gave" in Act II, but no Act II scene handed them over. Scenes 1 and 3 of Act II now do.
- **Hostnames:** `vex_drop.lotus.ny56` and `silas_dead_drop.iron.ny56` contained underscores, which real DNS forbids. They are now `vex-drop.lotus.ny56` and `silas-deaddrop.iron.ny56`.
- **"True IP" in Act I:** the story had the Port Sniffer revealing the shop's IP, which skipped the DNS step. The Browser now resolves the domain via DNS, and the Port Sniffer reveals that the "boutique" runs on heavy cluster hardware.
- **4GB dictionary in Act I:** the story loaded it whole into a tight baseline RAM pool. It now streams through memory in DMA blocks via the LRU cache.
- **Acoustic vocabulary:** one authoritative telemetry-to-sound table was added to the Kernel doc. The Iron-path crypto bug is now a CPU-bound fan surge (not "uncoalesced bus reads"), and the Act II NIC bug is coil whine.
- **"Hardware Exchange and App Store":** "App Store" appeared nowhere else, so it now reads "Hardware Exchange".
- **Supplier manifests:** the `/supplier_manifests/` folder mentioned in the story was added to the Verbena & Rust file layout.
- **Director's blackmail ledger:** the leverage files used in every Hale "Leverage Call" are now listed in the content spec.
- **Comply/defy as actions:** observable action signatures were defined for complying with or defying Vance, plus the case where the trace completes first.
