# Research H — Syntax survey for KEDO (Kaux Enriched Data Object)

Date: 2026-10-15. Target: a text DSL for game content, hand-written single-pass parser in C# (.NET 8, NativeAOT), compiling embedded hooks to a small integer-only IR.

Priorities (from the brief, in order): parsing performance > ease of use for humans and LLM agents (explicit delimiters, one way to do things, stable ids) > scalability > flexibility.

Evidence marks: **VERIFIED (url)** = read from the primary source during this session; **INFERRED** = from prior knowledge or secondary sources, not re-checked; **PARTIAL** = source read but it did not cover the specific claim.

Note on sources: several documentation sites returned 404 or truncated pages during this session (Paradox scripted-effects pages, Caves of Qud "Object_Blueprints", BG3 Osiris wiki, Godot pages moved under `engine_details/`). Where a primary page could not be read, the item is marked INFERRED and the nearest fetched page is cited.

---

## Part 1 — Game content languages

### 1.1 Paradox Clausewitz / Jomini script (CK3, EU4, Stellaris)

**Object declaration.** Everything is `key = value` or `key = { ... }`; blocks nest; indentation is irrelevant. Example: `primary_heir = { marry = root }`. Effects and triggers always take a value: `add_gold = 100`, `is_ai = yes`. VERIFIED (https://ck3.paradoxwikis.com/Scripting)

**Scopes / references.** `root`, `this`, `prev` (one level only), saved scopes `save_scope_as = my_son` then `scope:my_son`. Object references use typed prefixes: `set_culture = culture:english`, `capital_county = title:c_byzantion`, `marry = character:123456`, variables `var:test`. VERIFIED (same)

**Triggers vs effects.** Separate vocabularies; triggers use `AND/OR/NOT/NOR/NAND`, relational `< <= = != > >=`, and `?=` ("exists-then-compare"). Iterators: `any_` (trigger), `every_/ordered_/random_` (effect). VERIFIED (same)

**Scripted effects/triggers and `$PARAM$`.** Defined in `common/scripted_effects`; parameters are substituted textually with `$NAME$` and can replace parts of keys (`every_$WHO$ = { ... }`), called as `gift = { val = 100 }`. VERIFIED (same). Default values via `$PARAM|default$` and `inline_script` with parameters: INFERRED (Stellaris wiki page on scripted effects returned 404; the Effects page only says "An Effect runs in the scope in which it is called", https://stellaris.paradoxwikis.com/Effects).

**on_actions.** `on_death = { events = { death_management.0096 } }` — a block listing event ids without `key =` pairs; comments in each on_action file list the saved scopes available. VERIFIED (https://ck3.paradoxwikis.com/Scripting)

**Ids / keys.** Objects are keyed by their top-level key name; event ids are `namespace.number` (`death_management.0096`). File names are irrelevant to the id. VERIFIED (same)

**Override / merge across files.** Same path+filename in a mod replaces the whole vanilla file. A top-level declaration with the same key in a later file overrides the earlier one ("LIOS — Last In, Only Served"); load order is ASCIIbetical by filename (`01_defines.txt` overrides `00_defines.txt`); UI types are the exception (FIOS). "A single override can change an object but cannot remove it." In a playset the lower mod wins. VERIFIED (https://ck3.paradoxwikis.com/Modding)

**Expressions.** "Script has no inline math" — arithmetic goes through value blocks (`add`, `divide`, `subtract`). `while` capped at 1000 iterations, no `break`. VERIFIED (https://ck3.paradoxwikis.com/Scripting)

**Comments.** `#`. VERIFIED (same)

**Known mistakes / complaints (from the wiki's own caveats).** `$$` substitution is textual, so errors appear only at the use site; `prev` only goes back one level; variables on characters are lost at death; nested iterators lag; no arithmetic expressions; whole-object replacement means two mods touching the same key conflict silently; order depends on filename sorting. VERIFIED (same pages). Lack of up-to-date documentation is a recurring forum complaint. INFERRED (search results only: https://admin-forum.paradoxplaza.com/forum/threads/api-for-accessing-game-engine.1732691)

**Lesson for KEDO.** The `key = { }` shape is the simplest thing that parses in one pass and is familiar to a large modding community; typed prefixes (`culture:english`) are a cheap, unambiguous reference syntax. Avoid: textual parameter substitution, silent last-wins override, filename-based ordering, no expressions.

### 1.2 Ink (inkle)

**Structure.** Knots `=== name ===`, stitches `= name` (addressed `knot.stitch`), gathers `-`, labels `(name)`, diverts `-> target`, `-> END` / `-> DONE`, glue `<>`. VERIFIED (https://github.com/inkle/ink/blob/master/Documentation/WritingWithInk.md)

**Choices.** `*` once-only, `+` sticky, `[...]` for choice-only text, nested via repeated markers, conditions `* { not visit_paris } [Go] -> visit_paris`, fallback choice `* -> out`. VERIFIED (same)

**Variables / logic.** `VAR x = 1` (type inferred), logic lines prefixed `~`, `CONST PI = 3.14`, `LIST kettleState = cold, (boiling), recently_boiled` used both as state machines and flag sets (`+=`, `-=`, `?`, `!?`, `LIST_ALL`, `LIST_COUNT`...). Conditionals `{ x > 0: ... - else: ... }`, sequences `{a|b}`, `{&..}`, `{!..}`, `{~..}`. Knot names double as read counts. VERIFIED (same)

**Functions / tunnels / threads.** `=== function lerp(a, b, k) ===` with `~ return`, `ref` parameters; tunnels `-> knot ->` returning with `->->`; threads `<- knot` fork and collect choices. Rationale stated: tunnels avoid passing a "return-to" parameter and use a call stack; threads suit many independent parts. VERIFIED (same)

**Imports.** `INCLUDE file.ink` at top level only. VERIFIED (same)

**Comments / metadata.** `//`, `/* */`, `TODO:` lines become compiler notes, `# tag` attaches metadata to a line. VERIFIED (same)

**Ids.** Knot/stitch names are the ids; no explicit line ids in the core language. VERIFIED (same). Stated design goals: flow "clean and logically ordered, so branching dialogue can be tested by eye", "designed with redrafting in mind", diverts are flat with no call stack. VERIFIED (same)

**Known pain points.** Sigil-heavy syntax (`*`, `+`, `-`, `~`, `->`, `<-`, `<>`, `{}`) and whitespace/indentation conventions for nesting make it hard for tools and LLMs to reason about structure; INFERRED (community experience, not re-verified).

**Lesson for KEDO.** Ink's `wait`-like constructs (tunnels, threads) show the value of a call-stack model for suspended flow; `LIST` as a combined enum/flag-set is a compact idea. Its sigil density is the opposite of "explicit delimiters".

### 1.3 Yarn Spinner 3

**Nodes.** Header lines `title: Start` (required; letters, digits, underscores, no periods), other `key: value` headers, body between `---` and `===`. Lines `Mae: Well, this is great.`. VERIFIED (https://docs.yarnspinner.dev/write-yarn-scripts/scripting-fundamentals/lines-nodes-and-options)

**Variables.** `<<declare $gold = 42>>`, `<<set $x to ...>>` or `<<set $x = ...>>`, types number/string/bool fixed per variable, `{$x}` interpolation. Rationale: declare to fix type, initial value, and document purpose; undeclared variables are implicitly declared with inferred type. VERIFIED (https://docs.yarnspinner.dev/write-yarn-scripts/scripting-fundamentals/logic-and-variables)

**Smart variables.** `<<declare $is_powerful = $strength > 50 && $magic_ability >= 20>>` — recomputed on every read, chainable. VERIFIED (https://docs.yarnspinner.dev/write-yarn-scripts/scripting-fundamentals/smart-variables)

**Enums.** `<<enum Food>> <<case Apple>> ... <<endenum>>`, used as `Food.Apple` or `.Pear` when inferable. VERIFIED (https://docs.yarnspinner.dev/write-yarn-scripts/scripting-fundamentals/enums)

**once.** Block `<<once>>...<<endonce>>` with optional `<<else>>` and `<<once if cond>>`; also as a suffix on a line or option. VERIFIED (https://docs.yarnspinner.dev/write-yarn-scripts/scripting-fundamentals/once)

**Line groups / node groups.** `=> Guard: Hail! <<if $x>>` lines; the engine picks one. Node groups: several nodes with the same title, each with `when:` headers (`when: once`, `when: always`, `when: $has_sword`); run via `jump` or `detour`. VERIFIED (https://docs.yarnspinner.dev/write-yarn-scripts/scripting-fundamentals/line-groups, https://docs.yarnspinner.dev/write-yarn-scripts/advanced-scripting/node-groups)

**Ids.** `#line:4c49c5` tags uniquely identify a line for localisation; the compiler adds them if missing. VERIFIED (https://docs.yarnspinner.dev/write-yarn-scripts/advanced-scripting/tags-metadata)

**Known pain points.** The command syntax `<<...>>` is distinctive but verbose; line ids are generated hashes written back into source (tool-managed, not author-managed). INFERRED.

**Lesson for KEDO.** Declared, typed variables with a stated rationale; `once` and `when:` as first-class vocabulary for narrative gating; stable per-line ids are important enough that the tool injects them.

### 1.4 Cataclysm: DDA (JSON)

**Inheritance.** `"copy-from": "parent_id"` copies an object of the same `type`; chains allowed. `"abstract": "id"` declares a template that is discarded after load. Modifiers: `"relative": { field: delta }`, `"proportional": { field: factor }`, `"extend": { list_field: [..] }`, `"delete": { list_field: [..] }` (deleting an absent entry is not an error). `extend`/`delete` apply only to container fields; `relative`/`proportional` need per-type C++ support; some lists (e.g. `monstergroup`, `vitamins`) extend by default and need `"override": true` to replace. VERIFIED (https://github.com/CleverRaven/Cataclysm-DDA/blob/master/doc/JSON/JSON_INHERITANCE.md)

**Guidelines in the doc.** "Some restraint should be used"; deep `copy-from` chains reduce readability; prefer one or two levels of abstracts; cosmetic variants should use "variants" instead. VERIFIED (same)

**Mod override/removal.** No generic "override by id" documented; removal is via dedicated blacklist objects (`MONSTER_BLACKLIST`, `SCENARIO_BLACKLIST`), `monster_adjustment`, or copying a definition with `occurrences: [0,0]`. Load order via `dependencies` in `modinfo.json`. VERIFIED (https://github.com/CleverRaven/Cataclysm-DDA/blob/master/doc/MODDING.md)

**Ids.** Explicit `"id"` per object, unique per `type`; file names irrelevant. VERIFIED (JSON_INHERITANCE examples)

**Lesson for KEDO.** The four-verb vocabulary `extend / delete / relative / proportional` is the clearest explicit list-merge design in the game survey; the mistake is that it is inconsistent per type ("There is no universal default").

### 1.5 RimWorld (XML + PatchOperations)

**Defs.** `<Defs>` root; `<ThingDef Name="Base" Abstract="True">`; children use `ParentName="Base"`; lists are `<li>` items and `<li Class="CompOne">`. Lists **append** to the parent's list by default, other tags overwrite; `Inherit="False"` on a node replaces the inherited list entirely. Keywords are case-sensitive (`Abstract` vs `abstract`). VERIFIED (https://rimworldwiki.com/wiki/Modding_Tutorials/XML_file_structure)

**Patches.** XPath-targeted operations: `PatchOperationAdd`, `Insert` (sibling), `Remove`, `Replace`, `AttributeAdd/Set/Remove`, `SetName`, `AddModExtension`, `Sequence`, `Conditional`, `FindMod` (by mod *name*), `Test` (obsolete). Patches run before Def inheritance, so you cannot target tags that only exist in a parent. VERIFIED (https://rimworldwiki.com/wiki/Modding_Tutorials/PatchOperations)

**Ids.** `defName` is the stable id; `Name` is only for inheritance targets. INFERRED (the fetched page does not mention `defName`; the PatchOperations page uses `Defs/ThingDef[defName="Example"]`, VERIFIED).

**Known mistakes.** Append-by-default for lists surprises modders; Insert vs Add confusion; redefining core abstracts silently overrides vanilla since named parents are looked up across all mods (VERIFIED on the wiki page: "common cause of mod compatibility problems"); XPath is case-sensitive; `Sequence` masks errors. VERIFIED (PatchOperations page)

### 1.6 Caves of Qud (ObjectBlueprints.xml)

**Objects.** `<object Name="Ctesiphus" Load="Merge"><part Name="Render" ColorString="&amp;B"/></object>`. Children: `<part Name=... attr=.../>` (components keyed by name), `<tag Name= Value=>`, `<stat>`, `<property>`, `<intproperty>`, `<xtag>`, `<stag>`, `<inventoryobject>`, `<mutation>`, `<skill>`, `<builder>`, `<mixin Name= Include= Exclude= Priority= Load=>`. VERIFIED (https://wiki.cavesofqud.com/wiki/Modding:Objects)

**Merge / delete.** `Load="Merge"` merges into an existing blueprint; without it the definition replaces the object. `<removepart Name="Corpse"/>` removes a part; "most tags can be reverted with a `remove*` tag". Tag deletion via `Value="*delete"` is reported broken with `Load="Merge"` or inherited tags. Don't combine `Inherits` with `Load="Merge"`. VERIFIED (same; https://wiki.cavesofqud.com/wiki/Modding:Compatibility via search)

**Inheritance.** `Inherits="Parent"` attribute on `<object>`: INFERRED (not on the fetched page; standard in the shipped XML).

**Lesson for KEDO.** Components keyed by part name with per-key merge plus an explicit `removepart` is almost exactly KEDO's component model; the `*delete` sentinel value is the anti-pattern (magic string, interacts badly with merge).

### 1.7 Dwarf Fortress raws

**Syntax.** `[TOKEN:ARG:ARG]`; everything outside brackets is a comment; first line must be the file name (without `.txt`); `[OBJECT:CREATURE]` then `[CREATURE:DOG]`; one OBJECT type per file; parse order comes from the first line, which matters for `COPY_TAGS_FROM`. Duplicate raws "can cause strange, difficult-to-diagnose problems". VERIFIED (https://dwarffortresswiki.org/index.php/Raw_file)

**Known mistakes.** Filename-in-file coupling, positional args with no names, flat token stream where structure (CASTE sections, SELECT_/CUT_ edits) is implied by order. VERIFIED (header rules) / INFERRED (SELECT_/CUT_ semantics).

### 1.8 Factorio prototypes (Lua tables)

**Model.** `data.raw["prototype-type"]["internal-name"]`; prototypes are Lua tables with `type` and `name`; mods mutate directly: `data.raw["capsule"]["raw-fish"].stack_size = 200`. New types cannot be added by mods. VERIFIED (https://wiki.factorio.com/Data.raw)

**Ordering.** Three rounds per stage (`data.lua`, `data-updates.lua`, `data-final-fixes.lua`), mods sorted by dependency depth then name; changes are tracked per mod; unknown properties are ignored, missing ones default or error. VERIFIED (https://lua-api.factorio.com/latest/auxiliary/data-lifecycle.html)

**Known mistakes.** Mutation of a shared table means conflicts depend on stage/order; `table.deepcopy` idiom needed to derive variants (INFERRED); unknown properties silently ignored → typos pass (VERIFIED).

### 1.9 Godot `.tscn` / `.tres`

**Format.** Sections `[gd_scene format=3 uid="uid://..."]`, `[ext_resource type="Texture2D" uid="uid://..." path="res://x.tres" id="2_eorut"]`, `[sub_resource type="CapsuleShape3D" id="CapsuleShape3D_fdxgg"]`, `[node name="Arm" type="Node3D" parent="."]`, `[connection ...]`; properties as `key = value` lines; typed literals `Transform3D(...)`, `Color(...)`, `SubResource("id")`, `ExtResource("id")`, `NodePath("..")`, `PackedStringArray(...)`; defaults are omitted; `;` comments; whitespace insignificant. Referenced sub-resources must appear before use. VERIFIED (https://docs.godotengine.org/en/stable/engine_details/file_formats/tscn.html)

**Ids.** Three layers: file-local `id` for references inside the file, global `uid://` for assets (survives moves), `path` as fallback. VERIFIED (same)

**Lesson for KEDO.** Typed constructor literals `Type(args)` are easy to lex and self-describing; asset references by stable uid + path fallback is the right model for `asset` references.

### 1.10 Unreal `.ini` and DataTables

**.ini.** `[Section]`, `Key=Value`; array operators: `!` clear, `+` add if absent, `-` remove exact match, `.` append even if duplicate; plain `Key=Value` on an array replaces it; struct literals `(A=1,B=2)`; layered override order Base → Default → platform → User. VERIFIED (https://dev.epicgames.com/documentation/en-us/unreal-engine/configuration-files-in-unreal-engine)

**DataTable.** CSV whose first column is `Name` (row id), other columns map to struct fields; referenced by `FDataTableRowHandle { DataTable, RowName }`. VERIFIED (https://dev.epicgames.com/documentation/en-us/unreal-engine/data-driven-gameplay-elements-in-unreal-engine)

**Lesson.** Explicit per-line list operators (`+`, `-`, `!`) are the simplest explicit list-merge design in the survey; the pitfall is the silent "plain `=` replaces the whole array".

### 1.11 Naughty Dog DC (Uncharted/TLOU)

DC is a Racket (then MzScheme)-hosted "data scripting system" with many DSLs, used for gameplay scripting, animation, effects and sound (RacketCon 2013 abstract, Dan Liebgold). VERIFIED abstract only (https://con.racket-lang.org/2013/). The GDC 2009 state-scripting talk (Jason Gregory) describes states with `on (update)`/`on (event)` blocks, `wait` primitives and hot reload; the PDF was too large to fetch here (https://www.gameenginebook.com/resources/gdc09-statescripting-uncharted2.pdf) — INFERRED.

**Lesson.** S-expressions give a trivially single-pass parser and macros for free, but humans and LLMs make paren mistakes; the state/`on`/`wait` model is the reference for KEDO's triggers.

### 1.12 Baldur's Gate 3 / Divinity: `.lsx` + Osiris

`.lsx` is XML: `<region id>`/`<node id>`/`<attribute id type value>` with UUID handles. Osiris is a rule language: goals with INIT/KB/EXIT sections, rules `IF event AND condition THEN actions`, `PROC`, `QRY`, `DB_` fact databases, `_Var` variables. INFERRED (primary wiki pages returned 404 or empty; a Larian dev post confirms only the event/call model: https://gamebanshee.com/3fya; community wiki page exists: https://wiki.bg3.community/Tutorials/Osiris/Understanding-Osiris-Rules).

### 1.13 ZIL

Not useful for KEDO beyond "s-expression data + code in one file"; skipped. INFERRED.

---

## Part 2 — Data/config languages with principled merging or inheritance

### 2.1 KDL 2.0

**Syntax.** `node arg key=val { child }`; args are positional, props are `key=value` (rightmost wins on duplicates); children in `{}`; nodes end at newline, `;`, `}` or EOF; `\` line continuation; type annotations `(u8)123` and `(role)node`; strings: identifier (bare), quoted with escapes, multi-line `"""` (dedented to closing line), raw `#"..."#`; keywords `#true #false #null #inf #-inf #nan`; comments `//`, nestable `/* */`, slashdash `/-` to comment out a node, an entry, or a children block; optional `/- kdl-version 2` marker. VERIFIED (https://github.com/kdl-org/kdl/blob/main/draft-marchan-kdl2.md)

**Changes 1.0 → 2.0.** `true/false/null` became `#true/#false/#null`; bare identifiers allowed as string values; raw strings `r"..."` → `#"..."#`; slashdash positions clarified; multi-line strings must use `"""`. Rationale largely unstated in the changelog. VERIFIED (https://github.com/kdl-org/kdl/blob/main/CHANGELOG.md)

**No inheritance, no expressions, no references** — KDL is a document language; semantics are application-defined. VERIFIED (spec scope)

**Known mistakes.** The 1.0 bare `true`/`null` vs identifier ambiguity forced the `#` prefix in 2.0; the positional-args vs props mix means a schema is required to know what a node means. VERIFIED (changelog) / INFERRED (usability).

### 2.2 HCL

**Syntax.** Attributes `name = expr` (unique per body); blocks `type "label" "label" { ... }`; one-line blocks; comments `#`, `//`, `/* */`; expressions with operators, `cond ? a : b`, `for` expressions, splats `x[*].id`, function calls `max(1,2)`, templates `"${x}"` and `%{ if }` directives, heredocs `<<-EOT`; no reserved keywords (`for`, `if` are contextual). Stated rationale: "optimized for human authoring and maintenance, as opposed to machine generation"; errors in unselected conditional branches are not raised. VERIFIED (https://github.com/hashicorp/hcl/blob/main/hclsyntax/spec.md)

**Merge/inheritance.** None in the language; Terraform layers (`count`, `for_each`, `dynamic` blocks) are host semantics. Complaints: `count` tracks by index so removing a middle element recreates later resources; `dynamic` blocks cannot generate meta-arguments and hurt readability. INFERRED (secondary: https://scalr.com/learning-center/the-developers-guide-to-hcl-part-3-common-challenges-solutions/, https://developer.hashicorp.com/terraform/language/expressions/dynamic-blocks)

**Lesson.** The `type "label" { ... }` block header plus `=` attributes is a good template for typed object declarations; newline-as-separator with contextual keywords is parseable in one pass but needs care (HCL spec notes newlines are significant "in some contexts").

### 2.3 CUE

**Syntax/semantics.** Types and values form one lattice; `&` unification is order-independent; definitions `#Name` are schemas, closed by default (`...` opens them), excluded from output; disjunctions `string | int | *1` with `*` default; optional `?`, required `!`, hidden `_x` fields. "An instance may never violate the constraints of its parent"; defaults are "the kind [of inheritance] that allows instances to override any value of its parent". VERIFIED (https://cuelang.org/docs/tour/types/definitions/, https://cuelang.org/docs/tour/types/defaults/, https://cuelang.org/docs/concept/the-logic-of-cue/)

**Deletion.** Impossible by design — values only get more specific. VERIFIED (logic page)

**Known mistakes.** Closedness "very much complicates the evaluation process" and caused memory/CPU pressure in large configs; list operators confused users (open vs closed results); error messages were weak for a long time. VERIFIED issue text via search (https://github.com/cue-lang/cue/issues/2853; release notes v0.3.0-alpha6).

**Lesson.** Order-independent unification is elegant but incompatible with "override + delete" authoring that game content needs; `#Def` + closedness is a clean way to say "unknown field is an error".

### 2.4 Jsonnet

**Syntax/semantics.** JSON superset; `local`, functions, `self`, `super`, `$`; object `+` layers right over left; `::` hidden fields, `:::` force visible; `+:` merges nested objects/concats arrays with the super value (`{a+: b}` ≡ `{a: if "a" in super then super.a + b else b}`); conditional fields `[if c then "k"]: v` drop when null; asserts; lazy everywhere; `import`/`importstr`. VERIFIED (https://jsonnet.org/ref/language.html)

**Deletion.** By omitting a field via a computed null name or setting hidden `::`; no explicit delete. INFERRED (no `delete` in the reference).

**Rationale.** JSON compatibility "constrained us substantially, but we believe it was the correct one"; prototype-based OO with late binding and mixins; laziness for consistency with late binding; Turing-complete accepted; dynamic typing for simplicity; fields visible by default complicate compatibility. VERIFIED (https://jsonnet.org/articles/design.html)

**Known complaints.** Object juxtaposition `{a:1} {b:1}` valid but inconsistent with variables; comprehension scoping oddities; "inconsistent with late binding"; laziness makes small inputs compile for minutes. VERIFIED (https://github.com/google/jsonnet/issues/1110, https://github.com/google/jsonnet/issues/847, https://www.databricks.com/blog/2018/10/12/writing-a-faster-jsonnet-compiler.html)

### 2.5 Nickel

**Merge.** `&` is symmetric/commutative; records merge recursively; equal primitives merge, different ones fail ("non mergeable terms"); arrays merge only if equal (no element-wise merge); priorities via metadata: `| default` (lowest), `| priority N`, `| force` (highest); `| doc`, `| optional`, `| not_exported`; contracts accumulate through merges and are checked lazily on the final value. VERIFIED (https://nickel-lang.org/user-manual/merging/)

**Deletion.** Not expressible via merge. VERIFIED (by omission in the merging rules)

**Lesson.** Priorities as explicit metadata instead of positional overrides is a clean idea; arrays-don't-merge is honest but pushes list policy to the author.

### 2.6 Dhall

**Syntax.** Records `{ a = 1 }`, types `{ a : Natural }`, `//` right-biased shallow merge, `/\` recursive merge (collisions on non-records are type errors), `T::{ ... }` record completion (`T.default // r : T.Type`), `with a.b.c = 2` nested update, unions `< A : T | B >` consumed with `merge`, `let ... in`, `\(x : T) -> ...`, imports by path/URL/env with `sha256:` semantic hashes and `?` fallback, empty list needs annotation. VERIFIED (https://docs.dhall-lang.org/tutorials/Language-Tour.html)

**Guarantees.** Total (no recursion), only three effects via imports, semantic integrity hashes over the normal form, no computed/conditional imports. VERIFIED (https://docs.dhall-lang.org/discussions/Safety-guarantees.html)

**Known complaints.** No text comparison, no recursion, type system "too strong and too weak" (no inference). INFERRED (https://discourse.haskell.org/t/config-languages-and-dhall/13948 via search)

### 2.7 Pkl (Apple)

**Syntax.** `obj { ... }` amends a module property; `(parent) { ... }` amends an expression; `new Class { ... }` creates; late binding (overriding an input updates dependents); `class Bird { name: String }` with `abstract`/`open`, single `extends`; modifiers `hidden`, `local`, `fixed`, `const`; `Listing` amended by index `[0] { ... }`, replaced `[1] = new {...}`, appended with `new {...}`, `default { ... }` applies to every element; `Mapping` keyed `["k"] { ... }`; `amends "base.pkl"` (template, cannot add properties → catches typos) vs `extends "base.pkl"` (open module, can add); `import "x.pkl" as y`, glob imports; annotations `@Deprecated { message = "..." }`; `typealias`; `when`/`for` generators; spread `...obj` / `...?obj`. VERIFIED (https://pkl-lang.org/main/current/language-reference/index.html)

**Deletion.** Not documented; `delete` is a reserved keyword "currently without meaning"; nulling is not removal. VERIFIED (same)

**Known complaints.** Fully custom syntax as adoption barrier; unclear integration story (secondary). INFERRED (https://dev.to/dpark/pkl-and-the-goldilocks-problem-of-configuration-languages-4a9d)

**Lesson.** The `amends` vs `extends` distinction (template-fill vs schema-extend) and "amend cannot introduce new properties" are strong typo defenses; Listing index-amend `[0] { }` is the most explicit list-element override syntax surveyed.

### 2.8 Starlark

Python-subset for configuration; "no user-defined types, no inheritance, no reflection, no exceptions"; execution finite (no recursion, bounded loops); deterministic and hermetic; module values frozen after load for parallel safety; `load()` only at top level. VERIFIED (https://raw.githubusercontent.com/bazelbuild/starlark/master/spec.md)

**Lesson.** Freezing and finiteness are the right properties for an embedded content language; Python surface syntax (significant whitespace) is the wrong fit for the "explicit delimiters" priority.

### 2.9 TOML

`key = value` one per line; dotted keys; `[a.b]` tables; `[[x]]` arrays of tables; inline tables single-line, no trailing comma; four string forms; `#` comments; a table may be defined once; cannot append to a statically defined array. VERIFIED (https://toml.io/en/v1.0.0). Known pain: deep nesting and arrays of tables are verbose; no inheritance. VERIFIED (rules) / INFERRED (sentiment).

### 2.10 YAML

The "Norway problem": in YAML 1.1 `no`/`NO` parse as boolean false (22 boolean spellings); 1.2 restricted to `true`/`false` but most parsers still behave like 1.1; fix by quoting or schemas. INFERRED (https://ljmc.bearblog.dev/yaml-booleans-in-cloudformation-templates/, https://env.dev/cheatsheets/yaml-cheat-sheet). Significant whitespace, anchors/merge keys `<<:` as the only inheritance, implicit typing — all well-known complaints. INFERRED.

### 2.11 Unity YAML

Each object is its own YAML document `--- !u!1 &6` (class id, file-local id); cross references `{fileID: 6}` (and `guid` for cross-asset, INFERRED); `m_` property prefix; floats may be hex-encoded. VERIFIED (https://docs.unity3d.com/Manual/FormatDescription.html). UnityYAMLMerge keys arrays on `fileID`/`guid`/`propertyPath` heuristics and duplicates elements on two-sided edits (issue marked "As Designed"). INFERRED (https://issuetracker.unity.com/issues/9294/...)

### 2.12 Lua-as-data

See Factorio (1.8). Executable data: trivially extensible, but no static ids, order-dependent mutation, and unknown fields pass silently. VERIFIED (Factorio pages) / INFERRED (general).

### 2.13 EDN

`{}` maps, `[]` vectors, `()` lists, `#{}` sets, `:kw` keywords, symbols with `ns/name`, tagged elements `#myapp/Person {...}`, `#inst`, `#uuid`, discard `#_`, `;` comments; rationale: extensibility through tags without schemas. VERIFIED (https://github.com/edn-format/edn)

### 2.14 Protobuf text format

`field: value` (colon required for scalars, optional for messages), messages in `{}` or `<>`, repeated fields by repetition or `[a, b]`, enums as identifiers, `#` comments only, `[type.url] { }` for Any, `[ext.name]: v` for extensions; preferred extension `.txtpb`. VERIFIED (https://protobuf.dev/reference/protobuf/textformat-spec/)

---

## Part 3 — Cross-cutting findings (what matters for KEDO)

| Concern | Best practice seen | Worst practice seen |
|---|---|---|
| Object declaration | HCL `type "label" { k = v }`; Clausewitz `key = { }`; Godot `[node name= type=]` | DF raws (positional tokens, filename coupling); Ink/YAML sigils + whitespace |
| Stable ids | CDDA `id` per type; Clausewitz typed prefixes `culture:x`; Godot `uid://` + path fallback; Yarn `#line:` injected | Paradox filename-sorted override order; Factorio silent same-name overwrite |
| Inheritance | CDDA `copy-from` + `abstract`; RimWorld `ParentName` + `Abstract`; Pkl `amends` (no new props) vs `extends` | Jsonnet late-binding + laziness surprises; YAML merge keys |
| List merge | Unreal `+ - . !`; CDDA `extend/delete`; Pkl `[i] { }` amend | RimWorld append-by-default w/ `Inherit="False"` escape; Nickel arrays never merge |
| Deletion | Qud `<removepart Name=.../>`; CDDA `delete` | Qud `Value="*delete"` magic string; CUE/Nickel/Pkl: impossible |
| Unknown fields | Pkl typed objects reject; CUE closed defs reject | Factorio "unrecognized properties are ignored" |
| Expressions | HCL (rich but complex); Yarn smart variables (declared, typed) | Clausewitz "no inline math"; `$PARAM$` textual substitution |
| Hooks/waits | DC `on`/`wait`; Ink tunnels/threads; Yarn `once`/`when:` | Osiris rule engine (powerful but opaque) |
| Comments/metadata | KDL `//`, `/* */`, `/-`; Yarn `#tag`; Pkl `@Annotation {}` | — |
| Imports | Dhall hashed imports; Ink `INCLUDE` top-level only; Starlark `load()` top-level only | Jsonnet lazy `import` anywhere |

Parser-cost observations (INFERRED from the grammars read): Clausewitz, KDL, TOML, Protobuf text and Godot TSCN are all LL(1)-ish with a tiny token set and no operator precedence outside leaves; HCL, Jsonnet, CUE, Nickel, Pkl, Dhall need full expression parsers with precedence and (for the last five) an evaluator with late binding or unification — the opposite of "single-pass, integer-only IR".

---

## Recommendations for KEDO v0.1 syntax

Common example used for all candidates:

- abstract actor `sentinel_machine` with components `Health`, `Patrol`, `Sense`, tags;
- concrete `depot_sentinel` extending it, overriding `Health.max`, appending a tag, deleting `Patrol`, adding a `Sprite` component with an asset reference;
- trigger `on_arrive` that waits 30 minutes then applies an effect.

### Candidate A — "Typed block" (Clausewitz/HCL shape, keyword-led)

```kedo
// actors/sentinel.kedo
import "core/effects.kedo"

abstract actor sentinel_machine {
    name = "Sentinel machine"
    tags = [machine, hostile]

    component Health { max = 40  regen = 0 }
    component Patrol { radius = 6  interval = 10min }
    component Sense  { range = 8 }
}

actor depot_sentinel extends sentinel_machine {
    name = "Depot sentinel"
    tags += [depot]

    component Health { max = 60 }              // merged by key: only max changes
    delete component Patrol                    // explicit deletion
    component Sprite { sheet = asset:sentinel_sheet }

    on arrive(ctx) {
        wait 30min
        apply effect:alarm_raised to ctx.place
    }
}
```

Rules: declarations are `keyword id [extends id] { body }`; body entries are `key = value`, `key += list`, `key -= list`, `component Type { }`, `delete component Type`, `on event(ctx) { statements }`; values are literals, `[list]`, `{ block }`, typed refs `type:id`, or a **parenthesised** expression `(a + b * 2)`; hook statements are keyword-led (`wait`, `apply`, `set`, `if`, `emit`) and end at newline or `;`.

Pros: one token of lookahead everywhere (every construct starts with a keyword or `ident =`); `=` and `{}` only; deletion and list ops are explicit verbs/operators, so an LLM cannot confuse "set to empty" with "remove"; ids are declared in the header, type-namespaced, independent of file names; typed refs `effect:alarm_raised` are grep-able and resolvable without evaluation. Cons: slightly more keywords to learn (`component`, `delete`, `on`); parenthesised-expression rule is a small tax on authors.

### Candidate B — "Uniform node" (KDL shape)

```kedo
import "core/effects.kedo"

actor "sentinel_machine" abstract=#true {
    name "Sentinel machine"
    tags "machine" "hostile"
    component "Health" max=40 regen=0
    component "Patrol" radius=6 interval=(min)10
    component "Sense" range=8
}

actor "depot_sentinel" extends="sentinel_machine" {
    name "Depot sentinel"
    tags "depot" mode="append"
    component "Health" max=60
    component "Patrol" /- delete=#true     // or: delete "component" "Patrol"
    component "Sprite" sheet=(asset)"sentinel_sheet"
    on "arrive" {
        wait (min)30
        apply (effect)"alarm_raised" to="ctx.place"
    }
}
```

Pros: a single node grammar (`name args props { children }`) — the parser is tiny and already specified by KDL 2.0; type annotations `(min)30` give units for free. Cons: no native expressions (would have to be strings or a second grammar); positional args vs props is a known ambiguity for readers and LLMs; deletion and list-merge policy become conventions (`mode="append"`, `delete=#true`) rather than syntax, which violates "one way to do things"; quoted ids everywhere are noisier; references are strings until a later pass.

### Candidate C — "Amend expression" (Pkl/Jsonnet shape)

```kedo
import "core/effects.kedo" as fx

sentinel_machine = abstract actor {
    name = "Sentinel machine"
    tags = [machine, hostile]
    components = {
        Health = { max = 40; regen = 0 }
        Patrol = { radius = 6; interval = 10min }
        Sense  = { range = 8 }
    }
}

depot_sentinel = (sentinel_machine) {
    name = "Depot sentinel"
    tags = super.tags + [depot]
    components = (super.components) {
        Health = (super.Health) { max = 60 }
        Patrol = null                         // deletion by nulling
        Sprite = { sheet = asset("sentinel_sheet") }
    }
    on_arrive = fn(ctx) { wait(30min); fx.alarm_raised(ctx.place) }
}
```

Pros: maximally composable; everything is an expression, so mixins and templates fall out. Cons: requires an evaluator with `super`/late binding (Jsonnet's documented "inconsistent late binding" and laziness pitfalls; Pkl's `amends`/`extends` subtleties); `null`-as-delete is the exact ambiguity Pkl refused ("nulling is not removal"); ids become variable names, so there is no type namespace and no declared-id guarantee; parsing needs a precedence-climbing expression parser everywhere; worst fit for "single-pass + integer-only IR".

### Recommendation: Candidate A, with these fixed decisions

1. **Declarations are keyword-led and ids are declared in the header**: `actor depot_sentinel`, `abstract actor sentinel_machine`, `place depot_yard`, `effect alarm_raised`, `campaign ...`. Ids are unique per type across all loaded files; file names carry no meaning; a duplicate id is an error in v0.1 (no silent last-wins; add an explicit `override actor x { }` form later if mods need it — Paradox LIOS and Factorio mutation are the cautionary tales).
2. **One inheritance mechanism**: single `extends parent_id`, value-level copy then apply body (CDDA `copy-from` / RimWorld `ParentName`); `abstract` objects are discarded after load (CDDA/RimWorld/Pkl). Mixins: `include mixin_id` lines applied in order before the body (Qud `<mixin>` with explicit priority) — defer to v0.2 if not needed now.
3. **Scalars override; lists replace by default; explicit `+=` / `-=` for append/remove** (Unreal `+`/`-`, CDDA `extend`/`delete`). Never append-by-default (RimWorld lesson). No `relative`/`proportional` in v0.1.
4. **Components are a map keyed by type**: `component Type { ... }` merges field-wise into the inherited component of the same type (Qud part semantics); `delete component Type` removes it. No sentinel values (`null`, `"*delete"`) for deletion.
5. **References are typed prefixes**: `actor:x`, `effect:y`, `place:z`, `asset:sentinel_sheet` (Clausewitz prefixes). Assets are referenced by stable id declared in an asset manifest (`asset sentinel_sheet { path = "sprites/sentinel.png" }`), mirroring Godot uid + path; a raw path string is not a reference.
6. **Imports at top of file only** (`import "path"`), resolved before parsing bodies; no computed or conditional imports (Dhall/Starlark/Ink rule).
7. **Expressions**: literals, refs, `[lists]`, `{blocks}`, duration literals `30min`/`2h`/`3d` lexed as one token and lowered to integer ticks, and compound expressions only inside parentheses. This keeps `key = value` self-delimiting without making newlines significant in declaration bodies, and keeps the IR integer-only (durations, counts, rolls).
8. **Hooks**: `on event(ctx) { ... }` where the event name fixes the context type; statements are keyword-led (`wait 30min`, `wait decision ...`, `wait roll ...`, `apply`, `set`, `if cond { } else { }`, `emit`); statement terminator is newline or `;` (KDL rule), the only place newlines matter.
9. **Comments and metadata**: `//`, `/* */`, `///` doc comment attached to the next declaration; `@name(args)` annotations before a declaration for tooling (deprecated, editor hints). No slashdash in v0.1 (one way to disable: comment it out).
10. **Unknown keys are errors** when a schema exists for the type (Pkl typed objects, CUE closed defs); Factorio's silent ignore is the anti-pattern.

Why A over B: B's grammar is smaller, but it pushes semantics (merge mode, deletion, references, expressions) into conventions and strings, which is where modders and LLM agents make mistakes; A spends a handful of keywords to make every semantic operation visible to the parser in one pass, which is exactly the priority order given (parse speed, then explicitness). Why A over C: C needs an evaluator with late binding to even know what an object contains; it is the most flexible and the least predictable, and flexibility is the lowest priority.
