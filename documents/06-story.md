# GHOSTRIG — Story

*Document 6 of 7 — Narrative Script (Bare-Metal Mode narration)*

> This script narrates the Bare-Metal Mode experience. Silicon & Bus and Street Ghost modes follow the same beats with the presentation differences described in Document 4, §2. The game clock never pauses: every scene runs in live time, including time spent in the CScript IDE. Kompromat (leverage) is never handed to Kael in these scenes — the player must find it on their own — but the endings depend on it (see the Act IV preamble).

---

## Act I: Ghosts in the Seawall

### Scene 1: The Sound of Thrashing Hardware

The rain in the Lower East Side doesn't fall; it seeps. Outside your reinforced, salt-stained window, the Atlantic seawall groans under the pressure of a high-tide surge, pumping dull vibrations through the concrete walls of your sub-level apartment. Inside, the air smells of ozone, damp mildew, and cheap solder.

You are Kael, and your survival depends on squeezing every drop of throughput out of aging, black-market silicon. Your terminal runs GHOSTRIG — a bare-metal operating system where every application and hardware driver communicates with your rig's components via raw CScript command queues.

Right now, your system works, technically. Everything boots, every window renders, and no error dialogs pop up. Yet the machine feels sluggish, heavy, and loud. You don't even need to open your System Profiler to know why; you can hear the inefficiency in the room around you. Your salvaged mechanical drive emits a frantic, continuous chatter-click-chatter even when you're barely moving files, and your CPU cooling fan whines at 90% RPM just rendering your desktop.

It's been seventy-two hours since your twenty-two-year-old sister, Wren, missed her check-in. She was a social engineer and street-level ghost who took grey-market jobs to save up for clean-zone transit passes for both of you. Three days ago, she told you she had landed a score big enough to get you both out of the flood zones, and she never came back.

To keep your hands steady while waiting for a ping, you launch one of your desktop's built-in arcade games — a vector-graphics asteroid shooter. The game is playable, sitting at a choppy 24 FPS. When you check the My Rig performance overlay, GPU utilization is barely at 15%, yet your CPU is pinned at 100% and command latency is spiking.

Curious, you open the CScript IDE alongside the running game and pull up `LegacyGPUDriver.drv`. Right there in the render pipeline is the culprit: instead of packing the frame's geometry into a single `gpu.Draw(triangleBatch)` command, the lazy community-written driver loops through every individual polygon and calls `gpu.DrawSingle(triangle)` — flooding the command processor with thousands of individual `DeviceCommand` headers per frame.

Before you can refactor the batching loop, a sharp, urgent chime cuts through the whir of your fans: an incoming encrypted video call.

### Scene 2: The Last Contact

You accept the call. The video feed resolves in grainy monochrome, heavy with compression artifacts. On screen is Rook, a twitchy data broker who operates out of a noodle stall in the flooded market tunnels of Chinatown. Rook looks pale, his eyes darting off-camera every few seconds.

"Kael, thank god," Rook whispers, leaning close to his mic over the hiss of a steam pipe. "Listen to me, man, I'm burning this burner node the second we hang up. You need to stop looking for her."

"Where is she, Rook?" you ask, keeping your voice level while routing his call through a local proxy in My Rig. "You were her spotter on Tuesday. What was the job?"

Rook wipes sweat from his forehead. "It was supposed to be a milk run. Just a standard data grab for the Crimson Lotus. One of their lieutenants, Vex, hired us to pull a client list off an uptown server. Easy credits. But when Wren got inside the subnet, she freaked out. She started yelling over comms that the server architecture wasn't local gangware — it was heavy corporate iron, and the Crimson Lotus were just being used as cutouts."

"Cutouts for who?"

"I don't know! She didn't have time to explain," Rook says, his voice cracking. "Silent alarms tripped — total blackout on our comms. I bolted for the transit tunnels. Last I saw Wren, she was running toward the Battery Park bulkhead with two guys in unmarked tactical gear on her heels. Not Lotus enforcers, Kael. Professionals."

Rook glances over his shoulder before holding a crumpled piece of thermal receipt paper up to his camera.

"She shoved this into my jacket right before we split up. Told me if she didn't make it back, to give it to you and tell you to 'follow the roots.' I'm sorry, Kael. I'm out."

The feed dies with a flat, hollow tone.

### Scene 3: Bottlenecks and Protocol Overhead

You captured a freeze-frame of the receipt before the call dropped, and it sits on your desktop as an image file. Zooming in with the Image Viewer, you read Wren's cramped handwriting: a domain address (`verbena-and-rust.ny56`), a username (`v.chen`), and a partially smudged SHA-256 password hash missing its final six characters.

You open your Browser, which resolves the domain through DNS to `10.56.14.88`, and pull up Verbena & Rust — a boutique synthetic-flora shop on the Upper West Side selling bioluminescent orchids to wealthy corporate enclaves above the seawall. Next, you launch your Port Sniffer against that IP to map the shop's exposed services.

The sniffer works — it finds open ports — but it crawls at a snail's pace. Worse, your rig's audio shifts into a steady, high-pitched coil whine, and the System Profiler flashes a yellow warning:

> `WARNING: NetInterface_v1 submitting excessive single-packet interrupts. Bandwidth throughput degraded (31% Command Efficiency).`

If you try to run an active exploit or heavy port sweep at this sluggish packet rate, the target firewall's behavioral heuristics will flag your slow, sequential probes as an anomaly and drop your connection.

