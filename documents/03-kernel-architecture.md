# GHOSTRIG — Kernel Architecture & Runtime Specification

*Document 3 of 6 — Runtime: C# Kernel & System Libraries / CScript Userland & Drivers*

---

## 1. Runtime Layer Topology

The GHOSTRIG runtime enforces a strict architectural boundary between the trusted C# host core and the inspectable, player-modifiable CScript environment. All simulated hardware execution, cost accounting, filesystem access control, and network packet routing converge inside `src/Ghostrig.Kernel`.

| Architectural Layer | Language | Responsibilities & Boundaries |
|---|---|---|
| Applications & Websites | CScript (`.csc`, `.cform`) | Userland tools (Browser, Port Sniffer, Password Cracker, FTP Browser, Crawler, Notepad, Calc, Image Viewer, Audio Player, Video Player, CommLink, My Rig, CScript IDE, System Profiler, mini-games) and remote web pages built using WinForms-style CForms control trees. |
| Device Drivers | CScript (`.drv`) | Loadable hardware translation modules (`/sys/drivers/**/*.drv`) implementing the `IKernelDriver` command-dispatch contract and a device-class interface. They contain realistic structural inefficiencies (unbatched loops, CPU polling, cache flushes) rather than artificial delays. |
| Desktop Shell | C# | Window manager, taskbar, desktop launcher, audio synthesizer output, and host frame presentation (MonoGame DesktopGL). |
| System Libraries | C# | Thin system-call bindings (`Gfx`, `Input`, `Fs`, `Net`, `Proc`, `Log`, `Sys`, `Media`, `Shop`, `Account`) and CForms UI primitives. Every call enters the kernel for capability validation and cost billing. |
| Core Kernel | C# | Deterministic scheduler, step-budget enforcement, bus/DMA arbiter, `DriverStats` profiler, VFS/ACL security manager, DNS/network router, and rollback manager. |
| Hardware Definitions | JSON | Static physical specifications (`content/game/hardware.json`) defining clock speeds, bus lane widths, DMA channels, cache sizes, and thermal limits. |

## 2. CScript Execution Engine & Deterministic VM

To guarantee deterministic behavior and prevent sluggish simulated hardware from degrading the host player's real-world frame rate, CScript executes inside a custom, step-metered bytecode virtual machine managed by the kernel.

### 2.1 Compilation & Bytecode Pipeline

- **On-Demand Compilation:** When a player saves or hot-swaps a script in the CScript IDE (or the CScript Portal), the compiler parses CScript source into an Abstract Syntax Tree (AST), performs static type and capability checks, and emits compact bytecode. Syntax or type errors surface immediately as structured compiler diagnostics without mutating active runtime state.
- **Hot-Swap Atomicity:** Replacing a running driver or application swaps the bytecode module pointer at a clean instruction boundary. If initialization fails, the kernel automatically reverts to the previous valid bytecode image.

### 2.2 Dual-Clock Decoupling & Step Budgets

- **Simulated Time vs. Real Frame Time:** The host engine runs a fixed visual frame loop (e.g., 60 FPS) while the kernel advances a deterministic Simulation Clock measured in discrete compute steps and bus cycles. A slow CPU or unbatched driver increases the number of simulated ticks required to complete an operation without stalling the host UI thread.
- **No Pause:** The campaign Simulation Clock is never paused by opening the CScript IDE, the System Profiler, a CommLink call, or any other in-game application. The only out-of-clock coding environment is the CScript Portal (§2.4).
- **Per-Event Step Budgets:** Every UI event handler, network callback, and background worker executes under a deterministic step quota. If a script enters an infinite loop or exceeds its safe thermal/step ceiling, the kernel suspends the offending process cleanly.
- **Per-Event Rollback Snapshots:** Before invoking any CScript event handler or newly compiled driver entry point, the kernel captures a lightweight copy-on-write state delta. Unhandled runtime exceptions roll back partial mutations and emit an authentic kernel/profiler fault.

### 2.3 Determinism Contract

- No wall-clock reads, unseeded randomness, or host-thread timing ever influences simulation state.
- Any variation (e.g., packet-loss patterns, crawler traversal order ties) comes from seeded deterministic generators whose state is serialized with the save.
- Given the same save, hardware configuration, driver bytecode, and input event stream, the kernel produces bit-identical results.

### 2.4 CScript Portal Kernel Instances

