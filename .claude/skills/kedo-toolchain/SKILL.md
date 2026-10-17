---
name: kedo-toolchain
description: How the KEDO toolchain repository is organised and how to extend it - adding a syntax form, a diagnostic code, a resolver rule, a schema field kind, an opcode or a CLI/MCP tool, with the tests and documents that must change together. Use when changing C# code under src/Kedo.Core, src/Kedo.Compiler or src/Kedo.Cli, or the golden corpus under tests/corpus.
---

# Working on the KEDO toolchain

Read `AGENTS.md`, `spec/KEDO-00-Principios-y-Alcance.md` and the ADRs in `spec/adr/` first. Priorities: performance > ease of use > scalability > flexibility. 1.x is additive: never remove or change the meaning of syntax, AST kinds, IR opcodes, frame fields, diagnostic codes or public API; mark obsolete instead.

## Map

| Area | Where | Tests |
|---|---|---|
| Lexer, parser, syntax tree, formatter | `src/Kedo.Compiler/Syntax/*`, `Formatting/Formatter.cs` | `tests/Kedo.Tests/LexerTests.cs`, `ParserTests.cs`, golden corpus `tests/corpus/<case>/{input.kedo, tree.txt, diagnostics.txt, formatted.kedo, resolved.txt}` |
| Declarations model | `src/Kedo.Compiler/Model/*` | via resolver tests |
| Resolver (layers, merge, deletion, provenance, validation, hash) | `src/Kedo.Compiler/Resolution/*` | `ResolverTests.cs`, corpus `resolved.txt`, `examples-module/` |
| Schema registry and schema-as-data | `src/Kedo.Core/Schema/Schema.cs`, `src/Kedo.Compiler/Schema/*` | `SchemaTests.cs`, `spec/ejemplos/schema/patriark.kedo` |
| IR, VM, frames, host contract | `src/Kedo.Core/Runtime/*` | `BehaviorTests.cs` |
| Behaviour compiler | `src/Kedo.Compiler/Behaviors/BehaviorCompiler.cs` | `BehaviorTests.cs` |
| KEDO Pack binary | `src/Kedo.Core/Pack/*`, `src/Kedo.Compiler/Pack/PackWriter.cs` | `PackTests.cs` |
| CLI and MCP server | `src/Kedo.Cli/Program.cs`, `McpServer.cs` | manual probes; keep both over the same core |
| Benchmarks | `tests/Kedo.Benchmarks` | `dotnet run -c Release --project tests/Kedo.Benchmarks -- --filter '*' --job short` |

## Recipes

- **New syntax form**: add tokens in `Token.cs` and `Lexer.cs` (keyword table), a `NodeKind`, parser rule in `Parser.cs` (one token of lookahead; keep `FIRST` sets disjoint), formatter spacing rule if needed, `DeclarationBuilder` mapping, a golden corpus case, and the EBNF in `spec/KEDO-01-Gramatica.md`.
- **New diagnostic**: pick the next free code in its range (K1 lexical/parse, K2 declaration, K3 resolution, K4 validation, K5 logic), message lowercase without final period with identifiers in backticks and a hint, a test that triggers it and one that does not, and the row in the spec table.
- **New resolver rule**: implement in `Resolver.cs` (`ApplyField`, `MergeRecord`, list helpers), add a case to `ResolverTests` and, if it changes output, regenerate golden files with `KEDO_UPDATE_GOLDEN=1 dotnet test` and review the diff.
- **New schema field kind or list strategy**: `ValueKind`/`ListStrategy` in `Schema.cs`, `MetaSchema.cs` (enum values), `SchemaLoader.cs`, `JsonSchemaWriter.cs`, resolver `Lower`/`Check`, and the example schema.
- **New opcode**: `OpCode` + `Instr` encoding in `Runtime/Program.cs`, verifier and dispatch in `BehaviorVm.cs`, `Disassembler.cs`, emission in `BehaviorCompiler.cs`, a `BehaviorTests` case with suspend/resume if it touches frames. Keep zero allocations per instruction.
- **Public API**: every public member is declared in `src/<project>/PublicAPI.Unshipped.txt`; a new or removed one fails the build (RS0016/RS0017). Run `powershell -File tools/public-api.ps1` to write the lines the analyzer computes, and review the diff.
- **Structural edits and forms**: `kedo describe <paths> <type:id> --json` lists the fields of an object (schema order, value literal, layer); `kedo edit <file> set|unset|new|duplicate ...` changes one declaration and reprints the file canonically, refusing anything that would not parse (`--value-file=` passes a literal untouched). `Kedo.Compiler.Editing` holds both; the addon's form and the MCP tool `kedo_describe` use them.
- **Agent evaluation**: `kedo eval eval/tasks.json --reference` checks the 30-task suite (each reference solution passes; EvalTests also checks that no task passes untouched); `--agent="<command>"` with `--skills=` and/or `--mcp` measures an agent in the four configurations (docs/eval/README.md). Add a task with a solution and mechanical checks whenever a new language feature lands.
- **Importing existing JSON content**: `kedo import-json <file.json> --out=<dir> --module=<m> --map=coll:type,...` drafts `.kedo` files and prints a `--enums=` suggestion for string fields whose values are repeated identifiers; rerun with the fields that really are enumerations.
- **New CLI command / MCP tool**: implement over the core (no logic in the adapter), add `--json` output, update `PrintUsage`, add the tool with a stable `kedo_` name and a when-to-use description (never rename tools; add `_v2`).

## Verify before claiming done

```
dotnet build Kedo.sln -c Release      # zero warnings allowed (TreatWarningsAsErrors)
dotnet test tests/Kedo.Tests/Kedo.Tests.csproj
dotnet run --project src/Kedo.Cli -- check --schema=spec/ejemplos/schema spec/ejemplos
dotnet run --project src/Kedo.Cli -- fmt spec/ejemplos --check
```

Commit messages describe what changed and what was verified; docs and spec change in the same commit as the code.