You open the CScript IDE, navigate to `/sys/drivers/net/`, and inspect `NetCardDriver.drv`. The driver isn't broken — it delivers every byte accurately — but whoever wrote it implemented `SendQueue()` by iterating through the outbound buffer and calling `Hardware.SendPacket(msg)` one message at a time instead of grouping them via `Hardware.SendPacketBatch(messages)`. Every single packet incurs full bus-arbitration overhead.

You refactor the method to pack outbound frames into batches, hit **Compile & Hot-Swap**, and watch the profiler warning vanish. The coil whine from your motherboard drops to a faint, steady hum. When you re-run the Port Sniffer, the sweep finishes in two seconds flat — revealing that this "boutique" storefront sits on heavy Manhattan server-cluster hardware, with an unpatched file-transfer daemon listening on Port 2121.

### Scene 4: Thrashing the Disk

To recover the missing six characters of Wren's password hash, you open the Password Cracker app, clean up the string in Notepad, and load a 4GB rainbow-table dictionary from your local drive.

As soon as you hit Start, your apartment fills with the violent, rhythmic grinding-click of your mechanical platter drive working overtime. The System Profiler lights up again:

> `Disk Throughput: Normal | CPU Usage: 98% | Cache Efficiency: 0%`

Your drive is reading the dictionary accurately, so the cracker is slowly ticking through hashes, but at this rate it will take four hours to finish — time you don't have. With the cracker still grinding in the background, you open `StorageController.drv` in the CScript IDE and spot two textbook efficiency bugs:

- The driver is initialized with `disk.Mode = TransferMode.CPU` instead of `TransferMode.DMA`, forcing your processor to manually shepherd every single byte off the disk instead of letting the controller write directly to RAM.
- Inside `ReadBlock(int address, int size)`, the driver executes a `for` loop that calls `Hardware.Read(address + i, 1)` one byte at a time — and flushes the sector cache on every read — instead of pulling the entire contiguous block in a single `Hardware.Read(address, size)` call.

You switch the controller to `TransferMode.DMA`, replace the byte-by-byte loop with a single block-read command, and link a proper LRU caching struct (`lru_cache.csc`) from CS-Vault.

When you hot-swap the driver, the violent clicking of your hard drive instantly stops, replaced by a single, smooth whir as the dictionary streams into memory in large DMA blocks through the new cache. Your CPU load drops from 98% to 12%, and the Password Cracker tears through the remaining combinations in seconds.

A crisp chime rings out: **MATCH FOUND.**

### Scene 5: Following the Roots

With the credentials for `v.chen` unlocked, you launch your FTP Browser, connect to Port 2121, and drop into the internal directory tree of Verbena & Rust.

At first glance, it's just a normal storefront: `/srv/ftp/storefront/invoices_2056/`, `/botanical_patents/`, `/supplier_manifests/`. Remembering Wren's warning to follow the roots, you enumerate the server further and spot a hidden directory at the filesystem root — `/.root_archive/` — locked to Root. `v.chen` gets nothing but `403 Access Denied`.

But `v.chen` can read the web server's logs. In `/var/log/httpd/auth_audit.log`, you find the storefront admin logging in as `root` from an internal address, `10.56.14.2`, at odd hours — and a careless debug line that dumped a weakly salted Root password hash, reused from an old service account. You feed it into the Password Cracker; with your patched storage controller, it falls in under a minute. You reconnect as Root.

Inside `/.root_archive/` are raw CSV shipping logs and an encrypted ledger. You pull the files into your My Rig storage vault and open them side-by-side in Calc and Notepad. By summing the freight weights against the billing totals in Calc, the cover story collapses: Verbena & Rust is billing millions of credits for shipments of "synthetic peat moss" routed to orbital cargo terminals — at weights and frequencies physically impossible for a boutique flower shop.

Inspecting the raw routing headers in Notepad, you trace the billing shell back to its parent: **Aegis Logistics**, the orbital-to-surface freight arm of the OmniCorp conglomerate, with restricted access to Manhattan's sealed industrial piers. The headers also leak the IP of Aegis's external gateway.

Buried at the bottom of Tuesday night's manifest — logged at 11:42 PM, minutes after Wren vanished — is a single line item. Under cargo type, instead of botanical supplies, there is only a cold alphanumeric tag: **BIO-ASSET-09 (UN-AUGMENTED / VIABLE)**, scheduled for transfer to Pier 44, Sub-Level B.

Wren didn't stumble onto a mob stash. She stumbled onto a harvesting pipeline — and she wasn't the thief. She was the cargo.

---

## Act II: Cutouts, Cartels, and Crawlers

### Scene 1: The Lotus on the Line

You're still staring at the BIO-ASSET-09 (UN-AUGMENTED / VIABLE) line in Calc — the chill from the seawall somehow sharper inside your damp apartment — when your desktop's ambient soundscape shifts. A low, resonant sub-bass pulse hums through your desk, followed by a crisp two-tone ping that doesn't match any standard GHOSTRIG system alert.

An encrypted video call forces its way into your active workspace. Before you can manually route the feed through a proxy in My Rig, the connection handshakes and resolves. On screen sits Vex, a lieutenant of the Crimson Lotus, framed against the neon-lit clutter of a waterfront office in lower Manhattan. He wears a tailored dark jacket over a subtle cybernetic collar, tapping a stylus against a glass desk with unhurried confidence.

"You've got Wren's touch, Kael — less noise, cleaner pivots," Vex says smoothly. "We saw someone using v.chen on the Verbena & Rust relay five minutes ago. Figured either you were her brother or you had her notes."

"Where is she?" you ask, keeping your voice flat while your fingers quietly open the My Rig network view to trace the incoming route. "Rook said the Lotus hired her for a grab."

