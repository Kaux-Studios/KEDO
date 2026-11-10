# Invariantes del esquema y campos calculados — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Preparado: 10/11/2026 · Extensión K2 pedida por survivors-like · Estado: **implementado** (ADR-0016 aceptado el 11/11/2026, issue #22), salvo la tarea 8 (validadores en C#), que el ADR deja fuera por ahora (D11). Las expresiones van sin comillas (ADR-0016 D3): donde este plan dice "texto" o "dentro del literal", la implementación usa `RExpression` y posiciones en el archivo del esquema.

**Goal:** Implementar K4008 (invariantes declaradas en el esquema) y R15 (campos calculados, K3012) de KEDO-02 con un lenguaje de expresiones de esquema puro y exacto, sin cambiar el catálogo, los hashes ni el pack de un esquema que no los use.

**Architecture:** El cargador del esquema parsea las expresiones (texto) y las compila a un árbol tipado (`SchemaExpression`), comprobando rutas y clases contra el registro (K2013) y ordenando los calculados (K3012). El resolver gana una pasada `ComputeFields` entre la resolución y la etapa B; la etapa B evalúa las invariantes de cada objeto concreto (K4008). Las desreferencias se registran como dependencias para la resolución incremental. Todo en `Kedo.Compiler`; `Kedo.Core` sólo gana los descriptores (`FieldSchema.Computed`, `InvariantSchema`).

**Tech Stack:** C# 12 / .NET 8 (`global.json`), xUnit 2.9, BenchmarkDotNet; sin paquetes nuevos.

**Spec:** [ADR-0016](../../spec/adr/0016-invariantes-y-campos-calculados.md), sobre [KEDO-02 §5, §8, §11](../../spec/KEDO-02-Resolucion.md), [ADR-0005](../../spec/adr/0005-sin-enlace-tardio.md), [ADR-0008](../../spec/adr/0008-decimales-de-punto-fijo.md) y [ADR-0010](../../spec/adr/0010-diagnosticos-de-mezcla-y-orden-de-declaracion.md).

## Global Constraints

- 1.x es aditivo (ADR-0001): un esquema sin `invariants` ni `computed` resuelve igual, con los mismos hashes y el mismo pack byte a byte; ningún archivo esperado de `tests/corpus` cambia.
- Diagnósticos con código estable, posición exacta (dentro del literal de la expresión cuando el problema está ahí) y sugerencia; nunca excepciones por contenido; sin cascada (ADR-0010).
- Cero reflexión, AOT, sin advertencias; API pública nueva en `PublicAPI.Unshipped.txt` con `tools/public-api.ps1`.
- Al tocar el resolver: benchmarks completos y `tools/bench-gate.ps1`; puertas nuevas en `baseline.json`.
- Documentación en español; código y mensajes en inglés.

## Review Focus

1. **Un calculado que lee otro objeto que tiene calculados** (el árbol lee el ruleset): el orden entre tipos y la incrementalidad (editar el ruleset recalcula y revalida a sus lectores).
2. **Decimales:** `sum` de shares con escalas distintas da el valor exacto; multiplicar un decimal es K2013 al cargar, no en cada objeto.
3. **Un error de resolución en un objeto leído por una invariante:** una sola vez, sin K4008 en cascada.
4. **Objetos abstractos:** ni calculados ni invariantes (como la etapa B); los concretos los heredan del tipo, no del padre.
5. **Variantes (ADR-0012):** cada vista de variante calcula y valida sobre sus propios objetos.

## Estructura de archivos

