# Research E — Author behaviours: scripting runtimes, continuations, hooks, determinism

Date: 2026-10-15. Target: Godot 4.6.2 .NET, C# 12, `src/domain` (net8.0, Godot-free, deterministic; `GameState.TimeMinutes: long`, named `Pcg32` RNG streams in `GameState.RandomStreams`, `GameCommand(Id, ExpectedRevision)`, `CommandReceipt(Fingerprint, Revision, Message)` keyed by command id). Android .NET 9; iOS via NativeAOT planned.

Claims are marked **VERIFIED** (with URL) or **INFERRED** (my reasoning from verified facts; not directly confirmed by a primary source).

---

## 0. Platform ground truth (what the contract must survive)

- **NativeAOT limitations** — VERIFIED (https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/): "No dynamic loading, for example, `Assembly.LoadFile`. No runtime code generation, for example, `System.Reflection.Emit`. Requires trimming." Also: "`System.Linq.Expressions` always use their interpreted form". .NET 9+ lists iOS arm64 as supported (not experimental); .NET 8 lists iOS as "Experimental support".
- **NativeAOT on iOS-like platforms** — VERIFIED (https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ios-like-platforms/): "Starting from .NET 9, Native AOT supports targeting iOS-like platforms"; enabled with `<PublishAot>true</PublishAot>` + `<PublishAotUsingRuntimePack>true</PublishAotUsingRuntimePack>`, RID `ios-arm64`.
- **Godot .NET on iOS** — VERIFIED (https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/index.html, and search results from forum.godotengine.org thread 128950): Godot docs call iOS C# support experimental since 4.2; a Godot maintainer states iOS uses NativeAOT and "the Godot bindings aren't fully compatible with trimming", Godot roots the assembly during export as a workaround. Godot's iOS C# support PR #82729 uses .NET NativeAOT (https://github.com/godotengine/godot/pull/82729). A 4.3-era bug (icudt.dat missing with .NET 9) is noted (https://github.com/godotengine/godot/issues/100123).
- **Consequence for the contract (INFERRED):** anything in `src/domain` and the behaviour runtime must be trim-safe and reflection-free (or source-generated), no `Reflection.Emit`, no dynamic assembly loading, no `System.Linq.Expressions.Compile()` reliance. Prefer `[JsonSerializable]` source-gen contexts for the serializable frame. Keep `IsAotCompatible=true` on the domain csproj so the analyzer flags violations early (VERIFIED property semantics: same MS page).
- **App Store guideline 2.5.2** — VERIFIED (https://developer.apple.com/app-store/review/guidelines/): apps "may not download, install, or execute code which introduces or changes features or functionality of the app". **INFERRED:** author-created behaviours *bundled in the app* (campaign content shipped with the build) are fine; *downloading* new behaviours at runtime (user mods on iOS) is a review risk regardless of the runtime chosen, and interpreted-data IR is the least risky form if ever needed (4.7 explicitly carves out HTML5/JS mini apps only).

---

## 1. Scripting runtimes usable from C# under NativeAOT/trimming on iOS

### 1.1 MoonSharp (Lua 5.2 in pure C#)
- VERIFIED (https://raw.githubusercontent.com/moonsharp-devs/moonsharp/master/README.md and https://github.com/moonsharp-devs/moonsharp): "99% compatible with Lua 5.2" (no weak tables); README claims "Runs on Ahead-of-time platforms like iOS" and "Runs on IL2CPP converted code"; CLR interop uses "Runtime code generation ... where the platform supports it" (so reflection fallback elsewhere). 3-clause BSD. 399 commits, 92 open issues, 1.6k stars.
- VERIFIED (https://www.nuget.org/packages/MoonSharp): stable 2.0.0 dated 2016-10-14; prerelease 3.0.0-beta.1 dated 2026-07-04; targets .NET Standard 1.6 / .NET Framework 3.5.
- VERIFIED (https://www.moonsharp.org/): 2026-07-03 news post announces a new maintainer (Max Hayman); 2016 posts describe IL2CPP work needing manual registration and `link.xml` fiddling; a planned "generate C# source to avoid reflection" tool is not confirmed shipped.
- **Assessment (INFERRED):** The Lua VM core is pure C# and AOT-viable, but the *interop layer* is reflection-driven and was designed for Mono full-AOT/IL2CPP, not .NET NativeAOT trimming; expect IL2xxx/IL3xxx warnings and a need for descriptors/rooting. Ten years with no stable release; the 2026 maintainer change is encouraging but unproven. Lua `coroutine` exists in MoonSharp, but **no coroutine/VM-state serialization** exists (no Eris equivalent) — INFERRED from absence in README/docs.

### 1.2 Lua-CSharp (nuskey8; Lua 5.2 in pure C#, async-first)
- VERIFIED (https://github.com/nuskey8/Lua-CSharp): "Lua 5.2 interpreter implemented in C#"; "operates reliably in AOT environments since it does not rely on IL generation"; works with Unity Mono and IL2CPP; coroutines exposed as `LuaState` resumable from C#; `LuaPlatform` abstracts file system / OS env / stdio / **time provider** (sandbox seam); debug hooks on call/return/line/**instruction count**; a `LuaRuntimeException` thrown from a hook is catchable by `pcall` (so not a hard budget); memory limit only approximate; source generator `[LuaObject]`/`[LuaMember]` for reflection-free interop; MIT.
- VERIFIED (https://www.nuget.org/packages/LuaCSharp): 0.5.7, last updated 2026-09-26, 105.5K total downloads; depends on `LuaCSharp.Annotations` + `LuaCSharp.SourceGenerator`; net6.0 / netstandard2.1 / net8.0 / net10.0 targets.
- **Assessment (INFERRED):** the most credible pure-C# Lua for NativeAOT today (source-gen interop, no IL emit, active). Still pre-1.0 (0.5.x), no stated determinism guarantees (GC is .NET's; table iteration order follows .NET dictionaries, not Lua's), and **no VM-state serialization** — a suspended Lua coroutine cannot be put in a save file. Hard step budgets need wrapping the hook exception in a non-`pcall`-catchable path (not provided).

### 1.3 NLua / KeraLua (native Lua 5.4 via P/Invoke)
- VERIFIED (https://raw.githubusercontent.com/NLua/KeraLua/main/README.md): KeraLua binds Lua 5.4; "compatible with iOS, Mac, Android, UWP, and .NET"; separate `KeraLua.Mac.sln` for Mac/iOS/tvOS. NLua "added iOS support using KeraLua" (search result, https://github.com/nlua). Xamarin.iOS note: classes must be `Preserve`d or the linker strips them (nuget feed search result).
- **Assessment (INFERRED):** native Lua is P/Invoke, which NativeAOT supports, but iOS needs the static `.a` linked into the Godot Xcode project and all interop marshalling must be trim-safe (`LibraryImport` source-gen rather than `DllImport` with runtime marshalling). NLua's object interop is reflection-heavy (LuaInterface lineage). Would also make `src/domain` depend on a native lib — breaks the "Godot-free, pure C# domain, easy to unit test" property and cross-platform determinism (C Lua `math`, number formatting differences). Native Lua would, however, enable **Eris-style coroutine persistence** only if Eris were ported to 5.4 — see §2.

### 1.4 Jint (ECMAScript interpreter in C#)
- VERIFIED (https://raw.githubusercontent.com/sebastienros/jint/main/Jint/Jint.csproj): `TargetFrameworks` `net472;netstandard2.0;netstandard2.1;net8.0;net10.0`; `IsAotCompatible` set true for net7.0-compatible TFMs; comment: "What actually substantiates the IsAotCompatible property below is the `aot` CI leg"; a `NoWarn` list suppresses IL3050, IL2077, IL2075, IL2072, IL2070, IL2069, IL2067, IL2062, IL2060 ("real limitations, not resolved").
- VERIFIED (https://raw.githubusercontent.com/sebastienros/jint/main/README.md): "runs JavaScript directly in your process without native dependencies, bytecode generation"; constraints: `TimeoutInterval`, `MaxStatements`, `MemoryLimit`, recursion limits, cancellation; "Constraints are cooperative and cannot preempt arbitrary host callbacks"; a hardened untrusted-code profile "disables broad CLR and reflection access".
- VERIFIED (https://github.com/sebastienros/jint/issues/1667): .NET 7 NativeAOT async-interop bug closed "not planned" (2023-11); maintainer: "Async interop is probably something that I'm not willing to spend energy on."
- **Assessment (INFERRED):** Jint is the most mature "AOT-tested" C# interpreter with first-class execution budgets. Costs: JS semantics (doubles, dynamic objects) are a poor fit for an integer-minute deterministic domain; CLR interop paths are reflection-based (hence the suppressed IL warnings) so host API exposure must be via explicit delegates/wrappers; **no serializable continuation** (no way to persist a suspended generator/promise state).

### 1.5 Roslyn scripting / dynamic C#
- VERIFIED (https://github.com/dotnet/roslyn/blob/main/docs/wiki/Scripting-API-Samples.md): "Scripting APIs can't be used within Universal Windows Applications and .NET Native since the application model doesn't support loading code generated at runtime." Combined with NativeAOT's "No dynamic loading ... No runtime code generation" (VERIFIED above) → **not viable** on iOS NativeAOT. INFERRED: likewise `System.Linq.Expressions.Compile()`, `DynamicMethod`, `dynamic` keyword (needs Microsoft.CSharp binder, trimming-unfriendly) should be excluded from the domain.

### 1.6 Godot-side runtimes (live on the Godot side, NOT in the domain)
- **GDScript compiled from a string at runtime** — VERIFIED (https://docs.godotengine.org/en/stable/classes/class_script.html): `Script.source_code` ("The script source code or an empty string if source code is not available") and `reload(keep_state=false)` ("Reloads the script's class implementation"); `GDScript.new()` instantiates (https://docs.godotengine.org/en/stable/classes/class_gdscript.html). Community confirmation of the `GDScript.new(); source_code=...; reload(); new()` pattern (https://godotforums.org/d/35071-can-i-compile-and-execute-a-script-at-runtime). Export "script export mode" tokenizes `.gd` to binary tokens, but built-in scripts stay text in `.tscn` (forum search results) — INFERRED: therefore the GDScript parser/compiler ships in every export template, including iOS, and runtime compilation works there (GDScript is a bytecode interpreter; no JIT). Not verified by an iOS-specific primary source.
- **`Expression`** — VERIFIED (https://docs.godotengine.org/en/stable/classes/class_expression.html, https://docs.godotengine.org/en/stable/tutorials/scripting/evaluating_expressions.html): arithmetic, boolean ops, built-in global functions, indexing, method calls on a `base_instance`, type constructors; `parse(expression, input_names)` then `execute(inputs, base_instance, show_error, const_calls_only=false)`; "works even when Godot is compiled without the GDScript module"; docs warn that exposing a base instance "can allow cheating" / security vulnerabilities. `const_calls_only` comes from an engine commit adding a const-call mode "so an expression run from the editor can only call const functions" (search result mirror of Godot commit 9ddebc0c). INFERRED: `Expression` is an untyped Variant evaluator with no statements, no loops, no serializable state; useful only as an editor-side formula helper, never as the domain's rule engine.
- **"Compile on the Godot side into neutral IR executed in the domain"** — INFERRED assessment: viable and attractive *for the authoring pipeline* (the editor/GDExtension side parses author text or a graph into an IR document), but the *executor must be in the domain* (pure C#) so that: tests run without Godot, the same IR runs on all platforms identically, saves reference only IR ids. GDScript itself cannot be the executor because (a) it is not available in the domain library, (b) its runtime state is not serializable (`reload(keep_state)` is a hot-reload nicety, not a save mechanism), (c) GDScript floats/Variants are not the domain's integer model.

### 1.7 Custom small DSL / expression language in pure C#
- INFERRED: a hand-written tokenizer + Pratt parser + tree-walking or bytecode interpreter in C# is ~1–3k lines, has zero reflection, zero dependencies, deterministic integer semantics, and — crucially — **the interpreter owns its frames**, so a suspended behaviour is just data (`pc`, locals, call stack). None of the off-the-shelf interpreters above can offer that under NativeAOT. Precedent: Ink's runtime is exactly this (a small C# bytecode-ish container tree with a fully JSON-serializable `StoryState`, see §2).

### 1.8 Summary table

| Runtime | Pure C#? | NativeAOT/iOS | Serializable continuation | Step budget | Determinism control | Maintenance (2026-10) |
|---|---|---|---|---|---|---|
| MoonSharp | yes | claimed (Mono AOT/IL2CPP era), reflection interop | no | partial (no hard preempt) | weak | stable 2016, beta 2026-07, new maintainer |
| Lua-CSharp | yes | yes (no IL gen, source-gen interop) | no | hook-based, catchable | weak (.NET GC/dicts) | active, 0.5.7 (2026-09) |
| NLua/KeraLua | no (native) | needs static lib + trim-safe P/Invoke | only with an Eris port (none for 5.4 confirmed) | via `lua_sethook` | C-lib dependent | active |
| Jint | yes | `IsAotCompatible` + AOT CI leg, with suppressed IL warnings | no | yes (statements/timeout/memory/recursion) | JS doubles | active |
| Roslyn scripting | — | **no** | — | — | — | — |
| GDScript runtime compile | Godot-only | yes (interpreter) but Godot side | no | no | Variant/float | engine |
| Godot `Expression` | Godot-only | yes | no (stateless) | no | Variant/float | engine |
| Custom IR interpreter | yes | yes by construction | **yes** | yes | full | ours to maintain |

---

## 2. Serializable continuations / coroutines

### 2.1 What exists
- **Lua Pluto/Eris** — VERIFIED (https://www.Github.com/fnuecke/eris; lua-l threads https://lua-users.org/lists/lua-l/2015-07/msg00377.html, https://groups.google.com/g/lua-l/c/STCIknAXkd8): Eris "can serialize almost anything you can have in a Lua VM ... This in particular includes yielded coroutines"; "depends on vanilla Lua internals, so it won't work with other Lua implementations"; Lua 5.2/5.3 only; a thread "must be yielded" to be saved. An Oct-2025 lua-l thread asked about a 5.4 port with no confirmed answer (search result; INFERRED: no maintained 5.4 Eris). → Not available for any C# Lua, and tied to C Lua internals (not in `src/domain`).
- **C# iterators/async state machines** — INFERRED: compiler-generated classes with private fields; no stable layout, not serializable, cannot be rehydrated. Any "`yield return WaitMinutes(30)`" style in C# cannot be saved mid-way; only re-executed.
- **Ink runtime** — VERIFIED (https://raw.githubusercontent.com/inkle/ink/master/ink-engine-runtime/StoryState.cs): `kInkSaveStateVersion = 10`, `kMinCompatibleLoadVersion = 8`; state JSON holds `flows` (per-flow callstack threads, outputStream, currentChoices), `variablesState`, `evalStack`, `visitCounts`, `turnIndices`, `turnIdx`, `storySeed`, `previousRandom`, `inkSaveVersion`, `inkFormatVersion`; loading a too-old version throws "Ink save format isn't compatible". Content changed after save: VERIFIED (https://github.com/inkle/ink/releases/tag/0.8.1) "better handling of save states when the story has changed": diverts to missing targets "work up the path to existing content" with a runtime warning; new variables fall back to defaults; "It will never be possible to make this perfect". VERIFIED (https://github.com/inkle/inky/issues/253): choice/gather paths are positional, so inserting one shifts older saves. **Lesson:** store the RNG seed + previous-random in the frame; address resume points by *stable ids*, not positions.
- **Yarn Spinner** — VERIFIED (https://docs.yarnspinner.dev/3.1/api/csharp/yarn.unity/yarn.unity.dialoguerunner/yarn.unity.dialoguerunner.savestatetopersistentstorage): saves "all variables from variable storage" only; the FAQ-era guidance is to save the *current node name* and restart the node (search results, 2022 blog https://www.blog.radiator.debacle.us/2022/01/darners-digest-vol-3-on-yarn-spinner.html: mid-node VM-state saving "a way off"). **Lesson:** "resume at the start of the current node" is a legitimate, cheap fallback if nodes are the unit of side effects.
- **Paradox (CK3) on_actions/events** — VERIFIED (https://ck3.paradoxwikis.com/Event_modding): delayed firing via `delay = { months = … }` / `trigger_event = { id = … days = { 7 14 } }`; "an event will only successfully fire if it is valid both when the on_action is executed AND once the delay is complete" (re-validation at resume); "Scopes or local variables set in the effect here will not carry over to any event fired by the on_action"; "Avoid creating infinite fallback loops, or the game may be prevented from advancing time!". INFERRED: delayed events live in the save as (event id, fire date, saved scopes) — there is no suspended code, only declarative "what to run later with which arguments".
- **RimWorld quests** — VERIFIED (https://rimworldwiki.com/wiki/Modding_Tutorials/Quests): `QuestScriptDef` → `QuestNode`s run at generation time ("instanced and not scribed at any point") and emit `QuestPart`s, the runtime "logic blocks ... based on receiving quest related signals and acting upon them"; signals are strings with optional named arguments. Save XML contains QuestPart entries with signal/state fields (Steam guide search result). **Lesson:** separate *generation-time program* (not saved) from *runtime parts* (small, saved, signal-driven state machines).
- **Factorio** — VERIFIED (https://lua-api.factorio.com/latest/auxiliary/libraries.html, https://lua-api.factorio.com/latest/auxiliary/storage.html, https://lua-api.factorio.com/latest/classes/LuaBootstrap.html): `coroutine`, `io`, `os`, `loadfile`, `dofile` are unavailable "mostly to ensure determinism"; `pairs()` iterates in insertion order "because Factorio has to be deterministic"; `math.random` reimplemented on the map's shared generator; trig/exp/log functions replaced "so results are consistent across platforms"; `storage` saves nil/strings/numbers/booleans/tables/LuaObject refs, **functions throw on save**, metatables dropped unless registered by name; `on_load` may not write `storage` ("will lead to desyncs"); one handler per event per mod. **Lesson:** the mainstream deterministic-mod architecture is *no coroutines at all*: event handlers + a serializable data table + scheduled ticks.
- **CDDA `effect_on_condition`** — VERIFIED (https://raw.githubusercontent.com/CleverRaven/Cataclysm-DDA/master/doc/JSON/EFFECT_ON_CONDITION.md): declarative JSON with `condition`/`effect`/`false_effect`, `recurrence`, `deactivate_condition`, `eoc_type` (ACTIVATION, RECURRING, AVATAR_DEATH, NPC_DEATH, PREVENT_DEATH, EVENT with `required_event`), typed per-event context variables (e.g. `character_casts_spell` provides spell/difficulty/damage), `run_eocs` with `time_in_future` (delayed queue) and `iterations`; "reactivated EOCs currently lose all context variables and conditions" (a documented versioning wart); `u_`/`npc_`/`global_`/`context_` scoped variables. **Lesson:** delayed continuation = (eoc id, due time, talkers, context vars) — again data, not code.
- **Durable Functions / Durable Task (replay model)** — VERIFIED (https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-code-constraints): orchestrators "use event sourcing ... must be deterministic: an orchestrator function replays multiple times, and it must produce the same result each time"; time via `context.CurrentUtcDateTime`, GUIDs via `context.NewGuid()`, random numbers "returned from an activity ... saved into the orchestration history" or a fixed-seed generator; no I/O, no static/env vars, no threads. Versioning — VERIFIED (https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-versioning): breaking if you "add, remove, or reorder calls to activities, sub-orchestrations, timers, or external events"; mitigations: built-in orchestration versioning (instance pinned to a version; new code branches on version), side-by-side, or stop in-flight. **Lesson:** replay is a *second* valid continuation technique: persist the *history of awaited results* rather than the frame, re-run deterministic code to the suspension point, suppress already-applied effects. It needs no frame serialization but requires strict determinism of the behaviour code — which the domain already guarantees.

### 2.2 Techniques compared (INFERRED synthesis)

| Technique | Resume fidelity | Needs VM frames in save? | Version drift handling | Fit |
|---|---|---|---|---|
| A. Explicit state machine (RimWorld QuestPart, Paradox delayed events, CDDA queued EOC) | at declared states only | no (tiny state record) | easy: states are named | great for "wait N minutes / wait for decision / wait for roll" |
| B. Interpreter with serializable frames (Ink, Eris) | anywhere a `wait` happens | yes (pc + locals + stack) | hard unless ids are stable and a migrator exists | great if we own the interpreter (custom IR) |
| C. Replay / event-sourced (Durable Task) | anywhere | no (history of awaited results) | hard: any reorder of awaits breaks; mitigated by version pinning | good; cheap to implement on top of A or B; relies on determinism we already have |
| D. Host-language coroutines (C# iterators, Lua coroutines) | anywhere | impossible under NativeAOT / pure-C# Lua | n/a | **reject** |

**Recommendation:** B as the *definition* of a continuation (an IR interpreter whose frames are plain records), with A's discipline (suspension only at explicit, typed `Await` instructions with stable ids) and C's safety net (an "effects log" with receipts so a frame that is re-run or migrated never re-applies a side effect). Scripts and graphs both compile to the same IR, so one serializer, one tracer, one migrator.

---

## 3. Visual logic graph semantics (not UI)

- **Unreal Blueprints** — VERIFIED (https://dev.epicgames.com/documentation/en-us/unreal-engine/functions-in-unreal-engine): impure functions "must be explicitly executed by connecting exec wires"; pure functions "are wired to Data Pins and are automatically executed by the compiler" and "called one time for each node it is connected to"; a function "has one entry point" and "the output exec pin is activated". Latent nodes (Delay) are not allowed in functions; macros can hold them (Epic staff answer, https://forums.unrealengine.com/t/why-cant-i-add-a-delay-node-to-a-function/284366 and related search results). Pending latent actions live in engine memory, not the save game (INFERRED; FDelayAction docs show in-memory countdown only, https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/Engine/FDelayAction). **Lesson:** separate exec-flow (white wire) from data-flow (typed pins), and confine *suspension* to the top-level flow; pure/data nodes must be side-effect free and re-evaluable.
- **Godot VisualScript removal** — VERIFIED (https://godotengine.org/article/godot-4-will-discontinue-visual-scripting): "Despite continuous effort, it never gained traction and the path to improve it was never clear"; it mirrored the GDScript API 1:1 with no abstraction; expected to return as extensions "notably via GDExtension". **Lesson:** a graph that is just "GDScript with wires" fails; graphs win when they expose a *domain-level* vocabulary (hooks, waits, rolls, decisions) — exactly our IR's op set.
- **Orchestrator** — VERIFIED (https://raw.githubusercontent.com/CraterCrash/godot-orchestrator/main/README.md): GDExtension, Apache-2.0, OScript attached to nodes, custom functions, signals, any Variant type; main branch targets Godot 4.8.x (v2.6.x), 4.6.x → v2.4.x (bug fixes only); strict ABI matching ("mismatched versions may cause unexpected behavior or crashes"). No mention of saving execution state. INFERRED: an editor-only inspiration, not something to execute in the domain.
- **Behaviour trees (LimboAI/Beehave)** — VERIFIED (https://github.com/limbonaut/limboai/issues/2, open since 2023-12): request to serialize "running indexes of composite nodes" + blackboard + HSM states; maintainer notes blackboard scope chains, object references, "Godot's ResourceSaver isn't suitable", proposes a JSON serializer over `PROPERTY_USAGE_STORAGE` properties and an `ObjectSerializer` strategy. Beehave: no save support found (search). **Lesson:** even tick-based trees need (a) per-node running state and (b) a scoped variable store that is itself serializable with *no object references*, only ids.
- **Dialogic 2** — VERIFIED (https://docs.dialogic.pro/saving-and-loading.html): saves "information about Dialogic's timeline and Dialogic variables"; `get_full_state()`/`load_full_state()` for custom systems; visited-history optional. INFERRED: resume granularity is the timeline event, i.e. technique A.

**What a graph needs to compile to the same IR as a script (INFERRED design):**
1. Nodes with **typed ports**: exec in/out ports (ordered, at most one active successor per out port) and data ports with declared `ValueType` (Int, Bool, Text, EntityRef, Enum, List<T>, Record). Data edges form a DAG; exec edges may form cycles only through explicit `Loop` nodes with a budget.
2. **Pure vs impure** node kinds: pure nodes have no exec ports and are re-evaluated on demand (memoized per step); impure nodes have exec ports and may call `Effects`. Only impure top-level flow nodes may `Await`.
3. **Sub-graphs** = IR functions with explicit parameters/returns; no implicit capture of caller variables. Recursion depth-limited.
4. **Variables with scope**: `Hook` (per invocation, dies at `Complete`), `Behavior` (per instance; saved in the frame), `Entity` (attached to a domain entity id; saved with the entity), `Campaign` (global; saved in `GameState`). Variables are typed; EntityRefs are ids, never object references.
5. **Stable ids**: every node, port, variable and await site has a content-independent id (GUID or author-assigned), stored in the graph file; the compiler emits them into the IR as labels so the saved frame references labels, not positions (the Ink path-shift lesson).
6. Compilation lowers the graph to a linear IR (`Eval`, `Store`, `Branch`, `Call`, `Effect`, `Await`, `Complete`) with a source map `ir index → node id` used for traces and breakpoints; a textual script lowers to the same IR with `ir index → line:col`.

---

## 4. Hook/event contracts in data-driven games

- **Paradox** — VERIFIED (https://ck3.paradoxwikis.com/Event_modding): on_actions are "hooks in code"; each has an expected root scope type (Character for `on_birth_child`, Casus belli for `on_war_won_attacker`, Landed Title for `on_county_faith_change`); `trigger` gate; `effect` + `events`/`random_events` (weighted; `chance_of_no_event`)/`first_valid`/`on_actions` chaining/`fallback`; "we can create new custom on_actions, we cannot create new code on_actions"; infinite fallback loops can stall time. CK3 dev diary: "we use entirely on_actions to fire events" (https://admin-forum.paradoxplaza.com/forum/developer-diary/crusader-kings-3-dev-diary-30-event-scripting.1397140).
- **RimWorld** — VERIFIED (Quests wiki above): QuestParts react to string signals with optional named arguments; generation vs runtime split.
- **CDDA** — VERIFIED (EOC doc above): `eoc_type` enumerates hook kinds, events deliver typed context variables, EVENT EOCs "try to run on the NPC focus of the event and fall back to the avatar", `has_alpha`/`has_beta` guards because some events lack a talker; `run_eocs` `iterations` capped at 100 when conditional.
- **Factorio** — VERIFIED (LuaBootstrap doc above): single handler per event per mod (later registration overwrites); `raise_event` only for custom or whitelisted events; "If any LuaObject in the data is invalid, the event is silently not raised"; `on_init`/`on_load`/`on_configuration_changed` lifecycle with explicit restrictions; `get_event_order` exists (mod order), i.e. cross-mod ordering is by load order (INFERRED detail).
- **Durable Task** — VERIFIED above: no I/O in orchestrators; awaited results deduplicated through history.

**Contract rules to adopt (INFERRED from the above):**
- Hook ids are a closed, versioned catalogue owned by the domain (like code on_actions); authors cannot invent engine hooks, but can declare *custom signals* that behaviours raise and others subscribe to (like CDDA EVENT EOCs / RimWorld signals / Factorio custom events).
- Every hook has a **typed context record** (root entity id + named typed fields) documented in the catalogue; the compiler type-checks behaviours against it.
- **Ordering**: deterministic and total — by (hook id, priority, content-pack load order, behaviour id). Within a hook, each behaviour sees the state as left by previous ones.
- **Re-entrancy**: an effect executed inside a hook may *enqueue* further signals; they are dispatched after the current hook completes (breadth-first queue), never recursively. Depth of the signal cascade per command is capped (e.g. 64); exceeding it is a diagnostic failure of the offending behaviour, not a crash.
- **Idempotency**: effects are applied through the host with an `EffectKey = (behaviorInstanceId, awaitSiteOrEffectLabel, sequence)`; the host records applied keys in the instance's receipt set so a replay/migration cannot double-apply (same principle as `CommandReceipt`).
- **Limits**: per-activation step budget (e.g. 10k IR ops), per-loop iteration cap, call-depth cap, max allocated values, no I/O, no wall clock, no `Random` — only host-provided `Roll(streamId)` and `Now` (minutes).

---

## 5. Determinism and sandboxing

- RNG: Factorio re-implements `math.random` over the map generator and forbids `randomseed` (VERIFIED above); Durable Task routes randomness through recorded activity results or a fixed-seed generator (VERIFIED above); Ink stores `storySeed`/`previousRandom` in state (VERIFIED above). → Behaviours call `host.Roll(streamId, spec)`; the result is drawn from the named `Pcg32` stream in `GameState.RandomStreams` and **recorded in the frame/receipts** so resume never re-draws.
- Clock: Durable Task `CurrentUtcDateTime` replay-stable (VERIFIED above) → behaviours read `host.NowMinutes` (the `GameState.TimeMinutes` snapshot at activation) and request `Await.Minutes(n)` which the host schedules as `dueAt = NowMinutes + n` (data), never sleep.
- Floating point: platform `Math` libraries differ in last bits across x64/arm64 (VERIFIED examples: https://support.microsoft.com/kb/2435853 for VC++ x86 vs x64 trig; Linaro `pow` arm64 report https://linaro.atlassian.net/wiki/x/I4BxrAY; Factorio replaced trig/exp/log for this reason). → IR value model is **integer-only** (Int64, Bool, Text, Ids, Enums, Lists, Records); percentages as integer basis points; no float type at all in v1.
- Read-only queries vs transactional writes: queries (`GetAttribute`, `HasItem`, `Count`) are pure and may run during condition evaluation; writes go through `Effect` instructions that are validated by domain rules (the same `RuleViolation` path commands use) and are the *only* mutation channel. A behaviour activation runs inside the command application that triggered the hook, so the full snapshot save already captures its effects atomically (INFERRED from the project's full-snapshot design).
- Budgets: Jint's constraints are cooperative and "cannot preempt arbitrary host callbacks" (VERIFIED) and Lua-CSharp's hook exceptions are `pcall`-catchable (VERIFIED) — both argue for an interpreter we control where the step counter sits in the dispatch loop and host calls are bounded by construction.
- Tracing: every IR op carries a `SourceRef` (node id or line:col) and the trace records `(stepIndex, opIndex, sourceRef, opcode, operand summary, result)` plus effect keys and await decisions; traces are ring-buffered per activation and exported on failure (`BehaviorDiagnostic`). This mirrors Factorio's `debug.getinfo` `p` flag (current instruction index, VERIFIED) and CK3's requirement to inspect scopes per hook.

---

## 6. Save compatibility for suspended behaviours

Evidence: Ink save version/min-compatible constants and "closest valid container" recovery (VERIFIED above); CDDA "reactivated EOCs currently lose all context variables" (VERIFIED); Durable Functions breaking-change list and three mitigations (VERIFIED); Factorio `on_configuration_changed` runs "when the game or a mod version changes ... or a migration is applied" and may modify `storage` (VERIFIED).

Policy (INFERRED design):
1. Each behaviour definition carries `(BehaviorId, DefinitionVersion, ContentHash)`; each suspended `BehaviorFrame` stores all three plus `FrameFormatVersion`.
2. On load, for each frame: if `ContentHash` matches → resume. Else if the new definition declares `Migrations[fromVersion]` → run the migration (a pure function on the frame record: remap labels, add defaulted variables, drop removed ones). Else apply the definition's `OnStaleInstance` policy: `AbortSilently` (discard frame, log), `AbortWithCompensation` (run a declared `on_abort` entry with the frame's variables, then discard), or `RestartAtEntry` (Yarn-style: restart the behaviour from its current *stage* label — only valid if stages are declared as restart-safe).
3. Await sites and labels are addressed by stable ids (never IR offsets) so that edits elsewhere in the behaviour don't invalidate frames (Ink path-shift lesson). The compiler emits a diagnostic if a stable id used by any known-suspended stage disappears.
4. Applied-effect keys stay with the frame until completion so a migrated or restarted frame cannot re-apply them.
5. Full-snapshot saves already carry `GameState`; frames live inside it (e.g. `GameState.Behaviors: Dictionary<string, BehaviorFrame>`), serialized through the same source-generated JSON context.

---

## Recommended contract shape (draft)

Pure C# (net8.0, `IsAotCompatible`), no reflection, source-generated JSON. Names are sketches.

```csharp
namespace Patriark.Domain.Behaviors;

// ---------- Hook catalogue (owned by the domain, versioned) ----------
public enum ValueKind { Int, Bool, Text, EntityId, Enum, List, Record }
public sealed record FieldSpec(string Name, ValueKind Kind, string? EnumOrRecordType = null);
public sealed record HookDescriptor(
    string HookId,            // "on_arrive_node", "on_rest_complete", "on_roll_resolved", "signal:<custom>"
    int Version,
    string RootEntityKind,    // "Character" | "Node" | "Campaign" ...
    IReadOnlyList<FieldSpec> ContextFields,
    bool AllowsAwait,         // some hooks are query-like (e.g. modifiers) and must complete synchronously
    bool AllowsEffects);

public sealed record HookContext(string HookId, string RootEntityId, IReadOnlyDictionary<string, Value> Fields);

// ---------- Values (integer-only, id-based; no object refs, no floats) ----------
public abstract record Value;                       // Int(long) | Bool | Text | EntityId(kind,id) | Enum(type,name) | List | Record
// (concrete records omitted; all are immutable and JSON source-gen serializable)

// ---------- Restricted game API seen by behaviours ----------
public interface IBehaviorHost
{
    long NowMinutes { get; }                                   // snapshot of GameState.TimeMinutes at activation
    Value Query(string queryId, IReadOnlyList<Value> args);    // pure, catalogued read-only queries
    EffectResult Apply(EffectKey key, string effectId, IReadOnlyList<Value> args); // transactional write; idempotent by key
    RollResult Roll(EffectKey key, string streamId, RollSpec spec);               // draws from named Pcg32 stream; recorded under key
    void Raise(string signalId, IReadOnlyDictionary<string, Value> fields);       // enqueued, dispatched after current hook
    void Trace(TraceEvent e);
}

public readonly record struct EffectKey(string InstanceId, string SiteLabel, int Sequence);

// ---------- Suspension ----------
public abstract record AwaitRequest(string SiteLabel);
public sealed record AwaitMinutes(string SiteLabel, long DueAtMinutes) : AwaitRequest(SiteLabel);
public sealed record AwaitDecision(string SiteLabel, string PromptId, IReadOnlyList<DecisionOption> Options) : AwaitRequest(SiteLabel);
public sealed record AwaitRoll(string SiteLabel, string StreamId, RollSpec Spec) : AwaitRequest(SiteLabel);   // when the roll is player-visible/interactive
public sealed record AwaitSignal(string SiteLabel, string SignalId, IReadOnlyDictionary<string, Value>? Filter) : AwaitRequest(SiteLabel);

public abstract record Resumption;   // MinutesElapsed | DecisionMade(optionId) | RollResolved(RollResult) | SignalReceived(fields)

// ---------- Serializable frame (lives in GameState.Behaviors) ----------
public sealed record BehaviorFrame(
    int FrameFormatVersion,
    string InstanceId,
    string BehaviorId, int DefinitionVersion, string DefinitionContentHash,
    string HookId, HookContext Activation,                 // the typed context that started it
    IReadOnlyList<CallFrame> Stack,                        // each: FunctionId, ResumeLabel (stable id), Locals
    IReadOnlyDictionary<string, Value> InstanceVars,       // Behavior-scoped variables
    AwaitRequest? Pending,                                 // null => runnable
    IReadOnlyList<EffectKey> AppliedEffects,               // receipts: never re-apply
    IReadOnlyList<RecordedRoll> Rolls,                     // (key, streamId, result) for replay/audit
    long StepsUsed, int SignalDepth);
public sealed record CallFrame(string FunctionId, string ResumeLabel, IReadOnlyDictionary<string, Value> Locals);

// ---------- Execution entry points ----------
public interface IBehaviorRuntime
{
    RunResult Activate(BehaviorDefinition def, HookContext ctx, IBehaviorHost host, Budget budget);
    RunResult Resume(BehaviorDefinition def, BehaviorFrame frame, Resumption input, IBehaviorHost host, Budget budget);
}
public sealed record Budget(int MaxSteps, int MaxCallDepth, int MaxLoopIterations, int MaxValues);
public abstract record RunResult;  // Completed(trace) | Suspended(frame, trace) | Failed(BehaviorDiagnostic)

// ---------- Diagnostics ----------
public sealed record SourceRef(string Unit, string? NodeId, int? Line, int? Column);
public sealed record TraceEvent(int Step, string Opcode, SourceRef Source, string Summary);
public sealed record BehaviorDiagnostic(string Code, string Message, SourceRef? Source, IReadOnlyList<TraceEvent> Tail);

// ---------- Versioning policy on the definition ----------
public enum StalePolicy { AbortSilently, AbortWithCompensation, RestartAtStage }
public sealed record BehaviorDefinition(
    string BehaviorId, int Version, string ContentHash,
    IReadOnlyList<string> SubscribedHooks,
    IrProgram Program,                                   // the neutral IR
    IReadOnlyDictionary<int, IrMigration> Migrations,    // fromVersion -> frame transform
    StalePolicy OnStale, string? CompensationEntry);
```

**IR option recommended: a small, domain-owned, label-addressed linear IR ("PBIR") with an integer-only value model, interpreted by a hand-written C# VM whose call frames are the `BehaviorFrame` records above.** Why:
- It is the only option that gives a *serializable continuation* under NativeAOT (Ink proves the pattern in C#; Eris proves it for Lua but only for C Lua 5.2/5.3; no C# Lua/JS interpreter offers it).
- Both a script syntax and a graph lower to it (graph nodes ↔ IR functions/ops with stable labels), so one serializer, tracer, type-checker and migrator.
- Determinism and sandboxing are by construction (no float, no I/O, host-injected clock/RNG, step budget in the dispatch loop), unlike cooperative limits in Jint/Lua-CSharp.
- Zero third-party runtime risk (MoonSharp: no stable release since 2016; Lua-CSharp: 0.5.x; Jint: suppressed IL warnings, JS semantics).
- If a general-purpose language is wanted later for *pure expressions only* (no awaits), Lua-CSharp is the fallback candidate (VERIFIED AOT claim + source-gen interop), but it would sit *inside* an `Eval` op, never own control flow.

**Defer to the later task:** concrete script grammar and graph file format; the compiler(s) to PBIR and the Godot-side editor; the full hook catalogue contents; migration tooling UX; trace viewer; performance tuning; any decision on using Lua-CSharp for expressions.

**Fix now in the contract:** hook ids + typed contexts + versioning; `Value` model (integer-only, id-based); `IBehaviorHost` surface (Query/Apply/Roll/Raise/Trace) with `EffectKey` idempotency; `AwaitRequest`/`Resumption` kinds; `BehaviorFrame` schema with `FrameFormatVersion`, definition hash, stable labels; `Budget` and failure semantics (fail the behaviour, never the command); `StalePolicy`.

**Top risks:**
1. Godot .NET on iOS is still experimental and its bindings are "not fully compatible with trimming" (VERIFIED maintainer statement) — the domain can be AOT-clean, but the app around it may not be; validate an iOS NativeAOT export early.
2. Scope creep into "a real language": authors will want floats, strings ops, loops — keep the integer model and budgets; add ops to the catalogue rather than general facilities.
3. Versioning of suspended frames is the hardest part (Ink: "never possible to make this perfect"; Durable: reorder of awaits breaks replay) — enforce stable labels and ship the `StalePolicy` from day one.
4. Hook cascade/re-entrancy and ordering bugs (Paradox "infinite fallback loops" stall time) — queue-not-recurse, depth cap, total ordering.
5. Owning an interpreter is maintenance; mitigated by small op set, exhaustive golden-trace tests in `src/domain` tests, and the fact that every alternative lacks serializable continuations anyway.