- The CScript Portal (main menu) boots a **separate, isolated kernel instance** seeded from a progression snapshot: the hardware catalog entries, stock drivers, CS-Vault libraries, and API surface the player has unlocked in the campaign so far.
- Portal kernels share the exact same compiler, VM, libraries, and cost model as the campaign kernel, but never read or write the campaign save, its VFS, or its clock.
- Portal projects persist through an `IProjectRepository` facade (local storage first; hosted backend later). The campaign accesses the same repository as Kael's personal CS-Vault repository; pulling from it is a normal billed network transfer in campaign time.

## 3. Command-Protocol Driver Model & Cost Accounting

GHOSTRIG models drivers as protocol translators between OS system calls and simulated hardware objects. Drivers never use `Thread.Sleep()` or artificial timers; instead, execution time emerges from how efficiently a driver structures `DeviceCommand` packets.

### 3.1 Standardized Kernel Driver Interface

All CScript drivers implement a unified command-dispatch contract exposed to the kernel's I/O subsystem (shown here as the C#-side contract):

```csharp
public enum DriverCommand {
    Initialize, Shutdown, SubmitCommand, SubmitBatch,
    ReadBlock, WriteBlock, SetTransferMode, FlushCache, GetTelemetry
}

public class DeviceCommand {
    public int CommandType;
    public int Address;
    public byte[] Payload;
}
```

On top of `IKernelDriver`, each driver implements one **device-class interface** that the matching system library calls (interface segregation):

| Device class | Interface | Example entry points | Called by |
|---|---|---|---|
| Network | `INetDriver` | `SendQueue(Packet[])`, `PollSockets(Socket[])` | `Net` |
| Storage | `IStorageDriver` | `ReadBlock(address, size)`, `WriteBlock(address, data)` | `Fs` |
| Graphics | `IGpuDriver` | `SubmitFrame(Triangle[])` | `Gfx` |
| Audio | `IAudioDriver` | `QueueSamples(Sample[])` | `Media`, shell acoustics |
| Crypto | `ICryptoDriver` | `EncryptBlocks(Block[], Key)` | `Net`, `Fs` (encrypted files) |
| Bus / NVMe | `IBusDriver`, `INvmeDriver` | `RaiseInterrupts(Irq[])`, `SubmitQueue(NvmeCmd[])` | Kernel I/O |

Inside those entry points, drivers talk to the simulated device through the `Sys.Hardware` binding (e.g., `Hardware.SendPacket`, `Hardware.SendPacketBatch`, `Hardware.Read`), which is what the kernel meters.

### 3.2 Deterministic Cost Calculation Formula

When a CScript application calls a C# system library (such as `Net.Send()` or `Fs.Read()`), the library marshals the request into the active CScript driver. The kernel meters every hardware interaction using deterministic cycle formulas:

$$
C_{total} = N_{calls}\,(C_{arb} + C_{irq}) + \sum_{i=1}^{N_{calls}} \frac{S_i}{W_{bus}}\, C_{xfer}(Mode) + M_{miss}\, C_{mem}
$$

where:

- $N_{calls}$ is the number of discrete hardware submissions (e.g., $N = 1$ for `SendPacketBatch(messages)` vs. $N = k$ for looping `SendPacket(msg)` per item).
- $C_{arb}$ and $C_{irq}$ are the fixed bus-arbitration and hardware-interrupt overheads incurred per submission.
- $S_i / W_{bus}$ scales payload size $S_i$ against the physical bus lane width $W_{bus}$ defined in `hardware.json`.
- $C_{xfer}(Mode)$ applies a high CPU-cycle multiplier when `TransferMode.CPU` is active, or offloads transfer cycles to the DMA controller when `TransferMode.DMA` is configured.
- $M_{miss} \cdot C_{mem}$ charges memory-bus penalties whenever a driver flushes buffers prematurely (`FlushBuffer()`, `keyCache.Clear()`) instead of hitting an intact cache structure.

### 3.3 Driver Telemetry & Acoustic Feedback

The kernel aggregates real-time execution ratios into a `DriverStats` telemetry structure read by the System Profiler and the host audio engine:

- **Command Efficiency:** Ratio of payload bytes transferred to total `DeviceCommand` headers submitted.
- **Interrupt Efficiency:** Ratio of coalesced batch interrupts to raw single-descriptor interrupts.
- **Cache Efficiency:** Ratio of cache/key-schedule hits to total storage or cryptographic block operations.

**Physical acoustic translation** (the single authoritative mapping used by the audio engine and by all narrative text):

| Telemetry condition | Acoustic signature |
|---|---|
| High CPU utilization → thermal load (including CPU-bound cache misses) | Cooling-fan RPM ramp: hum → whine → shriek |
| High raw interrupt rate (low Interrupt Efficiency) | Coil whine; frequency-modulated stutter-hum or ticking at bursty rates |
| Uncoalesced / CPU-polled disk reads | Mechanical platter chatter and grinding clicks |
| Audio buffer flush / underrun | Uneven tick-tick artifacts in the audio output itself |

