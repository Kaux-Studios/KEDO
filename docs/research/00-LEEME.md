# Investigación de KEDO

| Documento | Qué contiene | Estado |
|---|---|---|
| [01 — Origen y decisiones heredadas](01-Origen.md) | Requisitos del creador, decisiones D2–D8 de la investigación original (contrato, registro, composición, assets, addon, undo, comportamientos) y el trade-off JSON vs DSL que llevó a [ADR-0003](../../spec/adr/0003-json-solo-como-artefacto.md). La síntesis completa y el análisis del código del primer juego quedaron en su repositorio, que es privado | Vigente |
| [Anexo A — Resources `.tres` y Godot headless](anexos/A-Resources-tres-y-headless.md) | Por qué `.tres` no sirve como fuente canónica; C# en el editor de Godot; NativeAOT en iOS; Godot 4.6.x | Vigente |
| [Anexo B — Plugins de editor y undo/redo](anexos/B-Plugins-de-editor-y-undo-redo.md) | API de `EditorPlugin`, inspector, pickers, `UndoRedo`, estado sucio, addons existentes, exportación, `GraphEdit` | Vigente para el addon |
| [Anexo D — Patrones de datos en otros juegos](anexos/D-Patrones-de-datos-en-otros-juegos.md) | Herencia y composición en RimWorld, CDDA, Qud, Paradox, Minecraft, DF, Unity, Unreal; versionado; ids; hashing; registro AOT; diagnósticos; contrato de GDO | Vigente |
| [Anexo E — Comportamientos y scripting](anexos/E-Comportamientos-y-scripting.md) | Runtimes bajo NativeAOT, continuaciones serializables, grafos, hooks, determinismo, contrato de ejecución | Vigente |
| [Anexo F — Rendimiento y arquitectura del toolchain](anexos/F-Rendimiento-y-arquitectura-del-toolchain.md) | Lexer/parser a mano sobre spans UTF-8, AST en arena plana, incrementalidad por archivo estilo Salsa, VM de registros con instrucciones de 32 bits, binario seccionado + XxHash128, BenchmarkDotNet en CI, PublicApiAnalyzers + package validation; arquitectura v1 recomendada y presupuestos | Vigente |
| [Anexo G — Herramientas para IA](anexos/G-Herramientas-para-IA.md) | MCP (spec 2026-07-28 sin estado, SDK C# 2.2.0), CLI primero → MCP → LSP; ≤10 herramientas; skills `SKILL.md` en `.agents/skills/` + plugin Claude Code; evidencia sobre LLM y DSL (ejemplos > gramática, validar en bucle, no restringir decodificación); distribución como dotnet tool; arquitectura v1 y lista de herramientas | Vigente |
| [Anexo H — Encuesta de sintaxis](anexos/H-Encuesta-de-sintaxis.md) | 25 lenguajes de contenido y configuración (Clausewitz, Ink, Yarn, CDDA, RimWorld, Qud, DF, Factorio, TSCN, KDL, HCL, CUE, Jsonnet, Nickel, Dhall, Pkl, Starlark, TOML, YAML…): declaración, herencia, mezcla, borrado, referencias, imports, expresiones, errores conocidos; tres candidatos de sintaxis sobre el mismo ejemplo y recomendación (bloques tipados) | Vigente (issue #1) |
| [Anexo I — Semántica de herencia y mezcla](anexos/I-Semantica-de-herencia-y-mezcla.md) | CUE, Nickel, Jsonnet, Pkl, Dhall, merge patches de Kubernetes, Nix, Terraform, CSS, Unreal, Unity, C3; rendimiento, validación en dos etapas, enlace tardío a favor y en contra; 26 reglas recomendadas con ejemplo resuelto | Vigente (issue #1) |

No hay anexo C: el mapa del código de Patriark vive en ese repositorio. Los anexos están en inglés con cada afirmación marcada VERIFIED (con URL) o INFERRED. Los documentos de síntesis y las decisiones están en español.

## Planes heredados

[docs/plans](../plans/) contiene los planes escritos para Patriark que corresponden al núcleo o al addon: 01 (contrato y registro), 03 (addon: biblioteca de contenido), 05 (addon: undo/redo y pendientes), 06 (contratos de extensión y comportamientos). Fueron escritos bajo la hipótesis "JSON por objeto"; al crear el backlog de KEDO se refunden en el plan de lenguaje (spec v0.1 → parser → resolver → IR/VM → CLI → addon). Los planes específicos del juego (compilador al pack, assets de Godot, verificación) quedaron en Patriark.
