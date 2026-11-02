# Unidad base de las duraciones (`base_unit`) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Preparado: 01/11/2026 · Extensión K1 pedida por un juego de acción (segundo consumidor de KEDO) · Estado: **plan para revisión**.

**Goal:** Implementar `manifest { base_unit = ms | s | min | h | d }` para que las duraciones se guarden, evalúen, validen, empaqueten y esperen en la unidad base del contenido, sin cambiar el significado ni los bytes del contenido en minutos (Patriark).

**Architecture:** La unidad se decide una vez por resolución a partir de los manifiestos (por defecto `min`) y viaja como dato: `ResolvedCatalog.TimeUnit`, `ResolvedObject.TimeUnit`, `RDuration.Unit`, `BehaviorProgram.TimeUnit`, `AwaitRequest.Unit` y una sección nueva del pack. Todo valor nuevo vale `Minute` por defecto (`DurationUnit.Minute = 0`), de modo que cada camino de 1.0 queda bit a bit igual. La VM pide la hora en la unidad del programa con una interfaz opcional nueva (`IBehaviorClock`), sin tocar `IBehaviorHost`.

**Tech Stack:** C# 12 / .NET 8 (`global.json`), xUnit 2.9, BenchmarkDotNet, `Microsoft.CodeAnalysis.PublicApiAnalyzers` 3.3.4, `System.Text.Json` source-generated.

**Spec:** este plan, sección **Decisiones** (la Tarea 1 la convierte en `spec/adr/0014-unidad-base-de-duraciones.md`), sobre [KEDO-01 §2, §10](../../spec/KEDO-01-Gramatica.md), [KEDO-02 §8, §11](../../spec/KEDO-02-Resolucion.md), [KEDO-03](../../spec/KEDO-03-API-Publica-y-Paquetes.md), [ADR-0001](../../spec/adr/0001-versionado-semver.md) y el precedente [ADR-0008](../../spec/adr/0008-decimales-de-punto-fijo.md).

## Global Constraints

- 1.x es aditivo (ADR-0001): no quitar ni cambiar significado de sintaxis, AST, IR, frames, códigos de diagnóstico ni API pública; marcar obsoleto, no borrar.
- Un archivo `.kedo` válido en 1.0 sigue siendo válido e idéntico en significado; el contenido en minutos produce el mismo catálogo, los mismos hashes y el mismo pack byte a byte.
- Golden corpus: ningún archivo esperado existente de `tests/corpus` cambia (`git status --porcelain tests/corpus` sólo muestra casos nuevos). Por eso el mensaje actual de K1002 (`durations use `s`, `min`, `h` or `d``) **no se toca**.
- Toda capacidad entra con caso en la spec, golden test (`input.kedo` → `tree.txt`, `diagnostics.txt`, `formatted.kedo`, `resolved.txt`) y traza esperada de la VM si ejecuta.
- Diagnósticos con código estable `KEDOxxxx`, posición exacta y sugerencia; mensajes en minúscula, sin punto final, identificadores entre comillas invertidas (ADR-0007 D11). Nunca excepciones por errores de contenido.
- `Kedo.Core` y `Kedo.Compiler` sin paquetes externos nuevos, cero reflexión, `IsAotCompatible`; `dotnet build Kedo.sln -c Release` sin advertencias.
- Todo miembro público nuevo va a `PublicAPI.Unshipped.txt` (RS0016/RS0017 son errores); las líneas las escribe `powershell -File tools/public-api.ps1`.
- La CLI expone lo nuevo con `--json`; el servidor MCP usa el mismo núcleo.
- Al tocar lexer, resolver, pack o VM: `dotnet run -c Release --project tests/Kedo.Benchmarks -- --filter '*' --job short` y `powershell -File tools/bench-gate.ps1` sin regresiones.
- Documentación en español; código, identificadores, mensajes de diagnóstico y nombres de archivo en inglés. Sin bitácoras fechadas en los documentos.
- Contenido de Patriark (`C:\dev\Project-Patriark`): sólo lectura; nunca se copia al repositorio de KEDO (ADR-0002, enmienda).
- Trabajar en una rama (`feat/base-unit`) o worktree, nunca en `main`.

## Review Focus

1. **Un mixin con `30s` aplicado a muchos objetos de un módulo en `min`**: se espera un solo K4014 por literal, no uno por objeto (como K3014, ADR-0010). Test en la Tarea 6.
2. **Editar el manifiesto en una sesión incremental** (addon, MCP, `IncrementalCatalog`): cambiar `base_unit` debe resolver todo de nuevo y dar lo mismo que una resolución completa. Test en la Tarea 5.
3. **Un frame guardado por 1.0** (JSON sin `unit`) se reanuda como minutos; un frame en minutos escrito ahora no lleva `unit` (mismos bytes). Test en la Tarea 9.
4. **Literales enormes** (`106751991167301d` en `ms`, `9223372036854775807d` en `min`): K4014 error, nunca excepción ni desborde silencioso. Test en la Tarea 6.
5. **Un archivo `variant` con su propio manifiesto en otra unidad**: K2012; todas las vistas de variante comparten la unidad de la base. Test en la Tarea 5.

## Decisiones

Verificado en el código: `Lexer.cs` acepta `s|min|h|d` (L218-240); `DurationValue.Minutes` hace `Amount / 60` (Declarations.cs L55), así que `30s` vale 0 y `90s` vale 1; `ConstEvaluator` suma `.Minutes` (L146-162); `RDuration(Provenance, long Minutes)` (ResolvedValue.cs L93); el hash escribe `"d"` + minutos (CatalogHasher.cs L79); el pack escribe `d.Minutes` (PackWriter.cs L145); `SchemaLoader` toma `DefaultInt` de `dd.Minutes` (L248) e ignora `min = 10min` (sólo lee `RInt`); `BehaviorCompiler` repite la conversión (L536-544); la VM hace `_host.NowMinutes + regs[a]` (BehaviorVm.cs L453-457). El manifiesto se parsea (`FileDeclarations.Manifest`) pero el resolver nunca lo lee. El resolver es plano: los objetos se indexan por `tipo:id` sin módulo y una referencia `tipo:modulo/id` se busca como `tipo:id` (Resolver.cs L1498).

Contenido de Patriark revisado (`content/kedo`, 18 archivos): `module.kedo` declara `base_unit = min`; el único literal de duración es `wait 1d` (test_1.kedo L41, exacto: 1440); el esquema usa `kind = int` (`duration_minutes`) y no tiene campos `kind = duration`. `samples/outpost` usa `min`, `h` y enteros. Ningún consumidor conocido escribe segundos.