"And she was doing fine until she wandered out of bounds," Vex replies, leaning forward slightly. "The Lotus took a smuggling contract from Aegis Logistics — freight slots on their orbital-to-pier runs. Wren was supposed to pull a manifest from a staging node, nothing more. Instead, Aegis pulled the rug out, locked their internal topology, and vanished with a shipment they owed us. Including your sister."

Vex drops a small credit packet into your account — enough to keep your power grid stable for another week — along with a package link to a network Crawler script on CS-Vault and a set of credentials for his private drop-node, `vex-drop.lotus.ny56`.

"Map Aegis Logistics's internal nodes for us," Vex says. "Find where they're staging their pier transfers, and drop it on my node. Do that, and the Lotus puts muscle behind getting Wren back. Cross us, and you're swimming in the East River before dawn."

The video feed cuts out. The sub-bass hum fades, leaving only the steady, healthy purr of your patched network driver and the soft whir of your newly smoothed storage controller.

### Scene 2: Upgrading Silicon and Sweeping Ports

You open My Rig and check your hardware inventory. Your aging Kestrel optical modem and tight memory pool might survive a light scan, but probing Aegis Logistics — an orbital freight shell with corporate-grade perimeter gear — means pushing heavier traffic and parsing larger topology graphs.

You launch your Browser, head to Silicon Alley Surplus, and spend a chunk of your credits on a refurbished enterprise OptiLink NIC and a 16GB DDR9 Memory Module. In GHOSTRIG, buying new gear isn't just clicking a button: you power down the simulated bay in My Rig, slot in the virtual hardware, and boot back up — every minute of it on the clock.

Immediately, your speakers pick up a thin, pulsing coil whine — a low-level hardware interrupt stutter. You open the System Profiler:

> `WARNING: OptiLink_NIC submitting unbatched DMA descriptors. Command Efficiency: 48% | Interrupt Efficiency: 54%`

Classic factory bloat. The hardware is fast, but the stock `OptiLinkDriver.drv` queues single-descriptor transfers instead of coalescing descriptors into ring-buffer batches. You open the CScript IDE, pull up `OptiLinkDriver.drv`, and refactor the transmit ring to batch descriptors before ringing the hardware doorbell. You also pull a lightweight GraphBloom helper (`graph_bloom.csc`) from CS-Vault to keep memory allocations low during topology sweeps.

You compile and hot-swap the driver. The whine stops.

You open the Port Sniffer, target Aegis Logistics's external gateway IP pulled from the Verbena & Rust routing headers, and run a sweep. Because your batched network driver avoids triggering noisy, one-packet-at-a-time anomalies, the scan slices cleanly through standard ports and flags an exposed, misconfigured management service on Port 8443 running a weak legacy auth token.

### Scene 3: Cracking the Gateway and Turning the Tables

You feed the captured auth-token prefix into the Password Cracker, format your mask list in Notepad, and kick off a targeted dictionary pass. With your upgraded memory module and clean storage cache, the cracker chews through candidate keys without thrashing your drive.

A clean chime sounds: **ACCESS GRANTED.**

You open your FTP Browser and mount the management gateway's file channel on Port 8443, dropping into Aegis Logistics's outer DMZ directory tree. There are no raw wetware files sitting in plain sight — just staging scripts, container manifests, and pointer configs for an internal corporate subnet.

Before you can launch the Crawler from CS-Vault into Aegis's interior network, a sharp crackle of line noise tears through your speakers — a classic carrier-band injection tone. Another video window forces itself open over your workspace.

This time, it's Silas, an enforcer for the Iron Syndicate, the Crimson Lotus's bitter rivals across the East River in Brooklyn. Silas looks like he's sitting in the cab of a semi-truck, rain streaking the windshield behind him.

"Don't bother doing Vex's errands, Kael," Silas says without preamble. "The Lotus didn't get stiffed by Aegis. Vex sold Wren's crew to Aegis to clear a debt on a contraband weapons shipment. Your sister wasn't collateral — she was part of the payment."

Your stomach drops, cold as the water outside your window. You open Notepad and pull up the timestamped routing logs you pulled from Verbena & Rust: the manifest entry for BIO-ASSET-09 lines up almost to the minute with the window Vex claimed Aegis "betrayed" them.

"Why tell me?" you ask, keeping your voice tight.

"Because the Iron Syndicate wants Aegis's dockside ledger and their internal network map," Silas says. A dead-drop address — `silas-deaddrop.iron.ny56` — and a set of upload credentials appear in your chat window. "Give us what you pull out of their crawler, and we'll give you real breaching tools from our private CScript repo — plus a crew to hit Pier 44 when the time comes. Play Vex's lapdog, and you're just digging your own grave."

Silas's feed snaps dead.

### Scene 4: Mapping the Dark Subnet

You sit back in your chair, listening to the gentle hum of your fans and the distant groan of the seawall. You have two ruthless syndicates whispering in your ear, each offering leverage, credits, and code — and neither one is telling you the whole truth.

You open the CScript IDE and inspect the Crawler script you pulled earlier, adding custom filter rules so it logs node latency, subnet hops, and hidden storage endpoints while stripping out decoy honeypots. You launch the Crawler against Aegis Logistics's interior network.

As the crawler maps branch after branch — shipping hubs, orbital customs relays, payroll servers — a pattern emerges in the latency graph. Most nodes respond within normal corporate ranges, but one cluster sits behind a heavily throttled relay with bizarrely high jitter — the signature of an isolated dark subnet staging high-security transfers.

Inside that hidden branch, the crawler indexes a concealed datastore: `/sys_vault/chrysalis_intake/`. Before you can pull the full archive, you grab a critical index file and drop it into My Rig — confirming that Pier 44, Sub-Level B isn't a warehouse at all, but a subterranean intake facility handling un-augmented subjects under Aegis's black-budget logistics wing.

