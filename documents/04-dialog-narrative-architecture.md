# GHOSTRIG — Dialog & Event-Driven Narrative Architecture

*Document 4 of 7 — Architecture: Macro-Timeline Spine, Action-Choice DAG & Kernel Telemetry Listeners*

---

## 1. Core Narrative Architecture: Implicit Action Sensing

Rather than relying on traditional dialogue trees that halt gameplay to ask the player explicit multiple-choice questions (e.g., "Choose a faction: Crimson Lotus, Iron Syndicate, or Lone Wolf"), GHOSTRIG employs a **Quality-Based Narrative (QBN) Storylet Engine** paired with an **Event-Driven Action-Choice Directed Acyclic Graph (DAG)**.

The runtime observes what the player actually executes inside the simulated operating system — which servers they probe, which files they modify, where they upload intelligence, and what kompromat they extract — and infers narrative decisions directly from player action.

### 1.1 The Three Structural Pillars

1. **Barebones Macro-Timeline (Act State Machine):** A linear spine defining the four acts (Act I through Act IV) and hard narrative convergence gates: discovering BIO-ASSET-09, reaching the Act II subnet fork, Special Agent Vance's Act III federal trace lock, and the Pier 44 endgame breach.
2. **Action-Choice Directed Graph (Transition Map):** Every narrative branch is modeled as a graph edge pairing a Player Choice ID with a concrete Observable Kernel Action Signature and Downstream Storylet Nodes.
3. **Scoped Kernel Event Listeners:** Because all CScript applications and websites invoke C# system libraries (`Fs`, `Net`, `Proc`, `Sys`, `Media`) through the kernel, the Narrative Runtime attaches lightweight telemetry listeners to the kernel's event bus. Only listeners belonging to the player's active timeline frontier are armed at any given moment.

### 1.2 Live Time

The simulation clock never pauses (Document 3, §2.2). Calls, trace locks, and breaches run in live time while the player keeps working on the desktop, including in the CScript IDE.

## 2. Mode-Specific Choice Presentation

While all three game modes execute against the exact same underlying Macro-Timeline and Action-Choice DAG, the presentation layer adapts to match each player archetype's preferred level of guidance.

| Game Mode | Choice Mechanism | Runtime Behavior |
|---|---|---|
| **Bare-Metal Mode** (Programmer / Hacker) | 100% implicit, action-driven | Zero explicit story-branch menus. The player deduces options from logs and calls, crafts payloads or spoofed dumps in CScript/Notepad, and executes network transfers. Kompromat must be discovered organically. Listeners detect the action and advance the timeline automatically. |
| **Silicon & Bus Mode** (Hardware Engineer) | 100% implicit, action-driven | Identical implicit action sensing to Bare-Metal Mode. Narrative branches trigger strictly from the player's executed system operations and hardware readiness, without UI prompts. Kompromat must be discovered organically. |
| **Street Ghost Mode** (Cyberpunk Fan) | Hybrid: action graph + guided story prompts | Preserves the full Action-Choice DAG underneath, while surfacing explicit diegetic choice buttons in CommLink and tactical objective prompts (including kompromat targets) in Kael's Notepad scratchpad. Selecting a guided option either executes the corresponding narrative commitment or highlights the exact target node/action that moves the story forward. |

## 3. Authoring Data Representation

Dialogue sequences and action-choice bindings are authored in declarative JSON schemas stored in `content/narrative/`, decoupled from C# engine code and CScript UI views.

### 3.1 Storylet & Action-Edge Schema

Each narrative node defines its timeline prerequisites, incoming communication beats, armed kernel listeners, and atomic world-state mutations:

```json
{
  "storylet_id": "map_dark_subnet_faction_fork",
  "act": 2,
  "prerequisites": "act == 2 && betrayal_logs_verified == true",
  "armed_listeners": [
    {
      "choice_id": "side_with_crimson_lotus",
      "event_type": "Net.FtpUpload",
      "match": {
        "dest_host": "vex-drop.lotus.ny56",
        "payload_hash": "SHA256_REAL_AEGIS_MAP"
      },
      "guided_prompt_text": "[Street Ghost] Upload the real Aegis map to Vex's drop-node.",
      "effects": [
        "faction_path = 'crimson_lotus'",
        "act = 3"
      ],
      "next_storylet": "lotus_poison_well_audio"
    },
    {
      "choice_id": "side_with_iron_syndicate",
      "event_type": "Net.FtpUpload",
      "match": {
        "dest_host": "silas-deaddrop.iron.ny56",
        "payload_hash": "SHA256_REAL_AEGIS_MAP"
      },
      "guided_prompt_text": "[Street Ghost] Send the true Aegis topology to Silas in Brooklyn.",
      "effects": [
        "faction_path = 'iron_syndicate'",
        "act = 3"
      ],
      "next_storylet": "syndicate_burn_bridge_crypto"
    },
    {
      "choice_id": "play_both_factions_lone_wolf",
      "event_type": "Composite.Sequence",
      "match": {
        "actions": ["upload_spoof_to_vex", "upload_spoof_to_silas"],
        "requires_kompromat": ["lotus", "iron"]
      },
      "guided_prompt_text": "[Street Ghost] Leak doctored packages to both syndicates to spark a turf war.",
      "effects": [
        "faction_path = 'lone_wolf'",
        "act = 3"
      ],
      "next_storylet": "lone_wolf_orchestrate_collision"
    }
  ]
}
```

The Lone Wolf edge requires both syndicates' kompromat because the doctored packages use each faction's own secrets to frame it. In Bare-Metal and Silicon & Bus modes the player must already have breached both syndicates' private networks (reachable from the drop-node credentials handed out in Act II) on their own initiative.

### 3.2 Act III Vance Response Listeners

Vance's federal trace lock (Act III, Scene 3 on every path) arms these edges. The trace runs in live time.

| `choice_id` | Observable action signature | Effect |
|---|---|---|
| `comply_with_vance` | `Proc.RemoteExec` of the FCCHO rootkit (`fccho_rootkit.csc`) on the Aegis Battery Park server farm | `vance_choice = 'comply'` |
| `defy_vance` | `Net.TraceDeflected`: the federal trace lock is defeated by player action (packet-reflection script, MAC/route spoofing, IronClad header forgery, etc.) before it completes | `vance_choice = 'defy'` |
| `trace_completes` | `Net.TraceLockComplete` with neither of the above | `vance_choice = 'captured'` → Ending F |

Street Ghost mode surfaces **Comply** and **Defy** as CommLink buttons; selecting one highlights the exact action to perform.

### 3.3 Dialogue Beat Schema

When a listener triggers an incoming or outgoing communication (such as an encrypted video call from Rook, Vex, Silas, or Special Agent Vance), the Dialogue Player consumes a structured beat list:

- **Channel & Presentation:** Specifies the CForms client (`CommLink_Video`, `DeadDrop_Chat`, `Scanner_Feed`), the audio ring/alert signature (e.g., sub-bass pulse for Vex, carrier static for Silas, federal chime for Vance), and the visual shader filter (monochrome scanlines vs. high-contrast federal crest override).
- **Beat Lines & Mid-Call Hooks:** Sequential dialogue lines supporting variable interpolation (e.g., referencing cracked usernames or timestamps) and synchronous mid-line side effects (e.g., spawning a CS-Vault link, granting drop-node credentials, transferring credits, or locking a live federal trace meter onto My Rig).
- **Interruptible Live Window:** While a video call is active, the desktop remains interactive and the clock keeps running. Players can capture a freeze-frame (saved as an image file), trace an incoming IP, or run a packet-reflection script mid-conversation, firing parallel listeners that alter the call's resolution.

## 4. Rules for Conditions, Leverage Gates & Effects

### 4.1 Deterministic World-State Blackboard

All narrative state lives in a strongly typed, deterministic key-value blackboard managed by the Narrative Runtime and serialized with the player's save:

- **Primitives:** Booleans (`betrayal_logs_verified`, `endgame_rig_stable`), integers (`act`, `open_port`), and strings (`faction_path` ∈ {`crimson_lotus`, `iron_syndicate`, `lone_wolf`}, `vance_choice` ∈ {`comply`, `defy`, `captured`}).
- **Vault Evidence Inventory:** Tracks specific files copied into Kael's My Rig secure vault, tagged with cryptographic content hashes so doctored files are distinguished from authentic ones. Derived flags:
  - `kompromat.lotus`: the authentic decrypted Vex recording is in the vault.
  - `kompromat.iron`: the authentic decrypted Silas recording is in the vault.
  - `kompromat.vance`: the authentic decrypted Vance recording is in the vault.

### 4.2 Mandatory Leverage (Kompromat) Rules

Holding audiovisual proof (kompromat) is enforced as a hard condition on the endgame:

- **Single-Faction Alliance (`crimson_lotus` or `iron_syndicate`):** When the player defies Vance and finishes the game for a syndicate, they must hold that syndicate's kompromat (Vex's wetware-trafficking kickbacks or Silas's cartel-massacre recording). Without it, the syndicate betrays Kael once Pier 44 is breached: **Kael is killed and Wren remains Aegis cargo** (Ending E).
- **Faction War (`lone_wolf`):** Igniting the waterfront war requires both syndicates' kompromat at the Act II fork (§3.1), so a Lone Wolf player always holds it.
- **Special Agent Vance (all paths, comply or defy):** The player must hold kompromat on Vance, obtained by breaching the FCCHO restricted oversight relay. Without it, Vance turns on Kael at the end: **Kael is arrested and imprisoned, but Wren is saved** (Ending F). This applies even when Kael complied.

### 4.3 Endgame Resolution Order

At the Pier 44 breach, outcomes resolve in this fixed order; the first matching rule ends the game:

1. **Kernel Panic (D):** A `Sys.ThermalCascade` event during the breach storylet (never in Street Ghost mode).
2. **Federal Capture by trace (F):** `vance_choice == 'captured'`.
3. **Betrayal (E):** `vance_choice == 'defy' && faction_path ∈ {crimson_lotus, iron_syndicate} && !kompromat[faction_path]`.
4. **Federal Capture by leverage (F):** `!kompromat.vance`.
5. **Branch ending:** `faction_path × vance_choice` → A1, A2, B1, B2, C1, or C2.

### 4.4 Atomic Effect Execution

When an Action Listener matches or a Street Ghost guided choice is confirmed, all associated effects execute as a single atomic transaction:

1. Mutate world-state blackboard variables.
2. Disarm obsolete listeners from the exited storylet frontier.
3. Grant or revoke network credentials, CS-Vault repository tokens, or credit packets.
4. Advance the Macro-Timeline cursor and arm listeners for the newly unlocked storylet(s).

## 5. Static Validation Integrity Pipeline

Because narrative progression relies on kernel event listeners rather than simple linear text links, an offline **Narrative Graph Validator** runs at build time to enforce structural soundness:

- **Schema & Type Checking:** Verifies that every condition expression parses cleanly and references declared variables in the world-state schema with matching types.
- **Action Signature Verification:** Cross-checks every `armed_listener` against valid kernel event types (`Net.FtpUpload`, `Fs.Copy`, `Proc.HotSwapDriver`, `Proc.RemoteExec`, `Net.TraceDeflected`, etc.), existing network hostnames/IPs, and actual files present in server VFS manifests.
- **Reachability & Dead-End Audit:** Performs a graph traversal from Act I, Scene 1 across all combinations of faction paths, Vance responses, kompromat holdings, and hardware stability flags (`endgame_rig_stable`) to confirm that every non-terminal storylet has at least one valid exit and every terminal leaf maps to one of the nine defined endings (A1, A2, B1, B2, C1, C2, D, E, F).
- **Mode Parity Check:** Ensures every Action Listener edge defines a corresponding `guided_prompt_text` entry so Street Ghost Mode never encounters an unguided dead end.