| Archivo | Responsabilidad |
|---|---|
| `src/Kedo.Core/Schema/Schema.cs` | `FieldSchema.Computed` (texto), `InvariantSchema(id, check, message, severity)`, `RecordSchema.Invariants`, `TypeSchema` vía su `Body` |
| `src/Kedo.Compiler/Schema/MetaSchema.cs` | `computed` en `kedo_field`; `invariants` (keyed por `id`) en `kedo_type`, `kedo_component`, `kedo_record` |
| `src/Kedo.Compiler/Schema/SchemaLoader.cs` | Leer y compilar las expresiones; K2013; orden de calculados y K3012 |
| `src/Kedo.Compiler/Schema/SchemaExpression.cs` (nuevo) | Árbol tipado, comprobador y evaluador exacto (int, decimal, duración, bool, símbolos) |
| `src/Kedo.Compiler/Resolution/Resolver.cs` | Pasada `ComputeFields` (antes de la etapa B y del hash); K4015; invariantes en la etapa B (K4008, K4016) |
| `src/Kedo.Compiler/Resolution/IncrementalCatalog.cs` | Dependencias por desreferencia |
| `src/Kedo.Compiler/Resolution/ResolvedValue.cs` | `LayerKind.Computed` para la procedencia |
| `src/Kedo.Compiler/Resolution/Validators.cs` (nuevo) | `IObjectValidator`, `ValidatorSet`, `ValidationSink` (D7) |
| `src/Kedo.Compiler/Editing/ObjectDescriber.cs`, `Schema/JsonSchemaWriter.cs`, `src/Kedo.Cli/McpServer.cs` | `computed`, `x-kedo.invariants`, catálogo (D8) |
| `tests/Kedo.Tests/InvariantTests.cs` (nuevo) | Unidad: lenguaje, evaluador, pasada, incrementalidad |
| `tests/corpus/invariants-*`, `computed-*` (nuevos) | Golden con `schema/` + `content/` (mismo formato que `host-api-*` de ADR-0015, o `module/` con esquema) |

---

### Task 1: ADR, issue y códigos

- [x] Confirmar libres K2013, K4015, K4016 (`Select-String -Path src/**/*.cs -Pattern '"K2013"|"K4015"|"K4016"'`) y que K4008/K3012 no se emiten.
- [x] Revisar ADR-0016 con el creador: preguntas 1–3. Pasar a "aceptado" o ajustar antes de seguir.
- [x] Commit `ADR-0016: schema invariants and computed fields (proposed)`.

### Task 2: Pines de compatibilidad

Antes de tocar código, tests que fijen hoy: `Resolve` de `spec/ejemplos` con su esquema y de `samples/outpost` da los mismos hashes de catálogo y por objeto; el pack de ambos es igual byte a byte; `kedo schema --json` del esquema de Patriark no cambia. Esperado: pasan antes y después de cada tarea.

### Task 3: Descriptores y metaesquema

- [x] `FieldSchema.Computed` (string?), `InvariantSchema`, `RecordSchema.Invariants` (lista ordenada).
- [x] `kedo_field.computed` (text); `invariants` keyed por `id` con `check` (text, required), `message` (text), `severity` (enum `error|warning`, default `error`).
- [x] Test: un esquema con `invariants` y `computed` carga sin K2005; uno sin ellos produce el mismo registro que hoy.

### Task 4: Lenguaje de expresiones de esquema

- [x] Parsear el texto con el parser de expresiones existente (`Parser` sobre un `SourceText` del literal, con desplazamiento para las posiciones).
- [x] Comprobador contra el registro: rutas (`a.b`, `root.`, `it`, desreferencia por campos `reference` con `target`, `tipo:id.campo`), agregados de D1 con su aridad y clases, aritmética decimal sólo con `+`, `-` y comparaciones; K2013 con posición dentro del literal y sugerencia para campos (Levenshtein, como K2005).
- [x] Evaluador sobre `ResolvedValue` sin asignar por elemento: `long` con `checked` (desborde → K4016), decimales como `(units, scale)` llevados a la mayor escala, duraciones en la unidad base del catálogo (ADR-0014).
- [x] Tests por agregado (`sum`, `count`, `all`, `any`, `unique`, `increasing`, `min`, `max`, `has`, `len`, `subsequence`), por clase y por error.

