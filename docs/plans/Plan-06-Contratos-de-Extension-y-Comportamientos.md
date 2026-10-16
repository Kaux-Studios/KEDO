# GDO-Plan-06 — Contratos de extensión y comportamientos

Preparado: 15/10/2026 · Issue [#3](https://github.com/Kaux-Studios/Project-Patriark/issues/3) · Estado: **plan para revisión previa**. Base: [GDO-01](../research/01-Investigacion-origen-Patriark.md) decisiones D3, D6 (extensión) y D8; anexos [E](../research/anexos/E-Comportamientos-y-scripting.md) completo, [B](../research/anexos/B-Plugins-de-editor-y-undo-redo.md) §7, [D](../research/anexos/D-Patrones-de-datos-en-otros-juegos.md) §4, §8.

## Objetivo

Fijar los contratos que las tareas posteriores (#35 fichas, #36 lógica, #37 campañas) honrarán sin reescribir la base: cómo se registra un tipo o componente, cómo se registra una ficha o validador en el plugin, y el contrato de ejecución de comportamientos (hooks tipados, API restringida, variables con alcance, diagnóstico, continuación guardable). Este plan **no construye el intérprete** ni el editor de grafos.

## Dependencias

- Plan 01 (registro, diagnósticos, hash). Puede avanzar en paralelo con 02–05.

## Alcance

**Incluye**

1. **Contrato de extensión de tipos** (dominio): guía y plantilla "cómo añadir un tipo o componente": clase record, `[JsonSerializable]` en el contexto del módulo, entrada en `Register`, validador, actualizador de versión, esquema regenerado, golden test. Un ejemplo completo de principio a fin (tipo `note` trivial) que se borra al final o queda como ejemplo documentado.
2. **Contrato de extensión del plugin** (editor): registro `tipo → ficha .tscn`, `tipo → validador de forma extra (GDScript)`, `tipo → preview`. Documentado con el ejemplo del Plan 03.
3. **Contrato de comportamientos** (dominio, `src/domain/behaviors/`), como tipos y tests, sin VM:
   - `HookDescriptor` (id con namespace, versión, entidad raíz, campos tipados, `AllowsAwait`, `AllowsEffects`) y `HookContext`.
   - Modelo `Value`: `Int(long)`, `Bool`, `Text`, `EntityId(kind,id)`, `Enum`, `List`, `Record`. Sin float.
   - `IBehaviorHost`: `NowMinutes`, `Query`, `Apply(EffectKey, …)`, `Roll(EffectKey, streamId, spec)`, `Raise`, `Trace`.
   - `EffectKey(InstanceId, SiteLabel, Sequence)` e idempotencia.
   - `AwaitRequest` (`Minutes`, `Decision`, `Roll`, `Signal`) y `Resumption`.
   - `BehaviorFrame` (formato versionado, ids de definición y hash, pila de `CallFrame` con `ResumeLabel`, variables de instancia, pendiente, efectos aplicados, tiradas registradas, pasos usados).
   - `Budget`, `RunResult`, `BehaviorDiagnostic`, `SourceRef`, `TraceEvent`.
   - `BehaviorDefinition` con `IrProgram` **opaco** (placeholder serializable: lista de instrucciones como records genéricos `(label, opcode, operands)`), `Migrations`, `StalePolicy`.
   - Catálogo inicial de hooks **vacío salvo tres descriptores de ejemplo** marcados "propuesta" (`on_arrive_node`, `on_check_resolved`, `on_structure_repaired`), derivados de puntos que ya existen en `GameRules`.
4. Tests: serialización ida y vuelta de `BehaviorFrame` con el contexto source-generated; estabilidad de `EffectKey`; orden total de hooks; política `StalePolicy` sobre un frame con hash distinto (decisión, sin ejecutar nada).
5. **Evaluación de `GraphEdit` documentada** (anexo B §7): modelo de datos primero, GraphEdit como vista, undo propio, limitaciones abiertas (#117734, #123550). Sin código.
6. Documento `doc/` (sección en `CONTENT-03-GDO.md`, consolidado en el Plan 07) con los contratos, lo que #36 decide y lo que queda prohibido (delegates en guardados, reloj real, azar propio, I/O, floats).

**Excluye**

- VM/intérprete, IR definitivo, gramática de script, formato de grafo, editor de grafos, catálogo completo de hooks, integración con `GameRules` (todo #36). Lógica de objetivos y diálogos (#27).

## Diseño

### Por qué fijar esto ahora

Todas las alternativas de runtime evaluadas (MoonSharp, Lua-CSharp, Jint, NLua, Roslyn, GDScript en runtime, `Expression`) carecen de continuación serializable bajo NativeAOT o viven fuera del dominio. La única vía compatible con FUND (una decisión aplica una vez, determinismo, guardado como snapshot) es un IR propio con frames como datos. Fijar el **contrato** evita que #35 o #37 diseñen fichas o campañas que dependan de callbacks vivos.

### Invariantes del contrato

1. Un comportamiento corre dentro de la aplicación del comando que disparó el hook; sus efectos quedan en el mismo snapshot atómico.
2. Un `EffectKey` aplicado nunca se reaplica, ni al reanudar ni tras migrar.
3. El azar sale de un stream `Pcg32` nombrado y se registra en el frame; el reloj es `TimeMinutes` en el momento de activación; esperar es programar `dueAt`, no dormir.
4. Señales se encolan y despachan tras el hook actual; profundidad máxima por comando (propuesta 64); exceder = diagnóstico del comportamiento, nunca crash del comando.
5. Presupuesto de pasos en el bucle de despacho (propuesta 10 000 por activación); agotarlo = `Failed` con traza.
6. Etiquetas estables para sitios de espera; un frame referencia etiquetas, nunca offsets.

### Relación con lo que existe

`CheckAttempt`, `CommandReceipt` y `EncounterInstance` ya practican idempotencia por id y versión de plantilla. El contrato extiende ese patrón; no lo reemplaza.

## Pasos

1. Guía y plantilla de extensión de tipos con el ejemplo `note` (medio día).
2. Contrato de extensión del plugin documentado sobre el ejemplo del Plan 03.
3. Tipos de comportamientos + contexto JSON + tests de ida y vuelta.
4. Tres descriptores de ejemplo y test de orden total.
5. Nota de evaluación de `GraphEdit`.
6. Sección de documento con "fijado por #3" / "decide #36" / "prohibido".

## Verificación

- `dotnet test` verde; sin advertencias AOT en `src/domain/behaviors`.
- Añadir el tipo `note` siguiendo la guía toma menos de una hora a alguien que no escribió la base (prueba con un segundo colaborador o agente).
- Revisión cruzada: #36 puede escribir su plan de investigación citando solo este contrato.

## Criterios de cierre

- [ ] Guía de extensión de tipos y de plugin con ejemplos.
- [ ] Tipos del contrato de comportamientos con tests de serialización y de política.
- [ ] Descriptores de ejemplo marcados como propuesta.
- [ ] Documento con límites claros entre #3 y #36.

## Riesgos

- Fijar demasiado y atar a #36: el IR queda opaco a propósito; solo se fijan superficie del host, valores, frame, espera y versionado.
- Fijar demasiado poco: los invariantes 1–6 son el mínimo para que un guardado con comportamientos suspendidos sea válido.
- Que el modelo entero sin float incomode a los autores: es deliberado (anexo E §5, diferencias de `libm` entre x64 y arm64); porcentajes en puntos básicos.

## Preguntas para la revisión previa

1. ¿Se acepta "sin float" en el modelo de valores?
2. ¿Los tres hooks de ejemplo deben existir en el código como descriptores o solo en el documento? Propuesta: en código, marcados como propuesta, sin suscriptores.
3. ¿`StalePolicy` por definición (propuesto) o global por campaña?