- **D1. Unidades:** `ms`, `s`, `min`, `h`, `d`, con `DurationUnit { Minute = 0, Millisecond = 1, Second = 2, Hour = 3, Day = 4 }` en `Kedo.Core` (`Kedo.Schema`). `Minute = 0` hace que todo valor por defecto (records, JSON, pack) sea el de 1.0. **Se agrega el sufijo `ms`**: hoy `30ms` es K1002, así que aceptarlo es aditivo, y un juego de acción necesita telegrafiados de 350 ms que no son segundos enteros. Sin `us`/`ns`: no hay caso y una cuenta de 64 bits en ms cubre 292 millones de años.
- **D2. `tick` no entra en KEDO.** La frecuencia del paso fijo es del host (60 Hz hoy en el juego de acción) y puede cambiar; un dato en ticks cambia de significado al cambiarla, y 1 tick a 60 Hz (16,67 ms) no es un entero de ms, así que tampoco hay una conversión exacta que KEDO pueda hacer. Factorio y RimWorld guardan ticks en sus datos y por eso cambiar la frecuencia es migrar contenido. Decisión: el contenido se escribe en tiempo simulado (`ms`), el host lo cuantiza a ticks (con el redondeo que elija) y su `IBehaviorClock.Now(Millisecond)` devuelve `tick * 1000 / hz`. Quien necesite ticks exactos como dato usa `kind = int` con nombre `*_ticks` en su esquema, sin núcleo nuevo.
- **D3. Una unidad por resolución.** La unidad sale de los manifiestos de todos los archivos que se resuelven juntos (base y variantes). Sin `base_unit`: `min`. Un valor que no es una unidad (`base_unit = minutes`, `"s"`, `5`) es **K2011, advertencia**, y se queda en `min`: en 1.0 ese manifiesto era válido y significaba minutos, así que la advertencia conserva su significado. Dos manifiestos con unidades válidas distintas (módulos distintos, o una variante) es **K2012, error**, con la etiqueta del primero; la resolución sigue en `min` sin K4014 (sin cascada, ADR-0010). Razón de no convertir entre módulos: el resolver es plano, el pack y el reloj del host tienen una sola unidad, y convertir valores heredados entre módulos con unidades distintas exigiría una unidad por objeto y por programa. Si un juego lo necesita, se agrega en un minor (aditivo: hoy es error).
- **D4. Literales que no son enteros en la unidad base: código nuevo K4014**, no K1002. K1002 es léxico (el token es inválido); `250ms` es un token válido cuyo problema depende del manifiesto, y se detecta al bajar el valor, como K4009 para los decimales. Regla **por literal** (también dentro de `( )` y de hooks): cada literal se convierte a la unidad base antes de operar, que es lo que 1.0 hacía con `a.Minutes + b.Minutes`. Severidad: con unidad base `min` es **advertencia y el valor se trunca hacia cero**, exactamente como 1.0 (`30s` sigue valiendo `0min`, `90s` `1min`); con cualquier otra unidad (contenido nuevo, que 1.0 no podía tener) es **error** y el valor no entra. Un literal que no cabe en 64 bits en la unidad base es K4014 error en toda unidad (1.0 desbordaba en silencio; sólo afecta literales absurdos). En 2.0 K4014 pasa a error también en `min`. Patriark no recibe ninguna advertencia nueva.
- **D5. Enteros** en campos `duration`, en `wait` y en los bornes enteros del esquema significan unidades base, como los minutos de 1.0.
- **D6. Representación sin romper la API.** `RDuration(Provenance, long Minutes)` queda; `Minutes` pasa a documentarse como "cantidad en `Unit`" y se agregan `Unit` (init, por defecto `Minute`) y `Amount` (alias de lectura). `CanonicalText` usa el sufijo de la unidad (`30min` igual que antes). El hash escribe `"d"` + i64 para `Minute` (igual que 1.0) y `"D"` + u8 unidad + i64 para las demás. `DurationValue.Minutes` queda y gana el caso `ms`. Renombrar o quitar `Minutes` exigiría `*REMOVED*`, prohibido desde 1.0.0 (KEDO-03).
- **D7. Esquema.** El esquema del juego se carga independiente del contenido, así que sus literales de duración se resuelven en **ms** (exactos para cualquier sufijo) y se guardan en `FieldSchema.MinDurationMs`, `MaxDurationMs` y `DefaultDurationMs`. `DefaultInt` sigue con el valor de 1.0 (minutos truncados) para los consumidores de la API. La validación compara en ms con `Int128` (sin desborde); el default se convierte a la unidad base con la regla de D4 y su K4014 se informa una vez por campo, sin archivo (el esquema no tiene posición). Los bornes enteros siguen en unidades base. Cambio consciente: 1.0 ignoraba en silencio `min = 10min`; ahora se cumple (ningún consumidor conocido tiene campos `kind = duration` con bornes de duración).
- **D8. Pack.** Un catálogo en `min` se escribe en **formato 2, igual byte a byte** (salvo la cadena de versión del toolchain de la cabecera). Otra unidad escribe **formato 3** con una sección nueva `SectionTimeUnit = 4` (un byte con la unidad) después de los programas. El lector acepta 1 a 3; un formato 3 sin esa sección o con un byte desconocido es `InvalidDataException`. Un runtime 1.0 rechaza el formato 3 (`format > 2`) y nunca lee segundos como minutos; es el mismo mecanismo de ADR-0008. `KedoPack.FormatVersion` **sigue siendo 2**: es `const` y figura en `PublicAPI.Shipped.txt` como `const Kedo.Pack.KedoPack.FormatVersion = 2 -> int`; cambiar su valor sería quitar esa línea. Se agrega `LatestFormatVersion = 3`. `PackTag.Duration` y `PackInt` no cambian: la cantidad está en `KedoPack.TimeUnit`.
- **D9. VM.** `IBehaviorHost` no cambia. Interfaz opcional nueva `IBehaviorClock { long Now(DurationUnit unit); }`: si el host la implementa, `wait` usa `Now(program.TimeUnit)`; si no, convierte `NowMinutes` (exacto hacia unidades más finas, truncado hacia `h`/`d`). `AwaitRequest` gana `Unit` (omitido en JSON cuando es `Minute`, así que los frames en minutos no cambian y `BehaviorFrame.CurrentFormat` sigue en 1); `DueAtMinutes` y `AwaitKind.Minutes` quedan con el sentido "tiempo simulado en `Unit`" (agregar un `AwaitKind` nuevo rompería los `switch` de los hosts). Reanudar un frame cuya unidad no es la del programa es un frame obsoleto y sigue la `StalePolicy`.
- **D10. EvalSuite.** `TraceHost` implementa `IBehaviorClock` con un reloj en ms (`NowMilliseconds`); `NowMinutes` pasa a derivarse de él (mismo comportamiento para quien lo usa en minutos).
- **D11. CLI, describe, MCP, addon.** `kedo describe` agrega `unit` a cada campo `duration` (y `min_duration`/`max_duration` si el esquema los fija con literal); `kedo catalog --json` y `kedo build --json` agregan `time_unit`, y `build` también `format`; el listado de `kedo compile` agrega ` time_unit=<sufijo>` a la cabecera sólo si no es `min`. El MCP hereda todo (usa `ObjectDescriber` y `Disassembler`). El addon de Godot no cambia: edita el literal (`500ms` pasa tal cual por `kedo edit set`). Los skills `kedo-syntax` y `kedo-author-content` documentan `ms` y `base_unit`.
- **D12. Fuera de alcance:** publicar 1.1.0 (versión, notas, `public-api.ps1 -Ship`), conversión entre módulos con unidades distintas, ticks, aritmética de duraciones fuera de la regla por literal, y las extensiones K2-K4 del juego.

## Estructura de archivos

| Archivo | Responsabilidad |
|---|---|
| `spec/adr/0014-unidad-base-de-duraciones.md` (nuevo) | Decisión D1-D12 |
| `src/Kedo.Core/Schema/DurationUnit.cs` (nuevo) | `DurationUnit` y conversiones exactas `DurationUnits` |
| `src/Kedo.Compiler/Resolution/ModuleTimeUnit.cs` (nuevo) | Leer `base_unit` de los manifiestos; K2011, K2012 |
| `src/Kedo.Compiler/Resolution/DurationLowering.cs` (nuevo) | Bajar un literal a la unidad base; K4014 |
| `src/Kedo.Compiler/Syntax/Lexer.cs` | Sufijo `ms` |
| `src/Kedo.Compiler/Model/Declarations.cs` | `DurationValue.Minutes` con `ms` |
| `src/Kedo.Compiler/Resolution/{Resolver,ResolvedValue,ConstEvaluator,CatalogHasher}.cs` | Unidad en la resolución, el valor, las expresiones y el hash |
| `src/Kedo.Core/Schema/Schema.cs`, `src/Kedo.Compiler/Schema/{SchemaLoader,MetaSchema,JsonSchemaWriter}.cs` | Bornes y default de duración en ms |
| `src/Kedo.Compiler/Behaviors/BehaviorCompiler.cs`, `src/Kedo.Core/Runtime/{Program,Disassembler}.cs` | Literales de hooks y unidad del programa |
| `src/Kedo.Core/Runtime/{Host,BehaviorVm}.cs` | `IBehaviorClock`, `AwaitRequest.Unit`, `wait` |
| `src/Kedo.Core/Pack/{KedoPack,PackValue}.cs`, `src/Kedo.Compiler/Pack/PackWriter.cs` | Formato 3 |
| `src/Kedo.Compiler/Evaluation/EvalSuite.cs` | `TraceHost` con reloj en ms |
| `src/Kedo.Compiler/Editing/ObjectDescriber.cs`, `src/Kedo.Cli/Program.cs` | `unit`, `time_unit`, `format` |
| `tests/Kedo.Tests/DurationUnitTests.cs` (nuevo) | Tests de la extensión (patrón de `DecimalAndMapTests.cs`) |
| `tests/corpus/durations-*` (nuevos) | Golden cases |

---

### Task 1: ADR-0014 e issue

**Files:**
- Create: `spec/adr/0014-unidad-base-de-duraciones.md`

**Interfaces:**
- Consumes: nada.
- Produces: el ADR que citan las demás tareas como `ADR-0014`; los códigos K2011 (advertencia), K2012 (error) y K4014 (advertencia en `min`, error en las demás).

- [ ] **Step 1: Confirmar que el número y los códigos están libres**

Run: `Get-ChildItem spec/adr; Select-String -Path src/**/*.cs -Pattern '"K2011"|"K2012"|"K4014"'`
Expected: el último ADR es `0013-defaults-vacios.md`; ninguna coincidencia de los tres códigos.

- [ ] **Step 2: Enlazar el issue**

Run: `gh issue list --search "base_unit"`; si no existe, `gh issue create --title "Duration base unit (base_unit = ms|s|min|h|d)" --body "<resumen de Decisiones>"`.
Expected: un número de issue para la cabecera del ADR.

- [ ] **Step 3: Escribir el ADR**

Formato de los ADR existentes (ver `0008-decimales-de-punto-fijo.md`): `# ADR-0014 — Unidad base de las duraciones`, línea `Fecha: 01/11/2026 · Estado: propuesto · Origen: issue #N (extensión K1 de un juego de acción)`, secciones **Contexto** (el defecto verificado y el contenido revisado de la sección Decisiones de este plan), **Cómo lo resuelven otros** (Factorio y RimWorld: ticks en los datos; Godot y Unreal: segundos `float`; Protocol Buffers `Duration`: segundos + nanos enteros; Go `time.Duration`: int64 de nanosegundos), **Decisión** (D1-D11 de este plan, con los tres códigos y la tabla de severidad) y **Consecuencias** (Patriark sin cambios; en 2.0 K4014 es error también en `min`; un contenido 1.0 que ya declaraba `base_unit = s` y escribía `30s` pasa de 0 a 30 segundos: era el comportamiento documentado en KEDO-01 §10).

- [ ] **Step 4: Revisar contra ADR-0001**

Leer el ADR y confirmar que cada cambio de la sección Decisiones dice por qué no rompe 1.x (valor por defecto `Minute`, advertencia y no error en `min`, formato 2 para `min`, `const` sin cambiar).
Expected: ninguna decisión sin justificación de compatibilidad.

- [ ] **Step 5: Commit**

```bash
git add spec/adr/0014-unidad-base-de-duraciones.md
git commit -m "ADR-0014: duration base unit (proposed)"
```

### Task 2: Pines de compatibilidad 1.0

Tests de caracterización escritos **antes** de tocar código: fijan lo que el contenido en minutos produce hoy.

**Files:**
- Create: `tests/Kedo.Tests/DurationUnitTests.cs`
- Modify: `tests/Kedo.Tests/PackTests.cs:14` (`private static` → `internal static` en `BuildExamples`)

**Interfaces:**
- Consumes: `PackTests.BuildExamples() -> (byte[] bytes, ResolvedCatalog catalog, DiagnosticBag bag)`.
- Produces: la clase `DurationUnitTests` con los helpers que usan las tareas siguientes:
  - `private static (ResolvedCatalog Catalog, DiagnosticBag Bag) Resolve(string content, string? baseUnit, SchemaRegistry? schema = null)` — arma `"kedo 1\nmodule m\n"`, más `manifest { base_unit = <baseUnit> }\n` si `baseUnit` no es null, más `content`, en el archivo `c.kedo`; esquema permisivo si `schema` es null.
  - `private static ResolvedValue Field(ResolvedCatalog catalog, string key, string field)`.
  - `private static BehaviorProgram Program(string content, string? baseUnit, string key)` — resuelve y compila con `BehaviorCompiler.Compile(catalog.ByKey[key], decls.Functions, schema, bag)`.

- [ ] **Step 1: Escribir los tests de caracterización**

