# GHOSTRIG — CScript Language Specification

*Document 7 of 7 — Language, Runtime Contract & Standard Library*

> **Status: approved design decisions (D1–D7); implementation in progress (M2).** This document defines the language players read and write for every application, website, library and driver in GHOSTRIG. It binds Documents 2, 3 and 5.

---

## 0. Design decisions

| # | Decision | Alternative considered |
|---|---|---|
| D1 | **C#-family syntax**, statically typed, with `var` inference — matches every code sample in Docs 5–6. | A Python/Lua-style dynamic language. |
| D2 | **Top-level declarations are public by default**; `private` hides them. This keeps the Doc 5 driver sample valid as written. | Private by default with an explicit `public`. |
| D3 | **Explicit `async` / `await`.** Operations that consume simulated time (network, disk, crypto, media, timers) return `Task` / `Task<T>`; `await` suspends the current function until the simulated clock reaches completion, and the UI stays responsive meanwhile (§8.3). | Implicitly blocking syscalls. |
| D4 | **Batteries included.** Every general-purpose data structure — including LRU/LFU caches, Bloom filters, tries, interval trees and skip lists — is in the standard library. The challenge of the driver puzzles is knowing *that* and *where* a cache or batch belongs, not re-implementing one. CS-Vault hosts exploit suites, community drivers and patches. | Withhold puzzle-relevant structures and ship them only via CS-Vault. |
| D5 | **The standard library is native** (C#, fast, step-metered by published cost tables). Players read its contracts — signatures, documentation and step costs — in the IDE's API browser, not its source. | Inspectable CScript source. |
| D6 | **`int` is 64-bit two's complement with wrapping arithmetic**, as packet and hash code expect; `Math.Checked*` helpers detect overflow. | Overflow throws. |
| D7 | **`float` is IEEE-754 binary64** under strict determinism rules (§9); transcendental functions are implemented in software inside the VM. | Fixed-point only. |

---

## 1. Design goals

1. **Readable by non-experts, satisfying for experts.** Street Ghost players should be able to follow a driver diff. Bare-Metal players should be able to write a packet-reflection payload or a generic cache from scratch.
2. **Deterministic by construction.** No language feature can observe the host machine, wall-clock time, thread scheduling or unordered iteration (§9).
3. **Every step costs.** Instructions, allocations and library calls are metered (§8.4), so efficiency is a property of the code the player writes.
4. **Capability-safe.** What a module may touch depends on its kind (app, library, driver, page) and is checked at compile time and again at load time (§10).
5. **Recoverable.** Faults roll back the current event, never the host (§8.6).

## 2. Source files and module kinds

Every CScript source file holds exactly one module. Its kind is fixed by its extension (Doc 5 §4.1):

| Extension | Kind | Entry point | Typical location |
|---|---|---|---|
| `.csc` with `@App` | Application | `void Main(Args args)` or `async Task Main(Args args)` | `/apps/`, `/home/kael/bin/` |
| `.csc` without `@App` | Library | none (imported) | `/sys/lib/`, `/lib/vault/`, `/lib/user/` |
| `.drv` | Device driver | `IKernelDriver` contract (§11) | `/sys/drivers/<class>/` |
| `.cform` | CForms page (website or app view) | `Form Build(PageContext ctx)` (may be `async Task<Form>`) | `/srv/www/public/`, `/apps/<app>/views/` |
| `.csc` with `@Test` | Test suite | test functions (§14.4) | anywhere |

```c
// Header of every module
@Attribute(args)*          // optional attributes (see §5.7)
module Name;               // must match the file name without extension
import Std.Text;           // zero or more imports
import Net as N;           // alias
import vault.ironclad;     // CS-Vault package (§12)
```

## 3. Lexical structure

- **Encoding:** UTF-8. Identifiers are ASCII letters, digits and `_`, and do not start with a digit.
- **Comments:** `// line`, `/* block */`, and `/// doc comment` (shown in the IDE's hover help).
- **Keywords:** `module import as private const var if else while do for foreach in break continue return switch case default try catch finally throw new null true false this base class struct interface enum where is fn yield typeof params async await static`. `get`, `set` and `void` are contextual.
- **Literals:**
  - integers: `42`, `0x2A`, `0b101010`, `1_000_000`
  - floats: `3.14`, `1e-9`
  - characters: `'A'`, `'\n'`, `'⁂'`
  - strings: `"text"`, `@"verbatim\path"`, and interpolated `$"{user}@{ip}"`
  - byte strings: `b"\x89PNG"` (type `bytes`)

## 4. Types

### 4.1 Built-in value types

| Type | Meaning |
|---|---|
| `bool` | `true` / `false` |
| `byte` | Unsigned 8-bit |
| `char` | Unicode scalar value |
| `int` | 64-bit signed, wrapping (D6) |
| `float` | 64-bit IEEE-754 (D7) |
| `Duration`, `Instant` | Simulated time (nanosecond precision) |

### 4.2 Built-in reference types

- `string`: immutable Unicode text. `s[i]` returns `char`, `s.Length` gives the length, and `+` concatenates.
- `bytes`: immutable byte sequence; `ByteBuffer` (§13) is its mutable builder.
- `T[]`: fixed-length mutable array with `.Length` and `.Slice(start, count)`, created with `new T[n]` or `[a, b, c]`.
- `fn(A, B) -> R`: function type; `fn(A) -> void` for actions.
- Parentheses group a type: `(fn(string) -> void)?` is a nullable function type, whereas `fn() -> int?` returns `int?`.
- `Task` / `Task<T>`: a pending simulated-time operation (§5.6).
- Tuples: `(int, string)`, destructured with `var (code, msg) = Probe();`.

### 4.3 User-defined types

```c
struct Packet { bytes Payload; int Port; }            // value semantics (copied)
class Session { private string user; Session(string u) { user = u; } }   // reference semantics
enum TransferMode { CPU, DMA }
interface Comparable<T> { int CompareTo(T other); }
class LruCache<K, V> where K : Hashable { ... }      // generics with interface constraints
```

- Classes support single inheritance (`class A : B, IFoo`), `base` calls, and interface implementation. Members are `public` unless marked `private`.
- Instance methods are **virtual by default** (Java-style): a derived class that declares a method with the same name and parameter types overrides it, and `base.Method()` calls the inherited version.
- `static` members belong to the type rather than an instance: `int.Parse(s)`, `Math.Min(a, b)`, `Task.WhenAll(…)`. Module-level functions are always static.
- Generics are type-checked at compile time. Constraints are interfaces only. The built-in constraint interfaces are `Hashable`, `Comparable<T>`, `Equatable<T>`, `Iterable<T>` and `Serializable`.
- **Nullability:** reference types are non-null by default; `T?` admits `null`. The operators `?.`, `??` and `!` behave as in C#, and the compiler tracks null-state.
- `is` performs type tests, and `x is Session s` binds. `(T)x` is a checked cast that throws `CastError`.

## 5. Declarations

### 5.1 Module-level members

Functions, `const` values, module globals, and the types above. Globals are per-process and initialized in declaration order on first load.

### 5.2 Functions

```c
int SendQueue(Packet[] messages) { ... }
T Max<T>(T a, T b) where T : Comparable<T> => a.CompareTo(b) >= 0 ? a : b;
void Log(string msg, int level = 1) { ... }             // default parameters
int Sum(params int[] xs) { ... }                        // variadic
```

### 5.3 Lambdas and closures

```c
fn(int) -> int square = x => x * x;
button.Click += (sender, e) => status.Text = "clicked";
```

Closures capture variables by reference.

### 5.4 Properties and indexers

Classes may declare `int Count { get => n; }` and `V this[K key] { get ...; set ...; }`.

### 5.5 Iterators

A function that returns `Iterable<T>` may use `yield return x;` and `yield break;`. Iteration is lazy and each resumption is metered.

### 5.6 Asynchronous functions

```c
async Task<PortState[]> ScanAsync(IpAddress ip, int[] ports) {
    var results = new List<PortState>();
    foreach (var port in ports) {
        results.Add(await Net.ProbeAsync(ip, port));   // suspends until the probe completes in simulated time
    }
    return results.ToArray();
}
```

- `async` functions return `Task` or `Task<T>`; `await` is only legal inside them. Event handlers, lambdas, `Main` and `Build` may be `async`.
- Every library operation that consumes simulated time is asynchronous and named `…Async` (`Net.ResolveAsync`, `Fs.ReadAsync`, `Crypto.HashAsync`, `Task.Delay(Duration)`). Cheap, instantaneous queries stay synchronous (`Fs.Exists`, `Sys.Now`).
- Calling an async function without `await` starts it and yields the `Task` (fire-and-forget is a compiler warning unless the task is stored or passed to `Proc.Track`). Combinators: `Task.WhenAll`, `Task.WhenAny`, `Task.Timeout`, and `CancellationToken` support on every `…Async` API.
- `async` iterators: `async Iterable<T>` with `await foreach` (e.g. streaming a large file in blocks).
- **Drivers are synchronous.** Driver entry points cannot be `async` and cannot `await`; their `Hardware` calls are billed to the syscall that invoked them (§11).

### 5.7 Attributes

| Attribute | Applies to | Meaning |
|---|---|---|
| `@App("Name")`, `@Icon("chart")` | module | Declares an application (launcher entry) |
| `@Driver("Device name")`, `@Version("1.0.4-stock")`, `@Implements("INetDriver")` | module | Declares a driver and the contract it satisfies |
| `@Page("Title")` | module | CForms page title |
| `@Capability("fs.write")` | module | Requests an optional capability (§10) |
| `@Persist` | global | Carries the value across a driver or app hot-swap (§8.7) |
| `@Test`, `@TestCase(args)` | function | Unit-test discovery (§14.4) |
| `@Budget(steps)` | function | Requests a larger per-event step budget, within the kind's ceiling |
| `@Deprecated("msg")` | any | Compiler warning at use sites |

## 6. Statements and expressions

- **Statements:**
  - `var` / typed locals and `const`
  - `if`/`else`, `while`, `do … while`
  - `for (init; cond; step)` and `foreach (T x in xs)`
  - `switch` with `case` on integers, strings and enums, plus type patterns `case Packet p:`
  - `break`, `continue`, `return`, `throw`, `try`/`catch`/`finally`, `yield`
- **Switch expressions:** `var label = mode switch { TransferMode.CPU => "polled", TransferMode.DMA => "dma", _ => "?" };`
- **Assignment:** `=`, `+= -= *= /= %= &= |= ^= <<= >>=`, `++`, `--`.

**Operator precedence** (highest first):

| Level | Operators |
|---|---|
| 1 | `x.y` `x?.y` `f(x)` `a[i]` `x++` `x--` `new` `typeof` `x!` |
| 2 | `+x` `-x` `!x` `~x` `++x` `--x` `(T)x` |
| 3 | `*` `/` `%` |
| 4 | `+` `-` |
| 5 | `<<` `>>` `>>>` |
| 6 | `<` `>` `<=` `>=` `is` |
| 7 | `==` `!=` |
| 8 | `&` |
| 9 | `^` |
| 10 | `\|` |
| 11 | `&&` |
| 12 | `\|\|` |
| 13 | `??` |
| 14 | `?:` |
| 15 | assignment, `=>` |

Integer `/` and `%` by zero throw `ArithmeticError`. `>>` is arithmetic and `>>>` is logical. `==` on strings, `bytes`, tuples and structs is structural; on classes it is reference equality unless `Equatable<T>` is implemented.

## 7. Errors

Errors are objects deriving from `Error` (`Code`, `Message`, `StackTrace`). Every kernel fault surfaces with its authentic code (Doc 2 §4.1):

| CScript type | Code | Raised by |
|---|---|---|
| `AccessDenied` | `403` | `Fs`, `Net` sessions |
| `NotFound` | `ENOENT` | `Fs` |
| `AlreadyExists` / `NotEmpty` / `IsDirectory` / `NotDirectory` | `EEXIST` / `ENOTEMPTY` / `EISDIR` / `ENOTDIR` | `Fs` |
| `InvalidPath` | `EINVAL` | `Fs`, `Std.Path` |
| `DnsError` | `NXDOMAIN` | `Net.Resolve` |
| `HostUnreachable` / `ConnectionRefused` | `EHOSTUNREACH` / `ECONNREFUSED` | `Net` |
| `AuthFailed` | `401` | `Net` logins |
| `ProtocolError` | `EPROTO` / `HW_PROTOCOL` | `Net`, `Sys.Hardware` |
| `HardwareError` | `HW_CONFIG` / `HW_STATE` | `Sys.Hardware`, `Shop` |
| `OutOfMemory` | `ENOMEM` | allocation, `Sys` |
| `ArithmeticError`, `IndexError`, `NullError`, `CastError`, `FormatError`, `ArgumentError` | `EARITH` … | VM and `Std` |
| `CompileError` | `CSC####` | `Compiler` |

Two faults are **uncatchable**: `StepBudgetExceeded` and `KernelPanic`. They abort the event and trigger rollback (§8.6). Players can define their own error classes by deriving from `Error`.

## 8. Execution model

### 8.1 Processes

Every running application, page and background worker is a process with:
- its own heap and module globals,
- a principal (the user it runs as),
- a capability set (§10),
- a mailbox of events.

Each process runs a single-threaded event loop. Awaited tasks resume on that loop; there is no parallel execution inside a process.

Drivers are not processes: they are kernel-resident modules invoked synchronously by system libraries.

### 8.2 Entry points

- **Apps:** run `Main(Args args)`, which typically builds a `Form` and returns. The process then lives for as long as its windows or workers do.
- **Pages:** run `Build()` in the Browser's sandbox process for that site.
- **Drivers:** called through their contract (§11).

### 8.3 Scheduling and the two clocks

Each host frame grants the kernel one frame of simulated time (16.67 ms at 1× speed). The scheduler runs runnable processes round-robin by process id, in fixed quanta of steps, until their billed cycles fill the slice.

When a function `await`s a time-consuming operation (network, disk, crypto, media), its continuation is queued to resume when the simulation clock reaches the operation's completion instant (D3); meanwhile the process keeps handling other events. Continuations that become ready at the same instant run in the order their operations were issued. Input events are stamped with the frame's simulated time. A slow rig therefore completes fewer steps per simulated second, and that is what makes it feel slow, while the host frame rate is unaffected (Doc 2 §2).

### 8.4 Metering

- Every bytecode instruction costs 1 step. Steps convert to CPU cycles as `cycles = steps × 1000 / ipc_milli` and are billed to the CPU, adding heat and simulated time.
- Allocation costs `1 + bytes/64` steps and charges the process's simulated RAM (`OutOfMemory` when exhausted). Garbage collection is deterministic: it is triggered by allocation volume, never by host memory pressure, and is itself metered.
- Native library calls cost published step formulas (e.g. `List.Sort` = `n·⌈log₂n⌉`; `Regex.Match` = 1 per backtracking step). Library code cannot escape metering.

### 8.5 Step budgets

| Context | Default budget per invocation | Ceiling with `@Budget` |
|---|---|---|
| UI event handler | 2,000,000 | 8,000,000 |
| Page `Build` / page event | 1,000,000 | 2,000,000 |
| Driver entry point | 500,000 | 2,000,000 |
| Background worker | unlimited (time-sliced) | — |

Long computation belongs in a worker (`Proc.Spawn`); long waiting belongs behind `await`.

### 8.6 Faults and rollback

Before every event handler or driver entry point, the kernel takes a copy-on-write snapshot of the process heap and module globals. An unhandled error, or an uncatchable fault, restores that snapshot, reports a profiler fault with a stack trace, and keeps the process alive. Effects already committed to the kernel (bytes on the wire, files written) are not undone; they are real.

### 8.7 Hot-swap

Recompiling a running module swaps its bytecode at the next instruction boundary between events. `@Persist` globals whose name and type are unchanged carry over; everything else is re-initialized. If a driver's `Initialize` throws, the kernel reverts to the previous image (Doc 3 §2.1).

## 9. Determinism rules

1. There is no access to host time, host randomness, host threads, environment variables or host files. `Sys.Now` is simulated time.
2. `Random` is seeded from the kernel's deterministic generator per process (`new Random(seed)` is also allowed).
3. `Map` and `Set` iterate in **insertion order**; `SortedMap` and `SortedSet` iterate in key order. Hash codes are deterministic, and string hashing is seeded per save rather than per host process.
4. Float arithmetic follows binary64 round-to-nearest-even with no fused multiply-add. `Math.Sin` and the other transcendental functions are software implementations with identical results on every platform. Formatting and parsing use invariant culture.
5. The order in which events are delivered to processes is defined entirely by simulated time and process id.

## 10. Capabilities and sandboxing

What each module kind may import:

| Library | App `.csc` | Driver `.drv` | Page `.cform` (remote) | Portal sandbox |
|---|---|---|---|---|
| `Core`, all `Std.*` | ✓ | ✓ (no `Std.Test`) | ✓ | ✓ |
| `Gfx`, `Input`, `CForms`, `Media` | ✓ | ✗ | ✓ (own page surface; same-site media only) | ✓ |
| `Fs` | ✓ (as the process principal) | ✗ | ✗ (`Page.Storage` only) | ✓ (sandbox FS) |
| `Net` | ✓ | ✗ | same-origin `Http` only | ✓ (sandbox world) |
| `Proc`, `Log`, `Compiler`, `Debug` | ✓ | `Log` only | ✗ | ✓ |
| `Sys` (time, info, telemetry) | ✓ | time only | time only | ✓ |
| `Sys.Hardware` | ✗ | ✓ (own bound device) | ✗ | ✓ (sandbox rig) |
| `Comm`, `Shop`, `Account`, `Vault` | ✓ | ✗ | ✗ | `Vault` only |

- Libraries inherit the capabilities of the module that imports them: importing a library that uses `Fs` into a page is a compile error naming the chain.
- Optional capabilities such as `@Capability("net.raw")` (raw packet crafting) and `@Capability("proc.hotswap")` are granted at install time by the user.
- Stock system apps hold a signed capability set.

## 11. Driver contract

A driver is a module whose public functions implement `IKernelDriver` plus exactly one device-class interface (Doc 3 §3.1). The compiler verifies signatures against `@Implements`.

```c
interface IKernelDriver {
    void Initialize(DriverContext ctx);   // bind device, set modes, allocate rings
    void Shutdown();
}
interface INetDriver     : IKernelDriver { int SendQueue(Packet[] messages); int PollSockets(Socket[] sockets); }
interface IStorageDriver : IKernelDriver { bytes ReadBlock(int address, int size); void WriteBlock(int address, bytes data); }
interface IGpuDriver     : IKernelDriver { void SubmitFrame(Triangle[] triangles); }
interface IAudioDriver   : IKernelDriver { void QueueSamples(Sample[] samples); }
interface ICryptoDriver  : IKernelDriver { bytes EncryptBlocks(bytes data, Key key); bytes DecryptBlocks(bytes data, Key key); }
interface IBusDriver     : IKernelDriver { void Service(Irq[] pending); }
interface INvmeDriver    : IKernelDriver { int SubmitQueue(NvmeCommand[] commands); }
```

Inside a driver, `Hardware` (from `import Sys.Hardware;`) is the device the driver is bound to. Its calls are the **only** operations the kernel bills; each call is one `DeviceCommand` submission (`N_calls` in the cost formula):

| Device | `Hardware` surface |
|---|---|
| NIC | `SendPacket(p)`, `SendPacketBatch(ps)`, `Poll(s)`, `PollBatch(ss)`, `RingSize`, `MaxBatch` |
| Storage / NVMe | `Mode` (`TransferMode.CPU` / `DMA`), `Read(address, size)`, `Write(address, data)`, `FlushCache()`, `SubmitQueue(cmds)` |
| GPU | `DrawSingle(triangle)`, `Draw(batch)`, `MaxDrawBatch` |
| Audio | `Queue(samples)`, `FlushBuffer()`, `BufferFrames` |
| Crypto (CPU) | `ExpandKey(key) -> KeySchedule`, `EncryptBlock(block, schedule)`, `DecryptBlock(…)` |
| Bus | `Acknowledge(irq)`, `AcknowledgeBatch(irqs)`, `CoalesceCap` |

Driver entry points are synchronous (§5.6). Cache accounting is done by the simulated device, never self-reported by the driver. A storage controller keeps a sector cache that `FlushCache()` empties. The crypto unit records whether a key schedule was reused or re-expanded (e.g. after `keyCache.Clear()`). Drivers cannot block, spawn processes, open files, or use timers: delays can only come from how commands are structured (Doc 2 §2).

> **Binding note:** CScript spells the enum members `TransferMode.CPU` / `TransferMode.DMA`, as in Docs 5–6. The C# kernel spells them `Cpu` / `Dma`; the binding maps them.

## 12. Packages and imports

- **Resolution order** for `import A.B;`:
  1. the importing module's directory
  2. `/lib/user/`
  3. `/lib/vault/` (installed CS-Vault packages)
  4. `/sys/lib/` (signed system libraries)
  5. native namespaces (`Std.*`, system libraries)

  An ambiguous name is a compile error.
- **CS-Vault packages:** a package is a directory containing a plain-text `package.cfg` (`name`, `version`, `depends`, `capabilities`) and one or more `.csc` modules. `await Vault.InstallAsync("ironclad")` downloads it into `/lib/vault/ironclad/` (a billed network transfer); after that, `import vault.ironclad;` works. CS-Vault hosts exploit suites (IronClad), community drivers and patches, tools such as `deadman_broadcast`, and players' own published libraries.
- **Versions:** versions are semantic (`1.2.0`). Each package records the exact dependency versions it resolved, so builds are repeatable.
- **Kael's personal repository** (the Portal cloud repository, Doc 1 §5) is just another CS-Vault source: `Vault.Pull("kael/netcard-batched")`.

## 13. Standard library catalog

The standard library is broad on purpose: players should be able to write real tools, games, servers and exploits without reinventing basics. Everything under `Std.*` is pure (no side effects outside the process) and available to every module kind. System libraries are capability-gated (§10) and billed through drivers.

### 13.1 Core (auto-imported)

- `Error` and the error hierarchy (§7)
- `Option<T>` and `Result<T, E>` with `Map` / `Bind` / `Unwrap` / `UnwrapOr` (for code that prefers values over exceptions)
- `Args` (parsed command-line arguments)
- primitive conversions: `int.Parse`, `float.TryParse`, `ToString(format)`
- `string` methods: `Split`, `Join`, `Trim*`, `Replace`, `IndexOf`, `StartsWith`, `Contains`, `PadLeft`, `ToUpper`, `Substring`, `Format`
- `bytes` methods: `Slice`, `Concat`, `IndexOf`, `ToHex`, `Equals`

### 13.2 Data structures — `Std.Collections`

`List<T>`, `Map<K,V>` (insertion-ordered), `SortedMap<K,V>`, `Set<T>`, `SortedSet<T>`, `Queue<T>`, `Stack<T>`, `Deque<T>`, `PriorityQueue<T,P>`, `RingBuffer<T>`, `BitSet`, `Matrix<T>`, `Grid2D<T>`, `Multimap<K,V>`, `ReadOnly` views.

**Caches and probabilistic structures (D4):** `LruCache<K,V>`, `LfuCache<K,V>`, `TtlCache<K,V>`, `BloomFilter<T>`, `CountMinSketch<T>`, `HyperLogLog<T>`, `Trie`, `IntervalTree<T>`, `SkipList<T>`, `ObjectPool<T>`, `ConsistentHashRing<T>`. Each reports hit/miss counters that `Debug.Inspect` and the Profiler display.

### 13.3 Algorithms and functional sequences — `Std.Seq`, `Std.Algo`

- `Std.Seq` — LINQ-style lazy operators on any `Iterable<T>`: `Map`, `Filter`, `Reduce`, `Fold`, `Any`, `All`, `Count`, `First`, `Last`, `Take`, `Skip`, `TakeWhile`, `Zip`, `Concat`, `Distinct`, `GroupBy`, `OrderBy`, `ThenBy`, `Join`, `Chunk`, `Window`, `Flatten`, `Range`, `Repeat`, `ToList`, `ToMap`, `ToSet`, `Sum`, `Min`, `Max`, `Average`.
- `Std.Algo`:
  - sorting and searching: stable `Sort`, `BinarySearch`, `Partition`, `Shuffle(Random)`, `TopK`
  - combinatorics: `Permutations`, `Combinations`, `CartesianProduct`
  - comparison: `LevenshteinDistance`, `LongestCommonSubsequence`
  - hashing helpers

### 13.4 Text — `Std.Text`

| Module | Contents |
|---|---|
| `StringBuilder`, `Format` | Composition, padding, number and byte formatting (`Format.Bytes(4_000_000_000)` → `3.7 GB`) |
| `Regex` | Deterministic, step-metered regular expressions (captures, named groups, replace, split) |
| `Encoding` | `Utf8`, `Ascii`, `Latin1`, `Hex`, `Base64`, `Base32`, `Url`, `Html` escape/unescape, `Rot13`, `Xor` |
| `Csv` | RFC-4180 reader/writer with typed columns (used by Calc) |
| `Json` | Parse to a dynamic `JsonValue`, serialize any `Serializable`, pretty-print, JSON-path queries |
| `Cfg` | INI/`.cfg` key-value files (server configs, `package.cfg`) |
| `Markdown` | Render `.md` to CForms rich text |
| `Template` | `{{name}}` text templating (fake manifests, phishing pages, reports) |
| `Diff` | Line/word diff, unified patch create/apply (Silicon & Bus patches; doctoring logs) |
| `Tokenizer`, `Parse` | Lexer builder and parser combinators for writing interpreters, DSLs and log parsers |
| `Search` | Glob, wildcard, fuzzy matching, grep-style multi-file search over `Iterable<string>` |

### 13.5 Math and numbers — `Std.Math`

- **Functions:** `Abs`, `Min`, `Max`, `Clamp`, `Sign`, `Pow`, `Sqrt`, `Exp`, `Log`, `Log2`, `Log10`, `Floor`, `Ceil`, `Round`, `Trunc`, and the trigonometric functions (deterministic software implementations).
- **Integers:** `Gcd`, `Lcm`, `ModPow`, `ModInverse`, `IsPrime`, `PopCount`, `RotateLeft`/`RotateRight`, `Checked*` overflow detectors.
- **Types:**
  - `BigInt`: arbitrary precision (RSA-style puzzles)
  - `Fixed`: Q32.32 fixed point
  - `Rational`
  - `Complex`
- **Geometry:** `Vector2`/`Vector3`, `Matrix3x2`/`Matrix4x4`, `Rect`, `Circle`, `Polygon`, plus intersection and containment tests.
- **`Std.Stats`:** `Mean`, `Median`, `Mode`, `StdDev`, `Variance`, `Percentile`, `Histogram`, `Correlation`, `LinearRegression`, `MovingAverage`, outlier detection (e.g. spotting the jittery dark-subnet relay in Crawler data).

### 13.6 Randomness — `Std.Random`

`Random` (seeded xoshiro256**) with:
- `NextInt`, `NextFloat`, `NextBytes`
- `Choice`, `Sample`, `Shuffle`, `Weighted`
- `Gaussian`, `Uuid`

### 13.7 Binary data and protocols — `Std.Binary`, `Std.Compress`

- **`Std.Binary`:**
  - `ByteBuffer` (mutable, growable), `BinaryReader`/`BinaryWriter` with big/little-endian integers, varints and length-prefixed strings
  - `Bits` (bit fields), `Struct.Pack`/`Unpack` with format strings (`">HHI"`)
  - `Checksum` (`Crc32`, `Adler32`, `Fletcher16`, internet checksum)
  - `HexDump`
- **`Std.Compress`:** `Rle`, `Lz77`, `Deflate`, `Huffman` — deterministic, step-metered.

### 13.8 Time — `Std.Time`

- `Instant`, `Duration`, and `DateTime` on the world calendar (Nov 2056), with arithmetic, formatting and parsing of log timestamps (`2056-11-14T23:42:00`).
- `Stopwatch` (simulated time).
- `Cron`-style schedule expressions (evaluated by `Proc.Schedule`).

### 13.9 Paths and addresses — `Std.Path`, `Std.Net`

- **`Std.Path`:** `Join`, `Normalize`, `Parent`, `Name`, `Extension`, `IsHidden`, `Relative`, `Glob`. Paths are checked against the VFS rules (absolute, no escaping the root).
- **`Std.Net` (pure, no I/O):**
  - `IpAddress`, `Cidr` (subnet math, `Contains`, `Hosts()`), `Port`
  - `Url` (parse/build), `MacAddress`, `Hostname.IsValid`
  - `HttpHeaders`, `Packet` builders (TCP/UDP/ICMP header layouts for raw crafting)

### 13.10 Graphs — `Std.Graph`

`Graph<T>`, `WeightedGraph<T>`, plus:
- traversal: `Bfs`, `Dfs`
- paths: `Dijkstra`, `AStar`, `ShortestPaths`
- structure: `TopologicalSort`, `ConnectedComponents`, `Cycles`
- `Centrality` (degree, betweenness)
- `ToDot` (export for the visualizer)

Large traversals can track visited nodes in a `BloomFilter<T>` to keep memory low (Act II Crawler).

### 13.11 Data tables — `Std.Data`

`Table`: in-memory typed rows with `Select`, `Where`, `Join`, `GroupBy`, `Aggregate`, `Pivot`, `OrderBy` and CSV/JSON import and export — for cross-referencing manifests, timestamps and tide tables.

### 13.12 Concurrency and messaging — inside `Proc`

`Task`/`Task<T>` combinators, `CancellationToken`, `Channel<T>` (bounded and unbounded, with `await ReadAsync`/`WriteAsync`), `Select` over channels, `AsyncMutex`, `Semaphore`, `Barrier`. All are deterministic, because scheduling is (§8.3).

### 13.13 Games and graphics helpers — `Std.Game`

- **Game loop:** `GameLoop` with fixed timestep driven by `Gfx.OnFrame`.
- **Visuals:** `Sprite`, `SpriteSheet`, `TileMap`, `Camera2D`, `ParticleSystem`, `Tween`/`Easing`, `Color` and palettes.
- **Physics and AI:** AABB/circle/polygon collision, `SpatialHash`, a simple rigid-body integrator, `StateMachine`, and `Pathfinding` on grids.
- **Input:** `InputMap` (bind keys to actions).

### 13.14 Testing and diagnostics — `Std.Test`, `Debug`

- **`Std.Test`:**
  - `Assert` (`Equal`, `True`, `Throws<T>`, `InRange`, `SequenceEqual`)
  - `@Test` / `@TestCase` discovery, `Fixture` setup/teardown
  - `Property.ForAll` (property-based testing with shrinking)
  - `Bench` (step-count micro-benchmarks)
- **`Debug`:** `Assert`, `Trace`, `Inspect(obj)` (pretty-printed object graphs), `Breakpoint()`, `StepCount()`, `Profile(fn)`. Results are reported in steps and simulated cycles.

### 13.15 System libraries (capability-gated; C# kernel bindings)

Every operation below that consumes simulated time is exposed as an `…Async` method returning `Task`/`Task<T>` (§5.6) — e.g. `Fs.ReadAsync`, `Net.ConnectAsync`, `Session.WriteAsync`, `Crypto.Sha256Async`, `Media.Video.OpenAsync`, `Vault.InstallAsync`. Names in the table are abbreviated without the suffix.

| Library | Main surface |
|---|---|
| `Sys` | `Now`, `Uptime`, `Calendar`, `Info` (OS version, hostname), `Telemetry` (`DriverStats` per device, temperature, acoustic levels), `Rig` (installed components), `Env` (process environment), `Exit(code)` |
| `Sys.Hardware` | Driver-only device surface (§11) |
| `Fs` | `Read`/`ReadText`/`ReadLines`/`ReadRange`, `Write`/`WriteText`/`Append`, `Exists`, `Stat`, `List`/`Walk`, `Mkdir`, `Copy`, `Move`, `Delete`, `Chmod`/`Acl`, `Watch` (change events), `Mount` (remote session as a path), `Signature`/`RestoreBaseline`, `Temp` |
| `Net` | `Resolve`, `ReverseLookup`, `Connect` → `Session` (`Login`, `Anonymous`, `List`, `Read`, `Write`, `Stat`); `Http` (`Get`, `Post`, `Fetch(page)`); `Ftp` (convenience client); `Probe(ip, port)` → `PortState`; `Scan(cidr, ports)`; `Traceroute`; `Raw` (`Send`/`Listen` crafted packets, needs `net.raw`); `Tunnel`/`Proxy` (chain relays); `Trace` (inspect and deflect incoming traces); `Mesh.Broadcast` |
| `Proc` | `Spawn(fn)` / `Exec(path, args)` → `Process`, `Kill`, `List`, `Self`, `Signal`, `Schedule(after, fn)` and `Every(period, fn)` (simulated-time timers; not available to drivers), `Channel`, `HotSwap(path)` (needs `proc.hotswap`), `ExitCode` |
| `Log` | Structured logging (`Info`/`Warn`/`Error` with key-value fields and correlation ids), sinks to `/var/log/*.log`, `Tail(path)` |
| `Crypto` | `Hash` (`Sha256`, `Sha1`, `Md5` legacy, `Blake3`), `Hmac`, `Kdf` (`Pbkdf2`, `Scrypt`-lite), `Cipher` (`AesCtr`, `AesGcm`, `ChaCha20`, `Xor`), `KeyPair` (toy RSA/ECDH for puzzles), `PasswordHash` (`sha256$salt$hex` format), `Rainbow` (table lookup helpers). Billed through the crypto driver (Act III). |
| `Media` | `Image` (decode/encode PNG/JPG, pixels, crop, scale, draw, `Capture(window)` freeze-frames), `Audio` (decode WAV/OGG, play, mix, `Synth` tones and noise, spectrum), `Video` (open `.vid`, frames, seek, play into `MediaView`, export stills), `Codec` info |
| `Gfx` | `Canvas` drawing: lines, rects, polygons, paths, text, images, vector glyphs, blend modes, clipping, transforms; `MeasureText`; `OnFrame` |
| `Input` | Keyboard, mouse and scroll state and events, focus, `Clipboard`, text composition, key-repeat config |
| `CForms` | See §13.16 |
| `Comm` | CommLink: incoming and outgoing calls, `Answer`/`Decline`/`Hang`, `Capture` freeze-frame, `TraceCaller`, `DeadDrop` chat (send/receive), `Scanner` feeds |
| `Vault` | `Search`, `Info`, `Install`, `Update`, `Remove`, `Publish` (personal repo), `Pull`/`Push` (Portal sync) |
| `Shop` | Vendor catalogs, prices, `Buy`, order status |
| `Account` | Credit balance, transfers, transaction history, `Keyring` (stored credentials, encrypted at rest) |
| `Compiler` | `Compile(path)` → diagnostics, `Check(source)`, `Format(source)`, `Symbols(path)` (for tooling), `Disassemble` (bytecode listing with step costs) |

### 13.16 CForms controls

- **Base primitives (Doc 2):** `Form`, `Panel`, `Label`, `Button`, `TextBox`, `ListView`, `Canvas`, `MediaView`.
- **Extended controls:**
  - selection: `CheckBox`, `RadioButton`, `ComboBox`, `ListBox`
  - structure: `TreeView`, `TabControl`, `MenuBar`/`ContextMenu`, `ToolBar`, `StatusBar`
  - data entry and progress: `ProgressBar`, `Slider`, `NumericUpDown`
  - layout containers: `SplitContainer`, `ScrollPanel`, `GroupBox`
  - data: `DataGrid` (Calc), `Chart` (line/bar/scatter), `HexView`
  - text: `RichText`, `CodeEditor` (syntax highlighting, completion hooks, diagnostics gutter), `Terminal`
  - media and web: `ImageView`, `WebView` (nested `.cform` page)
- **Layout:** `Dock`, `Anchor`, `FlowLayout`, `GridLayout`, `StackLayout`, margins and padding.
- **Dialogs:** `MessageBox`, `InputBox`, `FilePicker`, `ColorPicker`.
- **Events:** `Click`, `DoubleClick`, `TextChanged`, `KeyDown`/`KeyUp`, `MouseMove`, `SelectionChanged`, `Closing`, `Resize`, `Tick` (form timer, simulated time).
- **Theming:** shared desktop theme tokens; scanline and federal-crest shader styles for CommLink.

## 14. Tooling

### 14.1 Diagnostics

The format is `CSC1234 error|warning path:line:col message`, with a squiggle span and quick-fix hints. Diagnostic codes are stable and documented in the IDE.

### 14.2 IDE and Portal

The IDE and the Portal provide:
- syntax highlighting, completion, signature help, go-to-definition, find-references, rename
- format-on-save
- the API browser over §13
- a diff view against the signed baseline
- **Compile & Hot-Swap**

### 14.3 Debugger

The debugger offers breakpoints, step into/over/out, watch expressions, call stacks, and heap inspection. Pausing a process in the debugger does **not** pause the game clock (Doc 1 §4.3); the Portal is the place for unhurried debugging.

### 14.4 Testing

`@Test` modules run in a scratch process from the IDE (Run Tests) or with `Proc.Exec("/sys/bin/test.csc", path)`. Results report pass/fail, steps consumed, and simulated cycles.

### 14.5 Profiling

The System Profiler attributes steps and billed cycles to module, function and line, next to the driver telemetry (Doc 3 §3.3).

## 15. Grammar (EBNF, abridged)

```ebnf
CompilationUnit = { Attribute } "module" QualifiedName ";" { Import } { TopLevelDecl } ;
Import          = "import" QualifiedName [ "as" Identifier ] ";" ;
TopLevelDecl    = { Attribute } [ "private" ] ( FunctionDecl | ConstDecl | GlobalDecl | TypeDecl ) ;
Attribute       = "@" Identifier [ "(" [ ArgList ] ")" ] ;

TypeDecl        = ClassDecl | StructDecl | InterfaceDecl | EnumDecl ;
ClassDecl       = "class" Identifier [ TypeParams ] [ ":" TypeList ] [ Constraints ] "{" { MemberDecl } "}" ;
StructDecl      = "struct" Identifier [ TypeParams ] [ ":" TypeList ] [ Constraints ] "{" { MemberDecl } "}" ;
InterfaceDecl   = "interface" Identifier [ TypeParams ] [ ":" TypeList ] "{" { Signature ";" } "}" ;
EnumDecl        = "enum" Identifier "{" Identifier [ "=" IntLiteral ] { "," Identifier [ "=" IntLiteral ] } [ "," ] "}" ;
MemberDecl      = { Attribute } [ "private" ] ( FieldDecl | FunctionDecl | ConstructorDecl | PropertyDecl | IndexerDecl ) ;

FunctionDecl    = Type Identifier [ TypeParams ] "(" [ Params ] ")" [ Constraints ] ( Block | "=>" Expr ";" ) ;
ConstructorDecl = Identifier "(" [ Params ] ")" [ ":" ( "base" | "this" ) "(" [ ArgList ] ")" ] Block ;
PropertyDecl    = Type Identifier "{" Accessor { Accessor } "}" ;
IndexerDecl     = Type "this" "[" Params "]" "{" Accessor { Accessor } "}" ;
Accessor        = ( "get" | "set" ) ( Block | "=>" Expr ";" | ";" ) ;
Params          = Param { "," Param } ;
Param           = [ "params" ] Type Identifier [ "=" Expr ] ;
TypeParams      = "<" Identifier { "," Identifier } ">" ;
Constraints     = "where" Identifier ":" TypeList { "where" Identifier ":" TypeList } ;

Type            = ( PrimitiveType | QualifiedName [ TypeArgs ] | TupleType | FnType ) { "[" "]" } [ "?" ] ;
FnType          = "fn" "(" [ TypeList ] ")" "->" ( Type | "void" ) ;
TupleType       = "(" Type "," Type { "," Type } ")" ;

Block           = "{" { Statement } "}" ;
Statement       = Block | LocalDecl ";" | ExprStmt ";" | If | While | DoWhile | For | Foreach | Switch
                | "break" ";" | "continue" ";" | "return" [ Expr ] ";" | "throw" Expr ";" | Try
                | "yield" ( "return" Expr | "break" ) ";" ;
LocalDecl       = ( "var" | "const" | Type ) ( Identifier | TuplePattern ) [ "=" Expr ] ;
Foreach         = "foreach" "(" ( "var" | Type ) Identifier "in" Expr ")" Statement ;
Try             = "try" Block { "catch" [ "(" Type [ Identifier ] ")" ] Block } [ "finally" Block ] ;
Expr            = Assignment | Lambda | Conditional ;
Lambda          = ( Identifier | "(" [ LambdaParams ] ")" ) "=>" ( Expr | Block ) ;
```

The full grammar, including expressions and the operator precedence of §6, is generated from the parser and published with the compiler.

## 16. Examples

### 16.1 The Act I network-driver fix

```c
@Driver("Kestrel OM-1 Optical Modem")
@Version("1.0.5-kael")
@Implements("INetDriver")
module NetCardDriver;

import Sys.Hardware;

void Initialize(DriverContext ctx) { }
void Shutdown() { }

int SendQueue(Packet[] messages) {
    int sent = 0;
    // Submit in chunks the modem accepts: one DeviceCommand per chunk, not per packet.
    for (int i = 0; i < messages.Length; i += Hardware.MaxBatch) {
        sent += Hardware.SendPacketBatch(messages.Slice(i, Math.Min(Hardware.MaxBatch, messages.Length - i)));
    }
    return sent;
}

int PollSockets(Socket[] sockets) => Hardware.PollBatch(sockets);
```

### 16.2 A small application

```c
@App("Port Sniffer Lite")
@Icon("radar")
module PortSnifferLite;

import CForms;
import Net;
import Std.Net;

void Main(Args args) {
    var form = new Form("Port Sniffer Lite");
    var target = new TextBox("verbena-and-rust.ny56");
    var results = new ListView(["Port", "State", "Banner"]);
    var scan = new Button("Scan");
    scan.Click += async (s, e) => {
        IpAddress ip = await Net.ResolveAsync(target.Text);
        foreach (var port in [21, 22, 80, 2121, 8443]) {
            var state = await Net.ProbeAsync(ip, port);
            results.Add([$"{port}", $"{state.Status}", state.Banner ?? ""]);
        }
    };
    form.Add(target, scan, results);
    form.Show();
}
```

### 16.3 A generic library (publishable to CS-Vault)

```c
/// A minimal least-recently-used cache, written by hand to show generics.
/// (Std.Collections.LruCache is the production version.) Publishable to CS-Vault as kael/mini_lru 1.0.0.
module mini_lru;

class MiniLru<K, V> where K : Hashable {
    private Map<K, V> items = new Map<K, V>();
    private int capacity;
    MiniLru(int capacity) { this.capacity = capacity; }

    V? Get(K key) {
        if (!items.ContainsKey(key)) return null;
        V value = items[key];
        items.Remove(key); items[key] = value;          // move to most-recent (insertion order)
        return value;
    }

    void Put(K key, V value) {
        items.Remove(key);
        if (items.Count >= capacity) items.Remove(items.Keys.First());
        items[key] = value;
    }
}
```

### 16.4 A website page

```c
@Page("Verbena & Rust - Bioluminescent Orchids")
module StorefrontIndex;

import CForms;
import Std.Text;

async Task<Form> Build(PageContext ctx) {
    var page = new Form("Verbena & Rust");
    var rows = Csv.Parse((await ctx.Http.GetAsync("/catalog_orchids.csv")).Text);
    var grid = new DataGrid(rows);
    page.Add(new Label("Bioluminescent orchids, grown above the waterline."), grid);
    return page;
}
```

---

## Appendix A. Mapping to earlier documents

| Earlier statement | Where it is honoured here |
|---|---|
| Doc 2 §1: drivers in `/sys/drivers/**/*.drv`, apps `.csc`, pages `.cform` | §2 |
| Doc 2 §2: no fake timers in drivers | §10 (no `Proc` in drivers), §11 |
| Doc 3 §2.1: AST, static type and capability checks, structured diagnostics | §4, §10, §14.1 |
| Doc 3 §2.2: step budgets, suspension, rollback | §5.6, §8.3–§8.6 |
| Doc 3 §3.1: `IKernelDriver` + device-class interfaces | §11 |
| Doc 5 §2.1: driver sample | §16.1 (the original sample also compiles unchanged) |
| Doc 1 §5: Portal and personal repository | §10, §12 |
