# KEDO Research F — Performance & Toolchain (C# / .NET 8, NativeAOT, zero reflection)

Date: 2026-10-15. Scope: performance-first design of the KEDO lexer/parser, resolver, VM, serialization, benchmarking, extensibility and ergonomics. Each claim is tagged **VERIFIED** (with the URL it was checked against) or **INFERRED** (my reasoning / general knowledge, not confirmed against a primary source during this research).

Context assumptions: tens of thousands of declarative objects (`extends` / `import` / mixins / components), expressions and embedded functions compiled to an integer-only IR, deterministic VM with serializable frames, consumed by several games (Godot .NET today, Windows + Android exports). Target runtime: .NET 8, NativeAOT/trim compatible, no reflection.

---

## 1. Lexer / parser performance in C#

### 1.1 Numbers: combinators vs generators vs hand-written

- **Parlot vs Pidgin vs Sprache vs Superpower (JSON, Apple M4 Pro, .NET 10, BenchmarkDotNet 0.15.8)** — VERIFIED (https://github.com/sebastienros/parlot):
  - BigJson: Parlot 34.9 µs / 88 KB; Parlot source-generated 32.1 µs; Pidgin 77.5 µs / 92 KB; Superpower 375.5 µs / 846 KB; Sprache 811.0 µs / 5,091 KB.
  - Expression (small): Parlot raw 128 ns, Parlot fluent 223 ns, Pidgin 3,215 ns (14x slower).
  - Takeaway: Sprache ≈ 20-25x slower and ~50x more allocation than the best combinator; Superpower ≈ 10x slower. Pidgin is 2-4x behind Parlot. A compiled regex still beats all of them on a trivial pattern (40 ns vs 75-150 ns).
- **Pidgin's own benchmarks (older: .NET Core 3.0, i5-4460)** — VERIFIED (https://github.com/benjamin-hodgson/Pidgin): BigJson Pidgin 685 µs / 102 KB vs Sprache 3,598 µs / 5,292 KB vs Superpower 2,884 µs / 913 KB vs FParsec 750 µs / 343 KB. Pidgin states "A carefully written Pidgin parser can be competitive with a hand-written recursive descent parser" and "FParsec is faster than Pidgin". Expression benchmarks show FParsec 2x faster than Pidgin.
- **ANTLR C# runtime** — VERIFIED (https://github.com/KvanTTT/AntlrBenchmarks): C# (optimized fork) is ~2.1x slower than Java for a non-left-recursive grammar and ~8.6x slower for a left-recursive one. ANTLR's ALL(*) adaptive prediction and its heap-allocated `ParserRuleContext` tree are allocation-heavy; it is an ergonomic tool, not a fast one — INFERRED.
- **Farkle (LALR(1), F#/C#, build-time table generation)** — VERIFIED that it exists and claims generated-parser startup (https://github.com/teo-tsirpanis/Farkle); no comparative numbers found.
- **Hand-written recursive descent + Pratt** — no C# head-to-head benchmark was found, but every production-grade compiler in the .NET space (Roslyn's C#/VB parsers, Esprima-dotnet used by Jint) is hand-written — INFERRED from the codebases. Hand-written parsers avoid the closure/delegate layers and intermediate result objects that combinators pay for; Parlot's "raw" API (closest to hand-written) was 1.7-1.8x faster than its own fluent combinators — VERIFIED (Parlot README numbers above).

**Conclusion:** for a performance-first DSL, hand-write the lexer and a recursive-descent parser with a Pratt loop for expressions. Reserve combinators (Parlot, if any) for throwaway tooling. Avoid ANTLR, Sprache, Superpower.

### 1.2 Span-based lexing (`ReadOnlySpan<char>` / UTF-8)

- `Utf8JsonReader` is the canonical .NET model: a `ref struct` reader over `ReadOnlySpan<byte>`, `ValueSpan` returns the raw token slice with no allocation, numbers parsed directly from bytes — VERIFIED (https://learn.microsoft.com/dotnet/api/system.text.json.utf8jsonreader, https://learn.microsoft.com/dotnet/api/system.text.json.utf8jsonreader.valuespan).
- `SearchValues<T>` (.NET 8) gives vectorized `IndexOfAny` for a fixed set of bytes/chars — VERIFIED (Toub's .NET 8 perf deck: https://2023.net.developerdays.pl/mat/PerformanceImprovementsInNET8_ndd.pdf). Use it to skip whitespace/comments and to find string terminators (`"`, `\`, newline) in one vector pass — INFERRED application.
- Lexing UTF-8 bytes directly (files are UTF-8 on disk; no transcoding to UTF-16) halves memory traffic versus `string`/`char`. Identifiers and keywords are ASCII in practice, so byte comparisons are enough and keyword recognition can be a `switch` on length + first byte — INFERRED.
- Token representation: a struct `{ TokenKind kind (u8/u16); int start; int length; }` stored in a flat `List<Token>` or pooled array; token text is a slice of the source span, materialized only when needed (identifiers interned, strings unescaped) — INFERRED, consistent with the Zig layout below.

### 1.3 Interning and span-keyed lookups

- .NET 9 added `Dictionary/HashSet/FrozenDictionary.GetAlternateLookup<ReadOnlySpan<char>>` so a span can be looked up without allocating a string; needs a comparer implementing `IAlternateEqualityComparer` (built-in ordinal comparers qualify) — VERIFIED (https://blog.ndepend.com/alternate-lookup-for-dictionary-and-hashset-in-net-9/, https://faithlife.codes/blog/2024/07/alternate-lookups-in-dotnet-9/). On **.NET 8** this API does not exist; use a custom open-addressing intern table keyed by (hash, span) that returns a 32-bit symbol id (`CommunityToolkit.HighPerformance.StringPool` offers `GetOrAdd(ReadOnlySpan<char>)` semantics) — INFERRED (StringPool exists; no benchmark found).
- Recommendation: intern all identifiers to `SymbolId : int` at lex time; the rest of the pipeline compares ints, never strings. This also makes IR integer-only for free — INFERRED.

### 1.4 AST representation: Roslyn red/green vs rowan vs index arenas

- **Roslyn red/green** — VERIFIED (https://ericlippert.com/2012/06/08/red-green-trees/): green nodes are immutable, parent-less, store widths not positions, built bottom-up and shared across edits; red nodes are lazy facades giving parents and absolute positions, rebuilt per edit. `SyntaxTree.WithChangedText` does an incremental parse "reusing most of the current syntax tree's internal data" for minor edits, else a full parse — VERIFIED (https://learn.microsoft.com/dotnet/api/microsoft.codeanalysis.syntaxtree.withchangedtext). The incremental algorithm ("the blender") is based on Neal Gafter's PhD research — VERIFIED (langdev StackExchange answer by a former Roslyn dev; secondary).
- **rowan (rust-analyzer)** — VERIFIED (https://github.com/rust-lang/rust-analyzer/blob/master/docs/book/src/contributing/syntax.md): green nodes `{kind:u16, text_len:u32, children}`, whitespace/comments are ordinary tokens (no leading/trailing trivia lists), identical subtrees interned per file; red layer is a *cursor* rather than memoized nodes because "memoized red nodes (C#, Swift) ... more than doubles memory use"; parser never sees whitespace (attached by a `TreeSink`); unexpected input is wrapped in `ERROR` nodes; parser errors go to a sink rather than the tree.
- **Zig (Andrew Kelley, "stage2: rework AST memory layout")** — VERIFIED (commit description: https://git.jakstys.lt/motiejus/zig/src/commit/4dca99d3f6b732c415d270f0c97def144ed6d3b7): struct-of-arrays for tokens and nodes removes enum-tag padding; each node reduces to `main_token: u32` + two `u32` child indices (~13 bytes/node); a research paper attributes >30% speedup in later phases to this layout — VERIFIED (TU Delft repository paper cited in search; secondary).
- **tweedegolf DoD parser case study (Rust)** — VERIFIED (https://tweedegolf.nl/en/blog/88/data-oriented-design): arena-of-references vs boxed nodes = 1.69x faster; SoA/index version = a further ~1.12x and 12% less memory. The big win is "no per-node heap allocation"; SoA on top is modest.

**Is red/green worth it for KEDO?** Only the *IDE/formatter* path benefits from incremental reparse and lazy parents. The batch compiler (parse all files → resolve → emit) does not. The pragmatic design (INFERRED, synthesizing the above):

1. **Compiler AST = flat index arena.** `struct Node { NodeKind kind (u16); int mainToken; int lhs; int rhs; }` in a `Node[]`, plus an `int[] extra` side array for variable-length child lists (Zig's `extra_data`), per file. Node ids are stable within a file and fit in 32 bits. No parent pointers, no per-node objects, zero GC pressure beyond the arrays, trivially poolable (`ArrayPool<Node>`), trivially serializable.
2. **Trivia preserved as tokens** (rowan style) so the same arena serves the formatter: the token array keeps whitespace/comment tokens; the parser skips them via a precomputed "next significant" index.
3. **Red facade on demand**: a `readonly ref struct NodeRef { arena, index }` with lazily computed parent via an optional `int[] parent` array built in one O(n) pass when a tool asks. This gives most of Roslyn's API ergonomics at near-zero cost.
4. **Incrementality at file granularity**, not node granularity (see §2). File-level re-parse of a 2-5 KB content file is microseconds; node-level incrementality is not worth its complexity for a content DSL.

### 1.5 Error recovery

- matklad's resilient LL tutorial — VERIFIED (https://matklad.github.io/2023/05/21/resilient-ll-parsing-tutorial.html): resilience ("recover as much structure as possible") rather than guessing intent; `advance_with_error` wraps an unexpected token in an error node and guarantees progress; loops break when the lookahead is in the *recovery set* (follow sets of all ancestors, computed recursively); FIRST sets decide whether an expression can start; virtual `Eof` token; every loop iteration must consume or break; "fuel" counter to catch non-progress during development; parser emits flat `Open/Close/Advance` events, converted to a tree in a second pass (this maps perfectly onto an index arena).
- rowan: missing required children are simply absent; errors are reported to a sink, not stored in the tree — VERIFIED (syntax.md above).
- Apply to KEDO: object bodies are `{ key: value, ... }` lists; recovery sets are `{ '}', newline-separated key, top-level keywords (object/import/mixin) }`. Because objects are declarative, one bad property should never lose the rest of the object or the file — INFERRED.

### 1.6 Pooling and parallelism

- `ArrayPool<T>.Shared` for token/node arrays during parse; return after the file's arena is "frozen" into exactly-sized arrays — INFERRED (standard .NET practice).
- Parse files in parallel with `Parallel.ForEach` / partitioned tasks: parsing is embarrassingly parallel per file; Roslyn syntax trees are immutable and documented as thread-safe for concurrent analysis — VERIFIED (Roslyn docs mirror via search); Roslyn's compiler itself was "primarily single-threaded" early on with parallelism in method compilation — VERIFIED (2012 interview; dated). For 10k small files, expect parse to be I/O-bound; read all bytes with `File.ReadAllBytes` in parallel, lex/parse per file, then merge arenas in deterministic (sorted path) order — INFERRED.

---

## 2. Resolver / compilation at scale

### 2.1 Query/incremental model (Salsa, Roslyn incremental generators)

- **Salsa** — VERIFIED (https://salsa-rs.github.io/salsa/overview.html, https://salsa-rs.github.io/salsa/reference/algorithm.html): program = inputs + pure *tracked functions* memoized in a database; a global revision increments on every input write; each memo records its dependencies and the revisions they last changed; on query, Salsa re-executes only if a dependency "may have changed"; **backdating / early cutoff**: if a dependency re-executes but yields an equal value, dependents are *not* invalidated (e.g. adding a comment reparses but the AST is equal → downstream reused); **durability**: inputs tagged LOW/HIGH; Salsa records per durability level the last revision anything at that level changed, so a function depending only on HIGH-durability inputs is validated "very easily" without walking edges; **interning** gives stable integer ids for structurally equal values; accumulators collect diagnostics as a side channel.
- **Roslyn incremental generators** — VERIFIED (https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md): a pipeline of `IncrementalValue(s)Provider<T>` transforms (`Select/Where/Collect/Combine`); each step's output is cached and compared with `EqualityComparer<T>` (or `WithComparer`) — if equal, downstream is skipped; guidance: extract small value-typed data models early, many small steps (each is a cache checkpoint), combine minimal data, forward cancellation tokens. `ForAttributeWithMetadataName` is "usually 99x more efficient" than `CreateSyntaxProvider`.

Both converge on the same rule: **memoize pure functions keyed by value-equal inputs, and cut off propagation on equal outputs**.

### 2.2 KEDO incremental model (INFERRED design)

- Key everything by **content hash of the file** (xxHash3/XxHash128 of the bytes, see §4.3) → `FileId`. Parse memo: `hash → (tokens, nodes, diagnostics)`.
- Resolution stages as pure functions over *declarations*:
  1. `DeclIndex(file) = {object ids, imports, extends/mixin edges}` — small, value-equal record; this is the "early extraction" step so that edits inside bodies that don't change the declaration surface don't invalidate the graph.
  2. `Graph = union(DeclIndex over all files)` — a `FrozenDictionary<SymbolId, DeclRef>` plus adjacency arrays.
  3. `ResolvedObject(id) = merge(ResolvedObject(parent), mixins..., own body)` memoized per object; dependencies = parent + mixins + own file.
  4. `IR(function) = compile(expr tree, resolved symbol table)`.
- Early cutoff at step 1 and step 3 is what makes "edit one leaf object" cost O(subtree) instead of O(catalog).
- Durability analogue: core/base-game files (rarely edited) vs mod/campaign files (frequently edited). Keep two memo tables so an edit in the "hot" set never re-validates the "cold" set's edges.
- Persist the memo tables to disk as a content-addressed cache (`cache/<hash>.bin`), so CI and cold starts reuse them (Bazel-style; the Roslyn docs note that compilers historically "throw away" all knowledge per build — VERIFIED 2012 interview).

### 2.3 Dependency graph, ordering, cycles

- Inheritance/mixin/import edges form a DAG; detect cycles with an iterative DFS (no recursion, to be safe with deep chains) and report a Rust-style diagnostic with the cycle path. Topological order gives both the resolution order and a *deterministic* build order; tie-break by (sorted file path, declaration offset) so that output is byte-identical across machines and thread counts — INFERRED (standard practice; .NET's own `Deterministic` builds rely on sorted inputs).
- Parallel resolution: process the topological order in *levels* (Kahn's algorithm); all objects in one level are independent and can be resolved with `Parallel.For`. Memo writes must be deterministic in content (not order) — INFERRED.
- CDDA's `copy-from` model (missing fields inherit from parent, explicit fields replace, `relative`/`proportional`/`extend`/`delete` modifiers) is the reference for the merge semantics; CDDA loads JSON via breadth-first file order with non-deterministic sibling order — VERIFIED (https://docs.cataclysmdda.org/JSON_INHERITANCE.html, https://docs.cataclysmdda.org/JSON_LOADING_ORDER.html). KEDO should fix the order explicitly (sorted) rather than inherit that nondeterminism — INFERRED.

### 2.4 Memory layout for 10k–100k objects

- **Struct-of-arrays catalog.** Per object type (schema), store fields in typed column arrays (`int[]`, `float[]`, `SymbolId[]`), with an object = row index. This is what the Zig/tweedegolf results above support, and it makes component queries ("all objects with component X") a column scan. Variable-length fields (lists) go into a shared `int[] pool` with (offset, length) — INFERRED.
- **`FrozenDictionary<K,V>`** (.NET 8): read ~2x faster than `Dictionary` for simple keys (11.2 ns → 5.1 ns in one BenchmarkDotNet test), creation ~4x slower; gains shrink for complex keys — VERIFIED (https://startdebugging.net/2024/04/net-8-performance-dictionary-vs-frozendictionary/; blog, not Microsoft). Use it for the id → row table and the symbol table built once at load. `ImmutableDictionary` is ~5-7x slower than both and should not be used for lookups — VERIFIED (same source family; NDepend blog).
- **Lazy IR loading.** Keep IR for functions/triggers in a separate blob section with an offset table; the catalog references `(section, offset, length)`. Load the catalog columns eagerly (small), map the IR section lazily or `MemoryMarshal.Cast` it directly from a pinned/`ReadOnlyMemory<byte>` — INFERRED (MemoryPack's "zero-encoding" design shows memory-image loading is viable in C#; see §4).
- Budget sanity: 100k objects × ~64 bytes of hot columns ≈ 6.4 MB; 100k objects × 200-byte IR ≈ 20 MB. Both fit in mobile memory easily; the cost to avoid is *object graphs* (100k × several heap objects × GC tracing) — INFERRED.

---

## 3. Bytecode VM design in C#

### 3.1 Reference points

- **Ink** — VERIFIED (https://github.com/inkle/ink/blob/master/Documentation/ArchitectureAndDevOverview.md, CallStack.cs): *not* a bytecode VM; it is a tree-walking interpreter over `Runtime.Container`/`Runtime.Object` (`while (Step() || TryFollowDefaultInvisibleChoice()) {}`), with an evaluation stack, an output stream and a `CallStack` of `Thread`s whose `Element`s hold `{Pointer currentPointer, PushPopType type, Dictionary<string,Object> temporaryVariables, ...}`. State is serialized as JSON: paths (`cPath` + `idx`), `temp` dictionaries, thread index, previous pointer; on load, paths that no longer exist are approximated up the hierarchy. Lesson: a *path-addressed* frame (not a raw instruction pointer) is what makes saves survive content edits.
- **Yarn Spinner** — VERIFIED (https://github.com/YarnSpinnerTool/YarnSpinner/blob/main/YarnSpinner/yarn_spinner.proto): stack VM; program = protobuf `Program{nodes: map<string,Node>, initial_values}`; `Node{instructions, headers}`; instructions are a protobuf `oneof` of ~22 message types (JumpTo, JumpIfFalse, PushString/Float/Bool, Pop, CallFunc, Push/StoreVariable, RunLine, RunNode, DetourToNode, Return, Stop, saliency ops…); legacy V1 was `{opcode, operands[]}` with operands as `oneof{string,bool,float}`. Jump targets are instruction indices within a node; variables and nodes referenced *by name string*. Lesson: name-based operands are easy to version but pay for hashing at runtime; KEDO should resolve to integer slots at link time and keep a name table for debugging/serialization.
- **Lua-CSharp** — VERIFIED (https://github.com/nuskey8/Lua-CSharp, LuaValue.cs): pure C#, "designed with low allocation", no IL emit so works under AOT/IL2CPP; `LuaValue` is a `readonly struct { LuaValueType Type (byte); double value; object? referenceValue }` with `LayoutKind.Auto` (≈24 bytes on x64 — INFERRED size), `Unsafe.As` for typed reads; varargs exposed as `ReadOnlySpan<LuaValue>` over the VM stack; source generator for bindings. Its benchmark chart vs MoonSharp/NLua is an image (numbers not extractable); the README says MoonSharp "incurs significant allocations" (MoonSharp's `DynValue` is a class — INFERRED). FastLua reported 2-4x over MoonSharp/KopiLua — VERIFIED (https://github.com/acaly/FastLua; self-reported, old).
- **Jint** — VERIFIED (https://github.com/sebastienros/jint/releases/tag/v3.0.0, PR #1775): v3 is "at least twice as fast" as v2 with managed allocations cut sharply (e.g. 1,698 MB → ~100 MB on an object-array test); vs ClearScript/V8 on an array stress test Jint ~5.4 ms / 7 MB managed vs V8 ~4.3 ms (native memory not counted). Jint is a tree-walking interpreter over an Esprima AST; it reached this by caching, avoiding closures, and struct `JsValue` wrappers — INFERRED from its changelog.
- **Dispatch literature** — VERIFIED (https://eli.thegreenplace.net/2012/07/12/computed-goto-for-efficient-dispatch-tables): computed goto made CPython 15-20% faster than `switch` by removing the per-dispatch bounds check and giving each opcode its own indirect branch (better prediction). C# has no computed goto — INFERRED (language fact).

### 3.2 Dispatch in C# (INFERRED unless noted)

- **`switch` on a dense `byte`/`ushort` opcode** compiles to a jump table in RyuJIT when cases are dense and contiguous (sparse cases become compare chains) — VERIFIED only via a third-party blog (https://en.ittrip.xyz/?p=42834); treat as INFERRED-but-widely-known. Keep opcodes 0..N contiguous and the `switch` as the only statement in the loop body; the JIT emits one range check + indirect jump.
- **Delegate tables** (`Action<VmState>[]`) pay an indirect call + null check per instruction and cannot be inlined — VERIFIED qualitatively (https://www.nuget.org/packages/jigsawdotnet README via search; Microsoft archive on direct/virtual/delegate call costs). **Managed function pointers (`delegate*<ref Frame, void>`)** remove the delegate object and null check but still cost an indirect call and block inlining of the opcode body; they shine when opcode bodies are large and few, not for ~60 tiny integer ops — INFERRED (no .NET 8 head-to-head found).
- Recommendation: **single `switch`** in a hot loop with locals for `pc`, `sp`, `ref int code0 = ref MemoryMarshal.GetArrayDataReference(code)` and `Unsafe.Add(ref code0, pc)` to skip bounds checks (pattern used inside CoreLib's `Array.cs` — VERIFIED https://source.dot.net/System.Private.CoreLib/R/bf8bdb373df18a3e.html). Validate bytecode once at load (bounds of jumps, slot counts, stack depth) so the loop can be unchecked safely — the standard advice when removing checks (VERIFIED: D-list discussion "verify your bytecode before executing it"). Note one practitioner saw the JIT already eliminate checks in a simple loop and raw pointers 13% *slower* — VERIFIED anecdote (dev.to/furkankirat), so **measure** before keeping unsafe code.
- Avoid `try/catch` inside the dispatch loop (prevents enregistration of locals) and avoid `async` in the loop (state machine boxing). Yield to the host by *returning* a status (`Suspended(reason)`) with the frame intact — INFERRED; this is also what makes frames serializable.

### 3.3 Register vs stack

- Stack machines are simpler to compile to and to verify; register machines (Lua 5.x) execute fewer instructions. For KEDO's integer-only expressions and small trigger bodies, a **register machine with a fixed-size frame (`int[] regs`, slot indices in the instruction)** gives one less memory op per operation and makes frames trivially serializable (just the register file + pc) — INFERRED. The compiler does simple linear-scan slot allocation per function; no spilling needed because frames are small.
- Encoding: 32-bit fixed-width instruction `op:8 | a:8 | b:8 | c:8` (Lua-style) or `op:8 | a:8 | imm:16`; constants beyond 16 bits in a per-function `int[] constants`. Fixed width keeps decoding to shifts and masks and keeps the code array an `int[]`/`uint[]` that is `MemoryMarshal.Cast`-able from the serialized blob — INFERRED.

### 3.4 Value representation

- Integer-only IR means the core value is `int` (or `long`). Use **`int` registers and typed side tables** rather than a tagged union: strings, object references and lists are integer *handles* (`SymbolId`, `ObjectId`, `ListId`) resolved through the catalog. Then a frame is `int[]` and there is no boxing, no `object` field, and the frame struct is blittable — INFERRED.
- If dynamic typing is ever needed, Lua-CSharp's `{byte tag, double, object}` struct (≈24 B) is the proven C# shape; a NaN-boxing approach is not possible in safe C# with references because the GC must see object fields — INFERRED (GC constraint is a runtime fact).
- Fixed-point for fractional game math (e.g. 16.16 or 1/1000 units) keeps determinism across platforms; floats across Android/x64/arm64 are IEEE-deterministic for basic ops but not for transcendental functions and FMA contraction — INFERRED (commonly documented determinism concern).

### 3.5 Frames and serialization

- Frame = `struct Frame { int functionId; int pc; int regBase; int regCount; }` over a shared `int[] registers` stack; the whole VM state = `(int[] registers, Frame[] frames, int frameCount, int pendingReason)`. This serializes as a few arrays. Reference functions by **stable id** (content hash or declared id, see §7.4), not by array index, so saves survive recompiles; on load, map ids → current function and, if a function changed, either re-enter at its start or fail with a clear error (Ink approximates paths; Yarn docs tie saves to the exact compiled program version — VERIFIED via search summaries of docs.yarnspinner / ink docs).
- Never store `string` names in frames; keep a name table in the compiled artifact for debugging.
- Target numbers (INFERRED, order-of-magnitude from the literature: Lua ~50-100 M simple ops/s in C; C# switch interpreters typically 2-5x behind): aim for ≥ 30-50 M integer instructions/s single-threaded on desktop, ≥ 10 M/s on a mid-range Android device, with **zero allocations per instruction** (verify with `MemoryDiagnoser`: `Allocated = 0 B`).

---

## 4. Serialization performance

### 4.1 Candidates

| Library | Model | AOT status | Notes |
|---|---|---|---|
| System.Text.Json (source-gen) | JSON, UTF-8 | Required mode for NativeAOT; up to ~40% startup reduction vs reflection in Microsoft's tests — VERIFIED (https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/reflection-vs-source-generation) | Human readable; slower than binary; good for *source-level* caches and tooling output |
| MessagePack-CSharp v3 | compact binary, cross-language | Source generator built in and "AOT source generation of formatters is on by default"; `mpc` tool retired — VERIFIED (https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/develop/doc/migrating_v2-v3.md, https://neuecc.medium.com/messagepack-for-c-v3-release-with-source-generator-support-893ed30d0e89) | Varint/tag encoding costs CPU vs memcpy; proven in Unity/IL2CPP |
| MemoryPack | "zero-encoding" C# memory image | Source generated, "Native AOT friendly"; .NET 7 AOT had a runtime bug expected fixed in .NET 8; Unity IL2CPP supported — VERIFIED (https://github.com/Cysharp/MemoryPack) | Claims x2-x5 vs other binary serializers, x50-x200 for struct arrays (.NET 7, Ryzen 5950X; self-reported); C#-only; versioning rules: default mode allows add-only, `VersionTolerant` mode allows add/delete with explicit `[MemoryPackOrder]` at some cost |
| Custom binary (`BinaryPrimitives` / `MemoryMarshal.Cast`) | hand-rolled | Trivially AOT-safe; no dependency | Best possible load time for SoA tables (one `ReadAllBytes` + slicing); you own versioning |

### 4.2 Recommendation for KEDO compiled artifacts (INFERRED)

- The compiled catalog is already column arrays of blittable types. A **custom sectioned binary** ("KEDO Pack") — header `{magic, formatVersion, langVersion, toolHash, sectionTable}` + sections `{strings (UTF-8 + offsets), symbols, objectTable (SoA columns), irCode (uint[]), irConstants, debugInfo (optional), hash}` — loads by reading bytes once and `MemoryMarshal.Cast<byte,int>` per section. Zero per-object allocation. This is exactly MemoryPack's thesis minus the library.
- Use **MemoryPack** only if you want its generator to maintain struct layouts for you; the cost is a dependency and its `LayoutKind.Auto` cross-runtime caveat (Unity vs .NET binaries can differ — VERIFIED README).
- Use **System.Text.Json source-gen** for human-facing outputs: diagnostics (LSP), formatter configs, debug dumps, and the per-file incremental cache *index* (tiny). Never for the hot catalog.
- Versioning: bump `formatVersion` on layout change; keep a `langVersion` for semantic changes; refuse to load mismatched artifacts and recompile from source (sources are the truth, artifacts are a cache) — INFERRED.

### 4.3 Hashing cost

- xxHash family: XXH3 ~31.5 GB/s and XXH128 ~29.6 GB/s in native C on i7-9700K — VERIFIED (https://github.com/Cyan4973/xxHash via search); a .NET port measured ~12 GB/s on Ryzen 7 2700 (.NET 6, third-party `Standart.Hash.xxHash`) — VERIFIED (https://github.com/uranium62/xxHash). `System.IO.Hashing` ships `XxHash3`/`XxHash128` officially, explicitly "not suitable for security purposes" — VERIFIED (https://learn.microsoft.com/dotnet/api/system.io.hashing).
- BLAKE3 vs SHA-256 in .NET (BenchmarkDotNet, .NET 10, Ryzen 7 PRO 7840U, 1 MB): native BLAKE3 143.7 µs (~7 GB/s), managed BLAKE3 215.4 µs (~4.6 GB/s), SHA256 434.3 µs (~2.3 GB/s) — VERIFIED (https://www.nuget.org/packages/Blake3.Managed).
- Canonical JSON for hashing: RFC 8785 JCS (sort keys by UTF-16 code units, ECMAScript number formatting, UTF-8, no whitespace) — VERIFIED (https://datatracker.ietf.org/doc/rfc8785/). Producing canonical JSON costs a full serialization pass plus sorting; hashing a canonical *binary* form is ~free by comparison.
- Recommendation: **XxHash128 over the binary form** for content-addressing and cache keys (collision-resistant enough at 128 bits for non-adversarial inputs); optionally SHA-256 of the final pack for distribution integrity. Never SHA-256-over-canonical-JSON on the hot path — INFERRED.

### 4.4 Mobile load budgets

- Android vitals flags cold start ≥ 5 s, warm ≥ 2 s, hot ≥ 1.5 s to first frame as "excessive" — VERIFIED (https://support.google.com/googleplay/android-developer/answer/9844486). Apple's guidance is ≤ 400 ms to first frame (WWDC 2019) — VERIFIED via Sentry docs summary (secondary); the iOS watchdog figure was not confirmed.
- Implication: KEDO catalog load must be a small slice of a 1-2 s budget. Budget **≤ 100 ms for 10k objects on a mid-range Android device**, with IR lazily loaded. A memory-image binary (one read + slicing) is the only format that reliably hits that; JSON parsing 10k objects with STJ would plausibly be 100-300 ms on mobile — INFERRED.
- Godot specifics: C# Android export is experimental, uses the linux-bionic Mono runtime (not NativeAOT), arm64/x64 only; `GodotSharp` assemblies do not fully support trimming (workaround `TrimmerRootAssembly`); Godot overrides the RID to `android-arm64`, which currently breaks NativeAOT builds — VERIFIED (https://godotengine.org/article/platform-state-in-csharp-for-godot-4-2/, https://github.com/godotengine/godot/issues/97775, https://forum.godotengine.org/t/how-to-use-nativeaot-with-godot-c-on-android/75528). So KEDO must be *AOT-clean* (no reflection, no `Emit`, trim-safe) to be ready, but on Android it will likely run under Mono interpreter/JIT for now — keep the VM free of generic virtual dispatch and heavy generics, which are slow on Mono's interpreter — INFERRED.

---

## 5. Benchmarking and budgets

### 5.1 Tooling

- BenchmarkDotNet NativeAOT toolchain: the host stays a normal .NET process; it generates a project, compiles it with ILCompiler (latest `Microsoft.DotNet.ILCompiler` by default) and runs the native child; `NativeMemoryProfiler` does not support NativeAOT; `MemoryDiagnoser` reports GC/allocation stats — VERIFIED (https://benchmarkdotnet.org/articles/overview.html and changelogs via search).
- Baselines: `[Benchmark(Baseline = true)]` adds a `Ratio` column (mean of per-measurement ratios, with `RatioSD`); categories allow multiple baselines — VERIFIED (https://benchmarkdotnet.org/articles/features/baselines.html).
- Statistical gating: `[StatisticalTestColumn("3%")]` / absolute thresholds run Welch and Mann-Whitney TOST equivalence tests and print Faster/Slower/Same per method — VERIFIED (https://benchmarkdotnet.org/articles/samples/IntroStatisticalTesting.html).
- CI: `benchmark-action/github-action-benchmark` has a BenchmarkDotNet example; default alert threshold 200% (because hosted runners are noisy), separate `fail-threshold`, comment-on-alert — VERIFIED (https://github.com/benchmark-action/github-action-benchmark).

### 5.2 Setup for KEDO (INFERRED)

1. `bench/` project with jobs: `.NET 8 JIT` and `NativeAOT` (same benchmarks), `MemoryDiagnoser`, `ShortRun` in PR CI, full run nightly on a *dedicated* runner (self-hosted or a fixed VM) — hosted runners make a tight gate meaningless.
2. Gates: in PR CI, **allocation gates are exact** (`Allocated` must not grow for parse/VM hot paths; `0 B` for VM steps); **time gates are loose** (fail at > 150% vs stored baseline, alert at > 120%). Nightly: statistical test at 5% vs last release artifact.
3. Also track **artifact size** and **cold load time** (a separate console harness timing `Load()` from a `ReadAllBytes` of the pack, run on Android via an instrumented build occasionally).

### 5.3 Initial budgets (INFERRED, to be refined by the first real measurements)

| Metric | Desktop (x64, .NET 8) | Mid-range Android (arm64, Mono) |
|---|---|---|
| Lex+parse throughput | ≥ 50 MB/s source single-thread (Parlot does ~30 MB/s for JSON via combinators on M4; hand-written should beat it) | n/a (compile offline) |
| Parse+resolve 10k objects (~5 MB source), 8 cores | ≤ 500 ms cold, ≤ 50 ms incremental single-file edit | n/a |
| Compiled pack load, 10k objects | ≤ 20 ms | ≤ 100 ms |
| VM integer instructions/s | ≥ 50 M/s | ≥ 10 M/s |
| VM allocations per instruction | 0 B | 0 B |
| Frame save/restore (100 frames) | ≤ 50 µs, 0 allocations beyond the output buffer | ≤ 250 µs |
| Peak managed heap for 100k objects loaded | ≤ 64 MB | ≤ 96 MB |

Reference numbers used to derive these: Parlot JSON 35 µs for a "big" document (~1 KB? → tens of MB/s) VERIFIED; FrozenDictionary ~5 ns/lookup VERIFIED; Android 5 s cold-start ceiling VERIFIED.

---

## 6. Extensibility without reflection

### 6.1 Registries under NativeAOT

- The trimmer/AOT compiler cannot keep what only reflection would reach (`Assembly.GetTypes()`, `Activator.CreateInstance(name)`); the accepted pattern is a **source generator that emits a static registry** (a generated `switch` or a `[ModuleInitializer]` method that registers factories into a static table), because `typeof(T)`/direct references are static roots the trimmer keeps — VERIFIED (https://github.com/dotnet/runtime/issues/50333, Autofac AOT docs https://docs.autofac.org/en/latest/advanced/native-aot-trimming.html, dotnet/runtime PR #77293 which uses a generated `[ModuleInitializer]` for JS interop). `[DynamicDependency]` is the manual alternative — VERIFIED (same sources). CA2255 discourages `ModuleInitializer` in *libraries* — VERIFIED (Code Maze article via search); in Godot/Unity-like hosts also note MemoryPack's warning that `ModuleInitializer` is not supported on Unity and registration must be manual — VERIFIED (MemoryPack README).
- KEDO design (INFERRED): a `KedoRuntimeBuilder` where each game calls `.AddComponent<T>(KedoComponentDescriptor)` / `.AddNativeFunction(id, delegate*)` explicitly; a `Kedo.Generators` source generator can emit those calls from `[KedoComponent]` / `[KedoFunction]` attributes into a `partial static class KedoRegistrations { public static void Register(KedoRuntimeBuilder b) }` that the game calls once. No `ModuleInitializer` by default (explicit > implicit, and host-agnostic).
- Native functions: a table `delegate*<ref VmContext, void>[]` indexed by the function's integer id; the generator emits the thunks that read arguments from registers. Zero reflection, zero boxing.
- Schema/extension points as *data*, not types: component descriptors are records with field ids/types; the compiler validates against them; the VM only ever sees ints. New games add descriptors, not subclasses — INFERRED.

### 6.2 Versioned ABI for a shared language core

- **Breaking-change rules** (dotnet/runtime) — VERIFIED (https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/breaking-change-rules.md): allowed: adding members, adding interface implementations, adding DIMs (with caution), `struct`→`readonly struct`, widening visibility; disallowed: removing/renaming members, changing parameter names/order/types, adding `ref/in/out`, adding abstract members to extensible types, changing struct↔class, changing enum underlying type or existing enum values, adding fields to previously stateless structs, adding/removing `virtual`/`static`, changing thrown exceptions (except narrowing).
- **Tools** — VERIFIED (https://learn.microsoft.com/dotnet/fundamentals/apicompat/overview, package-validation docs): `EnablePackageValidation=true` runs after `pack`; `PackageValidationBaselineVersion` compares against the last shipped package; `EnableStrictModeForBaselineValidation` turns compatibility into equality (servicing); `ApiCompatGenerateSuppressionFile` records intentional breaks; `Microsoft.DotNet.ApiCompat.Task` for assembly validation; `Microsoft.DotNet.ApiCompat.Tool` global tool outside MSBuild.
- **PublicApiAnalyzers** — VERIFIED (https://github.com/dotnet/roslyn-analyzers/blob/main/src/PublicApiAnalyzers/PublicApiAnalyzers.Help.md via mirror): `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` as `AdditionalFiles`; RS0016 fires for undeclared public members, RS0017 for removed ones; per-TFM files possible; `#nullable enable` header. This makes every public-surface change a reviewable diff.
- **How Roslyn/MSBuild keep "1.x never breaks"**: the compiler's public API is additive-only, old members are kept and marked `[Obsolete]`/`[EditorBrowsable(Never)]`, `LangVersion` gates new language features so old sources compile identically, and every API change goes through a review of the `PublicAPI.*.txt` diff — INFERRED from Roslyn repository practice (the PublicApiAnalyzers originate from Roslyn's own process — VERIFIED that the analyzer lives in dotnet/roslyn-analyzers).
- KEDO policy (INFERRED):
  - Split packages: `Kedo.Core` (values, pack reader, VM; tiny surface, strict), `Kedo.Compiler` (parser/resolver; wider, evolves faster), `Kedo.Tooling` (LSP, formatter), `Kedo.Generators`.
  - SemVer with **three independent version numbers**: NuGet package version, *language version* (`kedo 1.x` syntax), *pack format version*. Runtime N must load pack formats ≤ N; compiler N must parse language versions ≤ N (with `#lang` header or project setting).
  - Make everything public `sealed` or `static` unless designed for extension; expose extension points as interfaces with *no* DIM additions after 1.0 (add a new interface instead).
  - Enable `EnablePackageValidation` + baseline + PublicApiAnalyzers from the first 1.0 package; strict mode on servicing branches.

---

## 7. Language-design patterns: ease of use that doesn't cost performance

### 7.1 Minimal syntax

- Declarative objects with a fixed small grammar (object, property, list, nested object, expression, function block) parse in one pass with no backtracking; every added syntax form must have a FIRST-set-disjoint start token so the parser stays LL(1)/LL(2). Keep expression grammar to a Pratt table (one small array of binding powers) — INFERRED.
- Newline-terminated properties (no mandatory commas/semicolons) cost nothing in a hand-written lexer that tracks `newlineBefore` on tokens — INFERRED (common in Paradox script, Yarn, Ink).

### 7.2 Optional typing with inference

- Types are inferred bottom-up from literals and component descriptors; explicit annotations are only needed at `extends`/mixin boundaries or for empty lists. Because values are ints at runtime, typing is a *compile-time* check with zero runtime representation cost; the VM never type-checks — INFERRED. (Lua-CSharp's generator similarly turns binding type errors into compile-time errors — VERIFIED README.)

### 7.3 Diagnostics

- rustc structure — VERIFIED (https://rustc-dev-guide.rust-lang.org/diagnostics.html): level (error/warning/note/help), optional code with `--explain`, main message, primary span + labeled secondary spans, sub-diagnostics (`help` = what the user can change, `note` = facts/context), suggestions with `Applicability` (`MachineApplicable`, `MaybeIncorrect`, `HasPlaceholders`, `Unspecified`) consumed by tools (VERIFIED https://doc.rust-lang.org/nightly/rustc/json.html); style: lowercase start, no trailing period, identifiers in backticks, "invalid" not "illegal", small spans, no duplicate errors; prefer defining diagnostics as structs separate from compiler logic.
- Elm philosophy — VERIFIED (https://elm-lang.org/news/compiler-errors-for-humans, https://elm-lang.org/blog/compilers-as-assistants): show the code as the user wrote it, give specific hints and links, be an assistant not an adversary.
- KEDO: `readonly record struct Diagnostic(DiagnosticCode Code, Severity, Span Primary, ImmutableArray<Label>, ImmutableArray<Fix>)` with codes `K0001…`; codes documented in a generated index; fixes carry an applicability flag so the LSP can auto-apply `MachineApplicable` ones; accumulate diagnostics per query (Salsa "accumulator" style) so they are cached with results — INFERRED. Diagnostics are produced only on the error path, so this costs nothing at steady state.

### 7.4 Formatter design

- gofmt: "no knobs", respects the author's line breaks rather than imposing a line-length algorithm, and comment handling is the hard part ("extremely hard to manipulate the AST and maintain comments in the right places"); `go/printer` fixes targeted comment-placement issues (e.g. never move comments across commas) — VERIFIED (https://go.googlesource.com/talks/+/master%5E/content/2015/gofmt-en.slide, golang.org/issue/3062). rustfmt uses empty trailing comments (`//`) as a layout hint for vertical lists — VERIFIED (Linux kernel Rust docs via search); 2024 style edition stopped aligning unrelated trailing comments — VERIFIED (https://doc.rust-lang.org/nightly/edition-guide/rust-2024/rustfmt-formatting-fixes.html).
- Practical rule: formatting works on the **lossless token array** (trivia as tokens, rowan-style). The formatter is a token-stream rewriter guided by the node arena: it only touches whitespace tokens, so comments can never be lost; attachment heuristics (comment on its own line → leading of next item; same line → trailing of previous) follow rowan/IntelliJ — INFERRED design built on VERIFIED rowan description. One true style, no options except maybe line width. Idempotency test in CI: `fmt(fmt(x)) == fmt(x)` and `parse(fmt(x)) ≡ parse(x)` modulo trivia.

### 7.5 Stable ids for nodes/objects

- Objects: the declared id (`enemy.goblin_archer`) is the stable key; the compiler hashes `(id, content hash of resolved body)` to detect semantic changes. Functions/triggers: `(owner object id, member name, ordinal)` → stable `FunctionId` string, mapped to an int slot at link time; saves store the string id (interned in the save's own table) so they survive re-linking — INFERRED, following Ink's path-based pointers (VERIFIED behavior).
- Syntax nodes (for the LSP) need only `(FileId, byte offset)`; rust-analyzer stores `(FileId, Range)` and re-parses on demand rather than holding trees — VERIFIED (syntax.md).

---

## Recommended architecture for KEDO v1 (performance-first)

1. **Lexer**: hand-written over `ReadOnlySpan<byte>` (UTF-8, no transcoding), `SearchValues<byte>` for whitespace/comment/string scanning, tokens as 12-byte structs in a pooled array, identifiers interned to `SymbolId:int` via a custom span-keyed intern table (no .NET 9 dependency). Newline-aware for comma-less properties.
2. **Parser**: hand-written recursive descent + Pratt expressions, LL(1-2), matklad-style resilience (error nodes, recovery sets from ancestor follow sets, fuel in debug). Emits `Open/Close/Advance` events → flat **index arena** (`Node{kind:u16, mainToken, lhs, rhs}` + `extra int[]`), trivia kept as tokens for the formatter. No red/green trees in v1; add a parent array + `ref struct` cursor for tools. Per-file parsing in parallel; deterministic merge by sorted path.
3. **Incremental model**: Salsa/Roslyn-incremental-generator semantics implemented in C#: memo tables keyed by `XxHash128(file bytes)`; stages = pure functions over small value-equal records (`DeclIndex` → graph → `ResolvedObject` → `IR`); early cutoff on equality; two durability tiers (base content vs mod/campaign); memo cache persisted content-addressed on disk. Cycle detection iterative; Kahn levels for parallel resolution; deterministic ordering everywhere.
4. **Catalog layout**: struct-of-arrays columns per schema, `FrozenDictionary<SymbolId,int>` for id→row, shared `int[]` pools for lists, IR in a separate lazily-loaded section.
5. **VM**: register machine, fixed 32-bit instructions (`op:8|a:8|b:8|c:8` / `op:8|a:8|imm:16`), single dense `switch` dispatch, `ref`/`Unsafe.Add` access after one-time bytecode verification, no try/catch or async inside the loop, status-return suspension. Values are `int` (handles for strings/objects/lists; fixed-point for fractions). Frames = `{functionId, pc, regBase, regCount}` over one `int[]` register stack; state serializes to three arrays plus a string id table for functions. Zero allocations per instruction, verified in CI.
6. **Compiled artifact**: custom sectioned binary ("KEDO Pack") read with one `ReadAllBytes` + `MemoryMarshal.Cast` per section; header with format/language versions and an XxHash128 content hash; optional SHA-256 for distribution. System.Text.Json source-gen only for tooling/diagnostic/LSP output and tiny cache indexes. MemoryPack is the fallback if hand-maintaining layouts becomes a burden; MessagePack only if cross-language consumers appear.
7. **Benchmarking**: BenchmarkDotNet with JIT + NativeAOT jobs and `MemoryDiagnoser`; exact allocation gates in PR CI; loose time gates (fail > 150%, alert > 120%) on hosted runners; nightly statistical tests on a fixed machine; artifact size and cold-load timers; occasional Android instrumented load-time runs. Budgets per §5.3.
8. **Extensibility/ABI**: explicit `KedoRuntimeBuilder` registration (+ optional source generator emitting the registration code), `delegate*` native function table, descriptor-as-data components. Packages `Kedo.Core / Kedo.Compiler / Kedo.Tooling / Kedo.Generators`; `EnablePackageValidation` + baseline + `PublicApiAnalyzers` from 1.0; strict mode on servicing branches; separate language and pack-format version numbers; everything `sealed` unless designed for extension.
9. **Ergonomics**: minimal LL grammar, inferred types with zero runtime cost, rustc/Elm-style diagnostics with codes and applicability-tagged fixes cached per query, gofmt-style no-knob formatter over the lossless token array, stable string ids for objects/functions mapped to ints at link time.

### Top risks

1. **Godot Android runs Mono, not NativeAOT today** (experimental C# export, RID override breaks AOT, GodotSharp not trim-clean). KEDO must be AOT-clean but will be *measured* on Mono's JIT/interpreter on Android; generic-heavy or `Unsafe`-heavy code may behave differently there. Mitigation: keep the VM monomorphic and benchmark on a real device early.
2. **Over-engineering incrementality.** Salsa-grade query systems are a large investment; file-granular memoization with early cutoff gets 90% of the benefit. Build the simple version first, keep stages pure so a finer-grained system can be swapped in.
3. **Serialization format ownership.** A custom pack means owning versioning and endianness/layout correctness (`LayoutKind.Auto` and struct padding differences bit MemoryPack users). Mitigation: only blittable primitives in sections, explicit little-endian, round-trip + golden-file tests, format version gate.
4. **Unsafe dispatch loop correctness.** Removing bounds checks without airtight bytecode verification turns content bugs into memory corruption. Mitigation: a verifier pass at load, debug build with checked access, fuzzing of the pack loader.
5. **Determinism across platforms** (float math, hash seeds, parallel ordering). Mitigation: integer/fixed-point only in IR, fixed hash seeds, sorted inputs and Kahn levels, byte-identical pack test across Windows/Linux/Android CI.
6. **Integer-only values limiting ergonomics** (strings/lists as handles). Mitigation: compile-time string ops and interning make most content needs expressible; add a typed string/list op subset in the VM only with benchmarks proving no regression on the integer path.
7. **API surface locking too early.** Once `Kedo.Core` ships 1.0 with package validation on, mistakes are permanent. Mitigation: 0.x until two games consume it; keep `Kedo.Core` minimal; everything else may evolve under `Kedo.Compiler`.

---

## Source index (primary/official unless noted)

- Parlot benchmarks: https://github.com/sebastienros/parlot
- Pidgin: https://github.com/benjamin-hodgson/Pidgin
- ANTLR runtime benchmarks: https://github.com/KvanTTT/AntlrBenchmarks
- Farkle: https://github.com/teo-tsirpanis/Farkle
- Lippert, red-green trees: https://ericlippert.com/2012/06/08/red-green-trees/
- Roslyn `SyntaxTree.WithChangedText`: https://learn.microsoft.com/dotnet/api/microsoft.codeanalysis.syntaxtree.withchangedtext
- Roslyn incremental generators design: https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md
- rust-analyzer syntax design (rowan): https://github.com/rust-lang/rust-analyzer/blob/master/docs/book/src/contributing/syntax.md
- matklad resilient LL parsing: https://matklad.github.io/2023/05/21/resilient-ll-parsing-tutorial.html
- Zig AST layout commit: https://git.jakstys.lt/motiejus/zig/src/commit/4dca99d3f6b732c415d270f0c97def144ed6d3b7
- tweedegolf DoD parser case study: https://tweedegolf.nl/en/blog/88/data-oriented-design
- Salsa book: https://salsa-rs.github.io/salsa/overview.html , https://salsa-rs.github.io/salsa/reference/algorithm.html
- CDDA inheritance/loading order: https://docs.cataclysmdda.org/JSON_INHERITANCE.html , https://docs.cataclysmdda.org/JSON_LOADING_ORDER.html
- FrozenDictionary benchmark (blog): https://startdebugging.net/2024/04/net-8-performance-dictionary-vs-frozendictionary/
- Alternate lookup (.NET 9): https://blog.ndepend.com/alternate-lookup-for-dictionary-and-hashset-in-net-9/
- Utf8JsonReader: https://learn.microsoft.com/dotnet/api/system.text.json.utf8jsonreader
- .NET 8 perf deck (SearchValues): https://2023.net.developerdays.pl/mat/PerformanceImprovementsInNET8_ndd.pdf
- Ink architecture + CallStack: https://github.com/inkle/ink/blob/master/Documentation/ArchitectureAndDevOverview.md , https://github.com/inkle/ink/blob/master/ink-engine-runtime/CallStack.cs
- Yarn Spinner proto: https://github.com/YarnSpinnerTool/YarnSpinner/blob/main/YarnSpinner/yarn_spinner.proto
- Lua-CSharp: https://github.com/nuskey8/Lua-CSharp (LuaValue.cs)
- FastLua: https://github.com/acaly/FastLua
- Jint v3: https://github.com/sebastienros/jint/releases/tag/v3.0.0 , https://github.com/sebastienros/jint/pull/1775
- Computed goto / dispatch: https://eli.thegreenplace.net/2012/07/12/computed-goto-for-efficient-dispatch-tables
- CoreLib `Unsafe.Add` + `GetArrayDataReference` usage: https://source.dot.net/System.Private.CoreLib/R/bf8bdb373df18a3e.html
- STJ reflection vs source-gen: https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/reflection-vs-source-generation
- MemoryPack: https://github.com/Cysharp/MemoryPack
- MessagePack v3 migration: https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/develop/doc/migrating_v2-v3.md
- System.IO.Hashing: https://learn.microsoft.com/dotnet/api/system.io.hashing ; xxHash: https://github.com/Cyan4973/xxHash ; .NET port numbers: https://github.com/uranium62/xxHash
- Blake3.Managed benchmark: https://www.nuget.org/packages/Blake3.Managed
- RFC 8785 JCS: https://datatracker.ietf.org/doc/rfc8785/
- Android vitals startup thresholds: https://support.google.com/googleplay/android-developer/answer/9844486
- Godot C# platform state 4.2: https://godotengine.org/article/platform-state-in-csharp-for-godot-4-2/ ; NativeAOT Android issue: https://github.com/godotengine/godot/issues/97775
- BenchmarkDotNet: https://benchmarkdotnet.org/articles/overview.html , https://benchmarkdotnet.org/articles/features/baselines.html , https://benchmarkdotnet.org/articles/samples/IntroStatisticalTesting.html
- github-action-benchmark: https://github.com/benchmark-action/github-action-benchmark
- Trimming/AOT registries: https://github.com/dotnet/runtime/issues/50333 , https://docs.autofac.org/en/latest/advanced/native-aot-trimming.html , https://github.com/dotnet/runtime/pull/77293
- ApiCompat / package validation: https://learn.microsoft.com/dotnet/fundamentals/apicompat/overview , https://learn.microsoft.com/dotnet/fundamentals/apicompat/package-validation/baseline-version-validator
- Breaking-change rules: https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/breaking-change-rules.md
- PublicApiAnalyzers help: https://github.com/dotnet/roslyn-analyzers/blob/main/src/PublicApiAnalyzers/PublicApiAnalyzers.Help.md
- rustc diagnostics guide: https://rustc-dev-guide.rust-lang.org/diagnostics.html ; JSON/applicability: https://doc.rust-lang.org/nightly/rustc/json.html
- Elm: https://elm-lang.org/news/compiler-errors-for-humans , https://elm-lang.org/blog/compilers-as-assistants
- gofmt talk: https://go.googlesource.com/talks/+/master%5E/content/2015/gofmt-en.slide ; comment placement issue: https://golang.org/issue/3062
- rustfmt 2024 edition changes: https://doc.rust-lang.org/nightly/edition-guide/rust-2024/rustfmt-formatting-fixes.html