```csharp
/// <summary>Duration base unit (ADR-0014).</summary>
public class DurationUnitTests
{
    [Fact]
    public void LegacyMinuteContentKeepsItsValues()
    {
        var (catalog, bag) = Resolve("thing t { a = 30s  b = 90s  c = (90s + 30s)  d = 1h  e = 2d  f = -30s  g = 15 }", baseUnit: null);
        Assert.False(bag.HasErrors);
        Assert.Equal("0min", Field(catalog, "thing:t", "a").CanonicalText);
        Assert.Equal("1min", Field(catalog, "thing:t", "b").CanonicalText);
        Assert.Equal("1min", Field(catalog, "thing:t", "c").CanonicalText);
        Assert.Equal("60min", Field(catalog, "thing:t", "d").CanonicalText);
        Assert.Equal("2880min", Field(catalog, "thing:t", "e").CanonicalText);
        Assert.Equal("0min", Field(catalog, "thing:t", "f").CanonicalText);
    }

    [Fact]
    public void LegacyWaitInSecondsLoadsWholeMinutes()
    {
        var program = Program("actor z { on go(ctx) { wait 90s } }", baseUnit: null, "actor:z");
        Assert.Contains(1L, program.Constants);
        Assert.DoesNotContain(90L, program.Constants);
    }

    [Fact]
    public void ExamplePackAndFramesArePinned()
    {
        var (bytes, catalog, bag) = PackTests.BuildExamples();
        Assert.False(bag.HasErrors);
        Assert.Equal("<PIN_PACK_SHA256>", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        Assert.Equal("<PIN_CATALOG_HASH>", catalog.Hash);
        var program = Program("actor s { on arrive(ctx) { wait 30min\n apply effect:alarm(ctx.place) } }", baseUnit: null, "actor:s");
        var host = new FakeHost();
        host.ContextValues["place"] = host.Sym("place:depot");
        var frame = new BehaviorVm(program, host).Activate("arrive", "inst-1").Frame!;
        Assert.Equal("<PIN_FRAME_JSON>", BehaviorVm.Save(frame));
    }
}
```

- [ ] **Step 2: Correr para obtener los pines**

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj --filter "FullyQualifiedName~DurationUnitTests"`
Expected: los dos primeros PASS; `ExamplePackAndFramesArePinned` FAIL mostrando los valores reales. Copiar cada valor real en su `<PIN_…>` (la cadena JSON del frame con las comillas escapadas).

- [ ] **Step 3: Correr de nuevo**

Run: el mismo comando.
Expected: 3 PASS.

- [ ] **Step 4: Guardar los packs de Patriark de antes (fuera del repositorio)**

Run:
```powershell
dotnet build src/Kedo.Cli -c Release
$out = Join-Path $env:TEMP "kedo-base-unit/before"; New-Item -ItemType Directory -Force $out | Out-Null
$kedo = "src/Kedo.Cli/bin/Release/net8.0/kedo.exe"; $p = "C:/dev/Project-Patriark/content/kedo"
& $kedo build "$p/patriark" --schema="$p/schema" --out="$out/patriark.kpk" --json
& $kedo build "$p/patriark" --schema="$p/schema" --variant=test_1 --out="$out/patriark-test_1.kpk" --json
```
Expected: `"written": true`, `"diagnostics": []` en ambos (hoy: 226 objetos y 0 programas; 233 objetos y 1 programa). Estos archivos no se agregan a Git.

- [ ] **Step 5: Commit**

```bash
git add tests/Kedo.Tests/DurationUnitTests.cs tests/Kedo.Tests/PackTests.cs
git commit -m "Pin the 1.0 meaning of durations in minutes"
```

### Task 3: `DurationUnit` y conversiones exactas en `Kedo.Core`

**Files:**
- Create: `src/Kedo.Core/Schema/DurationUnit.cs`
- Modify: `src/Kedo.Core/PublicAPI.Unshipped.txt`
- Test: `tests/Kedo.Tests/DurationUnitTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces (namespace `Kedo.Schema`):
  - `public enum DurationUnit : byte { Minute = 0, Millisecond = 1, Second = 2, Hour = 3, Day = 4 }`
  - `public static class DurationUnits` con:
    - `long MillisecondsPer(DurationUnit unit)` → 60 000, 1, 1 000, 3 600 000, 86 400 000.
    - `string Suffix(DurationUnit unit)` → `"min"`, `"ms"`, `"s"`, `"h"`, `"d"`.
    - `bool TryParseSuffix(string? suffix, out DurationUnit unit)` — sólo esos cinco textos exactos.
    - `bool TryConvert(long amount, DurationUnit from, DurationUnit to, out long result)` — false si no es entero en `to` o si desborda.
    - `long ConvertTruncating(long amount, DurationUnit from, DurationUnit to)` — divide truncando hacia cero; al multiplicar satura en `long.MinValue`/`long.MaxValue`.
    - `string Format(long amount, DurationUnit unit)` → `"350ms"` (cultura invariante).

- [ ] **Step 1: Escribir los tests**

```csharp
[Fact]
public void UnitsConvertExactlyOrSayWhy()
{
    Assert.True(DurationUnits.TryConvert(2, DurationUnit.Second, DurationUnit.Millisecond, out var ms));
    Assert.Equal(2000, ms);
    Assert.False(DurationUnits.TryConvert(90, DurationUnit.Minute, DurationUnit.Hour, out _));
    Assert.True(DurationUnits.TryConvert(120, DurationUnit.Minute, DurationUnit.Hour, out var h));
    Assert.Equal(2, h);
    Assert.False(DurationUnits.TryConvert(250, DurationUnit.Millisecond, DurationUnit.Second, out _));
    Assert.False(DurationUnits.TryConvert(106_751_991_167_301, DurationUnit.Day, DurationUnit.Millisecond, out _));
    Assert.Equal(0, DurationUnits.ConvertTruncating(30, DurationUnit.Second, DurationUnit.Minute));
    Assert.Equal(1, DurationUnits.ConvertTruncating(90, DurationUnit.Second, DurationUnit.Minute));
    Assert.Equal(0, DurationUnits.ConvertTruncating(-30, DurationUnit.Second, DurationUnit.Minute));
    Assert.Equal(long.MaxValue, DurationUnits.ConvertTruncating(long.MaxValue / 2, DurationUnit.Day, DurationUnit.Millisecond));
    Assert.True(DurationUnits.TryParseSuffix("ms", out var u) && u == DurationUnit.Millisecond);
    Assert.False(DurationUnits.TryParseSuffix("minutes", out _));
    Assert.Equal("350ms", DurationUnits.Format(350, DurationUnit.Millisecond));
    Assert.Equal(DurationUnit.Minute, default(DurationUnit));
}
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj --filter "FullyQualifiedName~DurationUnitTests.UnitsConvertExactlyOrSayWhy"`
Expected: FAIL de compilación: `DurationUnits` no existe.

- [ ] **Step 3: Implementar `DurationUnit` y `DurationUnits` en `src/Kedo.Core/Schema/DurationUnit.cs`**

`TryConvert` pasa por ms con aritmética `checked` dentro de `try/catch (OverflowException)` o con `Math.BigMul`/`Int128`; preferir `Int128` (sin excepciones): `Int128 total = (Int128)amount * MillisecondsPer(from)`, entero si `total % MillisecondsPer(to) == 0`, cabe si el cociente está en el rango de `long`.

- [ ] **Step 4: Declarar la API**

Run: `powershell -File tools/public-api.ps1`
Expected: `src/Kedo.Core/PublicAPI.Unshipped.txt` gana las líneas de `Kedo.Schema.DurationUnit` (5 miembros) y de los 6 métodos de `Kedo.Schema.DurationUnits`.

- [ ] **Step 5: Correr los tests**

Run: el comando del Step 2.
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Kedo.Core/Schema/DurationUnit.cs src/Kedo.Core/PublicAPI.Unshipped.txt tests/Kedo.Tests/DurationUnitTests.cs
git commit -m "DurationUnit and exact duration conversions in Kedo.Core"
```

### Task 4: Sufijo `ms` en el lexer

**Files:**
- Modify: `src/Kedo.Compiler/Syntax/Lexer.cs:221-224` (lista de sufijos)
- Modify: `src/Kedo.Compiler/Model/Declarations.cs:51-56` (`DurationValue.Minutes`)
- Modify: `spec/KEDO-01-Gramatica.md` §2 (fila Duración)
- Test: `tests/Kedo.Tests/LexerTests.cs`, `tests/Kedo.Tests/DurationUnitTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces: `TokenKind.Duration` para `<entero>ms`; `DurationValue.Minutes` devuelve `Amount / 60_000` para `"ms"` (semántica 1.0 de truncado).

- [ ] **Step 1: Escribir los tests**

En `LexerTests.cs`:
```csharp
[Theory]
[InlineData("t = 350ms")]
[InlineData("t = 1_500ms")]
public void LexesMillisecondDurations(string text)
{
    var (tokens, bag) = Lex(text);
    Assert.False(bag.HasErrors);
    Assert.Contains(TokenKind.Duration, Significant(tokens));
}
```
En `DurationUnitTests.cs`:
```csharp
[Fact]
public void MillisecondLiteralsAreOneTokenAndDecimalsStayRejected()
{
    var bag = new DiagnosticBag();
    var tokens = Lexer.Lex(SourceText.FromString("a = 350ms\nb = 1.5ms\nc = 5mss\n", "l.kedo"), bag);
    Assert.Equal(1, tokens.Count(t => t.Kind == TokenKind.Duration));
    Assert.Contains(bag.Items, d => d.Code == "K1002" && d.Message.Contains("whole numbers"));
    Assert.Contains(bag.Items, d => d.Code == "K1002" && d.Message == "invalid number suffix `mss`; durations use `s`, `min`, `h` or `d`");
    Assert.Equal(0, new DurationValue(default, 1500, "ms").Minutes);
}
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj --filter "FullyQualifiedName~LexesMillisecondDurations|FullyQualifiedName~MillisecondLiteralsAreOneToken"`
Expected: FAIL: `350ms` produce K1002.

- [ ] **Step 3: Implementar**

En `Lexer.cs` agregar `unit.SequenceEqual("ms"u8)` a `isUnit`; el texto del mensaje de K1002 no cambia (golden `errors-lexical`). En `DurationValue.Minutes` agregar `"ms" => Amount / 60_000`. En KEDO-01 §2: "entero seguido sin espacio de `ms`, `s`, `min`, `h` o `d` (`350ms`, `30min`, `2h`). Un solo token; se baja a entero en la unidad base del módulo (§10; `min` por defecto, ADR-0014)".