---

## Act III (Crimson Lotus Path): Silk, Steel, and Silent Alarms

### Scene 1: Poisoning the Well

You lock in your choice: the Crimson Lotus has the waterfront reach in Manhattan, and Vex already has eyes on Aegis Logistics's pier schedule. If you're going to play along with Vex to get Wren out, you have to keep Silas and the Iron Syndicate blind without tipping off Aegis's intrusion detection.

You open Notepad and pull up the raw topology dump your Crawler just pulled from Aegis's interior network. Alongside the real `/sys_vault/chrysalis_intake/` route pointing toward Pier 44, Sub-Level B, you splice in a set of believable decoy nodes — phantom freight relays and fake dockside berths in Red Hook — and format a clean decoy manifest for Silas. Meanwhile, in My Rig, you isolate the true Pier 44 staging coordinates and package a compressed route summary for Vex.

Before sending Vex's packet, you launch one of your desktop's built-in puzzle games — a circuit-routing board game — while waiting for Aegis's perimeter logs to cool down. Halfway through a tricky board, the game's audio stutters with a subtle, uneven tick-tick that doesn't match your fans. You open the System Profiler:

> `WARNING: AudioMixer_v1 flushing ring buffer every frame. Cache Efficiency: 38% | Command Efficiency: 45%`

Even your sound driver has a command-overhead quirk: it calls `audio.FlushBuffer()` after every tiny sound effect instead of keeping a steady stream buffered. You jump into the CScript IDE, fix the buffer-flush behavior in `AudioMixerDriver.drv`, and hot-swap it. The uneven tick-tick smooths out into clean, predictable ambient audio — which will make it much easier to hear if an unauthorized probe or line tap hits your rig later.

### Scene 2: Trading Maps for Muscle

You open your FTP Browser, connect to `vex-drop.lotus.ny56` using the credentials Vex slipped you in Act II, and upload the true Aegis topology map and the `/sys_vault/chrysalis_intake/` index. A moment later, a crisp chime rings out: an incoming video call.

Vex appears on screen, leaning back in his chair with a thin, approving smile. Behind him, two Lotus operatives are checking magazines on assault rifles.

"Clean work, Kael," Vex says. "You gave us the exact relay path Aegis uses to move high-value freight under the radar. We're already moving a strike squad toward the West Side piers to intercept their next convoy and see what's inside BIO-ASSET-09."

"Wren," you say sharply. "Don't treat her like a crate."

"Relax. If she's viable asset stock, Aegis keeps her alive until intake," Vex replies smoothly. "While our boys hit the street side, I need you digging deeper into `/sys_vault/chrysalis_intake/` on their dark subnet. Pull the medical telemetry and security lock codes for Pier 44, Sub-Level B. Keep your head down — if Aegis's trace locks onto you, we can't pull you out of a Lower East Side flood-hole."

The call disconnects. You turn back to your terminal, opening Calc to cross-reference the intake timestamps against tidal lock schedules at Pier 44. The deeper you look into the `/sys_vault/chrysalis_intake/` directory via your FTP Browser and a Crawler follow-up pass, the uglier the picture gets: Aegis isn't just smuggling un-augmented people; they're running baseline neural-compatibility tests on them before shipping them to a subterranean lab beneath Battery Park.

### Scene 3: The Thread Pulls Back

You start pulling encrypted manifests from the hidden datastore into your My Rig storage vault, watching your upgraded memory pool and DMA-enabled storage controller chew through the blocks without a hitch. Just as the largest archive finishes transferring, a cold, sharp chime cuts across your speakers — a tone you've never heard on your rig, completely distinct from Vex's sub-bass ping or Silas's carrier static.

Your active window flickers. A high-contrast federal crest snaps onto the display, accompanied by a clean, encrypted video feed.

Special Agent Vance of the Federal Cyber-Crimes & Homeland Oversight Bureau (FCCHO) appears on screen. There's no panic in his posture, no street-level grit — just cold, methodical authority.

"Kael," Vance says evenly. "Put the Crawler down. We've been watching the Aegis Logistics dark subnet for four months, and twenty minutes ago you pulled a federal honeypot-trap index from `/sys_vault/chrysalis_intake/`."

Your hands freeze over the keyboard. In My Rig, you watch the network trace indicator light up — not a sloppy gang trace, but a surgical, multi-hop federal lock zeroing in on your physical apartment in the Lower East Side.

"I'm looking for my sister," you say, keeping your voice controlled. "Wren. She was taken by Aegis."

"We know who Wren is, and we know you've been trading intelligence with the Crimson Lotus," Vance replies. "What you don't realize is that Aegis Logistics isn't a shipping front — it's an illegal wetware-harvesting operation run by rogue defense contractors, and the Lotus has been acting as their street-level collector. A federal tactical unit is already en route to your building."

Vance pauses, letting the weight of the moment settle over the hum of your cooling fans.

"You have a choice right now, Kael. Help us breach Aegis's subterranean server farm in Battery Park — deploy our federal rootkit before they wipe the drives and move the remaining assets, including your sister — and we'll pull the tactical squad back, clear your record, and get Wren out alive. Keep playing errand boy for a syndicate, and you go down in a raid alongside them."

The video feed holds steady on Vance's face, waiting for your answer as the distant wail of sirens begins to echo faintly through the rain-slicked streets above the seawall. The trace meter keeps climbing.

---

## Act III (Iron Syndicate Path): Rust, Iron, and Retaliation

### Scene 1: Burning the Bridge

