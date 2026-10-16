# Research D — Data-architecture patterns for Game Data Objects (GDO)

Date: 2026-10-15. Target: Godot 4.6.2 .NET / C# 12 / .NET 8, Godot-free `src/domain`, deterministic sim, saves must survive content updates, iOS NativeAOT planned.

Legend: **VERIFIED** = read in the cited primary source during this research. **INFERRED** = reasoned from verified facts, from secondary/community sources, or from prior knowledge not re-checked today. Where a secondary source (forum, blog) is the only evidence it is marked **COMMUNITY**.

---

## 1. Prototype inheritance and composition in shipped data-driven games

### 1.1 RimWorld — XML Defs

- **Identity.** Each Def has a `<defName>`; other Defs reference it by that string. Abstract/parent defs are addressed by the XML attribute `Name="..."`; children use `ParentName="..."`. `Abstract="True"` defs are never loaded into the game and exist only to be inherited. VERIFIED (https://rimworldwiki.com/wiki/Modding_Tutorials/Def_classes, https://rimworldwiki.com/wiki/Modding_Tutorials/XML_file_structure).
- **Inheritance rules.** Single parent, chains allowed (no documented depth limit). Scalars in the child overwrite; **lists append to the parent's list**; `Inherit="False"` on a node opts out of inheriting that node (used mostly on lists). VERIFIED (XML_file_structure page, PatchOperations page). The `Class="..."` attribute is NOT inherited: every child of a custom-class def must restate it. VERIFIED (Def_classes).
- **Collection merge is by position/append, not by id.** This is the documented source of the "comps" pitfall: `Inherit="False"` on `<comps>` wipes all inherited comps; the wiki's workaround is to create a new parent that omits the comp. VERIFIED (search summary of https://rimworldwiki.com/wiki/Modding_Tutorials/ThingComp). No in-XML "remove one list item from parent" op exists; you must use patches.
- **Patching (XPath).** `PatchOperationAdd/Insert/Remove/Replace/AttributeSet/AttributeAdd/AttributeRemove/Sequence/Conditional/FindMod/AddModExtension/SetName`. Patches run after all XML is combined, in mod load order, and **before inheritance is resolved** — so you cannot target a node that only exists via inheritance, but patching a parent flows to children. VERIFIED (https://rimworldwiki.com/wiki/Modding_Tutorials/PatchOperations; startup order at https://rimworldwiki.com/wiki/Modding_Tutorials/Application_Startup steps 8–11).
- **Load pipeline** (VERIFIED, Application_Startup): parse XML → combine into one doc → apply patches → register inheritance (Name/ParentName) → apply inheritance → parse XML into Def classes (cross-refs registered, not resolved) → DefDatabase → DefOf binding pass 1 → implied defs → **ResolveReferences (cross-refs)** → DefOf pass 2 (missing def is now an error) → `ResolveReferences()` on all Defs in a fixed order → `ErrorCheckAllDefs` (ConfigErrors, **Dev mode only**) → short hashes.
- **Override visibility tooling.** None built-in beyond Dev-mode log; the game logs `database_conflicts`-style messages only via ConfigErrors. INFERRED.
- **Pain points.** Append-only list semantics, Class not inherited, patches vs inheritance ordering, abstracts from core being redefined by mods (wiki recommends unique abstract names), ConfigErrors only in Dev mode. VERIFIED/INFERRED as above.

### 1.2 Cataclysm: Dark Days Ahead — JSON

- **Identity.** Every object has `"type"` and (for most types) a unique string `"id"` per type; ids are single words (snake_case). Abstract templates use `"abstract": "<id>"` instead of `"id"`, are discarded after load, and missing mandatory fields on them are not an error. Keys starting with `//` are comments by convention (`//1`, `//2`…). VERIFIED (https://raw.githubusercontent.com/CleverRaven/Cataclysm-DDA/master/doc/JSON/JSON_INFO.md).
- **Inheritance ops.** VERIFIED (https://raw.githubusercontent.com/CleverRaven/Cataclysm-DDA/master/doc/JSON/JSON_INHERITANCE.md):
  - `copy-from`: inherit from an id or abstract of the **same type**; chaining allowed; omitted fields take parent values, specified fields replace.
  - `relative`: add/subtract numeric fields (object syntax for nested fields).
  - `proportional`: multiply numeric fields by a factor.
  - `extend`: add entries to container fields (flags, effects…) keeping parent entries.
  - `delete`: remove entries from container fields; deleting a non-present entry is not an error.
  - Support is **per field, per type**, decided in C++ (`generic_factory`); the doc says "there is no default inheritance" and the source is the authority. Some containers extend by default (`monstergroup`, `material.vitamins`) and need `"override": true` to replace.
- **Depth.** No hard limit; the doc recommends shallow chains ("one or two levels of abstracts") and says to use `copy-from` sparingly.
- **Collection ops are by value (set semantics on flag lists)**, not by position. `extend`/`delete` only exist for container-typed fields.
- **Pain points.** Field-by-field support matrix is opaque to authors; abstract types sometimes partially support `copy-from`; `SPELL` flags not always inherited. VERIFIED (same doc).

### 1.3 Caves of Qud — object blueprints (XML)

- **Identity.** `<object Name="...">` in `ObjectBlueprints.xml`. Children use `Inherits="..."` (COMMUNITY example of `Inherits="BaseBracelet"`; the wiki page I could read documents `Load="Merge"`, parts and removal tags but not the `Inherits` attribute itself) (https://wiki.cavesofqud.com/wiki/Modding:Objects).
- **Composition.** A blueprint is a bag of `<part Name="...">` (C# `IPart` classes) plus `<tag>`, `<stag>`, `<xtag>`, `<stat>`, `<property>`, `<intproperty>`, `<mutation>`, `<skill>`, `<inventoryobject>`, `<builder>`, `<mixin>`. VERIFIED (same page).
- **Merge semantics.** Without `Load="Merge"` a mod definition **replaces** the base object entirely; with it, data merges (child keeps non-overridden base values). Re-defining `<mutation>`/`<skill>` merges attributes. Parts are keyed by `Name`, so **collection merge is by id** (part name). Removal: `<removepart Name="..."/>`, `<removemutation>`, "most" tags have a `remove*` form; tag deletion via `Value="*delete"` is documented as **currently broken with Load="Merge" or on inherited tags**. `<mixin>` has `Include/Exclude/Priority/Load` (`Load="Fill"` adds the mixin before inheritance, otherwise after). VERIFIED.
- **Takeaway.** The most "ECS-like" of the sample: by-id component merge + explicit remove ops. Pain point: removal semantics interact badly with merge/inheritance (documented bug).

### 1.4 Paradox Clausewitz script (CK3 as representative)

- **Identity.** Top-level `key = { ... }` blocks in `common/<folder>/*.txt`. VERIFIED (https://ck3.paradoxwikis.com/Scripting).
- **Override rules.** VERIFIED (https://ck3.paradoxwikis.com/Modding):
  - Same path+filename as vanilla → whole file replaced.
  - Same top-level key in a different file → files load in ASCIIbetical order and the **last wins ("LIOS")**; only top-level declarations can be overridden; an override can change but **cannot remove** an object. Conflicts are written to `database_conflicts.log`.
  - Exceptions: **on_actions** — `events`, `random_events`, `on_actions` lists are **appended** across definitions; `trigger`/`effect` overwrite (and produce errors; wiki says use a custom on_action). Localization replaces only inside `replace/`. UI types are FIOS (first wins). Events/characters use explicit `id_override_priority` / `history_override_priority` integers.
- **Inheritance.** No prototype inheritance; reuse is via `scripted_effects`, `scripted_triggers` (with `$PARAM$` substitution) and `script_values`. VERIFIED (Scripting page).
- **Checksum.** The game shows a checksum of the loaded script set in the main menu; it is used for multiplayer parity and (historically) ironman/achievements; CK3 ≥1.9 no longer disables achievements for mods. VERIFIED (Modding page) + COMMUNITY (Steam forum on checksum semantics).
- **Pain points.** Whole-file override is a footgun; LIOS by filename is implicit and conflict-prone; cannot delete objects by override.

### 1.5 Minecraft (Java) — data packs / resource packs

- **Identity.** Resource locations `namespace:path`; chars `[a-z0-9_.-]` for namespace, plus `/` for path; omitting namespace defaults to `minecraft`; `minecraft` is reserved for vanilla overrides; path maps to a file path `data/<namespace>/<registry>/<path>.json`. VERIFIED (https://minecraft.wiki/w/Resource_location).
- **Merging.** Tags (`#namespace:tag`) merge across packs (`"replace": false` default) — INFERRED from prior knowledge, not re-verified today. Other registry files replace whole-file by last pack.
- **Migration.** DataVersion is a monotonically increasing integer stored in `level.dat`, each chunk, and each `.dat/.nbt`; the game warns when a world is newer than the client. VERIFIED (https://minecraft.wiki/w/Data_version). DataFixerUpper: schemas + DataFix rewrite rules composed by `DataFixerBuilder` into an optimized converter between versions; adopted in 1.13. VERIFIED (https://raw.githubusercontent.com/Mojang/DataFixerUpper/master/README.md, https://minecraft.wiki/w/DataFixerUpper).
- **Pain points.** DFU has a reputation for heavy startup cost and complexity (COMMUNITY/INFERRED); the pattern itself (per-version chained fixers keyed on an integer data version) is the strongest reference for "versioned saves".

### 1.6 Dwarf Fortress — raws

- **Identity.** Each raw file has `[OBJECT:TYPE]` header and `[TYPE:ID]` lines; ID is the reference key. VERIFIED (search summary of https://dwarffortresswiki.org/index.php/v0.34:Raws).
- **Composition.** `COPY_TAGS_FROM:<creature>` plus **creature variations** as templates: `CV_NEW_TAG`, `CV_REMOVE_TAG` (prefix-match on a leading chain of arguments), `CV_CONVERT_TAG` with `CVCT_MASTER/TARGET/REPLACEMENT` (string find/replace inside tokens), `!ARGn` substitution, conditional `*_CTAG` variants. Application order is fixed: removes (bottom-up), converts (bottom-up), adds (top-down); variations cannot be nested. VERIFIED (https://dwarffortresswiki.org/index.php/Creature_variation_token).
- **Pain points.** Textual token rewriting (string replace) is fragile: an empty `CVCT_TARGET` freezes the game; `!ARG10` with 3 args expands to "one0"; cannot remove tags added by the same variation. Good negative example for "don't do string-level patching".

### 1.7 Unity — ScriptableObjects, Odin, Addressables

- **Identity.** Asset GUID (in `.meta`) + path; Addressables add a string **address** (not required unique; several assets may share an address, disambiguated by labels) and `AssetReference` fields; catalog maps address → location, with a catalog hash file for update checks. VERIFIED (https://docs.unity3d.com/Packages/com.unity.addressables@2.3/manual/AddressableAssetsOverview.html).
- **Inheritance/composition.** None at data level; composition is by C# class hierarchy / nested SO references. INFERRED.
- **Known pain points (COMMUNITY).** SO assets are shared singletons — runtime mutation leaks back into the asset in the Editor; Addressables can duplicate an SO when referenced from both addressable and non-addressable contexts (instance identity breaks). Odin Serializer needs an AOT scan/"Generate DLL" step before IL2CPP builds (VERIFIED https://odininspector.com/tutorials/serialize-anything/aot-serialization).
- **Lesson.** Engine-native assets conflate definition and instance and tie identity to a GUID that is invisible in the data itself.

### 1.8 Unreal — DataTables, DataAssets, Gameplay Tags

- **DataTable.** Rows are a C++ `FTableRowBase` struct; first column `Name` is the row key; CSV/JSON import with Ignore Extra/Missing Fields, Import Key Field; references via `FDataTableRowHandle {Table, RowName}`; docs say not to cache returned row pointers beyond local scope (reimport invalidates). VERIFIED (https://dev.epicgames.com/documentation/en-us/unreal-engine/data-driven-gameplay-elements-in-unreal-engine).
- **Primary assets.** `FPrimaryAssetId = Type:Name` (e.g. `MyGameZoneTheme:Forest`); `UPrimaryDataAsset` implements `GetPrimaryAssetId`; Asset Manager has **redirects for IDs, types and names** (renames) and bundles of soft refs (`TSoftObjectPtr`). Blueprint DataAssets are classes (inheritable); plain DataAssets are instances (not inheritable). VERIFIED (https://dev.epicgames.com/documentation/en-us/unreal-engine/asset-management-in-unreal-engine).
- **Gameplay Tags.** Dot-separated hierarchical names (`Event.Movement.Dash`), registered from ini/DataTable/native macros; `FGameplayTagContainer` with `HasTag/HasAny/HasAll` and `FGameplayTagQuery`. VERIFIED (https://dev.epicgames.com/documentation/en-us/unreal-engine/using-gameplay-tags-in-unreal-engine).
- **Lesson.** `Type:Name` ids, hierarchical tags with prefix-matching, and a central redirect table for renames are all directly reusable.

### 1.9 Cross-game comparison

| Game | Identity | Inheritance | Collection merge | Remove op | Override visibility |
|---|---|---|---|---|---|
| RimWorld | defName (+ Name for abstracts) | single parent chain, scalars replace, lists append | by position/append | only `Inherit="False"` (whole list) or XPath patch | Dev-mode ConfigErrors |
| CDDA | type + id / abstract | `copy-from`, same type, chains | `extend`/`delete` by value, per-field support | `delete` | none (loader errors) |
| Qud | Name | `Inherits`, `Load="Merge"` | parts by Name (by id) | `removepart`, `*delete` (buggy) | none |
| Paradox | key per folder | none; scripted_* reuse | on_actions append; else last-wins | cannot remove | `database_conflicts.log` |
| Minecraft | `ns:path` | none; tags merge | tags merge, `replace` | `replace:true` | none |
| DF | `[TYPE:ID]` | COPY_TAGS_FROM + variations | token-level add/remove/convert | CV_REMOVE_TAG | none |
| Unreal | `Type:Name`, RowName | class-level only | n/a | n/a | redirects |

---

## 2. Definition → resolved definition → runtime instance; save validity across content changes

### 2.1 Patterns observed

- **Resolve once at load, then freeze.** RimWorld resolves inheritance, then cross-references, then `ResolveReferences()` per def, then runs config error checks, then assigns **short hashes** — all before play. VERIFIED (Application_Startup). Factorio: prototypes are constructed at the end of the data stage and **cannot be modified in the control stage**. VERIFIED (https://lua-api.factorio.com/latest/auxiliary/data-lifecycle.html).
- **Abstracts vanish.** CDDA abstracts and RimWorld `Abstract="True"` defs are not present in the resolved catalog. VERIFIED.
- **Instances reference definitions by stable string id.** RimWorld saves store `defName` strings; on load `ScribeExtractor.DefFromNode` goes through `Verse.BackCompatibility.BackCompatibleDefName` to map renamed defs (the stack trace in a Workshop thread shows this call chain; the exact class API is COMMUNITY/INFERRED). RimWorld logs "Def has been renamed: X -> Y" on load. COMMUNITY (Steam Workshop threads returned by search).
- **Unreal** keeps a redirect table (IDs/types/names) in the Asset Manager. VERIFIED.
- **Factorio migrations.** `migrations/*.json` rename prototypes (`["wall","stone-wall"]` under `entity`/`item`…); `migrations/*.lua` run after load with full `game` access. Ordering: mod order, then lexicographic file name; all JSON migrations before any Lua. **Each save records which migrations (by name) have run**, so none runs twice; adding a mod to a save runs all its migrations. Renaming keeps `unit_number`; changing an entity's *type* assigns a new one and invalidates references; unmigratable ghosts are removed. VERIFIED (https://lua-api.factorio.com/latest/auxiliary/migrations.html).
- **Minecraft.** Integer `DataVersion` stamped on every persisted unit (level, chunk, entity file); DFU composes per-version fixes into one converter; the game warns when loading newer data. VERIFIED.
- **Paradox.** Script objects referenced by key in saves; `database_conflicts.log`; no documented migration framework (INFERRED: Paradox saves break across major versions and the studio communicates "save compatible" per patch — COMMUNITY practice, not verified here).

### 2.2 Concrete rules for a "versionado y migración" gate (proposal)

1. **Two id spaces.** `DefId` (string, namespaced, authored, stable) vs `InstanceId` (runtime, numeric/ulong, allocated by the sim's deterministic allocator). Instances persist `defId`, never a pointer or array index. (RimWorld defName, Factorio name + unit_number — VERIFIED patterns.)
2. **Content version stamp on every save unit.** Save header carries `{ contentVersion: int (monotonic), schemaVersion: int, contentHash: string, moduleVersions: {module: semver} }`. Minecraft DataVersion is the model (VERIFIED).
3. **Template version on instance.** Each instance also stores `defVersion` (the `version` field of the definition it was created from). On load, if `catalog[defId].version != instance.defVersion`, run the def's registered `Upgrade(instance, fromVersion)` hooks or re-derive derived fields from the new definition. INFERRED design.
4. **Rename ledger, not deletion.** A machine-readable `migrations/<module>/<n>-<name>.json` with `{ "rename": { "type": "item", "from": "patriark:old", "to": "patriark:new" } }` entries, applied before any code migration; a definition must never simply disappear while a prior shipped version referenced it — it becomes a `tombstone` entry (`"deprecated": {"since": 12, "replacedBy": "..."}`) that the validator reports as Warning if referenced. Factorio JSON migrations + Unreal redirects (VERIFIED).
5. **Migration ledger in the save.** Record migration names already applied (Factorio) so that re-loading never re-applies. VERIFIED pattern.
6. **Never downgrade.** Refuse (or load read-only) saves whose `contentVersion` is newer than the running content (Minecraft warns). VERIFIED pattern.
7. **Gate checks** (CI, run against a corpus of saved games per released version): (a) every `defId` referenced by any instance in the corpus resolves (directly or via rename ledger); (b) every type discriminator in the corpus is still registered; (c) migrations are idempotent (apply twice == apply once); (d) `schemaVersion` bump requires a fixer entry.
8. **Resolved catalog is immutable** (records / `ImmutableArray`), built in one pass, then handed to the sim; editor/debug tooling works on definitions, never on the resolved catalog. INFERRED from RimWorld/Factorio.

---

## 3. Namespaced stable IDs, content hashing, editorial metadata separation

### 3.1 Id scheme

- Minecraft `namespace:path` grammar is the most battle-tested: `[a-z0-9_.-]+ ":" [a-z0-9_./-]+`, default namespace reserved for core. VERIFIED. Unreal uses `Type:Name`. VERIFIED. CDDA uses bare snake_case ids scoped by `type`. VERIFIED.
- Proposal: `GdoId = "<module>:<type>/<path>"` is over-specified; keep **`module:path`** for the id and carry `type` as a separate field (the catalog is keyed by `(type, id)` like CDDA, which also allows the same path in two types if ever needed). Rules: lowercase, no spaces, `/` allowed for grouping (`patriark:weapons/sword_iron`), module prefix mandatory (no default namespace — Minecraft wiki itself recommends always writing the colon).
- Hierarchical tags like Unreal Gameplay Tags (`combat.melee.sword`) for classification, with prefix-match queries. VERIFIED pattern.

### 3.2 Hashing for identity/determinism

- Use **RFC 8785 JSON Canonicalization Scheme (JCS)**: recursive key sort by UTF-16 code units, ECMAScript number formatting, fixed string escaping, no whitespace, UTF-8; designed for hashing/signing. VERIFIED (https://www.rfc-editor.org/rfc/rfc8785). Note: JCS forbids NaN/Infinity and requires I-JSON (doubles) — fine for gameplay data if you store fixed-point as integers or strings.
- Hash **the resolved definition** (post-inheritance, post-patch) rather than the source file, so that refactoring an abstract parent that produces an identical resolved object does not change the content hash. Hash the whole catalog as a Merkle-style hash of sorted `(type,id) → objectHash` pairs; store it in the save header. Paradox's menu checksum serves the same purpose for multiplayer parity. INFERRED design; checksum practice COMMUNITY.
- Godot's `ResourceUID.create_id_for_path()` is deterministic per path+project (VERIFIED https://docs.godotengine.org/en/stable/classes/class_resourceuid.html), but UIDs are random otherwise and live in `.uid` sidecars/`.import`/headers (VERIFIED https://godotengine.org/article/uid-changes-coming-to-godot-4-4/) — they are not content hashes.

### 3.3 Separating editorial metadata from gameplay data

- CDDA convention: any key starting with `//` is a comment and is ignored by the loader. VERIFIED. Godot `.tres` discards comments and default-valued properties on save, which makes it hostile to editorial metadata. VERIFIED (https://docs.godotengine.org/en/stable/engine_details/file_formats/tscn.html).
- Proposal: a reserved top-level object **`"$editor"`** (or `"meta"`) per GDO holding `label`, `description`, `comments`, `position`, `color`, `author`, `lastEdited`. The canonicalizer **strips `$editor` (and any `//*` key) before hashing**; the resolver copies it into a side table (`EditorialIndex`) that is only available in the editor/tooling build. Display labels that are *gameplay-visible* (item names) are NOT editorial — they are localization keys (`"name": "item.sword_iron.name"`) and do count toward the hash.

---

## 4. Type registries / plugin extension in C# under NativeAOT

### 4.1 Constraints (VERIFIED)

- NativeAOT: no `Assembly.LoadFile`, no `Reflection.Emit`, trimming is mandatory, generic instantiations over structs are pre-generated, `System.Linq.Expressions` are interpreted. iOS/tvOS/MacCatalyst are supported targets on .NET 9+ (experimental on .NET 8). (https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- Trimming: unannotated `Type.GetMethods()`/`Activator.CreateInstance(unknownType)` produce IL2070/IL2072 warnings; fix by flowing `[DynamicallyAccessedMembers(...)]` on the `Type` parameters/fields; `[RequiresUnreferencedCode]` to propagate; `[DynamicDependency]` as last resort; avoid reflection in static constructors; prefer source generators over reflection-based serializers. (https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/prepare-libraries-for-trimming)
- System.Text.Json: polymorphism via `[JsonPolymorphic]` + `[JsonDerivedType(typeof(T), "discriminator")]`; `$type` must be first unless `AllowOutOfOrderMetadataProperties = true`; polymorphism is supported in **metadata-based** source generation, **not fast-path**; the **contract model** (`DefaultJsonTypeInfoResolver` subclass setting `JsonPolymorphismOptions.DerivedTypes`) lets you register derived types at runtime without attributes; `JsonTypeInfoResolver.Combine(...)`/`TypeInfoResolverChain` lets multiple source-generated contexts (one per module) be chained; `JsonSerializerIsReflectionEnabledByDefault=false` makes reflection fallbacks throw deterministically. (https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/polymorphism, https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
- Godot C#: iOS export is experimental, NativeAOT is the only runtime there, Godot generates an `rd.xml` to root Godot + project assemblies because the bindings are not trim-safe. VERIFIED (https://github.com/godotengine/godot/pull/82729, https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/index.html via search) + COMMUNITY (forum on trim-compat).

### 4.2 Options

| Approach | AOT-safe? | Extensible without touching core? | Notes |
|---|---|---|---|
| Attribute scan over `AppDomain.GetAssemblies()` + `Activator.CreateInstance` | No (IL2026/IL2070, assemblies may be trimmed) | Yes | Classic Unity/RimWorld style; breaks under trimming unless everything is rooted (what Godot's rd.xml does today — but rooting disables trimming benefits and is "no guarantee" per Godot forum). |
| Explicit registration (`IGdoModule.Register(registry)`) called from a composition root | Yes | Yes, per module | Each module (assembly) exposes one entry point; core never references modules; the host lists modules. Zero reflection. |
| Source generator that emits the registry from attributes | Yes (generated code is static) | Yes | Attributes become *compile-time* discovery: `[GdoType("patriark:item")] partial class ItemDef` → generator emits `ModuleRegistry.Register(...)`, the `JsonSerializerContext` `[JsonSerializable]` list and `[JsonDerivedType]` entries. Most work, best DX. |
| STJ contract model with `DefaultJsonTypeInfoResolver` | Uses reflection internally | — | `DefaultJsonTypeInfoResolver` is reflection-based; under AOT use the generated context + `JsonTypeInfoResolver.Combine`. INFERRED from docs. |

### 4.3 Recommended approach

1. **Explicit module registration as the runtime contract** (`IGdoModule { string Id; void Register(IGdoRegistry r); }`), invoked by a composition root that holds a static list of modules. This is the AOT truth.
2. **A source generator as the convenience layer**: scans `[GdoType("...")]`, `[GdoComponent("...")]`, `[GdoHook("...")]` in the module's assembly and emits (a) the `Register` body, (b) a `partial class <Module>JsonContext : JsonSerializerContext` with `[JsonSerializable]` for every GDO/component type in metadata mode, (c) `[JsonDerivedType]`-equivalent polymorphism options via `JsonPolymorphismOptions` applied through a generated `IJsonTypeInfoResolver` modifier (so core's `IComponent` base does not need to know derived types at compile time). Generators are compile-time and produce trim-safe code. VERIFIED feasibility from STJ docs ("polymorphism supported in metadata-based source generation"; "Combine"), design INFERRED.
3. **Registry shape** (string discriminator → factory/typeinfo), no `Type.GetType(string)`: `registry.Add("patriark:component/stats", new TypeEntry(typeof(StatsComponent), StatsJsonContext.Default.StatsComponent, validator, upgrader))`. All `Type` values come from `typeof(...)` so trim analysis sees them.
4. **Compile-time guard**: `<IsAotCompatible>true</IsAotCompatible>` + `<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>` on `src/domain` and all modules so any reflection fallback fails in tests on desktop, not on the device. VERIFIED properties.
5. Discriminator strings are **GDO type ids** (namespaced), never CLR type names, so renaming a class never changes data. INFERRED.

---

## 5. Diagnostics model

### 5.1 Reference shapes

- **Roslyn.** `DiagnosticDescriptor { Id, Title, MessageFormat, Category, DefaultSeverity, IsEnabledByDefault, Description, HelpLinkUri, CustomTags }` is the *static* part; `Diagnostic.Create(descriptor, location, args...)` adds `Location`, `AdditionalLocations`, `Properties`, effective `Severity`. VERIFIED (https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.diagnosticdescriptor).
- **JSON Schema 2020-12 output.** Output unit: `valid` (MUST), `keywordLocation` (JSON Pointer into the schema, SHOULD), `absoluteKeywordLocation` (SHOULD, may be omitted), `instanceLocation` (JSON Pointer into the instance, SHOULD), `error` or `annotation` (SHOULD), nested `errors`/`annotations`; formats flag/basic (flat list)/detailed/verbose. VERIFIED (https://json-schema.org/draft/2020-12/json-schema-core section 12).
- **RimWorld.** `ConfigErrors()` yields free-form strings per Def, logged only in Dev mode; cross-ref failures and DefOf misses are separate error paths. VERIFIED (Application_Startup).
- **Paradox.** `database_conflicts.log` / `error.log` text files. VERIFIED (Modding page).

### 5.2 Proposed minimal diagnostic record

```csharp
public enum DiagnosticSeverity { Hidden, Info, Warning, Error }

public sealed record DiagnosticDescriptor(
    string Code,            // "GDO0007" — stable, documented
    string Title,
    string MessageFormat,   // string.Format style
    string Category,        // "Schema" | "Reference" | "Inheritance" | "Balance" | "Asset" | "Migration"
    DiagnosticSeverity DefaultSeverity,
    string? HelpUri = null);

public sealed record GdoLocation(
    string Source,          // file path or "memory:<editor>"
    string? ObjectId,       // "patriark:weapons/sword_iron"
    string? ObjectType,     // "patriark:item"
    string? Pointer,        // RFC 6901 JSON Pointer into the *source* object: "/components/stats/damage"
    int? Line = null, int? Column = null);

public sealed record Diagnostic(
    DiagnosticDescriptor Descriptor,
    DiagnosticSeverity Severity,          // effective (after config overrides)
    string Message,                       // formatted
    GdoLocation Location,
    IReadOnlyList<GdoLocation> Related,   // e.g. the parent that conflicts
    IReadOnlyList<FixHint> Fixes,         // suggested fixes, not auto-applied
    IReadOnlyDictionary<string,string> Properties); // machine-readable extras ("expected", "actual")

public sealed record FixHint(string Title, string? ReplacementJson = null, string? Pointer = null);
```

Rules: validators never throw for content problems — they append Diagnostics; the loader fails only if any `Error` exists; `Pointer` always points into the **authored** object (pre-resolution) so the editor can navigate, with `Related` carrying the resolved/parent location; every descriptor has a stable code and a doc page; severity overridable by config (Roslyn `GetEffectiveSeverity` analogue).

---

## 6. Neutral-format authoring with an in-engine editor

### 6.1 Examples

- **Pandora (Godot addon).** Single `data.pandora` file at project root (JSON by inspection of the repo; the README does not state the format), category/entity tree with **properties propagating to child categories and entities**, generated `EntityIds` constants, `entity.instantiate()` for runtime instances with per-instance overrides (`set_integer(...)`), editor UI in a dock; "ALPHA, not production-ready". VERIFIED (https://github.com/bitbrain/pandora README). Single-file store = one merge-conflict hotspot. INFERRED.
- **Dialogic 2.** Timelines are plain-text `.dtl` (shortcode-ish syntax, tab indentation, editable in any editor) with a visual editor on top; characters are `.dch` resources (text; COMMUNITY says `var_to_str(inst_to_dict())`). VERIFIED (https://docs.dialogic.pro/timeline-text-syntax.html, https://docs.dialogic.pro/characters-and-portraits.html via search summaries). Filename (sans extension) is the character's stable id — a rename is a breaking change. VERIFIED.
- **Unreal DataTables** are the canonical "spreadsheet is source, engine imports" pipeline; reimport keeps the path, row `Name` is the key. VERIFIED.
- **Unity + Excel/Google Sheets → ScriptableObject importers** are ubiquitous (INFERRED, not verified today).
- **RimWorld/CDDA/Paradox/Minecraft** all ship with neutral text as the *only* source and no in-engine editor; community tooling (XML/JSON editors, VS Code extensions with schemas) fills the gap. INFERRED/VERIFIED.

### 6.2 Trade-offs: Godot `.tres` vs neutral JSON

| Concern | `.tres` (Godot Resource) | JSON per object/module |
|---|---|---|
| Diffability | Text, "version-control friendly" (VERIFIED), but editor rewrites: comments and defaults dropped, `ext_resource id="1_7bt6s"` and `sub_resource id="Type_xxxxx"` are generated opaque ids, ordering of sub-resources is semantic (VERIFIED tscn format page). Stale files cause noisy diffs; addon "Resource Resaver" exists for that (COMMUNITY). | Fully under your control; canonical key order; comments allowed by convention (`//`, `$editor`). |
| Merge conflicts | UIDs are non-deterministic across machines → `.uid`/uid conflicts after merges (COMMUNITY blog + Godot's own article explains why they rejected a central UID DB: "frequent merge conflicts"). | One file per object → conflicts localized; no generated ids if you forbid them. |
| Tests / CLI | Needs Godot runtime (or a parser for the `.tres` grammar) to load; `src/domain` must stay Godot-free → impossible to load `.tres` there without a custom parser. | `System.Text.Json` in plain `dotnet test`; CI validators without the engine. |
| Asset references | Native: `ExtResource`/`uid://` with editor-tracked renames (VERIFIED). | Must use string keys + a resolver layer (§7) and a validator for missing assets. |
| Undo/redo, inspector | Free via `EditorUndoRedoManager` + Inspector for `[Export]` fields. | Must build editor plugin: inspector-like UI, undo/redo, file watcher, re-validation. Dialogic/Pandora show it is doable but is real work. |
| Inheritance / patches | None (resource duplication only). | Explicit resolver (`parent`, component ops). |
| Determinism / hashing | Engine-controlled serialization; floats formatted by Godot; UIDs in file. | JCS hashing of resolved objects (§3). |
| NativeAOT / trimming | Godot's `[Export]`/`[GlobalClass]` rely on generated glue and rooted assemblies. | STJ source-gen contexts, trim-safe. |

**Recommendation (INFERRED):** neutral JSON (one file per GDO, folder per module and type, e.g. `content/<module>/<type>/<path>.json`) as the canonical source; a Godot editor plugin that reads/writes those files through the same `src/domain` loader/validator (so editor and CI agree), uses `EditorUndoRedoManager` for in-editor undo, and never emits generated ids. Keep `.tres`/`.tscn` strictly for presentation assets.

---

## 7. Asset references

- **Godot.** `uid://` ids stored in `.import` (imported assets), file header (`.tscn/.tres`), `.uid` sidecars (scripts/shaders, since 4.4); path kept as fallback; commit `.uid` files; `ResourceUID.path_to_uid / uid_to_path / ensure_path / create_id_for_path`; silent wrong-link risk when path fallback hits a different file. VERIFIED (ResourceUID class ref + 4.4 UID article).
- **Unity Addressables.** String address + labels, catalog maps to location, catalog hash file for update detection; addresses need not be unique (ambiguity risk). VERIFIED.
- **Unreal.** `FPrimaryAssetId Type:Name`, `TSoftObjectPtr` lazy references, Asset Manager redirects, bundles; cook-time errors for development-only assets. VERIFIED.
- **Proposal.** Definitions reference presentation by **logical asset key** (`"icon": "asset:patriark/items/sword_iron"`), a namespaced string in the same grammar as GDO ids. A per-platform **asset manifest** (`assets.manifest.json`, generated by an editor tool from the Godot project: key → `uid://…` + `res://…` fallback + type + content hash) is the only thing that knows about Godot. Build-time gate: every `asset:` key referenced by the resolved catalog must exist in the manifest and resolve to an existing uid; missing → `Error` diagnostic with category `Asset`. The sim never sees asset keys' targets, only the key string, preserving determinism and Godot-freedom. INFERRED design on VERIFIED primitives.

---

## 8. Behaviour / scripting contract basics (informs the shape only)

- **Paradox on_actions.** Named hook ids (`on_death`, …) with a documented scope per hook; `events`/`random_events`/`on_actions` lists **append** across mods; `trigger`/`effect` overwrite. Reusable `scripted_effects`/`scripted_triggers` with `$PARAM$`. VERIFIED (Modding, Scripting pages).
- **CDDA effect_on_condition.** `id`, `eoc_type` (ACTIVATION, RECURRING, EVENT with `required_event`, death hooks), `recurrence`, `condition`/`effect`/`false_effect`, `deactivate_condition`, `global` vs per-character queues, `run_eocs` with `time_in_future` (queued continuation), context variables lost on avatar swap, `global_val` persists for the world. VERIFIED (EFFECT_ON_CONDITION.md).
- **Factorio.** Prototypes frozen after data stage; control stage per-mod Lua state; `on_init`/`on_load`/`on_configuration_changed`; `on_load` may only re-register handlers/metatables and must not touch state or **desyncs happen**; events dispatched in mod load order; migrations run before `on_load`. VERIFIED (data-lifecycle, migrations).
- **RimWorld quests.** `QuestScriptDef` holds a root `QuestNode` tree; nodes read/write a "slate" and emit `QuestParts` (the persisted runtime objects); generation does a dry run. VERIFIED (search summary of https://rimworldwiki.com/wiki/Modding_Tutorials/Quests).
- **Contract shape to carry forward (INFERRED):** hook ids are namespaced strings with a declared typed context (`"hook": "patriark:on_unit_died"`, context type registered like a GDO type); scheduled work is a serializable record (`{hookId, dueTick, args}`) in a deterministic priority queue ordered by `(dueTick, seq)`; lists of handlers **append by id** across modules with explicit `order`/`priority` integers (Paradox lesson); no closures in saves (CDDA loses context vars; Factorio forbids non-serialized state).

---

## 9. Recommended GDO contract (draft)

### 9.1 Authored envelope (JSON)

```jsonc
{
  "$schema": "gdo/1",                       // envelope schema version (int in code)
  "id": "patriark:weapons/sword_iron",      // module:path, required unless "abstract"
  "abstract": false,                        // true → never in resolved catalog
  "type": "patriark:item",                  // GDO type id (registry discriminator)
  "version": 3,                             // content version of THIS definition (bump on gameplay-affecting change)
  "parent": "patriark:weapons/_base_sword", // optional, single parent, same type
  "tags": ["combat.melee.sword", "tier.1"], // hierarchical, set-merged with parent
  "components": {                           // keyed by component type id → by-id merge
    "patriark:stats":   { "damage": 12, "weight": 3.5 },
    "patriark:durability": { "max": 100 },
    "patriark:icon": null                   // null = delete inherited component
  },
  "ops": {                                  // CDDA-style, optional
    "relative":     { "components/patriark:stats/damage": 2 },
    "proportional": { "components/patriark:stats/weight": 0.9 },
    "extend":       { "tags": ["rare"] },
    "delete":       { "tags": ["tier.1"] }
  },
  "refs": {                                 // declared outbound references, validated
    "material": "patriark:materials/iron",
    "icon": "asset:patriark/items/sword_iron"
  },
  "$editor": { "label": "Iron sword", "notes": "...", "position": [120, 40] } // stripped before hashing
}
```

### 9.2 C#-ish shapes

```csharp
public readonly record struct GdoId(string Module, string Path) { public override string ToString() => $"{Module}:{Path}"; }
public readonly record struct GdoTypeId(string Value);
public readonly record struct AssetKey(string Value);

// Authored (as loaded from JSON; mutable-ish DTO, editor-facing)
public sealed class GdoDefinition {
    public int SchemaVersion { get; init; }
    public GdoId? Id { get; init; }           // null when Abstract
    public string? Abstract { get; init; }    // abstract name
    public GdoTypeId Type { get; init; }
    public int Version { get; init; }
    public GdoId? Parent { get; init; }
    public IReadOnlyList<string> Tags { get; init; }
    public IReadOnlyDictionary<GdoTypeId, JsonElement?> Components { get; init; } // raw until resolved
    public GdoOps? Ops { get; init; }
    public IReadOnlyDictionary<string, string> Refs { get; init; }
    public JsonElement? Editor { get; init; } // "$editor"
    public SourceInfo Source { get; init; }   // file, line
}

// Resolved (immutable, hashed, what the sim sees)
public sealed record ResolvedGdo(
    GdoId Id, GdoTypeId Type, int Version,
    TagSet Tags,
    ImmutableDictionary<GdoTypeId, IComponent> Components,   // typed via registry
    ImmutableDictionary<string, GdoId> DefRefs,
    ImmutableDictionary<string, AssetKey> AssetRefs,
    string ContentHash);                                     // JCS(SHA-256) of the canonical resolved form, $editor stripped

public sealed record ResolvedCatalog(
    int ContentVersion, string CatalogHash,
    ImmutableDictionary<(GdoTypeId, GdoId), ResolvedGdo> Objects,
    ImmutableArray<Diagnostic> Diagnostics);

// Runtime instance (sim state; serialized in saves)
public sealed record GdoInstance(ulong InstanceId, GdoId DefId, int DefVersion, /* mutable state components */ ...);

// Registry (explicit, AOT-safe; populated by generated code per module)
public interface IGdoRegistry {
    void AddType(GdoTypeId id, GdoTypeEntry entry);              // JsonTypeInfo, validator, upgrader
    void AddComponent(GdoTypeId id, ComponentEntry entry);
    void AddHook(string hookId, HookEntry entry);
    void AddMigration(Migration m);
}
public interface IGdoModule { string Id { get; } int ContentVersion { get; } void Register(IGdoRegistry r); }
```

### 9.3 Resolver rules (proposal)

1. **Load order**: parse all modules' files → build `(type, id|abstract)` index → reject duplicate ids (Error GDO0001) → apply **module patches** (RimWorld order: patches before inheritance) → resolve inheritance → materialize components via registry → resolve refs → validate → hash → freeze.
2. **Parent**: single, same `type` (CDDA rule), must resolve (Error GDO0002); **max depth 4** including abstracts (CDDA/RimWorld advise shallow; make it a configurable Error); cycles → Error GDO0003.
3. **Scalars**: child replaces parent. **Objects within a component**: deep-merge by key. **Components**: merge **by component id**; `null` deletes (Qud `removepart`). **Tags**: set union; `ops.delete` removes. **Arrays of objects**: merge by element `"id"` if present, otherwise child **replaces** the array (never positional append — RimWorld lesson); authors who want append use `ops.extend`.
4. **Ops** apply after inheritance in fixed order: `delete` → `extend` → `relative` → `proportional` (DF's fixed order lesson); op on an unsupported field → Error GDO0010; `delete` of absent entry → Warning (CDDA says not an error; we prefer a warning for typos).
5. **Conflicts**: two modules patching the same pointer of the same object without explicit `order` → Error GDO0020 listing both locations (Paradox `database_conflicts.log` made fatal). Module order is explicit in a manifest, not filename order.
6. **Refs**: every `refs.*` value must resolve to an existing resolved object of the declared target type (Error GDO0030) or, for `asset:` keys, to the asset manifest (Error GDO0040); references to a deprecated id resolve via rename ledger with Warning GDO0031.
7. **Abstracts** never enter the catalog; referencing one is Error GDO0004.
8. **Hashing**: `ContentHash = SHA256(JCS(resolved object minus $editor, minus Source))`; `CatalogHash = SHA256(concat of sorted "type:id=hash" lines)`; saves store `CatalogHash` and per-module `ContentVersion`.
9. **Determinism**: catalog construction iterates in sorted `(type, id)` order; no `Dictionary` enumeration order leaks into results; floats parsed with invariant culture; prefer integer fixed-point in gameplay fields.
10. **Diagnostics**: resolver returns `ResolvedCatalog` even on errors (with the diagnostics list) so the editor can show everything at once; the game refuses to start on any `Error`.

### 9.4 Top risks

1. **Editor plugin cost.** JSON-as-source means building inspector-like UI + undo/redo + validation feedback in Godot (Pandora is still alpha after years; Dialogic needed its own text syntax). Mitigation: generate the UI from the registry's component schemas; start with "open in external editor + live validation panel".
2. **Godot C# on iOS is experimental and relies on rooting assemblies** (rd.xml), which may hide trim bugs until device testing; STJ reflection fallback must be disabled project-wide so failures appear on desktop.
3. **Merge-op complexity creep** (CDDA's per-field support matrix, Qud's broken `*delete`). Keep ops to the four listed, apply to JSON pointers generically, and test resolver exhaustively with golden files.
4. **Version discipline.** `version` on definitions and the rename ledger only work if CI enforces "shipped id never disappears" and "gameplay change bumps version"; without a save corpus in CI the gate is theatre.
5. **Hash instability from numbers/culture.** JCS requires double semantics; mixing decimals/fixed-point or culture-specific parsing silently changes hashes. Decide number representation per field type up front.
6. **Asset key drift.** Manifest generation must be part of the export pipeline; otherwise refs validate in the editor but fail in builds.

---

## Sources (primary unless noted)

- RimWorld wiki: Def classes https://rimworldwiki.com/wiki/Modding_Tutorials/Def_classes ; XML file structure https://rimworldwiki.com/wiki/Modding_Tutorials/XML_file_structure ; PatchOperations https://rimworldwiki.com/wiki/Modding_Tutorials/PatchOperations ; Application Startup https://rimworldwiki.com/wiki/Modding_Tutorials/Application_Startup ; Quests https://rimworldwiki.com/wiki/Modding_Tutorials/Quests ; ThingComp https://rimworldwiki.com/wiki/Modding_Tutorials/ThingComp (via search summary). BackCompatibility call chain: Steam Workshop thread (community) https://steamcommunity.com/workshop/filedetails/discussion/1321849735/3110267314139419788
- CDDA: JSON_INHERITANCE.md https://raw.githubusercontent.com/CleverRaven/Cataclysm-DDA/master/doc/JSON/JSON_INHERITANCE.md ; JSON_INFO.md https://raw.githubusercontent.com/CleverRaven/Cataclysm-DDA/master/doc/JSON/JSON_INFO.md ; EFFECT_ON_CONDITION.md https://raw.githubusercontent.com/CleverRaven/Cataclysm-DDA/master/doc/JSON/EFFECT_ON_CONDITION.md
- Caves of Qud: https://wiki.cavesofqud.com/wiki/Modding:Objects
- Paradox CK3: Modding https://ck3.paradoxwikis.com/Modding ; Scripting https://ck3.paradoxwikis.com/Scripting
- Minecraft: Resource location https://minecraft.wiki/w/Resource_location ; Data version https://minecraft.wiki/w/Data_version ; DataFixerUpper https://minecraft.wiki/w/DataFixerUpper and https://raw.githubusercontent.com/Mojang/DataFixerUpper/master/README.md
- Dwarf Fortress: Creature variation token https://dwarffortresswiki.org/index.php/Creature_variation_token ; Raws https://dwarffortresswiki.org/index.php/v0.34:Raws (search summary)
- Factorio: Migrations https://lua-api.factorio.com/latest/auxiliary/migrations.html ; Data lifecycle https://lua-api.factorio.com/latest/auxiliary/data-lifecycle.html
- Unity: Addressables overview https://docs.unity3d.com/Packages/com.unity.addressables@2.3/manual/AddressableAssetsOverview.html ; Odin AOT https://odininspector.com/tutorials/serialize-anything/aot-serialization ; SO pitfalls (community) https://discussions.unity.com/t/scriptableobject-references-in-addressables/765466
- Unreal: Data-driven gameplay elements https://dev.epicgames.com/documentation/en-us/unreal-engine/data-driven-gameplay-elements-in-unreal-engine ; Asset management https://dev.epicgames.com/documentation/en-us/unreal-engine/asset-management-in-unreal-engine ; Gameplay Tags https://dev.epicgames.com/documentation/en-us/unreal-engine/using-gameplay-tags-in-unreal-engine
- Godot: ResourceUID https://docs.godotengine.org/en/stable/classes/class_resourceuid.html ; UID article https://godotengine.org/article/uid-changes-coming-to-godot-4-4/ ; Resources https://docs.godotengine.org/en/stable/tutorials/scripting/resources.html ; TSCN format https://docs.godotengine.org/en/stable/engine_details/file_formats/tscn.html ; C# platforms https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/index.html (search summary) ; iOS NativeAOT PR https://github.com/godotengine/godot/pull/82729 ; Pandora https://github.com/bitbrain/pandora ; Dialogic docs https://docs.dialogic.pro/timeline-text-syntax.html , https://docs.dialogic.pro/characters-and-portraits.html (search summaries)
- .NET: Native AOT https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ ; Prepare for trimming https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/prepare-libraries-for-trimming ; STJ polymorphism https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/polymorphism ; STJ source generation https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
- Diagnostics: Roslyn DiagnosticDescriptor https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.diagnosticdescriptor ; JSON Schema 2020-12 core §12 https://json-schema.org/draft/2020-12/json-schema-core
- Hashing: RFC 8785 JCS https://www.rfc-editor.org/rfc/rfc8785