- [ ] **Step 4: Correr los tests y el corpus**

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj; git status --porcelain tests/corpus`
Expected: todo PASS; ninguna línea de `git status` bajo `tests/corpus`.

- [ ] **Step 5: Commit**

```bash
git add src/Kedo.Compiler/Syntax/Lexer.cs src/Kedo.Compiler/Model/Declarations.cs spec/KEDO-01-Gramatica.md tests/Kedo.Tests/LexerTests.cs tests/Kedo.Tests/DurationUnitTests.cs
git commit -m "Lex millisecond durations (350ms)"
```

### Task 5: `base_unit` del manifiesto, K2011 y K2012

**Files:**
- Create: `src/Kedo.Compiler/Resolution/ModuleTimeUnit.cs`
- Modify: `src/Kedo.Compiler/Resolution/Resolver.cs:9-48` (`ResolvedCatalog.TimeUnit`), `:87-92` (constructor), `:99-123` (`Resolve`), `:134-140` (`ResolveIncremental`), `:264-280` (`ResolveView`), `:581` (`ResolvedObject.TimeUnit`)
- Modify: `src/Kedo.Compiler/Resolution/ResolvedValue.cs:279-318` (`ResolvedObject.TimeUnit`)
- Modify: `src/Kedo.Compiler/PublicAPI.Unshipped.txt`, `spec/KEDO-01-Gramatica.md` §10 y §12
- Create: `tests/corpus/durations-unit-conflict/module/a-core.kedo`, `b-game.kedo`, `c-typo.kedo`
- Test: `tests/Kedo.Tests/DurationUnitTests.cs`, `tests/Kedo.Tests/IncrementalTests.cs`

**Interfaces:**
- Consumes: `DurationUnits.TryParseSuffix` (Tarea 3).
- Produces:
  - `internal static class ModuleTimeUnit { public static DurationUnit Determine(IReadOnlyList<FileDeclarations> files, DiagnosticBag diagnostics, out bool conflict); }` — lee la `FieldEntry` `base_unit` de cada `FileDeclarations.Manifest`, en el orden de entrada.
  - `public DurationUnit ResolvedCatalog.TimeUnit { get; internal set; }` y `public DurationUnit ResolvedObject.TimeUnit { get; internal set; }`.
  - `internal static ResolvedCatalog Resolver.Resolve(IEnumerable<FileDeclarations> files, SchemaRegistry schema, DiagnosticBag diagnostics, DurationUnit? forcedUnit)`; el `Resolve` público delega con `null`. Con `forcedUnit` no se leen manifiestos.
  - Campos del resolver que usa la Tarea 6: `private readonly DurationUnit _unit;` y `private readonly bool _silentDurations;` (true tras K2012).
  - Mensajes: K2011 (advertencia, en el valor) "`base_unit = {texto}` is not a duration unit (`ms`, `s`, `min`, `h` or `d`); durations stay in `min`"; K2012 (error, en el segundo manifiesto, etiqueta "first declared here" en el primero) "`base_unit = {b}` differs from `base_unit = {a}` declared in another manifest; content resolved together counts durations in one unit".

- [ ] **Step 1: Escribir los tests**

```csharp
[Fact]
public void TheManifestSetsTheUnitAndMinutesAreTheDefault()
{
    Assert.Equal(DurationUnit.Minute, Resolve("thing t { a = 1min }", baseUnit: null).Catalog.TimeUnit);
    var (catalog, bag) = Resolve("thing t { a = 2s }", baseUnit: "ms");
    Assert.False(bag.HasErrors);
    Assert.Equal(DurationUnit.Millisecond, catalog.TimeUnit);
    Assert.Equal(DurationUnit.Millisecond, catalog.ByKey["thing:t"].TimeUnit);
}

[Fact]
public void AnUnknownUnitWarnsAndKeepsMinutes()
{
    var (catalog, bag) = Resolve("thing t { a = 1min }", baseUnit: "minutes");
    Assert.Equal(DurationUnit.Minute, catalog.TimeUnit);
    Assert.Contains(bag.Items, d => d.Code == "K2011" && d.Severity == Severity.Warning && d.Message.Contains("`base_unit = minutes`"));
    Assert.False(bag.HasErrors);
}

[Fact]
public void AVariantCannotChangeTheUnit()   // Review Focus 5
{
    var bag = new DiagnosticBag();
    var files = new[]
    {
        "kedo 1\nmodule m\nmanifest { base_unit = ms }\nthing t { a = 2s }\n",
        "kedo 1\nmodule m\nvariant fast\nmanifest { base_unit = s }\noverride thing t { a = 1s }\n",
    }.Select((text, i) => DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(text, $"f{i}.kedo"), bag), bag)).ToList();
    var catalog = Resolver.Resolve(files, new SchemaRegistry { Permissive = true }, bag);
    Assert.Contains(bag.Items, d => d.Code == "K2012" && d.IsError && d.File == "f1.kedo");
    Assert.Equal(DurationUnit.Minute, catalog.TimeUnit);
    Assert.Equal(DurationUnit.Minute, catalog.Variants["fast"].TimeUnit);
    Assert.DoesNotContain(bag.Items, d => d.Code == "K4014");
}
```
En `IncrementalTests.cs` (Review Focus 2):
```csharp
[Fact]
public void EditingTheManifestResolvesEverythingAgain()
{
    var schema = new SchemaRegistry { Permissive = true };
    var files = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["module.kedo"] = "kedo 1\nmodule m\nmanifest { base_unit = s }\n",
        ["a.kedo"] = "kedo 1\nmodule m\nthing t { a = 2min }\n",
    };
    var session = new IncrementalCatalog(schema);
    AssertSame(session, files, schema);
    files["module.kedo"] = files["module.kedo"].Replace("base_unit = s", "base_unit = ms");
    AssertSame(session, files, schema);
    Assert.Equal(0, session.LastReusedObjects);
}
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj --filter "FullyQualifiedName~DurationUnitTests|FullyQualifiedName~EditingTheManifest"`
Expected: FAIL de compilación: `ResolvedCatalog.TimeUnit` no existe.

- [ ] **Step 3: Implementar `ModuleTimeUnit.Determine`**

Recorre los archivos en orden; por cada `base_unit` válida recuerda la primera `(unidad, Origin)`; un `EnumValue` cuyo nombre no pasa `TryParseSuffix`, o cualquier otra clase de valor, es K2011 y se ignora; la primera unidad distinta de la recordada es K2012 (una vez) y `conflict = true`. Devuelve `Minute` si no hay ninguna o si hubo conflicto.

- [ ] **Step 4: Conectar el resolver**

`ResolveView` y `ResolveIncremental` calculan `unit = forcedUnit ?? ModuleTimeUnit.Determine(files, diagnostics, out conflict)` y la pasan al constructor (`_unit`, `_silentDurations = conflict`); el catálogo y cada `ResolvedObject` nuevo reciben `TimeUnit = _unit`. En `ResolveIncremental`, si `previous is not null && previous.Catalog.TimeUnit != unit`, tratar `previous` como `null`. `Resolve` (público) delega en el interno; cada vista de variante llama a `Determine` sobre los mismos archivos (sus diagnósticos repetidos ya se deduplican por `DiagnosticKey`).

- [ ] **Step 5: Golden de módulo**

`a-core.kedo`: `kedo 1` / `module core` / `manifest { base_unit = min }` / `thing a { t = 1min }`. `b-game.kedo`: `module game`, `manifest { base_unit = s }`, `thing b { t = 30s }`. `c-typo.kedo`: `module game`, `manifest { base_unit = seconds }`.

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj --filter "FullyQualifiedName~ModuleCaseMatchesGoldenFiles"` dos veces (la primera genera los archivos).
Expected: `diagnostics.txt` con exactamente `error K2012 b-game.kedo:…` y `warning K2011 c-typo.kedo:…`, sin K4014; `resolved.txt` con `t = 1min` en `thing a` y `t = 0min` en `thing b`. Segunda corrida PASS.

- [ ] **Step 6: Spec y API**

KEDO-01 §10: el manifiesto de ejemplo con `base_unit = min` y las reglas: unidades `ms s min h d`, `min` por defecto, una unidad para todo lo que se resuelve junto (base y variantes), K2011 y K2012, y que los enteros en campos `duration` son unidades base. KEDO-01 §12: filas K2011 y K2012. Run: `powershell -File tools/public-api.ps1` (agrega `ResolvedCatalog.TimeUnit.get` y `ResolvedObject.TimeUnit.get`).

- [ ] **Step 7: Correr todo**

Run: `dotnet build Kedo.sln -c Release; dotnet test tests/Kedo.Tests/Kedo.Tests.csproj; git status --porcelain tests/corpus`
Expected: 0 advertencias; todo PASS; sólo `?? tests/corpus/durations-unit-conflict/`.

- [ ] **Step 8: Commit**

```bash
git add src/Kedo.Compiler tests spec/KEDO-01-Gramatica.md
git commit -m "Read base_unit from manifests (K2011, K2012)"
```

### Task 6: Duraciones bajadas a la unidad base y K4014

**Files:**
- Create: `src/Kedo.Compiler/Resolution/DurationLowering.cs`
- Modify: `src/Kedo.Compiler/Resolution/ResolvedValue.cs:92-97` (`RDuration`)
- Modify: `src/Kedo.Compiler/Resolution/Resolver.cs:1117-1131` (`IntValue` en campo `duration`), `:1124` (llamada a `ConstEvaluator`), `:1157-1158` (`DurationValue`)
- Modify: `src/Kedo.Compiler/Resolution/ConstEvaluator.cs:14-25` (`Evaluate`), `:48`, `:85`, `:119-124`, `:146-162`
- Modify: `src/Kedo.Compiler/Resolution/CatalogHasher.cs:79`
- Modify: `src/Kedo.Compiler/PublicAPI.Unshipped.txt`, `spec/KEDO-02-Resolucion.md` §3, §8 R23, §11
- Create: `tests/corpus/durations-base-unit/input.kedo`, `tests/corpus/durations-not-whole/input.kedo`, `tests/corpus/durations-legacy-minutes/input.kedo`
- Test: `tests/Kedo.Tests/DurationUnitTests.cs`

