---
name: kedo-syntax
description: Reference for reading and writing KEDO (.kedo) content files, the Kaux Enriched Data Object language. Use whenever you create or edit a .kedo file, need the exact syntax of declarations, extends, with (mixins), component, remove, list operators (=, =!, +=, -=, name[key]), references (type:id, asset:id), durations, fixed-point decimals, maps with quoted keys, expressions, hooks (on ... wait/apply/emit) or functions.
---

# KEDO syntax (language 1)

KEDO files are UTF-8 text, `.kedo` extension. Every file starts with the header:

```kedo
kedo 1
module <module_name>
```

Then declarations. Identifiers are `[a-z_][a-z0-9_]*`; object ids may contain dots (`bridge.reinforce`). Comments: `//`, `/* */`, and `///` doc comments attached to the next declaration. Reserved words cannot be used as object or field names: `kedo module manifest abstract mixin extends with component remove on function let set if else wait apply emit stop return decision roll signal option as and or not true false` (they are fine as enum **values**).

## Declarations

```kedo
/// Doc comment.
@editor(label = "Depot sentinel")           // annotations are optional tooling metadata
abstract actor sentinel_machine {            // abstract: base only, never in the catalog
    name_key = "actor.sentinel"              // text
    tags = [machine, hostile]                // bare identifiers are enum values
    component health { max = 40  regen = 0 } // component: typed block merged by component type
}

actor depot_sentinel extends sentinel_machine with armored, fast {   // one parent (same type) + ordered mixins
    tags += [depot]                          // add to a set list
    component health { max = 60 }            // merges: only max changes
    remove component patrol                  // explicit deletion (error if no earlier layer provides it)
    component sprite { sheet = asset:sentinel_sheet }                // asset reference
    options[risk] { check = { difficulty = 14 } }                    // edit one item of a keyed list by key
    options += [{ id = distract  duration = 15min  resolution = d20  exit = arrive }]  // append new keyed items
    friend = actor:other_sentinel            // reference type:id ; cross-module: item:core/rope
}

mixin armored { component health { regen = 1 } }      // partial object, no type, applied before own fields
```

Entries inside `{ }` are separated by whitespace or `;` (several per line allowed). Values: integers (`1_500`), fixed-point decimals (`1.25`, `-0.5`; data only, never inside hooks, functions or `( )` expressions; the schema's `scale` sets the decimal places and more digits is an error, never rounding), durations (`30min`, `2h`, `3d`, `45s`; integer minutes at runtime), strings `"..."`, `true`/`false`, enum values (bare identifiers), references `type:id`, lists `[a, b]` (comma **or newline** separated), records `{ k = v }`, maps (records with free keys; a key that is not an identifier is quoted: `scene_table = { "role:bridge_west" = riverbed }`), and parenthesised constant expressions `(4 + stage * 2)`.

## Assignment operators (resolution semantics)

| Form | Scalar | Record | List |
|---|---|---|---|
| `k = v` | replace | merge field by field (maps: key by key) | depends on the list strategy: `atomic` replaces, `set` unions, `keyed` merges by key, `append` concatenates |
| `k =! v` | error | replace whole | replace whole |
| `k += [..]` | error | error | add items (set/keyed/append) |
| `k += last [..]` / `k += first [..]` | error | error | add items pinned to the end / start of the resolved list, also after descendants add more (ADR-0011) |
| `k -= [..]` | error | error | remove by value (set) or by key (keyed); error if absent |
| `k[key] { .. }` | — | — | merge into the keyed item `key` |
| `remove k.sub` | removes an inherited optional field | | |

A mixin that contributes a common trailing item (a shared `leave` option) writes `options += last [..]`; objects using it keep writing `options += [..]` and the pinned item stays last. `first`/`last` are contextual: only special between `+=` and `[`. To unpin, `-=` the item and `+=` it again. To add to an inherited `set` or `keyed` list write `+=` (with `=` it still merges but warns K3014); to replace write `=!`; to change one keyed item write `k[key] { }`. `k = { }` over an inherited record changes nothing (K3015): write `k =! { }` to clear it.

Layers apply in this order: schema defaults → resolved parent → mixins (in `with` order, flattened, first occurrence wins) → own entries. Later wins. There is no `null`, no `self`/`super`, no late binding.

## Hooks and functions (logic)

```kedo
event bridge.reinforce {
    on check_resolved(ctx) {                            // hook name comes from the game's catalog; ctx fields are typed
        if (ctx.outcome == outcome.success and ctx.option_id == "repair") {   // enums in expressions are qualified: type.value
            emit signal:structure_repaired(ctx.structure, ctx.character)     // queued signal
            wait 2d                                     // suspend; resumes after 2 days of game time
            apply effect:caravan_arrives(place:city_a)  // the only way to change the game; idempotent
        }
        stop                                            // optional early end
    }
}

function reinforce_cost(stage: int) -> int {           // pure function, callable from hooks; no recursion, no loops
    if (stage <= 1) { return 4 }
    return 4 + stage * 2
}
```

Statements end at newline or `;`. Statement forms: `let x = e`, `set x = e` (locals only; ctx is read-only), `wait <duration>`, `wait decision prompt:id { option a { ... } option b { ... } }`, `wait roll <stream>(args) as r`, `wait signal signal:id [(args)] [as s]`, `apply effect:id(args)`, `emit signal:id(args)`, `if (c) { } else if (c) { } else { }`, `stop` (hooks), `return e` (functions), and calls. Expressions: `+ - * / %`, comparisons, `and or not`, unary `-`, `ctx.field`, `receiver.query(args)` (host query), function calls. No floats, no strings concatenation at runtime, no lists/records as runtime values.

## Modules

Each module directory has `module.kedo` with `manifest { version = 1  requires = [core] }`. All files under the directory belong to the module; no per-file imports. Cross-module references use `type:module/id`.

## Verify

```
kedo check --schema=<schema dir> <paths> --json     # diagnostics with codes K1xxx (lexical/parse) K2xxx (declaration) K3xxx (resolution) K4xxx (validation) K5xxx (logic)
kedo fmt <paths>                                     # canonical formatting (only whitespace changes)
kedo resolve <paths> / kedo explain <paths> type:id field.path
```

Canonical examples: `spec/ejemplos/*.kedo` (Patriark sentinel, bridge, campaign) and the schema `spec/ejemplos/schema/patriark.kedo`.
