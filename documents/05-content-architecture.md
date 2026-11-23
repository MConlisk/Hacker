# GHOSTRIG — Content Architecture & World Data Specification

*Document 5 of 7 — Scope: Hardware Catalog, `.drv` CScript Drivers, IP-Bound VFS Internet, Search, Media & Kompromat Assets*

---

## 1. Simulated Hardware Catalog (`content/game/hardware.json`)

All physical components installable in the player's chassis (My Rig) or sold at black-market vendors (Silicon Alley Surplus, Hardware Exchange) are defined declaratively in `content/game/hardware.json`. Each component entry specifies physical capabilities that the C# kernel uses to compute deterministic step costs, thermal output, and acoustic telemetry.

| Slot Category | Key Attributes | Catalog Examples & Progression Role |
|---|---|---|
| CPU | `clock_mhz`, `cores`, `ipc`, `tdp_watts` | Salvaged sub-level processor (baseline) up to multi-core enterprise silicon; determines step-execution throughput and thermal ceiling. |
| Memory (RAM) | `capacity_mb`, `bandwidth_gbps`, `cas_latency` | Tight baseline pool, upgraded in Act II to a 16GB DDR9 Memory Module, with enterprise tiers beyond; governs dictionary loading in Password Cracker and graph caching in Crawler. |
| Storage & Controller | `read_mbps`, `dma_channels`, `platter_noise` | Aging mechanical platter drive up to Cryo-NVMe Array; supports `TransferMode.CPU` and `TransferMode.DMA`. |
| Network (NIC) | `link_mbps`, `ring_size`, `max_batch` | Aging Kestrel OM-1 optical modem (baseline) up to refurbished enterprise OptiLink NIC; governs packet batching and firewall-heuristic stealth. |
| Bus & I/O Controller | `bus_width_bits`, `irq_coalesce_cap` | Stock southbridge up to QuantumBus I/O Controller; required to survive high-throughput Act IV breach loads without thermal cascade. |
| GPU & Audio DSP | `max_draw_batch`, `buffer_frames` | NovaGX-2000 GPU and AudioMixer DSP; drive desktop/mini-game rendering, image/video decoding, audio playback, and diagnostic acoustic alerts. |

## 2. CScript Device Drivers (`/sys/drivers/**/*.drv`)

To clearly distinguish inspectable CScript drivers from the underlying C# host engine, all device drivers carry the `.drv` extension and are authored in pure CScript. (CScript applications use `.csc`; CForms pages use `.cform`.)

### 2.1 Driver Anatomy & Structural Flaw Catalog

Every `.drv` module declares metadata attributes and implements the `IKernelDriver` contract plus its device-class interface (Document 3, §3.1), invoked by C# system libraries. Stock or aftermarket drivers perform their functional job accurately while exhibiting realistic systems-engineering bottlenecks:

```c
// /sys/drivers/net/NetCardDriver.drv  (CScript)
@Driver("Kestrel OM-1 Optical Modem")
@Version("1.0.4-stock")
@Implements("INetDriver")
module NetCardDriver;

import Sys.Hardware;

int SendQueue(Packet[] messages) {
    int sent = 0;
    // INEFFICIENCY FLAW: Single-packet submission incurs full bus arbitration
    // and interrupt overhead per frame instead of batching.
    for (int i = 0; i < messages.Length; i = i + 1) {
        Hardware.SendPacket(messages[i]);
        sent = sent + 1;
    }
    return sent;
    // OPTIMIZED FIX: return Hardware.SendPacketBatch(messages);
}
```

| Driver File (`.drv`) | Profiler Name | Act / Context | Structural Design Flaw & Player Optimization |
|---|---|---|---|
| `LegacyGPUDriver.drv` | `LegacyGPU_v1` | Act I (Arcade / UI) | Calls `gpu.DrawSingle(tri)` per polygon instead of `gpu.Draw(triangleBatch)`. |
| `NetCardDriver.drv` | `NetInterface_v1` | Act I (Port Sniffer) | Loops `Hardware.SendPacket(msg)` instead of `Hardware.SendPacketBatch(messages)`. |
| `StorageController.drv` | `StorageCtl_v1` | Act I (Password Cracker) | Sets `TransferMode.CPU` and reads 1 byte at a time, flushing the sector cache per read, instead of `TransferMode.DMA` block reads with an LRU cache. |
| `OptiLinkDriver.drv` | `OptiLink_NIC` | Act II (Aegis Gateway) | Submits single DMA descriptors instead of coalescing ring-buffer batches before ringing the doorbell. |
| `AudioMixerDriver.drv` | `AudioMixer_v1` | Act III (Lotus Path) | Calls `audio.FlushBuffer()` after every sound effect instead of streaming a steady ring buffer. |
| `CryptoDriver.drv` | `CryptoEngine_v1` | Act III (Syndicate Path) | Calls `keyCache.Clear()` after every block instead of reusing the expanded key schedule. |
| `ProxyRouterDriver.drv` | `ProxyRouter_v1` | Act III (Lone Wolf Path) | Polls virtual sockets sequentially instead of batching poll descriptors in one call. |
| `BusController.drv` & `NVMeQueue.drv` | `BusCtl_v1`, `NVMeQueue_v1` | Act IV (Breach Prelude) | Uncoalesced bus interrupts and unbatched NVMe queues that cause thermal cascade (Kernel Panic) if unpatched. |