**Interfaces:**
- Consumes: `_unit`, `_silentDurations` (Tarea 5); `DurationUnits.*` (Tarea 3).
- Produces:
  - `internal static class DurationLowering { public static long? ToUnit(long amount, string suffix, DurationUnit unit, string? file, TextSpan span, DiagnosticBag? diagnostics, ISet<string>? reportedSites = null); }` — exacto: el valor. No entero con `unit == Minute`: K4014 advertencia y `ConvertTruncating`. No entero con otra unidad: K4014 error y `null`. Desborde: K4014 error y `null`. `diagnostics == null` no informa nada; `reportedSites` evita repetir un sitio (`$"{file}|{span.Start}"`).
  - Mensajes K4014: advertencia "`{lit}` is not a whole number of `min` (the module's `base_unit`) and counts as `{n}min`; write it in `min`, or set a smaller `base_unit` in the manifest"; error "`{lit}` is not a whole number of `{u}` (the module's `base_unit`); write it in `{u}` or a larger unit, or set a smaller `base_unit` in the manifest"; desborde "`{lit}` does not fit in a 64-bit count of `{u}`".
  - `RDuration`: `public DurationUnit Unit { get; init; }`, `public long Amount => Minutes;`, `CanonicalText => DurationUnits.Format(Minutes, Unit)`. Toda construcción interna de `RDuration` pone `Unit = _unit`.
  - `ConstEvaluator.Evaluate(ExprValue expr, DiagnosticBag diagnostics, DurationUnit unit, Func<DurationValue, long?> toUnit)`: cada literal pasa por `toUnit` y queda como `DurationValue(origin, base, DurationUnits.Suffix(unit))`; la aritmética usa `Amount`.
  - Resolver: `private long? ToUnit(DurationValue dv)` (llama a `DurationLowering.ToUnit` con `_silentDurations ? null : _d` y `_durationSites`), y un delegado cacheado en un campo para `ConstEvaluator`.
  - Hash: `RDuration` con `Unit == Minute` escribe `"d"` + i64 (igual que 1.0); con otra, `"D"` + u8 `(byte)Unit` + i64.

- [ ] **Step 1: Escribir los tests**

```csharp
[Fact]
public void DurationsResolveInTheBaseUnit()
{
    var (catalog, bag) = Resolve("thing t { windup = 350ms  cooldown = 2s  combo = (1s + 250ms)  long = 2min  raw = 40  neg = -250ms }", baseUnit: "ms");
    Assert.False(bag.HasErrors);
    var cooldown = Assert.IsType<RDuration>(Field(catalog, "thing:t", "cooldown"));
    Assert.Equal((2000L, DurationUnit.Millisecond), (cooldown.Amount, cooldown.Unit));
    Assert.Equal("350ms", Field(catalog, "thing:t", "windup").CanonicalText);
    Assert.Equal("1250ms", Field(catalog, "thing:t", "combo").CanonicalText);
    Assert.Equal("120000ms", Field(catalog, "thing:t", "long").CanonicalText);
    Assert.Equal("-250ms", Field(catalog, "thing:t", "neg").CanonicalText);
    Assert.DoesNotContain(bag.Items, d => d.Code == "K4014");
}

[Fact]
public void ANonWholeLiteralWarnsInMinutesAndFailsElsewhere()
{
    var (legacy, lbag) = Resolve("thing t { a = 30s  b = (90s + 30s) }", baseUnit: null);
    Assert.False(lbag.HasErrors);
    Assert.Equal(3, lbag.Items.Count(d => d.Code == "K4014" && d.Severity == Severity.Warning));
    Assert.Equal("0min", Field(legacy, "thing:t", "a").CanonicalText);
    Assert.Equal("1min", Field(legacy, "thing:t", "b").CanonicalText);

    var (strict, sbag) = Resolve("thing t { a = 250ms  b = (500ms * 2)  c = 3s }", baseUnit: "s");
    Assert.Equal(2, sbag.Items.Count(d => d.Code == "K4014" && d.IsError));
    Assert.Contains(sbag.Items, d => d.Message.StartsWith("`250ms` is not a whole number of `s`"));
    Assert.False(strict.ByKey["thing:t"].Body.Fields.TryGetValue("a", out _));
    Assert.Equal("3s", Field(strict, "thing:t", "c").CanonicalText);
}

[Fact]
public void AMixinLiteralIsReportedOnce()   // Review Focus 1
{
    var (_, bag) = Resolve("mixin quick { a = 30s }\nthing t1 with quick { }\nthing t2 with quick { }\nthing t3 with quick { }", baseUnit: null);
    Assert.Single(bag.Items, d => d.Code == "K4014");
}

[Fact]
public void AHugeLiteralIsAnErrorNotAWrap()   // Review Focus 4
{
    var (_, ms) = Resolve("thing t { a = 106751991167301d }", baseUnit: "ms");
    Assert.Contains(ms.Items, d => d.Code == "K4014" && d.IsError && d.Message.Contains("64-bit"));
    var (_, min) = Resolve("thing t { a = 9223372036854775807d }", baseUnit: null);
    Assert.Contains(min.Items, d => d.Code == "K4014" && d.IsError);
}

[Fact]
public void TheUnitIsPartOfTheHashButMinutesHashAsBefore()
{
    var a = Resolve("thing t { a = 30min }", baseUnit: null).Catalog.ByKey["thing:t"].Hash;
    var b = Resolve("thing t { a = 30min }", baseUnit: "min").Catalog.ByKey["thing:t"].Hash;
    var c = Resolve("thing t { a = 30s }", baseUnit: "s").Catalog.ByKey["thing:t"].Hash;   // same amount 30, other unit
    Assert.Equal(a, b);
    Assert.NotEqual(a, c);
}
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj --filter "FullyQualifiedName~DurationUnitTests"`
Expected: FAIL: `RDuration.Unit` no existe; los pines de la Tarea 2 siguen PASS una vez que compile.

- [ ] **Step 3: Implementar `DurationLowering`, `RDuration`, el resolver, `ConstEvaluator` y el hash**

Según el bloque Interfaces. `IntValue` en un campo `duration` da `new RDuration(prov, iv.Number) { Unit = _unit }` sin conversión (D5). `DurationLowering` distingue "no entero" de "no cabe" con `Int128` (`(Int128)amount * MillisecondsPer(from)` contra `MillisecondsPer(to)` y el rango de `long`), porque `TryConvert` sólo devuelve `false`. Si `toUnit` devuelve `null` dentro de una expresión, `ConstEvaluator` devuelve `null` sin agregar K5003.

- [ ] **Step 4: Golden cases de un archivo**

`durations-base-unit/input.kedo`: `manifest { base_unit = ms }` y `attack slash { windup = 350ms  cooldown = 2s  combo_window = (1s + 250ms)  long = 2min  fraction = 1.5s }`. `durations-not-whole/input.kedo`: `manifest { base_unit = s }` y `attack jab { blink = 250ms  double = (500ms * 2)  ok = 3s  minute = 1min }`. `durations-legacy-minutes/input.kedo`: sin manifiesto, `event pause { short = 30s  ninety = 90s  sum = (90s + 30s)  milli = 1500ms  hour = 1h  day = 1d }`.

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj --filter "FullyQualifiedName~CaseMatchesGoldenFiles"` dos veces.
Expected tras revisar los archivos generados: `durations-base-unit` → `resolved.txt` con `windup = 350ms`, `cooldown = 2000ms`, `combo_window = 1250ms`, `long = 120000ms` y sin `fraction`; `diagnostics.txt` sólo con diagnósticos sobre `1.5s`, entre ellos `error K1002 … durations are whole numbers: write `1.5s` in a smaller unit`, y ningún K4014. `durations-not-whole` → dos `error K4014` (`250ms` y `500ms`), `ok = 3s`, `minute = 60s`, sin `blink` ni `double`. `durations-legacy-minutes` → cinco `warning K4014` (30s, 90s, 90s, 30s, 1500ms) y `short = 0min`, `ninety = 1min`, `sum = 1min`, `milli = 0min`, `hour = 60min`, `day = 1440min`. `formatted.kedo` conserva `350ms`. Segunda corrida PASS.

- [ ] **Step 5: Spec y API**

KEDO-02 §3: la fila de escalares menciona que una duración vale una cantidad entera en la unidad base. R23: K4014 en la etapa A. §11: fila K4014 "Duración que no es entera en la unidad base del módulo o no cabe en 64 bits (advertencia con truncado en `min`, error en las demás unidades)". Run: `powershell -File tools/public-api.ps1` (agrega `RDuration.Unit.get/init` y `RDuration.Amount.get`).

- [ ] **Step 6: Correr todo**

Run: `dotnet build Kedo.sln -c Release; dotnet test tests/Kedo.Tests/Kedo.Tests.csproj; git status --porcelain tests/corpus`
Expected: 0 advertencias; todo PASS (incluido `ExamplePackAndFramesArePinned`); sólo los tres directorios nuevos como `??`.

- [ ] **Step 7: Commit**

```bash
git add src/Kedo.Compiler tests spec/KEDO-02-Resolucion.md
git commit -m "Resolve durations in the base unit (K4014)"
```

### Task 7: Bornes y default de duración en el esquema

**Files:**
- Modify: `src/Kedo.Core/Schema/Schema.cs:66-74` (`FieldSchema`)
- Modify: `src/Kedo.Compiler/Schema/SchemaLoader.cs:13-14` (resolución en ms), `:238-249` (`Field`)
- Modify: `src/Kedo.Compiler/Schema/MetaSchema.cs:32-34` (docs de `min`, `max`, `default`)
- Modify: `src/Kedo.Compiler/Resolution/Resolver.cs:584-608` (`Defaults` pasa a método de instancia), `:1489-1494` (rango de `RDuration`), más un `CheckSchemaDurations()` llamado después de `Index` en `ResolveView` y `ResolveIncremental`
- Modify: `src/Kedo.Compiler/Schema/JsonSchemaWriter.cs:218-221`
- Modify: `src/Kedo.Core/PublicAPI.Unshipped.txt`
- Test: `tests/Kedo.Tests/DurationUnitTests.cs`

**Interfaces:**
- Consumes: `Resolver.Resolve(..., DurationUnit? forcedUnit)` (Tarea 5); `DurationLowering.ToUnit` (Tarea 6).
- Produces:
  - `FieldSchema`: `public long? MinDurationMs { get; init; }`, `public long? MaxDurationMs { get; init; }`, `public long? DefaultDurationMs { get; init; }`.
  - `SchemaLoader.Load` resuelve el esquema con `forcedUnit: DurationUnit.Millisecond`. Un `RDuration` en `default` llena `DefaultDurationMs` y además `DefaultInt = ConvertTruncating(ms, Millisecond, Minute)` (valor de 1.0); en `min`/`max` llena `MinDurationMs`/`MaxDurationMs` (`Min`/`Max` siguen `null` en ese caso, como en 1.0).
  - K4014 del default: "default `{ms}ms` of `{record}.{field}` is not a whole number of `{u}` (the content's `base_unit`)", sin archivo, una vez por campo; advertencia y truncado en `min`, error y sin default en las demás.
  - K4004 con bornes en ms: "`{path}` = {CanonicalText} is out of range [{min}, {max}]" con `DurationUnits.Format(…, Millisecond)` y `-∞`/`∞`; con bornes enteros, el texto de 1.0 ("`{path}` = {CanonicalText} is out of range", idéntico para `min`).
  - JSON Schema: `pattern` `^[0-9][0-9_]*(ms|s|min|h|d)$`; `default` con `DefaultDurationMs`: `{ms/60000}min` si es divisible (igual que 1.0), si no `{ms/1000}s` si es divisible, si no `{ms}ms`; sin `DefaultDurationMs`, `{DefaultInt}min` como 1.0.

- [ ] **Step 1: Escribir los tests**

```csharp
private const string ActionSchema = """
    kedo 1
    module s
    kedo_type attack { fields = [
        { name = windup  kind = duration  min = 100ms  max = 2s  default = 400ms }
        { name = cooldown  kind = duration  min = 0  default = 3 }
    ] }
    """;