You make your decision: Vex lied to your face, and the Crimson Lotus treated Wren like currency. If you want enough firepower to crack open Pier 44, you're going to side with the Iron Syndicate — and make sure the Lotus pays for setting her up.

You open Notepad and pull up the raw topology dump your Crawler extracted from Aegis Logistics's internal network. Instead of sending Vex the clean map, you strip out the real `/sys_vault/chrysalis_intake/` coordinates, replace them with a dead-end Aegis customs honeypot, and package the genuine Pier 44 Sub-Level B routing data for Silas. In My Rig, you also bundle the server IP and auth logs Vex used to contact you in Act II — giving the Iron Syndicate a direct vector into the Lotus's waterfront relay.

Before initiating the transfer, you notice your rig's cooling fan surging into a strained, rising whine every time you test-encrypt a chunk of the package. You open the System Profiler to check your thermals and throughput before pushing heavy encrypted traffic:

> `WARNING: CryptoEngine_v1 clearing key-schedule cache on every block. Cache Efficiency: 12% | Command Efficiency: 51%`

You launch one of your desktop's built-in terminal mini-games — a signal-decoding logic puzzle — while your upload queue stages, then jump into the CScript IDE. Inspecting `CryptoDriver.drv`, you spot the exact inefficiency: every time a data chunk finishes encrypting, the driver calls `keyCache.Clear()` and reloads the round keys from system RAM instead of reusing the expanded key schedule across the batch. You refactor the caching check, compile, and hot-swap the driver. The fan settles back to a low purr, and your encryption throughput jumps immediately.

### Scene 2: Brooklyn Iron

You launch your FTP Browser, connect to `silas-deaddrop.iron.ny56` with the credentials Silas provided, and upload the true Aegis topology map, the `/sys_vault/chrysalis_intake/` index, and Vex's relay credentials. Seconds later, a sharp carrier-band crackle snaps through your speakers, and an incoming video call opens over your workspace.

Silas appears on screen, standing inside a dimly lit Brooklyn warehouse lined with heavy industrial server racks and stacked shipping containers. Behind him, two technicians in greasy coveralls are already feeding your network map onto a wall monitor.

"Now that is how you settle a debt," Silas says, a cold grin crossing his scarred face. "Our boys are already hitting the Lotus relay you handed over — Vex is going to be too busy putting out fires on his own docks to notice what's coming at Pier 44."

"Keep your end of the bargain, Silas," you say, leaning toward your mic. "Wren is tagged as BIO-ASSET-09 in Sub-Level B. I need the breaching tools you promised so I can drop their internal security before your crew moves in."

Silas nods toward one of his techs and drops a private CS-Vault repository token into your chat window alongside a heavy credit transfer. "That gives you access to our proprietary IronClad exploit suite — kernel-level payload injectors built for corporate gate controllers. Pull the biometric lock overrides and subject manifests from `/sys_vault/chrysalis_intake/` before Aegis realizes the Lotus isn't backing them anymore."

The call cuts off. You immediately open CS-Vault, pull down the Syndicate's exploit modules, and use your Port Sniffer and FTP Browser to push deeper into Aegis's dark subnet. Opening the retrieved intake logs in Calc and Notepad, the true scale of the operation comes into focus: Aegis Logistics isn't just smuggling contraband — it's running an off-the-books neural-harvesting clinic under Battery Park, selecting un-augmented flood-zone residents like Wren for wetware extraction.

### Scene 3: The Thread Pulls Back

You begin downloading the encrypted subject telemetry and gate-control schematics from `/sys_vault/chrysalis_intake/` into your My Rig vault. Thanks to your patched DMA storage controller and fixed crypto cache, the gigabytes stream in silently, your cooling fans holding a steady, low purr.

Then, at 99% transfer completion, a cold, sharp chime — completely unlike Vex's sub-bass pulse or Silas's line static — cuts across your speakers.

Your desktop flickers. A crisp federal seal snaps onto the screen, overlaying your FTP Browser with a high-priority video override. Special Agent Vance of the Federal Cyber-Crimes & Homeland Oversight Bureau (FCCHO) stares back at you, calm and unreadable.

"Kael," Vance says evenly. "Step back from the terminal. We've had a passive tap on Aegis Logistics's dark subnet for four months, and your IronClad exploit signatures just tripped a federal watchdog inside `/sys_vault/chrysalis_intake/`."

Your fingers freeze over the keyboard. In My Rig, the incoming trace monitor spikes — not a sloppy gang ping, but a multi-node federal triangulation lock closing rapidly on your Lower East Side apartment.

"I'm getting my sister out," you say, keeping your voice steady. "Wren. Aegis took her."

"We know who Wren is — and we know you just handed Aegis's internal topology to the Iron Syndicate," Vance replies, his tone hardening. "You think Silas is going to rescue your sister out of kindness? The Syndicate wants Aegis's wetware tech for themselves. A federal tactical unit is already en route to your building."

Vance pauses, letting the hum of your rig fill the silence between you.

"You have one chance to walk away clean, Kael. Use your root access inside Aegis's subterranean server farm in Battery Park to deploy our federal rootkit before they purge the drives and move the remaining subjects — including Wren. Cooperate, and we call off the raid, scrub your record, and extract your sister safely. Keep running ops for Brooklyn mobsters, and you'll be in federal custody before Silas's crew even crosses the river."

The video feed holds on Vance's steady gaze, waiting for your answer as the distant wail of sirens begins to echo faintly above the seawall. The trace meter keeps climbing.

---

## Act III (Lone Wolf Path): Smoke, Mirrors, and Crossfire

### Scene 1: Orchestrating the Collision