### Task 5: Campos calculados

- [x] Grafo de dependencias entre calculados (por tipo y entre tipos a través de referencias); K3012 con la ruta `a → b → a` al cargar el esquema.
- [x] Pasada `ComputeFields` en `Resolver.Resolve` y en `IncrementalCatalog`, después de resolver y antes de `ValidateStageB` y `CatalogHasher.Hash`; sólo objetos concretos; procedencia `computed`.
- [x] K4015 cuando el contenido escribe un calculado (en la etapa A, con la ubicación del campo).
- [x] Tests: valor, orden entre tipos, hash que cambia sólo si cambia un dato del que depende, pack con el campo.

### Task 6: Invariantes en la etapa B

- [x] Evaluar las invariantes del tipo, de cada componente y de cada record anidado (en su ruta) para cada objeto concreto; K4008 con `id`, mensaje con `{expr}` resueltos y etiquetas a la procedencia de los campos leídos; agrupación R25.
- [x] Sin cascada: un objeto leído con errores de resolución da una sola K4016 y ninguna K4008.
- [x] Tests con los seis ejemplos del juego (tabla del ADR): reparto que suma 100, banda 30–40, subsecuencia de sectores, capacidad por sub-rama con `sum(nodes, cost)` agrupado por `count(…)`, relación con el ruleset, curva creciente, prohibición.

### Task 7: Incrementalidad

- [x] Registrar cada desreferencia (`tipo:id` leído) en `References`.
- [x] Test: editar el ruleset revalida y recalcula a sus lectores; cada `Update` da lo mismo que una resolución completa (patrón de `IncrementalTests`).

### Task 8: Validadores en C# (D7)

- [ ] (fuera por ahora, ADR-0016 D11) `IObjectValidator`, `ValidatorSet`, `ValidationSink` y la sobrecarga de `Resolver.Resolve` / `IncrementalCatalog`.
- [ ] (fuera por ahora, ADR-0016 D11) Test: un validador por tipo informa K4008 con su `id`; sin `ValidatorSet` nada cambia.
- [ ] (fuera por ahora, ADR-0016 D11) Documentar el límite (no lo ven la CLI, el MCP ni el addon).

### Task 9: Herramientas

- [x] `kedo describe`: `computed: true` y la expresión; addon: campo deshabilitado (GDScript lee `computed`).
- [x] `kedo schema --json`: `x-kedo.computed`, `x-kedo.invariants` sólo si hay.
- [x] `kedo explain`: capa `computed` con la expresión.
- [x] MCP `kedo_catalog kind=types` muestra calculados e invariantes.

### Task 10: Golden

- [x] `tests/corpus/invariants-payouts` (suma, banda, mensajes con valores), `invariants-order` (unicidad, subsecuencia, creciente), `invariants-cross-object` (ruleset), `computed-fields` (valores, orden, hash), `computed-cycle` (K3012), `computed-written` (K4015), `invariants-errors` (K2013 con posiciones dentro del literal).

### Task 11: Rendimiento

- [x] Benchmark `ResolveWithInvariants|10000` con un esquema típico (dos calculados y tres invariantes por tipo); puerta en `baseline.json`; presupuesto ≤ 115 % de `Resolve|10000`.
- [x] `Resolve|10000` sin invariantes: mismas asignaciones (puerta existente).

### Task 12: Spec, docs y skills

- [x] KEDO-02 R15 y R24 pasan de "diseño" a implementado, con referencia al ADR; tabla §11 con K2013, K4015, K4016.
- [x] Skills `kedo-syntax` y `kedo-author-content` (mirror en `.claude/skills`): sintaxis de `invariants` y `computed`, y qué hacer ante K4008.
- [x] Verificación final: build sin advertencias, tests, benchmarks y puerta; `git status --porcelain tests/corpus` sólo muestra casos nuevos.
