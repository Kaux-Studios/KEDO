# Research I — Merge / inheritance semantics for KEDO

Date: 2026-10-15. Scope: value-level `extends`, mixins, components, list merging, deletion, late binding, provenance, cycles, performance, validation. Every claim is tagged VERIFIED (URL, read today) or INFERRED (reasoning / prior knowledge not re-checked against a primary source today).

Sources read today (primary): CUE concept doc + spec; Nickel merging manual + RFC 001 (overriding); Jsonnet language reference + formal spec; Pkl language reference; Dhall built-in types reference + type-inference standard; Kubernetes strategic-merge-patch design doc + Server-Side Apply docs; RFC 7386 and RFC 6902; Nixpkgs manual (overlays) + `lib.recursiveUpdate`; Terraform override files; CSS Cascade 5; Unreal config-file docs + UObject docs + UBlueprintGeneratedClass API ref; Unity 6.6 prefab variants; Python 2.3 C3 MRO paper; Databricks Sjsonnet post; Salsa book; Lippert on red-green trees.

---

## 1. Survey of merge models

### 1.1 CUE — lattice / unification

- Model: every value, including "types", lives in one partial order with top `_` and bottom `_|_`; unification `&` is the greatest lower bound; "we could always unambiguously merge two such configurations independently of order." VERIFIED (https://cuelang.org/docs/concept/the-logic-of-cue/)
- Spec: "The unification of values a and b is defined as the greatest lower bound of a and b. It is commutative, associative, and idempotent." "Any evaluation error in CUE results in a bottom value." "The unification of a value with bottom is always bottom." VERIFIED (https://cuelang.org/docs/reference/spec/)
- Conflict = bottom: `true & false` is an error; conflicting required fields make the struct faulty, conflicting optional fields do not. No override exists anywhere. VERIFIED (concept doc).
- Defaults: `*` marks a disjunct; "non-starred elements are dropped in favor of starred ones if the starred ones do not resolve to bottom." Defaults unify pairwise (`⟨v1,d1⟩ & ⟨v2,d2⟩ => ⟨v1&v2, d1&d2⟩`), so `(*1|2) & (1|*2)` has value `1|2` and NO default: ambiguous defaults vanish rather than pick one. VERIFIED (spec). CUE calls defaults "CUE's equivalent of inheritance, specifically the kind that allows instances to override any value of its parent." VERIFIED (concept doc).
- Closedness: "By default, structs are open to adding fields"; a referenced definition `#X` "will not unify with a struct that would add a field anywhere within the definition that it does not already define"; "Closing a struct is equivalent to adding `..._|_` to it"; embedding bypasses closedness. VERIFIED (spec).
- Cycles: reference cycles (`a: a`) are treated as `_`; field cycles (`a: b, b: a & v`) resolve to `v` ("detect cycles of this kind, ignore r, and take v"); structural cycles (`a: b: a`, infinite `#List`) are disallowed unless some conjunct is non-cyclic (`tail: null | #List`). VERIFIED (spec).
- Lists: open (`[1, ...]`) or closed; the spec models lists as `{Elem, Tail}` structs (stated as illustrative), which gives position-wise unification; closed lists of different length conflict. VERIFIED (spec). Nickel's RFC says of CUE: "values can only be replaced, not extended, and a record can't be overridden a second time". VERIFIED (https://github.com/tweag/nickel/blob/master/rfcs/001-overriding.md)
- Rationale against override: "In CUE, if one sees a concrete value for a field, it is guaranteed that this will be the final result"; with overrides, a concrete value "does not guarantee a final answer, because another concrete value that occurs elsewhere can override it"; "an instance may never violate the constraints of its parent." VERIFIED (concept doc).
- Performance: evaluator v3 (2024+) brought a new disjunction algorithm, new closedness algorithm, new scheduler and "structure sharing"; closedness "has resulted in significant memory pressure, and this can grow when a closed value such as a definition is referenced many times from different contexts"; v0.14 notes report up to 50% CPU/memory reductions from a rewritten closedness data structure. VERIFIED (https://tip.cuelang.org/docs/concept/faq/upgrading-from-evalv2-to-evalv3, https://github.com/cue-lang/cue/issues/2853, https://cuelang.org/releases). Lesson: a general lattice evaluator with closedness bookkeeping is expensive at scale and took years to make fast.

Columns for the matrix: no inheritance (commutative composition by `&`); linearization none; lists positional; deletion impossible; late binding yes (references resolve against the unified result with explicit cycle rules); diamond harmless (idempotent); provenance: conflict errors list all contributing positions — INFERRED from CUE's error style, not re-verified today.

### 1.2 Nickel — merge with priorities, recursive records

- `&` is symmetric: "Merge is a symmetric operation". Records merge recursively; non-record values merge only if equal (numbers, bools, strings, enum tags, `null`, arrays via `std.contract.Equal`); otherwise `_|_` ("non mergeable terms"). Arrays are NOT concatenated by `&`; `@` concatenates. VERIFIED (https://nickel-lang.org/user-manual/merging/)
- Priorities: `default` < numeric `priority n` (any number incl. negative/fractional; plain = 0) < `force` ("can never be overridden"). Different priority: the higher "simply erases the other"; equal priority: recursive merge, may fail. VERIFIED.
- Metadata: field-level contracts (`foo | Number = 1`) propagate through merges and are enforced on the final value; `doc` propagates, and with two docs "the behavior is unspecified". "As long as the field's value isn't requested, contracts are accumulated but not yet applied" — intermediate values may violate a contract without error until forced. VERIFIED.
- Recursive overriding = late binding: "Records are recursive by default"; overriding `version` recomputes `input.url`; setting `firewall.open_proto.ftp = false` drops port 21 from the computed `open_ports`. VERIFIED.
- RFC 001 (design rationale): Nix fixpoints, Nix overlays, NixOS modules, Jsonnet and CUE all "represent recursive records as functions of self and compute a fixpoint". Inheritance is "order-dependent, grouped in layers"; merging is "commutative, with precedence encoded as priorities". Costs: "Recomputing the fixpoint at each field access is wasteful"; mitigations: memoize the fixpoint, invalidate `self` only on merge, hoist non-recursive fields; each field holds a thunk plus, for recursive fields, its original expression. Priorities cause "non-locality: the final result depends on other priorities of the field's values"; at equal priority "the order is not specified, and may even be randomized by the interpreter". Nix-style userland override lacks "first-class access to information such as source location and metavalues"; merge trees "need to be accessible even after evaluation" to explain results. VERIFIED (https://github.com/tweag/nickel/blob/master/rfcs/001-overriding.md)

Columns: n-ary commutative merge; no linearization; lists equal-or-error; deletion none (only `optional`/`not_exported` hide); late binding yes; diamond harmless if equal; provenance: merge tree retained as a design goal.

### 1.3 Jsonnet — object inheritance with `self`/`super`

- "Object inheritance A + B creates a new object with B layers on top of A layers"; "`self` is not a value, but a reference to the 'current' object", lookup "starting from the top of the stack"; `super` "searches for the field starting from the layer below the current one"; "This context is determined when an object is indexed from outside" — `self` is bound to the final composed object: full late binding. VERIFIED (https://jsonnet.org/ref/language.html). Formal rule: in `e^L + e^R`, right-hand bodies get `self :=` the combined object and `super :=` an object wrapping the left side; on manifestation `self` is replaced by the object and `super` by `{}`. VERIFIED (https://jsonnet.org/ref/spec.html)
- `+:` field merge: "`{ a +: b }` is equivalent to `{ a: if "a" in super then super.a + b else b }`"; relevant "for nested objects (which will be inherited) and arrays (which will be concatenated)"; "It is not an error to have `+:` without a matching field on the left hand side." Plain `:`: "By default nested objects are completely replaced when overridden." Arrays: `+` concatenates. VERIFIED (language ref; spec confirms the `in super` guard in the desugaring).
- Visibility: `:` "visible unless parent's field with the same name is hidden"; `::` hidden; `:::` forces visible; spec rule `h_L + h_R = h_L if h_R = ':', otherwise h_R`. Hidden fields are omitted from manifestation and equality. VERIFIED.
- Deletion: none. `null` is a value ("objects can have null fields"); the only removal is a computed field name evaluating to `null`. Hiding with `::` is the idiom. VERIFIED. `std.prune` strips nulls — INFERRED.
- Laziness: function arguments "are passed lazily and evaluated only when used"; array elements lazy; strings strict; "both object fields and array elements have yet to be executed to yield values". VERIFIED.
- Diamond / multiple inheritance: `a + b + c` is left-associative layering with no linearization; a mixin added twice layers twice (doubles `+:` arrays). INFERRED.
- Performance (Databricks Sjsonnet): in google/jsonnet "each time you ask for the value, the field is re-evaluated" ("more like method evaluation than field references"); `local x1 = {k: x0.k + x0.k}` × 20 is exponential; Sjsonnet "caches object fields to avoid unnecessary re-computation" → 23x there, 458x on a Kubernetes config, 28–60x on a monorepo. VERIFIED (https://www.databricks.com/blog/2018/10/12/writing-a-faster-jsonnet-compiler.html). The semantic root: because `self` is bound per composed object, a base object's field value cannot be reused by a derived object; evaluation is per (final object, field). INFERRED.

Columns: binary `+` chained (order-dependent); no linearization; lists concatenate under `+:`, replace under `:`; deletion none; late binding full; cycles = runtime failure (INFERRED: stack overflow / max stack frames); provenance: none built-in (stack traces only) — INFERRED.

### 1.4 Pkl — `amends` / `extends`, late-bound properties

- "An object can be amended to form a new object that only differs in selected properties"; "their prototype chain cannot be directly accessed or even modified"; `pigeon {}` ≡ `pigeon = (super.pigeon) {}`. "When a typed object is amended, its properties can be overridden or amended, but new properties cannot be added" (closed by class). VERIFIED (https://pkl-lang.org/main/current/language-reference/index.html)
- Late binding: "Late binding of properties is an incredibly useful feature for a configuration language"; "Property values are lazily evaluated on the first read"; amending a property changes properties defined in terms of it. `const` members "are not" late-bound (they may reference only other `const`s). VERIFIED.
- Listings/mappings: amend by index `[0] { … }`, replace `[1] = new {…}`, add with `new {…}`; "Like regular listing elements, the default element is late-bound"; explicit values beat defaults; "Keys are eagerly evaluated; values are lazily evaluated on the first read." VERIFIED.
- Deletion: no delete; documented workaround converts to Map, removes the key, converts back. VERIFIED.
- Modifiers: `hidden` (omitted from output, equality, hashing), `local`, `fixed` ("cannot be assigned to or amended"; "the fixedness of the overridden property must be preserved"), `const`. VERIFIED.
- Classes: "Classes are arranged in a single inheritance hierarchy"; "Only modules declared as open can be extended"; `abstract` exists. VERIFIED.

Columns: single inheritance for classes; linear amend chain; lists by index/append; deletion none; late binding full; cycles: runtime error (INFERRED); provenance: none built-in (INFERRED).

### 1.5 Dhall — no inheritance, explicit operators

- `⫽` (`//`): "non-recursively merges record values, preferring fields from the right record when they conflict"; `∧` (`/\`): "recursively merges record values"; `⩓`: "recursively merges record types"; "If they share a field in common that is not a record type then that is a type error." `l ∧ r` is typed through `T₀ ⩓ T₁`, so a same-named non-record field under `∧` is a type error (chain of rules; the standard does not say it in one sentence). VERIFIED (https://docs.dhall-lang.org/references/Built-in-types.html, https://github.com/dhall-lang/dhall-lang/blob/master/standard/type-inference.md). `::` fills a record from a schema's `default`; `with` does nested update. VERIFIED.
- Why no inheritance: Dhall is total and non-recursive (no general recursion, no `self`), so there is nothing to late-bind; "templates" are plain functions and overriding is explicit `//`/`with`. INFERRED (core Dhall design principle; not re-read on a page today).

Columns: no inheritance; explicit right-biased override or recursive merge that errors on scalar conflicts; lists replaced; deletion only via projection/`with` (INFERRED); late binding none; cycles impossible; provenance none.

### 1.6 Kubernetes — Strategic Merge Patch, Server-Side Apply, RFC 7386, RFC 6902

- SMP (doc says "This document is old and probably obsolete", recommends SSA): per-field `patchStrategy`/`patchMergeKey` from Go struct tags; default for lists is replace (order preserved); `merge` lists of maps combine by key and are treated as "an unordered set"; `$patch: replace|delete|merge`; `$deleteFromPrimitiveList/<name>`; `$setElementOrder/<name>` retains the given relative order (order of unnamed elements unspecified); `$retainKeys` for unions. Limitations: delete removes all entries matching the key; duplicates possible in sets; unknown directives silently ignored; no patch versioning. VERIFIED (https://github.com/kubernetes/community/blob/master/contributors/devel/sig-api-machinery/strategic-merge-patch.md)
- SSA: `+listType=atomic|set|map`, `+listMapKey`, `+mapType/+structType=atomic|granular`. "If configured as atomic, the entire list is replaced during merge. At any point in time, a single manager owns the list." "If set or map, different managers can manage entries separately." "The atomic list type is recursive." Custom resources default: "all lists are considered atomic". Provenance: `managedFields` stores manager, operation (`Apply`/`Update`), apiVersion, time and a `fieldsV1` tree of owned paths; "Whenever a field's value does change, ownership moves from its current manager to the manager making the change"; a conflict "is a special status error" and can be forced with `--force-conflicts`. VERIFIED (https://kubernetes.io/docs/reference/using-api/server-side-apply/)
- RFC 7386 (obsoleted by 7396, same algorithm): recursive object merge; "Null values in the merge patch are given special meaning to indicate the removal of existing values in the target"; non-objects (arrays) are replaced whole; a literal null therefore cannot be set. VERIFIED (https://www.rfc-editor.org/rfc/rfc7386)
- RFC 6902: ops `add|remove|replace|move|copy|test`; array `add` inserts at index, `-` appends; "Operations are applied sequentially in the order they appear in the array"; any failure ⇒ "application of the entire patch document SHALL NOT be deemed successful"; JSON Pointer paths. VERIFIED (https://www.rfc-editor.org/rfc/rfc6902)

Lessons: (a) list merge strategy must be declared per field in the schema; (b) keyed lists need a declared key and a defined order rule for new elements; (c) deletion must be an explicit directive, never `null`; (d) per-path ownership is a cheap, proven provenance model.

### 1.7 Nix — overlays and `recursiveUpdate`

- Overlays are functions of `final` and `prev`: `final` "corresponds to the final package set" (fixed point; "use this set for the dependencies of all packages"), `prev` "corresponds to the result of the evaluation of the previous stages" and "does not contain any of the packages added by the current overlay, nor any of the following overlays." "Nixpkgs can be configured with a list of overlays, which are applied in order"; "the order of the overlays can be significant if multiple layers override the same package." VERIFIED (https://nixos.org/manual/nixpkgs/stable/)
- `lib.recursiveUpdate`: "A recursive variant of the update operator //"; "The recursion stops when one of the attribute values is not an attribute set, in which case the right hand side value takes precedence" ⇒ lists are replaced, not merged. VERIFIED (https://noogle.dev/f/lib/attrsets/recursiveUpdate)
- Risk: `final` is a lazy fixpoint; self-dependent use yields "infinite recursion" only when forced, with poor locations. INFERRED (well known; not on the pages read today).

### 1.8 Terraform override files

- Override files (`override.tf`, `*_override.tf`) are skipped at first, then applied "in lexicographical order"; "an attribute argument within an override block replaces any argument of the same name in the original block"; "any nested blocks within an override block replace all blocks of the same type in the original block"; "The contents of nested configuration blocks are not merged." Exceptions: `lifecycle` merged per argument; override `provisioner`s drop originals; `connection` replaces; `depends_on` forbidden in overrides (error); `required_providers` merged per element; `locals` per value. "Use override files only in special circumstances"; "Over-use of override files hurts readability". VERIFIED (https://developer.hashicorp.com/terraform/language/files/override)

Lesson: shallow replace-by-default with a short explicit exception list and a deterministic file order is easy to specify and to explain.

### 1.9 CSS cascade — cautionary example

- Sorting order (descending): "Origin and Importance", "Context", "Element-Attached Styles", "Layers", "Specificity", "Order of Appearance". `!important` inverts origin order (important UA > user > author vs normal author > user > UA), inverts layer order (normal: last layer wins; important: first layer wins) and inverts context order. VERIFIED (https://www.w3.org/TR/css-cascade-5/)
- Why cautionary: six orthogonal precedence axes, one of which reverses two others; nobody predicts the winner without devtools, and "where did this value come from" needs a panel listing every losing declaration. Any value-level merge with more than one precedence axis (priority numbers AND source order AND `force`) drifts this way. INFERRED.

### 1.10 Unreal — CDO inheritance and `.ini` layering

- "Each UCLASS maintains one Object called the Class Default Object (CDO)", "generated by the class constructor and unmodified thereafter"; CDO and class "should generally be considered read-only". VERIFIED (https://dev.epicgames.com/documentation/en-us/unreal-engine/objects-in-unreal-engine). `UBlueprintGeneratedClass` exposes a list of properties whose defaults differ from the parent's default object, plus `GetArchetypeForCDO()`. VERIFIED (https://docs.unrealengine.com/4.27/en-US/API/Runtime/Engine/Engine/UBlueprintGeneratedClass/). Blueprint defaults stack on the parent CDO and instances are delta-serialized against their archetype (only differing properties saved). INFERRED (the Blueprint compiler-overview page returned no body / 403 today).
- Config: 12-layer hierarchy (Base.ini → BaseEngine.ini → platform base → DefaultEngine.ini → platform → user files); "key-value assignments in latter files override those from files earlier in the hierarchy." Array prefixes: `!` "Empty the contents of an array", `+` "Append the value to the array if it does not yet exist", `-` "Remove the value from the array. Must be an exact match", `.` "Append the value to the array, even if the value already exists". Worked example yields `[2,3,2]`. "Command-line overrides do not work with arrays." VERIFIED (https://dev.epicgames.com/documentation/en-us/unreal-engine/configuration-files-in-unreal-engine)

Lesson: `+`/`-`/`!`/`.` is a proven, minimal author-facing vocabulary for set-like list edits across layers.

### 1.11 Unity prefab variants

- "A prefab variant inherits properties from a base prefab"; "Overrides in the variant take precedence over the base values"; overrides can "modify property values, add or remove components, or add or remove child GameObjects"; "A variant can use any prefab as its base, including other variants"; "Revert All" / "Apply all to Prefab Variant parent". VERIFIED (https://docs.unity3d.com/Manual/PrefabVariants.html, Unity 6.6 manual). Removed-component overrides arrived in Unity 2022.1; earlier versions only allowed adding. INFERRED.
- Model: single-parent chain, per-property override deltas on the variant, components keyed by (type, slot) — the closest analogue to "components keyed by type id with add/remove overrides". INFERRED.

### 1.12 C3 linearization (reference point for multiple inheritance)

- `L[C(B1…BN)] = C + merge(L[B1]…L[BN], B1…BN)`; take the first head not in the tail of any list; fail if none. Properties: monotonicity, local precedence order. Failure: `A(X,Y)`, `B(Y,X)`, `C(A,B)` → "TypeError: MRO conflict among bases Y, X". VERIFIED (https://www.python.org/download/releases/2.3/mro/)

---

## 2. Comparison matrix

| System | Arity | Linearization | Order of application | Lists | Deletion | Late binding (`self` = final?) | Cycles | Diamond | Provenance |
|---|---|---|---|---|---|---|---|---|---|
| CUE | n-ary `&`, commutative | none | order-independent | positional unify; open/closed | none | yes | ref cycles → `_`; structural cycles error | harmless | error positions (INFERRED) |
| Nickel | n-ary `&`, commutative | none | priorities; equal priority "unspecified" | equal-or-error; `@` concat | none | yes (recursive records) | runtime | harmless if equal | merge tree kept (design) |
| Jsonnet | binary `+` chained | layering | left→right | `+:` concat; `:` replace | none (hide `::`) | yes, full | runtime (INFERRED) | re-layered | none |
| Pkl | single `extends`; linear `amends` | trivial | base → amender | by index, add, late default | none | yes | runtime (INFERRED) | n/a | none |
| Dhall | none | n/a | explicit `//`, `∧` | replace | n/a | no | impossible | n/a | none |
| K8s SMP / SSA | patch | n/a | sequential | atomic / set / map by key | `$patch: delete`, `$deleteFromPrimitiveList` | no | n/a | n/a | `managedFields` |
| RFC 7386 | patch | n/a | one patch | replace whole | `null` | no | n/a | n/a | none |
| RFC 6902 | patch ops | n/a | sequential, atomic | index / `-` append | `remove` | no | n/a | n/a | none |
| Nix overlays | list | none | list order | replace (`recursiveUpdate`) | `removeAttrs` (INFERRED) | yes (`final`) | infinite recursion at force | n/a | none |
| Terraform override | base + overrides | n/a | lexical file order | nested blocks replaced | none | no | n/a | n/a | none |
| CSS | n/a | n/a | 6-axis cascade | n/a | `initial`/`unset` (INFERRED) | computed-value inheritance | n/a | n/a | devtools only |
| Unreal CDO / .ini | single chain; 12 layers | trivial | parent → child; earlier → later | `+ - ! .` | `-`, `!` | no | n/a | n/a | "differs from parent CDO" |
| Unity variants | single chain | trivial | base → variant | components by slot | remove override | no | n/a | n/a | per-property override marks |

---

## 3. Performance / implementation notes

- Laziness costs: Jsonnet re-evaluated fields per access until implementations added per-object field caches (VERIFIED, Databricks); Nickel keeps a thunk per field plus the original expression for recursive fields and must memoize the fixpoint (VERIFIED, RFC 001); CUE's closedness bookkeeping scales with definition reuse and needed a multi-year evaluator rewrite (VERIFIED, issue 2853 / releases). All three pay for answering "what is `o.f`" before `o` is fully resolved and for `self` meaning the final object. A content compiler that resolves every object anyway gains nothing from laziness and pays thunk/closure overhead plus the loss of a plain data representation. INFERRED.
- Topological resolution: `extends` and mixin edges form a DAG; resolve in topological order (Kahn or DFS post-order), fail on a cycle with the full path. Each object resolves once, reading only already-resolved parents: O(objects × fields) with chain depth typically < 10. INFERRED (standard).
- Structural sharing: resolved objects are immutable; a child shares unchanged sub-records/lists with its parent (persistent data structures). Roslyn green nodes: "immutable, persistent, has no parent references, is built bottom-up"; "we rebuild only the portions of the green tree that were affected by the edit". VERIFIED (https://ericlippert.com/2012/06/08/red-green-trees/). Hash-consing (interning equal immutable subtrees by content hash) gives O(1) equality/hash and dedupes memory where most children leave most components untouched. INFERRED (Filliâtre & Conchon, "Type-safe modular hash-consing", 2006; not re-read today).
- Content hashing: hash a canonical serialization (schema field order, resolved list order, references by stable id). A child's hash depends on its own resolved content, not on the parent's hash, so an upstream no-op change does not re-hash dependents. INFERRED.
- Incremental by file (Salsa model): inputs (files) → tracked queries (parse, index declarations, resolve object, validate object). "Each time you set an input, the revision is incremented"; memoized values are reused when no dependency changed; when a recomputed value is unchanged, "we can backdate the result, meaning that we say that, even though the inputs changed, the output didn't", so dependents are not re-run. VERIFIED (https://salsa-rs.github.io/salsa/overview.html, https://salsa-rs.github.io/salsa/reference/algorithm.html). Mapping: `Resolve(id)` depends on `Decl(id)`, `Resolve(parent)`, `Resolve(mixin_i)`, and `ExistsWithType(ref)` for each reference; `Decl` depends on `Parse(file)`. A file edit re-parses; declarations with an unchanged content hash are backdated; only objects whose declaration or ancestors changed re-resolve.
- Deterministic ordering: never iterate a hash map into output or diagnostics; order by (file path, declaration order) or by id; keep authored list order with explicit inherited-before-own rules. Nickel's "may even be randomized by the interpreter" at equal priority is the thing to avoid. VERIFIED (quote) / INFERRED (design).

---

## 4. Typing and validation

- Where systems validate: CUE during unification (constraints are values; conflicts surface at the unification point). Nickel: contracts accumulate lazily and apply when forced, so intermediate states can be invalid (VERIFIED). Pkl: typed objects are closed; `fixed`/`const` enforced at amend time (VERIFIED). K8s: schema markers drive merging; validation after merge (INFERRED).
- Recommended for KEDO: two stages. Stage A on the declaration (unknown component type id, unknown field under a closed schema, wrong scalar kind, malformed list op) — exact authored location, no resolution needed. Stage B on the fully resolved object (required fields, ranges, enums, reference target existence and type, cross-field invariants). Required-field checks on `abstract` bases are skipped; they run for every concrete descendant. INFERRED.
- Diagnostics must point at the authored location of an inherited value: keep `path → (file, span, objectId, layer)` per resolved object (SSA `managedFields` as the model, VERIFIED above). A range violation on `Child.Stats.Hp` inherited from `Base` reads: "Child.Stats.Hp = -5 out of range [0, 9999]; value defined at base.kedo:12:8 (Base), inherited by child.kedo:3:1 (Child extends Base)". Nickel's observation that userland mechanisms lack "first-class access to information such as source location" is the argument for building provenance into the resolver. VERIFIED (quote) / INFERRED (design).
- Structural typing of components: a component type id selects a schema (fields, kinds, required/optional, defaults, constraints). A reference field may require "object with component X" (structural) or "object of type T" (nominal). Schemas are closed by default (CUE definitions, Pkl typed objects): unknown field ⇒ error with a did-you-mean. INFERRED.
- Defaults: schema defaults are the lowest layer; resolution records "schema default" vs "authored" so any explicit value beats a default and diagnostics can say so; hashing uses the resolved value only. INFERRED (Nickel `default`, CUE `*` as models).

---

## 5. Late binding: for and against

For (Jsonnet, Pkl, Nickel, CUE, Nix all chose it):
- Derived fields (`MaxHp = BaseHp * Level`) follow subclass overrides automatically; Nickel RFC's `port`/`protocol` example. VERIFIED (RFC 001).
- Fewer explicit parameters; authors "just override the leaf".

Against, in a performance-first deterministic compiler:
- Every field body becomes a closure evaluated per final object; base results cannot be reused by children; cost scales with objects × late-bound fields. Jsonnet needed per-object caches to be usable; Nickel documents fixpoint recomputation as a cost. VERIFIED (Databricks, RFC 001).
- Cycles become dynamic and are detected only when forced, with poor locations (Nix "infinite recursion", Jsonnet stack overflow); CUE had to write special cycle rules into its spec. VERIFIED (CUE spec) / INFERRED (others).
- Non-local reasoning: a base author cannot know what their expression evaluates to in a subclass; the CUE guarantee "if one sees a concrete value for a field, it is guaranteed that this will be the final result" is lost. VERIFIED (quote).
- Incrementality: a change to a base field body may change every descendant's value even where the descendant overrides nothing, so dependency tracking must be per field rather than per object.
- Game content rarely needs expressions at merge time; scaling and display strings are better computed at runtime or in a separate, explicitly ordered computed-fields pass.

Alternatives:
1. Explicit parameters/templates: `extends Template(level: 3)`; no `self`; cacheable per argument tuple (Dhall style).
2. Computed fields evaluated once after resolution: `computed MaxHp = BaseHp * Level` runs in a final pass over resolved fields in a statically determined order; cycles are static errors. Gives most of the benefit (children override `BaseHp`, `MaxHp` follows) with no per-field closures in the merge and a well-defined hash.
3. References instead of copies: `Stats: @BaseStats` shares by id; runtime propagation without inheritance.

Decision for v1: no late binding in the merge; computed fields (option 2) in a strictly later pass, pure and non-recursive, over the object's own resolved fields and referenced objects' resolved fields.

---

## 6. Recommended resolution semantics for KEDO v1

### 6.1 Inheritance arity and linearization
- R1. `extends` takes exactly one parent of the same KEDO type (single inheritance: Pkl classes, Unity variants, Unreal CDO chain). Mixins (`with M1, M2`) are partial objects compatible with the type; an object may include any number, in authored order.
- R2. Mixins may `with` other mixins but may not `extends`. The inclusion list is flattened depth-first, left-to-right, duplicates removed keeping the FIRST occurrence, so a mixin reached twice through a diamond applies once at its earliest position. This order-preserving dedupe replaces C3: mixins have no parent relation to each other, so C3's "MRO conflict" failure mode cannot arise. If two mixins write the same scalar path, the later one wins (R6) and the compiler emits warning K-W-MIXIN-SHADOW with both locations.
- R3. `extends` + `with` edges form a DAG across all files; resolution runs in topological order; each object is resolved exactly once; resolved objects are immutable.

### 6.2 Order of application (layers)
- R4. Layers for object O: `SchemaDefaults(type) → Resolved(Parent) → Mixin_1 … Mixin_n (flattened per R2) → Own(O)`. Later layers take precedence. Schema defaults are a virtual bottom layer so any explicit value beats a default (Nickel `default`, CUE `*`).
- R5. `Resolved(Parent)` is the parent's final value (its own mixins already applied). Mixins apply on top of the parent's result and the child's own fields apply last — the only order that lets a child override a mixin (Jsonnet `A + M + Own`).

### 6.3 Merge rules by kind (path-wise, deterministic)
- R6. Scalars (number, bool, string, enum, asset path, reference): last layer wins. No priorities, no `force`, no `!important`: one precedence axis (layer order) by design (CSS lesson; Nickel "non-locality" quote).
- R7. Records (nested structs): recursive field-wise merge (RFC 7386 / `recursiveUpdate` style). Wholesale replacement requires the explicit `=!` marker. This is the opposite of Jsonnet's `:` default and Terraform's nested-block rule, chosen because partial overrides of nested stat blocks are the dominant authoring case.
- R8. Lists — the schema declares the strategy per field (K8s SSA lesson); unmarked lists are `atomic`:
  - `atomic`: replaced whole by the later layer.
  - `set` (unique scalars): `+ v` add-if-absent, `- v` remove exact, `!` clear (Unreal vocabulary); plain `= [...]` replaces. Order: inherited elements first in inherited order, then additions in authored order.
  - `keyed(k)` (records with unique key `k`): elements merge by key per R7; `- key` removes; new keys append after inherited ones in authored order; `= [...]` replaces. Duplicate key within one layer: K-E-LIST-DUPKEY.
  - `append`: later layer concatenates after inherited (Jsonnet `+:`); no removal except `!` clear.
  No index-based patching (RFC 6902 / Pkl `[0] {…}`) in v1: positional edits break under base changes.
- R9. Components: the component collection is a `keyed(typeId)` list of records. `component X { … }` merges into inherited X (R7); `component X = { … }` replaces; `remove component X` deletes; an X the parent lacks is appended. Resolved order: inherited first, then added in authored order; the canonical form for hashing sorts by type id unless the schema marks component order as significant.
- R10. Tags: a `set` whose default merge is union (`tags = [...]` in a child adds; `tags =! [...]` replaces; `- t` removes; `!` clears). Tags are the one place where replace-by-default would surprise authors.
- R11. References (`@Id`) and asset paths are scalars (R6). They resolve to ids at resolution time, are never inlined, and never cause the target to be merged into the referrer; existence and type are checked in Stage B.

### 6.4 Deletion
- R12. Deletion is explicit and typed: `remove field.path`, `remove component X`, `- value`/`- key` in sets and keyed lists. `null` is a value, never a delete (RFC 7386 trap). Removing something absent from the inherited layers is K-E-REMOVE-MISSING (not a silent no-op: Unreal's "must be an exact match" and K8s's "delete all matching" both cause confusion). Removing a `required` field on a concrete object is K-E-REQUIRED-REMOVED.
- R13. A removal persists in descendants unless re-added; provenance records the removing layer so "why is X missing" is answerable.

### 6.5 Late binding
- R14. None in the merge: authored values are literals or references; no layer evaluates an expression against the final object. `Resolve(O)` depends only on `Resolved(Parent)`, the mixin declarations and O's own declaration.
- R15. Computed fields (may ship in v2): declared in the schema (`computed MaxHp = BaseHp * Level`), evaluated after all objects are resolved, read-only over resolved fields of `this` and of referenced objects, in a statically computed order; cycles are K-E-COMPUTED-CYCLE at schema load. Included in the content hash; never author-overridable (if authors must override, make it a stored field with a default).

### 6.6 Provenance
- R16. Every resolved object carries `canonical path → {file, span, objectId, layerKind ∈ {default, parent, mixin(name), own, removed}}` for every leaf, list element and component, pointing at the ORIGINAL authoring site; the inheritance chain is reconstructible from the object graph. Provenance is excluded from the content hash and stored beside it.
- R17. `kedo explain <Object>.<path>` prints the winning value, its origin, and every shadowed candidate in layer order.

### 6.7 Cycles and limits
- R18. `extends`/`with` cycle ⇒ K-E-INHERIT-CYCLE listing the full path (A → B → C → A) with each edge's location; cycle members are unresolved; dependents report K-E-UNRESOLVED-BASE once (no cascade).
- R19. Reference cycles between objects (`A.Next = @B`, `B.Next = @A`) are allowed; references do not participate in resolution order.
- R20. Limits as errors, not silent truncation: inheritance depth ≤ 32 (K-E-DEPTH), flattened mixins per object ≤ 64 (K-E-MIXIN-COUNT), record nesting ≤ 16 (K-E-NESTING).
- R21. Determinism: iteration in declaration order within a file and sorted file path across files; diagnostics sorted by (file, span, code); resolved output canonicalised before hashing.

### 6.8 Validation and diagnostic codes
- R22. Stage A (per declaration): parse errors; K-E-UNKNOWN-TYPE / K-E-UNKNOWN-COMPONENT / K-E-UNKNOWN-FIELD (closed schemas, with suggestion); K-E-KIND (scalar kind mismatch); K-E-LIST-OP (op not allowed for the field's strategy); K-E-MIXIN-MISSING; K-E-PARENT-MISSING; K-E-PARENT-TYPE; K-E-DUP-ID (both locations).
- R23. Stage B (per resolved concrete object; skipped for `abstract`): K-E-REQUIRED, K-E-RANGE, K-E-ENUM, K-E-REF-MISSING, K-E-REF-TYPE, K-E-ASSET-MISSING (warning in fast mode), schema-declared invariants (K-E-INVARIANT).
- R24. Every Stage-B diagnostic cites the resolved object and path, the authored origin via provenance, and the inheritance chain. Warnings: K-W-MIXIN-SHADOW, K-W-OVERRIDE-SAME-VALUE, K-W-UNUSED-MIXIN, K-W-ATOMIC-REPLACE (an inherited non-empty atomic list replaced without `=!`).

### 6.9 Content hashing and incrementality
- R25. `Hash(O) = H(canonical(Resolved(O)))`; references hashed by id so a change in B does not re-hash A for merely referencing B; an optional deep hash over the transitive reference closure serves asset pipelines.
- R26. Incremental: per-file parse → per-declaration content hash → early cutoff (unchanged declaration hash ⇒ nothing downstream re-resolves). Otherwise re-resolve that object and its inheritance descendants; re-validate referrers only if the object's type or component set changed. Maintain inverse indexes parent→children and id→referrers.

### 6.10 Worked example: 3-level chain with a mixin

Schema (abridged): type `Unit`; components `Stats { Hp: int[0..9999] required; Spd: int default 5 }`, `Loot { Table: keyed(Id) list of { Id; Weight: int } }`; `tags: set<string>`; `abilities: set<ref Ability>`.

```
// base.kedo
abstract Unit Creature {
  component Stats { Hp = 100 }                                  // Spd = schema default 5
  component Loot  { Table = [ {Id="gold", Weight=10}, {Id="hide", Weight=3} ] }
  tags = ["animal"]
}

// mixins.kedo
mixin Elite {
  component Stats { Hp = 250; Spd = 7 }
  component Loot  { Table += [ {Id="trophy", Weight=1} ]; Table -= ["hide"] }
  tags = ["elite"]                                              // set: adds
  abilities = [@Roar]
}

// wolves.kedo
Unit Wolf extends Creature {
  component Stats { Hp = 120 }
  abilities = [@Bite]
  tags = ["pack"]
}

Unit AlphaWolf extends Wolf with Elite {
  component Stats { Spd = 9 }
  component Loot  { Table[gold] { Weight = 25 } }
  remove component Vision                                       // K-E-REMOVE-MISSING
}
```

Resolution order: Creature → Wolf → AlphaWolf (Elite is a mixin declaration, not an object).

Wolf = defaults ⊕ Creature ⊕ Own(Wolf):
- Stats.Hp = 120 (own, shadows Creature 100); Stats.Spd = 5 (schema default through Creature).
- Loot.Table = [gold:10, hide:3] (inherited).
- tags = {animal, pack}; abilities = {@Bite}.
- Concrete ⇒ Stage B: Hp in range; @Bite exists and is an Ability.

AlphaWolf = defaults ⊕ Resolved(Wolf) ⊕ Elite ⊕ Own(AlphaWolf):
- Stats.Hp: Wolf 120 → Elite 250 → own (none) ⇒ 250; origin mixins.kedo Elite (layer mixin(Elite)); shadowed: wolves.kedo Wolf = 120, base.kedo Creature = 100.
- Stats.Spd: default 5 → Elite 7 → own 9 ⇒ 9; origin wolves.kedo AlphaWolf (own).
- Loot.Table (keyed Id): [gold:10, hide:3] → Elite: append trophy:1, remove hide ⇒ [gold:10, trophy:1] → own: gold.Weight = 25 ⇒ [gold:25, trophy:1]. Provenance: gold.Weight ← AlphaWolf own; gold.Id ← Creature; trophy ← Elite; hide ← removed by Elite.
- tags = {animal, pack} ∪ {elite}; canonical order for hashing: animal, elite, pack.
- abilities = {@Bite, @Roar}.
- `remove component Vision` ⇒ K-E-REMOVE-MISSING at wolves.kedo (AlphaWolf): "no component Vision in inherited layers (Creature, Wolf, Elite)". The object still resolves; it is marked invalid for export.
- `kedo explain AlphaWolf.Stats.Hp` → "250 from mixin Elite (mixins.kedo:3); shadowed: 120 Wolf (wolves.kedo:2), 100 Creature (base.kedo:2)".
- Incrementality: editing only wolves.kedo re-resolves Wolf and AlphaWolf; editing base.kedo re-resolves all three; changing a comment in base.kedo re-parses but the Creature declaration hash is unchanged ⇒ nothing re-resolves (backdating).

If late binding were allowed and Creature wrote `DisplayHp = Hp` as an expression, AlphaWolf would re-evaluate it; under R15 the schema instead declares `computed DisplayHp = Stats.Hp`, evaluated once per object after resolution: Wolf 120, AlphaWolf 250.

### 6.11 Top risks
1. Authors expect late binding (every config language they know has it) and will duplicate derived values. Mitigation: ship computed fields (R15) early; document "no self"; provide `kedo explain`.
2. Mixin diamonds and ordering surprises (R2/R6): silent last-wins among mixins. Mitigation: K-W-MIXIN-SHADOW on by default; consider promoting same-path scalar conflicts between two mixins to an error unless the own layer resolves them.
3. Keyed-list behaviour depends on schema annotations; an unannotated list is atomic and replaces silently (K8s default, same trap). Mitigation: K-W-ATOMIC-REPLACE.
4. Deletion across base revisions: a renamed key in a base turns every descendant's `- key` into K-E-REMOVE-MISSING. Mitigation: deduplicate identical errors by origin; resist adding a soft `remove?` in v1.
5. Hash instability from unordered iteration or from including provenance/file paths. Mitigation: a single canonicalisation function with golden-file tests; provenance excluded from the hash.
6. Provenance memory at 10k+ objects. Mitigation: compact (pathIndex → originId) arrays structurally shared with the parent, or replay-on-demand (resolution is deterministic and cheap for one object).
7. Skipping required-field checks on `abstract` bases surfaces one base error N times. Mitigation: group Stage-B errors by origin ("Hp out of range, defined at base.kedo:3, affects 37 objects").