You decide neither syndicate gets your loyalty — and neither one gets Wren as a bargaining chip. Vex sold her out, Silas wants Aegis's tech for himself, and the only way a nobody from the Lower East Side survives both factions is to make them tear each other apart while you slip through the smoke.

Your quiet work on the side has led here. Through the drop-node credentials both men handed you, you have already slipped into each syndicate's private network and walked out with the one thing each would kill to bury: Vex's recorded wetware kickbacks and Silas's dock-massacre surveillance feed. Now you put them to work.

You open Notepad and pull up the raw topology dump your Crawler extracted from Aegis Logistics's internal network. Working carefully, you build two doctored packages:

- **For Vex and the Crimson Lotus:** you edit the node logs to make it look like the Iron Syndicate is already staging an ambush at Aegis's secondary West Side transfer depot — and you attach a clip of Silas's massacre feed, re-stamped to look like a fresh threat aimed at Lotus territory.
- **For Silas and the Iron Syndicate:** you alter the staging timestamps and relay headers so Aegis's high-value shipment appears to be guarded by a Crimson Lotus escort at the Manhattan docks — and you attach a cut of Vex's kickback recording that makes it look like Vex sold out Brooklyn crews too.

In My Rig, you keep the only unedited copy of the true `/sys_vault/chrysalis_intake/` coordinates pointing to Pier 44, Sub-Level B.

Before pushing both packages out simultaneously, you hear a faint, irregular stutter-hum coming from your motherboard — the coil-whine signature of unbatched interrupts. Running two spoofed proxy tunnels at once will expose any timing hiccups, so you open the System Profiler:

> `WARNING: ProxyRouter_v1 issuing unbatched socket polls. Interrupt Efficiency: 34% | Command Efficiency: 41%`

You launch one of your desktop's built-in terminal games — a grid-defense strategy game — while your proxy nodes warm up, then jump into the CScript IDE. Opening `ProxyRouterDriver.drv`, you find the loop polling each virtual network interface sequentially instead of batching poll descriptors in a single hardware call. You refactor the polling queue, compile, and hot-swap the driver. The irregular stutter-hum smooths out into a clean, steady purr.

### Scene 2: The Spark in the Harbor

You launch your FTP Browser and drop both doctored intelligence packages onto `vex-drop.lotus.ny56` and `silas-deaddrop.iron.ny56` within seconds of each other, routing both uploads through staggered municipal relays in Queens so neither syndicate can trace the packets back to the same source.

Within ten minutes, your Browser news ticker and local police-band scanner feed light up: gunfire at the West Side freight depots, followed immediately by retaliatory explosions across the East River piers. Both Vex and Silas hit your inbox with frantic, overlapping video-call requests demanding to know why the other faction was waiting for them — calls you quietly let ring out while you focus on the real target.

With both syndicates locked in a chaotic waterfront turf war and Aegis Logistics's perimeter teams distracted by the street-level crossfire, you open your Crawler and FTP Browser and push straight into Aegis's dark subnet.

Pulling the raw intake spreadsheets and medical logs into Calc and Notepad, the full horror of the operation snaps into focus: Aegis isn't just smuggling contraband — it's an off-the-books neural-harvesting pipeline run by rogue defense contractors beneath Battery Park, abducting un-augmented flood-zone residents like Wren for wetware extraction.

### Scene 3: The Thread Pulls Back

You initiate a direct, high-speed download of the `/sys_vault/chrysalis_intake/` biometric gate keys and blackmail ledgers into your My Rig vault. Thanks to your patched DMA storage controller and coalesced proxy router, the encrypted archives stream onto your drive in quiet, uninterrupted blocks.

Then, just as the final checksum verifies, a cold, sharp chime — completely alien to your rig and utterly distinct from either syndicate's call tones — cuts cleanly through your speakers.

Your active windows drop behind a high-contrast federal override screen. Special Agent Vance of the Federal Cyber-Crimes & Homeland Oversight Bureau (FCCHO) appears on the video feed, his expression unreadable and razor-sharp.

"Kael," Vance says evenly. "Hands off the keyboard. We've been monitoring Aegis Logistics's dark subnet for four months — and we've also been watching you bounce spoofed routing packets between the Crimson Lotus and the Iron Syndicate for the past hour."

Your pulse spikes. In My Rig, the incoming trace monitor locks solid — not a crude mob trace, but a multi-hop federal triangulation grid pinpointing your exact sub-level apartment in the Lower East Side.

"I'm looking for my sister," you say, keeping your voice steady over the hum of your cooling fans. "Wren. Aegis took her."

"We know who Wren is, and we know you're the only operator in New York who just pulled clean root credentials out of `/sys_vault/chrysalis_intake/` without burning the datastore," Vance replies. "Right now, half the Manhattan waterfront is on fire because of the gang war you just sparked, and a federal tactical unit is five minutes from breaching your door."

Vance leans forward slightly, locking eyes with you through the lens.

"You have one way out of this, Kael. Use your root access inside Aegis's subterranean server farm in Battery Park to deploy our federal rootkit before the contractors wipe their drives and move the remaining subjects — including Wren. Cooperate with us, and we call off the raid, bury your involvement in tonight's gang war, and extract your sister alive. Refuse, and you spend the rest of your life in a federal black site while Wren disappears into Aegis's lab forever."

The feed holds steady on Vance's cold gaze, waiting for your decision as the distant wail of sirens rises above the storm outside the seawall. The trace meter keeps climbing.

---

## Act IV: The Battery Park Endgame

Act IV brings your Act II faction choice and your Act III response to Special Agent Vance into collision at Pier 44, Sub-Level B — the subterranean wetware clinic beneath Battery Park.