[Fact]
public void SchemaDurationsAreExactInMilliseconds()
{
    var bag = new DiagnosticBag();
    var schema = SchemaLoader.Load(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(ActionSchema, "s.kedo"), bag), bag) }, bag);
    Assert.False(bag.HasErrors);
    var windup = schema.FindType("attack")!.Body.Find("windup")!;
    Assert.Equal((100L, 2000L, 400L), (windup.MinDurationMs, windup.MaxDurationMs, windup.DefaultDurationMs));
    Assert.Equal(0, windup.DefaultInt);
    Assert.Null(windup.Min);

    var (catalog, cbag) = Resolve("attack a { }\nattack b { windup = 50ms }\nattack c { windup = 3s }", baseUnit: "ms", schema);
    Assert.Equal("400ms", Field(catalog, "attack:a", "windup").CanonicalText);
    Assert.Equal("3ms", Field(catalog, "attack:a", "cooldown").CanonicalText);
    Assert.Equal(2, cbag.Items.Count(d => d.Code == "K4004"));
    Assert.Contains(cbag.Items, d => d.Message.Contains("[100ms, 2000ms]"));
}

[Fact]
public void ASchemaDefaultThatIsNotWholeInTheContentUnitIsReportedOnce()
{
    var bag = new DiagnosticBag();
    var schema = SchemaLoader.Load(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(ActionSchema, "s.kedo"), bag), bag) }, bag);
    var (catalog, cbag) = Resolve("attack a { }\nattack b { }", baseUnit: "s", schema);
    Assert.Single(cbag.Items, d => d.Code == "K4014" && d.IsError && d.Message.Contains("attack.windup"));
    Assert.False(catalog.ByKey["attack:a"].Body.Fields.TryGetValue("windup", out _));
}
```
Y en `DecimalAndMapTests.JsonSchemaDescribesDecimalsMapsAndConstraints` no se toca nada; un test nuevo `JsonSchemaWritesDurationDefaults` en `DurationUnitTests` comprueba `"400ms"` como `default` de `windup`, `"3min"` de `cooldown` y que el `pattern` contiene `ms|`.

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj --filter "FullyQualifiedName~DurationUnitTests"`
Expected: FAIL: `FieldSchema.MinDurationMs` no existe.

- [ ] **Step 3: Implementar** según el bloque Interfaces. `Defaults` calcula el default de duración con `DefaultDurationMs` si existe (convertido sin diagnósticos: los informa `CheckSchemaDurations`, que recorre una vez tipos, componentes, records y sus `Element`), y si no con `DefaultInt` tal cual. La comparación de rango usa `(Int128)du.Amount * DurationUnits.MillisecondsPer(du.Unit)`.

- [ ] **Step 4: API**

Run: `powershell -File tools/public-api.ps1`
Expected: 6 líneas nuevas en `src/Kedo.Core/PublicAPI.Unshipped.txt` (get/init de los tres campos).

- [ ] **Step 5: Correr todo**

Run: `dotnet build Kedo.sln -c Release; dotnet test tests/Kedo.Tests/Kedo.Tests.csproj`
Expected: 0 advertencias; todo PASS (incluidos `SamplesTests` y `SchemaTests`, que usan `min = 0  default = 0`).

- [ ] **Step 6: Commit**

```bash
git add src/Kedo.Core src/Kedo.Compiler tests/Kedo.Tests/DurationUnitTests.cs
git commit -m "Exact duration bounds and defaults in the schema"
```

### Task 8: Literales de hooks y unidad del programa

**Files:**
- Modify: `src/Kedo.Compiler/Behaviors/BehaviorCompiler.cs:43-47` (campo `_unit`), `:50-91` (`Compile`), `:536-544` (literal `Duration`)
- Modify: `src/Kedo.Core/Runtime/Program.cs:59-60` (doc de `WaitMinutes`), `:110-126` (`BehaviorProgram.TimeUnit`)
- Modify: `src/Kedo.Core/Runtime/Disassembler.cs:12`
- Modify: `src/Kedo.Core/PublicAPI.Unshipped.txt`
- Test: `tests/Kedo.Tests/DurationUnitTests.cs`

**Interfaces:**
- Consumes: `ResolvedObject.TimeUnit` (Tarea 5); `DurationLowering.ToUnit` (Tarea 6).
- Produces:
  - `public DurationUnit BehaviorProgram.TimeUnit { get; init; }` (por defecto `Minute`); `BehaviorCompiler.Compile` (firma sin cambios) la toma de `obj.TimeUnit`.
  - Un literal de duración en un hook o función pasa por `DurationLowering.ToUnit(…, obj.TimeUnit, _file, tk.Span, _d)`; si devuelve `null`, se emite `LoadConst 0` y el error ya quedó informado.
  - `Disassembler.Dump`: la primera línea es `program {id} v{n}` y, si `TimeUnit != Minute`, `program {id} v{n} time_unit={sufijo}`.
  - El doc de `OpCode.WaitMinutes`: "Suspend until `r[a]` units of the program's `TimeUnit` (minutes by default) elapse".

- [ ] **Step 1: Escribir los tests**

```csharp
[Fact]
public void HookLiteralsCompileInTheProgramUnit()
{
    var program = Program("actor z { on go(ctx) { wait 350ms\n wait 2s } }", baseUnit: "ms", "actor:z");
    Assert.Equal(DurationUnit.Millisecond, program.TimeUnit);
    Assert.Contains(350L, program.Constants);
    Assert.Contains(2000L, program.Constants);
    Assert.StartsWith("program actor:z v1 time_unit=ms\n", Disassembler.Dump(program));
    Assert.StartsWith("program actor:z v1\n", Disassembler.Dump(Program("actor z { on go(ctx) { wait 1min } }", baseUnit: null, "actor:z")));
}

[Fact]
public void ANonWholeWaitIsK4014()
{
    var bag = new DiagnosticBag();
    var decls = DeclarationBuilder.Build(Parser.Parse(SourceText.FromString("kedo 1\nmodule m\nmanifest { base_unit = s }\nactor z { on go(ctx) { wait 250ms } }", "h.kedo"), bag), bag);
    var schema = new SchemaRegistry { Permissive = true };
    var catalog = Resolver.Resolve(new[] { decls }, schema, bag);
    BehaviorCompiler.Compile(catalog.ByKey["actor:z"], decls.Functions, schema, bag);
    Assert.Contains(bag.Items, d => d.Code == "K4014" && d.IsError && d.File == "h.kedo");
}
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj --filter "FullyQualifiedName~HookLiteralsCompile|FullyQualifiedName~ANonWholeWait"`
Expected: FAIL: `BehaviorProgram.TimeUnit` no existe.

- [ ] **Step 3: Implementar** según el bloque Interfaces; borrar la conversión duplicada de `BehaviorCompiler.cs:540-541`.

- [ ] **Step 4: API**

Run: `powershell -File tools/public-api.ps1` (agrega `BehaviorProgram.TimeUnit.get/init`).

- [ ] **Step 5: Correr todo**

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj`
Expected: todo PASS; `LegacyWaitInSecondsLoadsWholeMinutes` y `DisassemblyListsHooksAndAwaitSites` siguen PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Kedo.Core src/Kedo.Compiler tests/Kedo.Tests/DurationUnitTests.cs
git commit -m "Compile hook durations in the program's time unit"
```

### Task 9: `wait` en la unidad del programa (`IBehaviorClock`) y `TraceHost`

**Files:**
- Modify: `src/Kedo.Core/Runtime/Host.cs:6` (doc de `HostValue.Int`), `:28-32` (nueva interfaz al lado de `IBehaviorHost`), `:63-76` (`AwaitKind.Minutes` doc, `AwaitRequest.Unit`)
- Modify: `src/Kedo.Core/Runtime/BehaviorVm.cs:26-32` (campo `_clock`), `:50-70` (`Resume`), `:453-457` (`WaitMinutes`)
- Modify: `src/Kedo.Compiler/Evaluation/EvalSuite.cs:336-346`, `:386-399` (`TraceHost`)
- Modify: `src/Kedo.Core/PublicAPI.Unshipped.txt`, `src/Kedo.Compiler/PublicAPI.Unshipped.txt`, `spec/KEDO-01-Gramatica.md` §8 (`wait`)
- Test: `tests/Kedo.Tests/DurationUnitTests.cs`