## 3. IP-Bound Filesystems, DNS & the In-Game Internet

In GHOSTRIG, the catalog of simulated server filesystems forms the in-game internet. Every virtual filesystem in the catalog is bound directly to a routable IP address, may have one or more domain names registered with the simulated DNS server, and serves as the actual host filesystem from which websites and remote services are stored and streamed.

### 3.1 IP Binding, DNS Registration & Host Streaming

- **Direct IP-to-Filesystem Binding:** Each VFS instance is anchored to a specific IP address (e.g., `10.56.14.88` for Verbena & Rust, internal Aegis cluster IPs, syndicate drop-nodes, and dark-subnet relays). Unregistered or dark-subnet hosts can still be reached directly by IP address even when no public DNS record points to them.
- **DNS Server Zone Records:** Registered domains (such as `verbena-and-rust.ny56`) map to target IPs in the DNS service. Querying DNS resolves domains into IPs, and reverse/forward lookups expose hostnames tied to discovered IP addresses during investigation. Hostnames follow real DNS rules (letters, digits, hyphens; no underscores).
- **Web Streaming from Host VFS:** When a player browses a site in the Browser application, the web server daemon streams CForms pages (`.cform`, CScript) and referenced assets directly from that host's VFS directory (such as `/srv/www/public/`) under the active session's permission tier. The Browser compiles and runs the page in a sandboxed process billed against Kael's rig.

### 3.2 Key Story Hosts

| Host | Role | Notable services / paths |
|---|---|---|
| `verbena-and-rust.ny56` → `10.56.14.88` | Shell storefront (Act I) | HTTP 80, FTP 2121; see §4.2 |
| Aegis external gateway (IP from Verbena routing headers) | Aegis DMZ (Act II) | Management gateway 8443 (weak legacy auth token) exposing a file channel |
| Aegis dark-subnet relay (no DNS) | Hidden intake datastore (Acts II–III) | `/sys_vault/chrysalis_intake/` |
| `vex-drop.lotus.ny56` | Vex's drop-node (creds from Vex, Act II) | FTP 2121; pivots into the Lotus private network |
| `silas-deaddrop.iron.ny56` | Silas's dead-drop (creds from Silas, Act II) | FTP 2121; pivots into the Iron Syndicate private network |
| FCCHO oversight relay (no DNS) | Federal relay | `/sec/oversight/` |
| `cs-vault.ny56` | CS-Vault package server | Public packages, faction repositories, Kael's personal repository (Portal sync) |

### 3.3 Browser Search Engine (Discovery Portal)

The Browser application features a built-in search homepage that indexes publicly reachable web pages, registered domains, and keyword metadata across the VFS internet:

- **Domain & Keyword Queries:** Players can search by domain names, brand/entity terms (e.g., verbena, orchids, aegis, freight, omnicorp), or visible keywords present in indexed public web files.
- **Search Index Scope:** Only public/indexed web content and registered domains exposed to the search crawler appear in search results. Hidden directories (`/.root_archive/`), non-indexed internal relays, or unannounced dark subnets must be uncovered via port scanning, directory enumeration, login IP logs, or narrative leads.

## 4. Virtual Filesystem (VFS) Hierarchies & File Taxonomy

Every local machine and remote server hosts a realistic Unix/corporate-style directory hierarchy populated with domain-appropriate files.

### 4.1 Six File Types

To ensure every file in the world can be inspected, cracked, executed, or played using Kael's desktop toolset, every VFS file belongs to exactly one of six types:

| # | Type | Extensions | Opened with |
|---|---|---|---|
| 1 | **Plain Text** | `.txt`, `.log`, `.cfg`, `.csv`, `.md` | Notepad, Calc (CSV), Browser |
| 2 | **Encrypted** | `.enc`, `.vault`, `.dat` | Displays hex/Base64 ciphertext until decrypted with Password Cracker or a recovered keyfile; decryption yields a file of one of the other five types. |
| 3 | **CScript** | `.csc` (apps, libraries, exploits), `.drv` (drivers), `.cform` (CForms pages) | CScript IDE, Browser (`.cform`), Proc (execution) |
| 4 | **Image** | `.png`, `.jpg` | Image Viewer, Browser |
| 5 | **Audio** | `.wav`, `.ogg` | Audio Player |
| 6 | **Video** | `.vid` (GHOSTRIG container: frame sequence + audio track) | Video Player |