**Comply or defy.** Kael complies by deploying the FCCHO rootkit on Aegis's Battery Park server farm. He defies by beating the federal trace lock himself — reflecting it, spoofing it, or forging around it — before it completes. If the trace completes before he does either, the tactical unit takes him (Ending F).

**Leverage decides who keeps their word.** Nobody in this city honors a deal out of trust:

- Defy Vance on the Lotus or Iron path without your ally's kompromat, and the syndicate turns on you the moment Pier 44 is open (Ending E).
- On every path, Vance himself only keeps his word if you hold the recording from his own oversight relay. Without it, he locks you away once Wren is safe (Ending F).

The branch endings below describe what happens when Kael holds every piece of leverage he needs.

### Prelude: Preparing the Breach

With sirens echoing above the seawall and Vance's trace clock ticking down, you open your Browser to the black-market Hardware Exchange, spending your remaining credits on an enterprise QuantumBus I/O Controller and a multi-channel Cryo-NVMe Array. You slot the components into My Rig, reboot, and listen to the system's acoustic profile.

If you rush ahead without opening the CScript IDE, your rig's fans surge to a deafening shriek and the System Profiler flags severe command-coalescing and DMA-descriptor bottlenecks. Spending precious seconds on the live clock to debug the factory drivers (`BusController.drv` and `NVMeQueue.drv`) and compile clean batching routines drops your rig into a razor-quiet, high-throughput hum — and determines whether your hardware survives the final intrusion.

### Branch A: From Act II — Crimson Lotus Path

#### Ending A1: Comply with Special Agent Vance ("The Gilded Leash")

- **The Breach:** You accept Vance's ultimatum. Using the root keys pulled from `/sys_vault/chrysalis_intake/`, you open a direct FTP Browser and Port Sniffer tunnel into Battery Park's central server farm and inject the FCCHO federal rootkit. To cover your tracks, you route the rootkit's command frames through the very relay nodes you shared with Vex earlier.
- **The Leverage Call:** Before Aegis's automated purge daemon can wipe the facility drives, you force an encrypted video call to Director Marcus Hale, head of Aegis's Battery Park facility, flashing his skimmed black-budget ledgers on screen. Terrified of federal prosecution, he remotely disengages the magnetic containment locks on Sub-Level B.
- **The Outcome:** Federal tactical teams breach Pier 44 minutes before Vex's Crimson Lotus strike squad arrives. Caught in a pre-staged federal ambush, Vex and the Lotus waterfront leadership are wiped out or arrested in one sweep. You and Wren are extracted safely and relocated to a clean, climate-controlled corporate enclave above the seawall — rich, legitimate, and permanently bound to run black-budget cyber-ops whenever Special Agent Vance calls. The leash runs both ways: the recording you lifted from his oversight relay is the only reason the enclave isn't a cell.

#### Ending A2: Defy Vance & Double Down with the Crimson Lotus ("Silk Over Acid")

- **The Breach:** You reject Vance's offer. Using your patched network driver and a custom packet-reflection script in the CScript IDE, you bounce the federal triangulation trace back onto an abandoned municipal relay in Queens, buying yourself a four-minute blind spot before the tactical team hits your door.
- **The Leverage Call:** You force a video call to Director Hale using the stolen executive credentials and blackmail ledgers, threatening to dump his embezzlement records to OmniCorp Internal Affairs unless he drops Sub-Level B's perimeter fire-doors. At the same time, you open a second video feed to Vex, feeding him live camera telemetry and sentry patrol coordinates from your Crawler — and letting him glimpse a single frame of his own kickback recording, so he understands exactly what happens if Wren doesn't come home.
- **The Outcome:** Crimson Lotus enforcers storm Pier 44 through the unlocked bulkhead gates just as Aegis's private mercenaries try to evacuate the wetware pods. Amid the firefight, Vex's team pulls Wren out of containment and brings her back to you. With Aegis's Manhattan cell dismantled and the Iron Syndicate blindsided, the Crimson Lotus absorbs the smuggling corridor — elevating you from a sub-level code-scrabbler to Vex's untouchable Chief Digital Architect, even as you quietly watch Vex's every move for signs of future betrayal. Vance's recording keeps the Bureau at arm's length.

### Branch B: From Act II — Iron Syndicate Path

#### Ending B1: Comply with Special Agent Vance ("Cold Iron, Clean Slate")

- **The Breach:** Realizing Silas only wants Aegis's neural-processor hardware for Brooklyn's black-market labs, you accept Vance's deal. You deploy the FCCHO rootkit into Battery Park's mainframe while feeding Silas's IronClad exploit signatures directly into the federal watchdog's packet-capture logs — handing Vance the cryptographic proof needed to indict the Iron Syndicate's leadership.
- **The Leverage Call:** Using the stolen blackmail files in My Rig, you force a video call to Director Hale and coerce him into unlocking the Sub-Level B medical wing before Aegis's cleaners can liquidate BIO-ASSET-09.
- **The Outcome:** Federal tactical units hit Pier 44 and simultaneously raid the Brooklyn warehouse Silas broadcast from in Act III. Both the Iron Syndicate and the crippled remnants of the Crimson Lotus are dismantled overnight. With his own recording sitting in your vault, Vance honors his word: your criminal record is erased, Wren is pulled from the wetware clinic unharmed, and the two of you receive clean-zone transit passes out of the flood zones — though you know you can never touch an unauthorized terminal again without federal eyes watching.

#### Ending B2: Defy Vance & Strike with the Iron Syndicate ("Heavy Metal")