**Interfaces:**
- Consumes: `BehaviorProgram.TimeUnit` (Tarea 8); `DurationUnits.ConvertTruncating` (Tarea 3).
- Produces:
  - `namespace Kedo.Runtime; public interface IBehaviorClock { long Now(DurationUnit unit); }` — doc: "Optional companion of `IBehaviorHost`: simulated time at activation in the requested unit".
  - `AwaitRequest`: `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public DurationUnit Unit { get; init; }`; `DueAtMinutes` se documenta "due time in `Unit` (minutes by default)".
  - VM: `_clock = host as IBehaviorClock` en el constructor; `WaitMinutes` usa `now = _clock?.Now(_program.TimeUnit) ?? DurationUnits.ConvertTruncating(_host.NowMinutes, DurationUnit.Minute, _program.TimeUnit)` y crea el `AwaitRequest` con `Unit = _program.TimeUnit`. `Resume` marca obsoleto (`stale = true`) un frame con `Pending.Kind == AwaitKind.Minutes && Pending.Unit != _program.TimeUnit`.
  - `TraceHost : IBehaviorHost, IBehaviorClock`: `public long NowMilliseconds { get; set; }`; `NowMinutes { get => NowMilliseconds / 60_000; set => NowMilliseconds = value * 60_000; }`; `public long Now(DurationUnit unit) => DurationUnits.ConvertTruncating(NowMilliseconds, DurationUnit.Millisecond, unit)`; `Run` avanza `NowMilliseconds` hasta `ConvertTruncating(pending.DueAtMinutes, pending.Unit, Millisecond)`.

- [ ] **Step 1: Escribir los tests**

```csharp
private sealed class ClockHost : IBehaviorHost, IBehaviorClock
{
    public long Ms { get; set; } = 10_000;
    public SymbolTable Symbols { get; } = new();
    public List<string> Applied { get; } = new();
    public long NowMinutes => Ms / 60_000;
    public long Now(DurationUnit unit) => DurationUnits.ConvertTruncating(Ms, DurationUnit.Millisecond, unit);
    public HostValue Context(string field) => HostValue.None;
    public HostValue Query(string queryId, ReadOnlySpan<HostValue> args) => HostValue.Int(0);
    public bool Apply(EffectKey key, string effectId, ReadOnlySpan<HostValue> args) { Applied.Add(effectId); return true; }
    public long Roll(EffectKey key, string streamId, ReadOnlySpan<HostValue> args) => 1;
    public void Raise(string signalId, ReadOnlySpan<HostValue> args) { }
    public void Trace(string message) { }
}

private const string Telegraph = "actor boss { on slam(ctx) { wait 350ms\n apply effect:shockwave(3) } }";

[Fact]
public void WaitIsDueInTheProgramUnitFromTheClock()
{
    var program = Program(Telegraph, baseUnit: "ms", "actor:boss");
    var first = new BehaviorVm(program, new ClockHost()).Activate("slam", "i");
    Assert.Equal(AwaitKind.Minutes, first.Frame!.Pending!.Kind);
    Assert.Equal((10_350L, DurationUnit.Millisecond), (first.Frame.Pending.DueAtMinutes, first.Frame.Pending.Unit));
    var json = BehaviorVm.Save(first.Frame);
    Assert.Contains("\"unit\":1", json);
    var host = new ClockHost { Ms = 10_350 };
    Assert.Equal(RunStatus.Completed, new BehaviorVm(program, host).Resume(BehaviorVm.Load(json)!, new Resumption(AwaitKind.Minutes, 0)).Status);
    Assert.Equal(new[] { "effect:shockwave" }, host.Applied);
}

[Fact]
public void AHostWithoutClockConvertsNowMinutes()
{
    var program = Program(Telegraph, baseUnit: "ms", "actor:boss");
    var frame = new BehaviorVm(program, new FakeHost()).Activate("slam", "i").Frame!;   // FakeHost.NowMinutes = 1000
    Assert.Equal(60_000_350L, frame.Pending!.DueAtMinutes);
}

[Fact]
public void AFrameSavedBy10ResumesAsMinutes()   // Review Focus 3
{
    var program = Program("actor s { on arrive(ctx) { wait 30min\n apply effect:alarm(1) } }", baseUnit: null, "actor:s");
    var frame = new BehaviorVm(program, new FakeHost()).Activate("arrive", "i").Frame!;
    var json = BehaviorVm.Save(frame);
    Assert.DoesNotContain("\"unit\"", json);
    Assert.Equal(DurationUnit.Minute, BehaviorVm.Load(json)!.Pending!.Unit);
    var msProgram = Program(Telegraph, baseUnit: "ms", "actor:boss");
    // Same definition hash and version: only the unit differs, so the unit check alone makes the frame stale.
    var sameDefinition = frame with { DefinitionHash = msProgram.DefinitionHash, DefinitionVersion = msProgram.DefinitionVersion };
    var stale = new BehaviorVm(msProgram, new FakeHost()).Resume(sameDefinition, new Resumption(AwaitKind.Minutes, 0));
    Assert.Equal(RunStatus.Failed, stale.Status);
    Assert.Contains("stale frame", stale.Error);
}

[Fact]
public void TheTraceHostRunsAMillisecondHook()
{
    var host = new TraceHost();
    var status = host.Run(Program(Telegraph, baseUnit: "ms", "actor:boss"), "slam");
    Assert.Equal("Completed", status);
    Assert.Equal(new[] { "effect:shockwave(3)" }, host.Applied);
    Assert.Contains("suspended: Minutes slam/1", host.Lines);
    Assert.Equal(350, host.NowMilliseconds);
    Assert.Equal(0, host.NowMinutes);
}
```
(`AFrameSavedBy10ResumesAsMinutes` usa el pin JSON de la Tarea 2: `ExamplePackAndFramesArePinned` tiene que seguir PASS.)

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj --filter "FullyQualifiedName~DurationUnitTests"`
Expected: FAIL de compilación: `IBehaviorClock` no existe.

- [ ] **Step 3: Implementar** según el bloque Interfaces. Nada nuevo dentro del bucle de despacho fuera del `case OpCode.WaitMinutes`.

- [ ] **Step 4: Spec y API**

KEDO-01 §8: "`wait <duración>` espera tiempo simulado en la unidad base; el host da la hora con `IBehaviorClock.Now(unidad)` o, si no lo implementa, se convierte `NowMinutes` (ADR-0014)". Run: `powershell -File tools/public-api.ps1` (Core: `IBehaviorClock`, `IBehaviorClock.Now`, `AwaitRequest.Unit.get/init`; Compiler: `TraceHost.NowMilliseconds.get/set`, `TraceHost.Now`).

- [ ] **Step 5: Correr todo**

Run: `dotnet build Kedo.sln -c Release; dotnet test tests/Kedo.Tests/Kedo.Tests.csproj`
Expected: 0 advertencias; todo PASS, incluidos `BehaviorTests` (`DueAtMinutes == 1030`) y `EvalTests`.

- [ ] **Step 6: Commit**

```bash
git add src/Kedo.Core src/Kedo.Compiler tests/Kedo.Tests/DurationUnitTests.cs spec/KEDO-01-Gramatica.md
git commit -m "Wait in the program's time unit through IBehaviorClock"
```

### Task 10: Pack formato 3 con la unidad

**Files:**
- Modify: `src/Kedo.Core/Pack/KedoPack.cs:18-29` (constantes), `:33-40` (constructor), `:64-75` (versión), `:105-160` (sección de unidad antes de los programas)
- Modify: `src/Kedo.Compiler/Pack/PackWriter.cs:10-14`, `:90-125`
- Modify: `src/Kedo.Core/Pack/PackValue.cs:14`, `:35` (docs)
- Modify: `src/Kedo.Core/PublicAPI.Unshipped.txt`, `src/Kedo.Compiler/PublicAPI.Unshipped.txt`, `spec/KEDO-03-API-Publica-y-Paquetes.md` (fila "Formato del pack compilado")
- Test: `tests/Kedo.Tests/DurationUnitTests.cs`

**Interfaces:**
- Consumes: `ResolvedCatalog.TimeUnit` (Tarea 5), `BehaviorProgram.TimeUnit` (Tarea 8).
- Produces:
  - `public const int KedoPack.LatestFormatVersion = 3;`, `public const int KedoPack.SectionTimeUnit = 4;`, `public DurationUnit KedoPack.TimeUnit { get; }`. `FormatVersion` sigue `= 2` con doc "format written for packs in minutes (the 1.0 layout); other units are written as `LatestFormatVersion`". `Load` acepta `MinFormatVersion..LatestFormatVersion`; con formato 3 exige la sección 4 y un byte que sea un `DurationUnit` definido; cada programa leído recibe `TimeUnit = pack.TimeUnit`.
  - `public static int PackWriter.FormatFor(DurationUnit timeUnit)` → 2 para `Minute`, 3 para las demás. `Write` escribe 3 secciones (bytes de hoy) en `min`; en otra unidad, 4 secciones con la de unidad (1 byte) **al final**, justo antes del trailer. Un programa con `TimeUnit` distinto del catálogo es `ArgumentException` (uso indebido de la API, no error de contenido).

- [ ] **Step 1: Escribir los tests**

```csharp
[Fact]
public void APackInAnotherUnitIsFormat3AndKeepsIt()
{
    var (catalog, bag) = Resolve("attack a { windup = 350ms }\n" + Telegraph, baseUnit: "ms");
    Assert.False(bag.HasErrors);
    var program = BehaviorCompiler.Compile(catalog.ByKey["actor:boss"], Array.Empty<FunctionEntry>(), new SchemaRegistry { Permissive = true }, bag)!;
    var bytes = PackWriter.Write(catalog, new[] { program });
    Assert.Equal(3, BitConverter.ToInt32(bytes, 4));
    var pack = KedoPack.Load(bytes);
    Assert.Equal(DurationUnit.Millisecond, pack.TimeUnit);
    Assert.Equal(new PackInt(350, true), pack.Find("attack:a")!.Body.Fields["windup"]);
    Assert.Equal(DurationUnit.Millisecond, pack.ProgramOf(pack.Find("actor:boss")!)!.TimeUnit);
}

[Fact]
public void AMinutePackStaysFormat2()
{
    var (bytes, _, _) = PackTests.BuildExamples();
    Assert.Equal(2, BitConverter.ToInt32(bytes, 4));
    Assert.Equal(DurationUnit.Minute, KedoPack.Load(bytes).TimeUnit);
}

