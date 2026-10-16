---
name: kedo-author-content
description: Workflow for creating or changing KEDO game content safely with the kedo CLI or the kedo MCP tools - learn the catalog first, write, validate with kedo check, fix diagnostics, format, inspect the resolved object. Use when asked to add, modify, port or review game content objects (actors, events, items, campaigns, mixins) written in .kedo.
---

# Authoring KEDO content

Loop: **look up → write → check → fix → format → verify resolution**. Never invent type names, field names, enum values or ids: the schema is closed and `kedo check` rejects unknown names (K2001, K2005 with a did-you-mean, K4003).

1. **Look up what exists.** With MCP: `kedo_catalog` with `kind` = `types` (fields and kinds), `enums`, `components`, `hooks`, `objects` (existing ids and chains), `mixins`. With the CLI: `kedo schema <schema dir> --json` and `kedo resolve <paths>`. Reuse an existing object as `extends` base or a mixin before writing fields from scratch.
2. **Write the file** following the `kedo-syntax` skill. One object per concern; put shared parts in `abstract` objects or `mixin`s; keep ids stable (they are referenced everywhere and stored in saves).
3. **Check.** `kedo check --schema=<schema> <paths> --json` (or `kedo_check`). Read every diagnostic: `code`, `line:column`, `message`, and `related` locations. Fix **errors** until `ok` is true; treat **warnings** (K3010 mixin shadowing, K3011 atomic list replaced without `=!`) as things to make explicit.
4. **Format.** `kedo fmt <paths>` (or `kedo_format` and write the returned text). Formatting changes only whitespace; commit formatted files.
5. **Verify the effective object.** `kedo resolve` / `kedo_resolve type:id` shows the object after defaults, parent, mixins and own fields, with the inheritance chain and hash. `kedo explain <paths> type:id field.path` (or `kedo_explain`) answers "where did this value come from" and lists shadowed candidates.
6. **Logic.** If the object has hooks, `kedo compile` (or `kedo_compile`) shows the IR and reports unknown context fields (K5001), recursion (K5002), waits or effects not allowed by the hook (K5004/K5005).

Rules of thumb:

- To change one item of a list of records, use `options[risk] { ... }`; to add items, `+=`; to remove, `-=`; to replace everything, `=!`. Plain `=` on a keyed list merges by key, on a set unions, on an atomic list replaces (warning).
- Deleting is explicit: `remove component x`, `remove field.sub`, `-= [key]`. Removing something that no earlier layer provides is an error, not a no-op.
- Durations are literals with units (`30min`); plain integers are accepted for duration fields and mean the base unit (minutes).
- Enum values are bare in bodies (`context = travel`) and qualified in expressions (`outcome.success`).
- Keep hooks small and declarative; only `apply` changes the game; `wait` suspends the hook and the game saves its state.

Diagnostic codes are documented in `spec/KEDO-01-Gramatica.md` §12 and `spec/KEDO-02-Resolucion.md` §11.