- **The Breach:** You slam the door on Vance's ultimatum, weaponizing the IronClad kernel injectors Silas gave you in Act III. In the CScript IDE, you chain a descriptor-flood payload into Aegis's gate controllers while spoofing your own apartment's MAC and routing headers to stall the incoming federal raid.
- **The Leverage Call:** You open a forced video call to Director Hale, holding his skimmed accounts hostage on screen until he overrides the biometric seals on Sub-Level B. With the doors unlocked and Aegis's automated turrets blinded by your IronClad rootkit, you signal Silas over encrypted comms — reminding him, with a few seconds of his own dock-massacre footage, what reaches every newsfeed in New York if Wren isn't delivered safely.
- **The Outcome:** Iron Syndicate heavy crews breach Pier 44 from the river side in armored barges, tearing through Aegis's security detail before federal tactical teams can mobilize past the flooded Lower East Side checkpoints. Silas's crew extracts Wren from the holding bay and delivers her safely to your new fortified safehouse in Brooklyn. The Iron Syndicate seizes Aegis's wetware schematics and claims total control of New York's docks, making you their highest-paid technical architect — and a permanent name on the FCCHO's most-wanted list, one Vance can never act on while you hold his recording.

### Branch C: From Act II — Lone Wolf Path

#### Ending C1: Comply with Special Agent Vance ("Controlled Burn")

- **The Breach:** With half the waterfront burning from the gang war you sparked and federal breach charges outside your door, you take Vance's lifeline. You inject the FCCHO rootkit into Battery Park's core servers and hand over the unedited routing logs proving how both Vex and Silas colluded with Aegis's smuggling ring.
- **The Leverage Call:** You force a video call to Director Hale using his stolen executive credentials, leveraging his personal blackmail vault to force open Sub-Level B's containment bays and cut power to the facility's neural-extraction rigs.
- **The Outcome:** Vance calls off the breach team outside your apartment and sends federal units straight into Pier 44, rescuing Wren while rounding up the surviving Crimson Lotus and Iron Syndicate lieutenants caught in the harbor crossfire. Vance buries your role in inciting the waterfront riots in exchange for your permanent cooperation as an off-the-books federal cyber-consultant — keeping you and Wren safe above the seawall, forever in the Bureau's pocket, and Vance forever in yours.

#### Ending C2: Defy Vance & Burn Everyone ("The Ghost in the Flood")

- **The Breach:** You refuse to kneel to Aegis, the syndicates, or the Feds. Working at fever pitch in the CScript IDE, you link a custom distributed-broadcast module (`deadman_broadcast.csc`) from CS-Vault into your patched network driver, turning your rig into a high-speed dead-man's switch.
- **The Leverage Call:** You initiate a forced video call to Director Hale — not to negotiate, but to watch his face as you simultaneously broadcast the entire `/sys_vault/chrysalis_intake/` wetware archive, Aegis's blackmail ledgers, both syndicates' smuggling manifests and kompromat, and Special Agent Vance's black-sting recording to every public news node and pirate mesh relay across the Eastern Seaboard. While Aegis's network buckles under millions of incoming connections, you execute a final IronClad lock-override script to pop every door in Sub-Level B and guide Wren out through the flooded metro tunnels over voice comms.
- **The Outcome:** As federal agents finally kick down your apartment door, your rig executes a self-written firmware wipe, bricking your drives into useless silicon. With New York erupting into riots, corporate stock collapses, and gang warfare, you and Wren slip into the unmonitored flood-zone tunnels with untraceable cash and no digital footprint — completely broke, completely off the grid, and finally free.

### Fail-State Endings

#### Ending D: Thermal Cascade ("Kernel Panic") — any branch

- **Trigger:** You enter the final Pier 44 breach without upgrading your hardware or fixing the command-protocol inefficiencies in your CScript drivers, and the rig overheats mid-breach. (Not possible in Street Ghost mode.)
- **The Outcome:** Halfway through uploading your final payload and forcing open Sub-Level B's containment locks, unbatched bus interrupts and CPU-bound storage reads push your rig past thermal limits. A violent electrical pop echoes from your power supply, your speakers emit a continuous flatline tone, and GHOSTRIG locks into an unrecoverable kernel panic — freezing Pier 44's magnetic doors mid-cycle during the federal and syndicate crossfire, leaving Wren's fate unknown as your screen goes dark.

#### Ending E: Betrayal ("Paid in Full") — Crimson Lotus or Iron Syndicate, defying Vance

- **Trigger:** You defy Vance and see the breach through for Vex or Silas without holding that syndicate's kompromat.
- **The Outcome:** The bulkhead gates open exactly as planned — and your ally's crew walks straight past the holding bay. Over the comms, Vex (or Silas) thanks you for the clean work, explains that Aegis pays better for a full shipment than for a rescued one, and that loose ends who know the corridor don't get to retire. You hear the door of your apartment give way a minute later. Wren stays in the manifest as BIO-ASSET-09, and her pod is loaded onto the next orbital freighter on schedule.

#### Ending F: Federal Capture ("Concrete and Glass") — any branch

- **Trigger:** You reach the end without kompromat on Special Agent Vance — or the federal trace lock completes before you either comply or defeat it.
- **The Outcome:** Federal tactical units hit Pier 44 and pull Wren from containment alive; Vance makes sure the cameras catch the rescue. Then the second team hits your door. Without anything to hold over him, Vance has no reason to keep a loose, talented operator on the street: you are charged under every cyber-crimes statute the Bureau can stack and disappear into a federal facility. Your last view of Wren is a news clip of her wrapped in a thermal blanket on the pier, searching the crowd for a brother who isn't coming.