[Fact]
public void AnUnknownUnitByteIsRejected()
{
    var (catalog, _) = Resolve("attack a { windup = 350ms }", baseUnit: "ms");
    var bytes = PackWriter.Write(catalog, Array.Empty<BehaviorProgram>());
    bytes[^17] = 9;   // the time unit section is the last byte before the 16-byte trailer
    System.IO.Hashing.XxHash128.Hash(bytes.AsSpan(0, bytes.Length - 16), bytes.AsSpan(bytes.Length - 16));
    Assert.Throws<InvalidDataException>(() => KedoPack.Load(bytes));
}
```

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj --filter "FullyQualifiedName~DurationUnitTests"`
Expected: FAIL de compilación: `KedoPack.TimeUnit` no existe.

- [ ] **Step 3: Implementar** según el bloque Interfaces.

- [ ] **Step 4: Spec y API**

KEDO-03, fila del formato del pack: "El runtime carga formatos 1 a 3. El escritor usa el formato más bajo que expresa el contenido: 2 para duraciones en minutos, 3 (sección de unidad, ADR-0014) para las demás". Run: `powershell -File tools/public-api.ps1` (Core: `LatestFormatVersion`, `SectionTimeUnit`, `TimeUnit.get`; Compiler: `PackWriter.FormatFor`).

- [ ] **Step 5: Correr todo**

Run: `dotnet build Kedo.sln -c Release; dotnet test tests/Kedo.Tests/Kedo.Tests.csproj`
Expected: 0 advertencias; todo PASS, en especial `ExamplePackAndFramesArePinned`, `PackBytesAreDeterministic` y `CorruptionAndWrongFormatAreRejected`.

- [ ] **Step 6: Commit**

```bash
git add src/Kedo.Core src/Kedo.Compiler tests/Kedo.Tests/DurationUnitTests.cs spec/KEDO-03-API-Publica-y-Paquetes.md
git commit -m "Pack format 3 carries the time unit; minute packs stay format 2"
```

### Task 11: CLI, describe y skills

**Files:**
- Modify: `src/Kedo.Compiler/Editing/ObjectDescriber.cs:75-130` (`WriteField`)
- Modify: `src/Kedo.Cli/Program.cs:371-437` (`Build`), `:439-485` (`Catalog`)
- Modify: `.agents/skills/kedo-syntax/SKILL.md:41`, `.agents/skills/kedo-author-content/SKILL.md:21` y su espejo `.claude/skills/` con `powershell -File tools/sync-skills.ps1`
- Test: `tests/Kedo.Tests/EditingTests.cs`, `tests/Kedo.Tests/DurationUnitTests.cs`

**Interfaces:**
- Consumes: `ResolvedObject.TimeUnit`, `ResolvedCatalog.TimeUnit`, `FieldSchema.MinDurationMs/MaxDurationMs`, `PackWriter.FormatFor`.
- Produces:
  - `kedo describe` (y `kedo_describe` del MCP): en cada campo de `kind = duration`, `"unit": "<sufijo>"`; con `MinDurationMs`/`MaxDurationMs`, `"min_duration"`/`"max_duration"` como `DurationUnits.Format(ms, Millisecond)`. Los campos `min`/`max` numéricos no cambian.
  - `kedo catalog --json`: `"time_unit": "<sufijo>"` después de `catalog_hash`. `kedo build --json`: `"time_unit"` y `"format"`. La salida de texto no cambia para `min`.
  - Skills: `kedo-syntax` dice "durations (`350ms`, `45s`, `30min`, `2h`, `3d`; an integer count of the module's `base_unit` at runtime, `min` unless `manifest { base_unit = ms }`; a literal that is not whole in that unit is K4014)"; `kedo-author-content` dice que los enteros en campos de duración son unidades base y que la unidad está en `kedo describe` (`unit`).

- [ ] **Step 1: Escribir los tests**

```csharp
[Fact]
public void DescribeNamesTheUnitOfDurationFields()
{
    var bag = new DiagnosticBag();
    var schema = SchemaLoader.Load(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(ActionSchema, "s.kedo"), bag), bag) }, bag);
    var (catalog, _) = Resolve("attack a { windup = 300ms }", baseUnit: "ms", schema);
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream)) { ObjectDescriber.Write(writer, catalog.ByKey["attack:a"], schema); }
    var fields = JsonDocument.Parse(stream.ToArray()).RootElement.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!);
    Assert.Equal("ms", fields["windup"].GetProperty("unit").GetString());
    Assert.Equal("300ms", fields["windup"].GetProperty("value").GetString());
    Assert.Equal("100ms", fields["windup"].GetProperty("min_duration").GetString());
    Assert.Equal("ms", fields["cooldown"].GetProperty("unit").GetString());
}
```
CLI (verificación manual, no hay tests de CLI en el repositorio): ver Step 4.

- [ ] **Step 2: Correr y ver que falla**

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj --filter "FullyQualifiedName~DescribeNamesTheUnit"`
Expected: FAIL: la propiedad `unit` no existe.

- [ ] **Step 3: Implementar** describe, CLI y skills; correr `powershell -File tools/sync-skills.ps1`.

- [ ] **Step 4: Verificar la CLI**

Run (con un directorio temporal que tenga `module.kedo` con `manifest { base_unit = ms }` y `attack a { windup = 350ms }`):
```powershell
dotnet run --project src/Kedo.Cli -c Release -- catalog <dir> --json
dotnet run --project src/Kedo.Cli -c Release -- build <dir> --out=<dir>/a.kpk --json
dotnet run --project src/Kedo.Cli -c Release -- catalog samples/outpost --schema=samples/outpost/schema --json
```
Expected: `"time_unit": "ms"`; `"format": 3`; outpost con `"time_unit": "min"` y el mismo `catalog_hash` que antes del cambio.

- [ ] **Step 5: Correr todo**

Run: `dotnet test tests/Kedo.Tests/Kedo.Tests.csproj`
Expected: todo PASS, incluidos `SkillsTests` y `EditingTests`.

- [ ] **Step 6: Commit**

```bash
git add src/Kedo.Compiler/Editing/ObjectDescriber.cs src/Kedo.Cli/Program.cs .agents/skills .claude/skills tests/Kedo.Tests
git commit -m "Expose the time unit in describe, catalog and build"
```

### Task 12: Verificación final, benchmarks y compatibilidad con Patriark

**Files:**
- Modify: `tests/Kedo.Benchmarks/baseline.json` sólo si `bench-gate` muestra una mejora real (con `-Update`); si no, ningún archivo.

**Interfaces:**
- Consumes: todo lo anterior y los packs de antes de la Tarea 2 (`$env:TEMP\kedo-base-unit\before`).
- Produces: evidencia de que nada de 1.0 cambió.

- [ ] **Step 1: Build y tests**

Run: `dotnet restore --locked-mode; dotnet build Kedo.sln -c Release; dotnet test tests/Kedo.Tests/Kedo.Tests.csproj`
Expected: 0 advertencias, 0 errores; todos los tests PASS.

- [ ] **Step 2: Corpus y API**

Run: `git status --porcelain tests/corpus; git diff --stat main -- src/*/PublicAPI.Shipped.txt`
Expected: sólo los cuatro casos nuevos `durations-*`; ningún cambio en los `PublicAPI.Shipped.txt`.

- [ ] **Step 3: Benchmarks**

Run: `dotnet run -c Release --project tests/Kedo.Benchmarks -- --filter '*' --job short; powershell -File tools/bench-gate.ps1`
Expected: el gate pasa (asignaciones dentro del 10 %, tiempos dentro de 3x); `Run100kInstructions` con 0 B por instrucción.

- [ ] **Step 4: Patriark byte a byte**

Run:
```powershell
dotnet build src/Kedo.Cli -c Release
$out = Join-Path $env:TEMP "kedo-base-unit/after"; New-Item -ItemType Directory -Force $out | Out-Null
$kedo = "src/Kedo.Cli/bin/Release/net8.0/kedo.exe"; $p = "C:/dev/Project-Patriark/content/kedo"
& $kedo build "$p/patriark" --schema="$p/schema" --out="$out/patriark.kpk" --json
& $kedo build "$p/patriark" --schema="$p/schema" --variant=test_1 --out="$out/patriark-test_1.kpk" --json
Get-FileHash "$env:TEMP/kedo-base-unit/before/*.kpk", "$out/*.kpk" | Format-Table Hash, Path
```
Expected: `"diagnostics": []` (ningún K4014 ni K2011), `"time_unit": "min"`, `"format": 2`, y los mismos hashes antes y después para cada pack (la cadena de versión del toolchain no cambió porque este plan no sube la versión).

- [ ] **Step 5: Commit (sólo si se actualizó la línea base)**

```bash
git add tests/Kedo.Benchmarks/baseline.json
git commit -m "Benchmark baseline after the duration base unit"
```

---

## Autorrevisión

1. **Cobertura:** unidades y `ms` (T3, T4); `tick` decidido fuera (D2, ADR en T1); almacenamiento y evaluación (T6); validación min/max y default (T7); pack y compatibilidad de lectores (T10); `wait` sin cambiar `IBehaviorHost` (T9); EvalSuite (T9); CLI, describe, MCP y addon (T11, D11); `PublicAPI.Unshipped.txt` en cada tarea con API nueva; diagnósticos nuevos y contenido aceptado hoy (D3, D4, T2, T6); módulos con unidades distintas (D3, T5); benchmarks (T12); spec en el mismo cambio que cada capacidad (T4-T10).
2. **Pasos:** cada test tiene nombre y aserciones con valores; los pasos de código dan firma, archivo y valores; ningún cuerpo que la firma ya determine.
3. **Tipos:** `DurationUnit`, `DurationUnits.{MillisecondsPer, Suffix, TryParseSuffix, TryConvert, ConvertTruncating, Format}`, `ModuleTimeUnit.Determine`, `DurationLowering.ToUnit`, `ResolvedCatalog.TimeUnit`, `ResolvedObject.TimeUnit`, `RDuration.{Unit, Amount}`, `FieldSchema.{Min,Max,Default}DurationMs`, `BehaviorProgram.TimeUnit`, `IBehaviorClock.Now`, `AwaitRequest.Unit`, `KedoPack.{LatestFormatVersion, SectionTimeUnit, TimeUnit}`, `PackWriter.FormatFor`, `TraceHost.{NowMilliseconds, Now}`: mismos nombres en todas las tareas.
4. **Review Focus:** las cinco líneas tienen su test en T5, T6 y T9.
5. **Proporción:** el código del plan son tests y firmas; los cuerpos quedan para quien implementa.