Image, audio, and video files are decoded through the `Media` library and the active GPU/audio drivers, so decoding cost depends on the rig. CommLink freeze-frames are saved as `.png` files.

### 4.2 Realistic Server Directory Hierarchy & Tiered Permissions

Directories enforce role-based Access Control Lists (Guest, User, Operator, Root), forcing players to compromise lower-privilege accounts and read forensic login logs before escalating:

```text
// Example VFS Layout: IP 10.56.14.88  ->  DNS: verbena-and-rust.ny56
/
|-- /srv/www/public/                    [ACL: Guest (Read)]
|   |-- index.cform                     (CScript: CForms storefront page)
|   |-- catalog_orchids.csv             (Plain Text: botanical inventory)
|   `-- orchid_hero.png                 (Image: storefront banner)
|-- /var/log/httpd/                     [ACL: User (v.chen - Read)]
|   |-- access_2056_11.log              (Plain Text: login records + source IPs)
|   `-- auth_audit.log                  (Plain Text: admin pivot trail; root logins
|                                        from 10.56.14.2 and a leaked root hash)
|-- /srv/ftp/storefront/                [ACL: User (v.chen - Read/Write)]
|   |-- invoices_2056/march.csv         (Plain Text: commercial billing)
|   |-- botanical_patents/notes.txt     (Plain Text: synthetic flora specs)
|   `-- supplier_manifests/q4.csv       (Plain Text: supplier shipments)
`-- /.root_archive/                     [ACL: Root (Read)]
    |-- orbital_freight_raw.csv         (Plain Text: impossible peat-moss weights)
    |-- aegis_shell_ledger.enc          (Encrypted: black-budget transfers)
    `-- manifest_tuesday_2342.txt       (Plain Text: BIO-ASSET-09, Pier 44 tag)
```

**Act I escalation path:** v.chen (User) reads `auth_audit.log`, which records the storefront admin's Root logins and a weakly salted Root password hash reused from an old service account. Kael cracks it with Password Cracker and reconnects as Root to open `/.root_archive/`.

## 5. Kompromat & CS-Vault Assets

### 5.1 Kompromat Files

Each kompromat file is stored encrypted (`.enc`) inside a restricted directory on its owner's private network and decrypts to a **Video** (`.vid`) file. Kompromat is never handed to the player or surfaced by story beats in Bare-Metal or Silicon & Bus modes; it must be found organically. Street Ghost mode lists it as a guided objective.

| Owner | Location (encrypted) | Decrypts to | Content |
|---|---|---|---|
| Crimson Lotus (Vex) | Lotus private network: `/var/vault/vex_wetware_kickbacks.enc` | `vex_wetware_kickbacks.vid` | Recorded audio/video of Vex selling un-augmented flood-zone runners to Aegis contractors to clear weapons debts. |
| Iron Syndicate (Silas) | Iron private network: `/srv/private/silas_dock_massacre.enc` | `silas_dock_massacre.vid` | Encrypted surveillance feed and voice order implicating Silas in a cartel slaughter and an illicit wetware-lab buyout. |
| FCCHO / Special Agent Vance | FCCHO oversight relay: `/sec/oversight/vance_black_sting.enc` | `vance_black_sting.vid` | Off-the-books directive and audio log proving Vance intentionally allowed Aegis to harvest flood-zone citizens to bait syndicate targets. |

### 5.2 Aegis Director Leverage

Every ending's "Leverage Call" coerces **Director Marcus Hale**, head of Aegis's Battery Park facility. His leverage is plot-critical rather than optional and is found along the main path:

- `/.root_archive/aegis_shell_ledger.enc` on Verbena & Rust (black-budget transfers through the storefront shell).
- `/sys_vault/chrysalis_intake/hale_skim_ledger.enc` on the Aegis dark subnet (Hale's personal embezzlement from OmniCorp).

### 5.3 CS-Vault Repository Packages

CS-Vault is hosted on a simulated package server (`cs-vault.ny56`) accessible via Browser or CScript IDE. It contains:

- Reusable CScript modules (`lru_cache.csc`, `graph_bloom.csc`, `ironclad_injector.csc`, `deadman_broadcast.csc`).
- Faction repositories gated by tokens (e.g., the Iron Syndicate's IronClad suite).
- Mode-specific driver diff patches in Silicon & Bus and Street Ghost modes.
- **Kael's personal repository**, which mirrors the CScript Portal's cloud repository so Portal projects can be pulled into the live game (in campaign time, billed against the rig).
