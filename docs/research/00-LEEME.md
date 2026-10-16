# Investigación de KEDO

| Documento | Qué contiene | Estado |
|---|---|---|
| [01 — Investigación de origen (Patriark, issue #3)](01-Investigacion-origen-Patriark.md) | Síntesis de las cinco investigaciones hechas para la base de GDO de Patriark: estado del código, siete decisiones, riesgos, spikes. **Escrita cuando la propuesta era JSON por objeto**; su D1 quedó superada por [ADR-0003](../../spec/adr/0003-json-solo-como-artefacto.md). El resto (contrato, resolver, registro, assets, addon, comportamientos) sigue vigente como base de KEDO | Histórica, con partes vigentes |
| [Anexo A — Resources `.tres` y Godot headless](anexos/A-Resources-tres-y-headless.md) | Por qué `.tres` no sirve como fuente canónica; C# en el editor de Godot; NativeAOT en iOS; Godot 4.6.x | Vigente |
| [Anexo B — Plugins de editor y undo/redo](anexos/B-Plugins-de-editor-y-undo-redo.md) | API de `EditorPlugin`, inspector, pickers, `UndoRedo`, estado sucio, addons existentes, exportación, `GraphEdit` | Vigente para el addon |
| [Anexo C — Código actual de Patriark](anexos/C-Patriark-Codigo-actual.md) | Mapa del primer cliente: pack, validador, lógica de campaña en código, assets, guardado, tests, costuras de integración | Vigente para la integración |
| [Anexo D — Patrones de datos en otros juegos](anexos/D-Patrones-de-datos-en-otros-juegos.md) | Herencia y composición en RimWorld, CDDA, Qud, Paradox, Minecraft, DF, Unity, Unreal; versionado; ids; hashing; registro AOT; diagnósticos; contrato de GDO | Vigente |
| [Anexo E — Comportamientos y scripting](anexos/E-Comportamientos-y-scripting.md) | Runtimes bajo NativeAOT, continuaciones serializables, grafos, hooks, determinismo, contrato de ejecución | Vigente |
| Anexo F — Rendimiento y arquitectura del toolchain | Lexer/parser, AST, incremental, VM, serialización, benchmarks, compatibilidad de API | En curso (15/10/2026) |
| Anexo G — Herramientas para IA | MCP, LSP, skills, diseño de lenguaje amigable para modelos, bucles con validador | En curso (15/10/2026) |

Los anexos están en inglés con cada afirmación marcada VERIFIED (con URL) o INFERRED. Los documentos de síntesis y las decisiones están en español.

## Planes heredados

[docs/plans](../plans/) contiene los planes escritos para Patriark que corresponden al núcleo o al addon: 01 (contrato y registro), 03 (addon: biblioteca de contenido), 05 (addon: undo/redo y pendientes), 06 (contratos de extensión y comportamientos). Fueron escritos bajo la hipótesis "JSON por objeto"; al crear el backlog de KEDO se refunden en el plan de lenguaje (spec v0.1 → parser → resolver → IR/VM → CLI → addon). Los planes específicos del juego (compilador al pack, assets de Godot, verificación) quedaron en Patriark.