### 3.4 Mode-Specific Billing Profiles

- **Bare-Metal Mode:** Bills 100% of computed cycles against the player's installed hardware in My Rig.
- **Silicon & Bus Mode:** Bills against installed hardware with expanded bus-lane and DMA-channel routing parameters; unlocks automated driver synthesis once bus topology targets are satisfied.
- **Street Ghost Mode:** Bills kernel costs against a high-end reference rig profile while preserving full CScript execution, preventing thermal cascades or soft-locks.

## 4. Hardware Bay & Bus Simulation (My Rig)

Physical components are loaded from `content/game/hardware.json` and instantiated inside the player's simulated chassis:

- **Component Slots:** CPU, RAM modules, Storage Controllers/Drives, Network Interface Cards (NICs), GPU, Audio DSP, and Bus I/O Controllers.
- **Power-Down Hot-Swapping:** Installing new hardware requires powering down the simulated bay in My Rig, slotting the component, and rebooting — which binds the hardware slot to its default factory CScript driver. Power-down and reboot consume campaign time.
- **Thermal Cascade State:** If cumulative heat dissipation exceeds chassis cooling capacity during a sustained high-load breach, the kernel triggers a hardware protection fault (Kernel Panic), halting execution cleanly into the narrative fail-state (Ending D). Disabled in Street Ghost mode.

## 5. Virtual Filesystem (VFS), Permissions & Network Stack

Every local machine, web host, and corporate server in GHOSTRIG is instantiated as a first-class `NetworkNode` inside the kernel's network simulator.

### 5.1 Multi-Host Virtual Filesystem & Tiered ACLs

- **Isolated Directory Trees:** Each `NetworkNode` owns an independent VFS hierarchy (e.g., `/srv/ftp/storefront/invoices_2056/`, `/.root_archive/`, `/sys_vault/chrysalis_intake/`).
- **Role-Based Access Control (RBAC):** Files and directories carry explicit owner IDs, group IDs, and permission masks (Guest, User, Operator, Root). System calls via `Fs` validate the active session token's privilege tier before returning directory listings or file streams, returning authentic `403 Access Denied` errors when unauthorized.
- **Typed Files:** Every file belongs to one of the six file types defined in Document 5, §4.1. `Fs` reports the type; opening a file in an application that cannot handle its type raises an explicit unsupported-format error.
- **Stock Integrity Signing:** Local system libraries and baseline drivers carry cryptographic signatures. The kernel tracks whether a module is Stock-Signed or Player-Modified and exposes a one-click restoration hook to revert any file to its signed baseline.

### 5.2 DNS Resolution, Packet Routing & Access Logs

- **Simulated DNS Subsystem:** Domains (such as `verbena-and-rust.ny56`) are never hard-coded in client apps. Calls to `Net.Resolve(hostname)` dispatch UDP-style queries to configured DNS server nodes, returning IP addresses and incurring network step costs.
- **Port Service Daemons:** Each `NetworkNode` binds listening services to specific ports: HTTP/CForms web servers (80), FTP daemons (2121), and management gateways (8443). Management gateways expose an authenticated file-transfer channel once a valid token is presented; the FTP Browser can mount both FTP daemons and management file channels.
- **Forensic Login Logs:** Web servers and gateways automatically append structured entries (`[Timestamp] [User] [Source_IP] [Action]`) to local access logs upon authentication attempts, enabling players to trace lateral movement paths across subnets.

## 6. CForms UI Pipeline & Retained Rendering

- **C# Controls, CScript Composition:** Applications and websites construct UI trees in CScript (`.csc` / `.cform`) by instantiating C# CForms controls (`Form`, `Panel`, `Label`, `Button`, `TextBox`, `ListView`, `Canvas`, `MediaView`).
- **Retained Draw Groups:** To keep CScript UI overhead minimal, `Gfx` maintains a retained scene graph. Unchanged control subtrees are cached as static draw groups; only invalidated controls re-emit `DeviceCommand` draw primitives to the GPU driver.
- **Kernel-Side Text Measurement:** Font glyph metrics and line wrapping are computed natively in C# via `Gfx.MeasureText()` and billed as a single deterministic system call.
- **Media Decoding:** `Media.Decode()` turns image, audio, and video files into frames and sample buffers through the active GPU and audio drivers, so playback cost and smoothness depend on the rig like any other workload.
