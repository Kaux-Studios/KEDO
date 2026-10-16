# GDO-Plan-01 — Contrato común de GDO y registro de tipos

Preparado: 15/10/2026 · Issue [#3](https://github.com/Kaux-Studios/Project-Patriark/issues/3) · Estado: **plan para revisión previa**. Base: [01 — Origen](../research/01-Origen.md) decisiones D2, D3, D4; evidencia en el anexo [D](../research/anexos/D-Patrones-de-datos-en-otros-juegos.md) y en el mapa del código de Patriark (anexo C, en ese repositorio).

## Objetivo

Definir en `src/domain` (y lo mínimo en `src/infrastructure`) el contrato neutral de Game Data Objects: identidad, tipo, versión, tags, padre, componentes, referencias, metadatos editoriales; el resolver de composición; el registro explícito de tipos; el modelo de diagnósticos; el hash canónico. Sin Godot, sin reflexión, con tests de golden files. No cambia todavía ningún contenido jugable.

## Dependencias

- Spike S5 (equivalencia de `SnapshotCodec.HashCanonical` con RFC 8785 sobre los packs actuales).
- Ninguna otra. Es el primer plan.

## Alcance

**Incluye**

1. Tipos del sobre: `GdoId` (`módulo:ruta`), `GdoTypeId`, `AssetKey`, `GdoDefinition` (DTO de autoría), `ResolvedGdo`, `ResolvedCatalog`, `GdoInstanceRef` (`DefId` + `DefVersion`, para uso futuro de instancias).
2. Gramática de ids: `[a-z0-9_.-]+ ":" [a-z0-9_./-]+`; módulo obligatorio; sin namespace por defecto.
3. Registro: `IGdoRegistry` / `IGdoModule` con entradas por tipo (`JsonTypeInfo` source-generated, validador, actualizador de versión) y por componente. Un módulo inicial `patriark` registrando los cuatro tipos de ejemplo: `campaign`, `actor`, `place`, `ambient` **sin atributos de gameplay** (solo sobre, nombre, descripción, tags, refs).
4. Resolver: índice por `(tipo, id|abstract)`; duplicados; padre único del mismo tipo; profundidad ≤ 4; ciclos; mezcla de escalares, componentes por id (`null` borra), tags por unión, arrays por `"id"` o reemplazo; ops `delete → extend → relative → proportional` sobre punteros JSON; abstractos fuera del catálogo; referencias (`refs.*`) verificadas contra el catálogo y, para `asset:`, contra un manifiesto que en este plan es una lista inyectada (el real llega en el Plan 04).
5. Diagnósticos: `DiagnosticDescriptor` (código estable `GDO0001`…, título, categoría, severidad por defecto), `GdoLocation` (archivo, id, tipo, puntero RFC 6901, línea), `Diagnostic`, `FixHint`. Los validadores **acumulan**, no lanzan; el resolver devuelve el catálogo con su lista aunque haya errores.
6. Hash: `ContentHash` por objeto resuelto sin `$editor` ni `Source`; `CatalogHash` sobre líneas `tipo:id=hash` ordenadas. Reutilizar `SnapshotCodec.HashCanonical` si S5 confirma equivalencia; si no, implementar JCS y documentar la diferencia.
7. Guardas de compilación: `IsAotCompatible=true` y `JsonSerializerIsReflectionEnabledByDefault=false` en `Patriark.Domain.csproj` e `Patriark.Infrastructure.csproj`; corregir las dos llamadas reflexivas incidentales (`JsonContentCatalog.cs:44`, `PoCatalog.cs:30`) si el analizador las marca.
8. Tabla de códigos de diagnóstico documentada en `doc/` (nombre provisional `CONTENT-03-GDO.md`, se consolida en el Plan 07).

**Excluye**

- Convertir el pack actual (Plan 02). Esquema JSON generado para el editor (Plan 02 lo emite; aquí solo se deja el modelo de tipos preparado). Parches entre módulos, variantes de campaña, atributos por familia (#35). Generador de código por atributos (posterior).

## Diseño

### Ubicación

```
src/domain/content/gdo/
  GdoId.cs, GdoTypeId.cs, AssetKey.cs
  GdoDefinition.cs            // autoría: id, abstract, type, version, parent, tags, components(JsonElement), ops, refs, editor, source
  ResolvedGdo.cs, ResolvedCatalog.cs
  GdoRegistry.cs, IGdoModule.cs, GdoTypeEntry.cs, GdoComponentEntry.cs
  GdoResolver.cs              // índice, herencia, ops, refs, hash
  Diagnostics/ (Descriptor, Location, Diagnostic, FixHint, Codes.cs)
  PatriarkModule.cs           // registra campaign, actor, place, ambient
src/domain/content/gdo/types/
  CampaignDefinition.cs, ActorDefinition.cs, PlaceDefinition.cs, AmbientDefinition.cs   // records inmutables
src/infrastructure/content/gdo/
  GdoJsonContext.cs           // [JsonSerializable] para el sobre y los cuatro tipos; snake_case; Disallow
  GdoFileReader.cs            // lee content/gdo/**/*.json → GdoDefinition con Source (archivo, línea)
```

Los componentes llegan como `JsonElement` en el DTO y se materializan a records tipados por el registro al resolver. Así el sobre no conoce los tipos y el núcleo no cambia al añadir uno.

### Reglas que fijan comportamiento (se prueban una por una)

| Código | Regla |
|---|---|
| GDO0001 | Id duplicado en el mismo tipo |
| GDO0002 | Padre inexistente |
| GDO0003 | Ciclo de herencia |
| GDO0004 | Referencia a un abstracto |
| GDO0005 | Profundidad de herencia > 4 |
| GDO0006 | Padre de otro tipo |
| GDO0010 | Op sobre campo no soportado |
| GDO0011 | `delete` de entrada ausente (advertencia) |
| GDO0020 | Conflicto de dos fuentes sobre el mismo puntero sin orden explícito |
| GDO0030 | `refs.*` no resuelve a un objeto del tipo declarado |
| GDO0031 | Referencia a id deprecado resuelta por ledger (advertencia) |
| GDO0040 | `asset:` ausente del manifiesto |
| GDO0050 | Id con gramática inválida |
| GDO0060 | Tipo no registrado |

### Determinismo

Iteración en orden `(tipo, id)` ordinal; sin enumerar `Dictionary` hacia el resultado; números en campos de gameplay como enteros (minutos, unidades internas, puntos básicos); parseo con cultura invariante.

## Pasos

1. S5: script en `tools` que canonicaliza `prototype-9.json` con `HashCanonical` y con una implementación JCS mínima; comparar. Decidir.
2. Escribir los tipos del sobre y el registro con tests unitarios de gramática de ids y de registro/duplicados.
3. Resolver por etapas con golden files en `tests/content/gdo/golden/<caso>/{input/*.json, expected.json, diagnostics.json}`: herencia simple, profundidad, ciclo, componentes por id, `null` borra, arrays por id vs reemplazo, cada op, conflicto, refs válidas/ inválidas, abstractos.
4. Diagnósticos: test que un catálogo con tres errores devuelve los tres, no solo el primero.
5. Hash: test de estabilidad (`$editor` cambia → hash igual; `version` cambia → hash distinto; orden de claves → igual).
6. `IsAotCompatible` en dominio/infraestructura; `dotnet build` limpio de advertencias IL.
7. Documentar códigos y reglas en el doc provisional.

## Verificación

- `dotnet test tests/Patriark.Tests.csproj` verde; `tools/check.ps1 -SkipGodot` verde (nada del juego cambia).
- Cobertura: cada código GDO00xx tiene al menos un test que lo produce y otro que no.
- Sin advertencias `IL2xxx`/`IL3xxx` en dominio e infraestructura.

## Criterios de cierre

- [ ] Sobre, registro, resolver, diagnósticos y hash implementados con golden tests.
- [ ] Cuatro tipos de ejemplo registrados sin atributos de gameplay.
- [ ] Documento de códigos y reglas.
- [ ] Guardas AOT activas y build limpio.
- [ ] Nada del catálogo jugable actual cambió (hash de `prototype-9` idéntico antes y después).

## Riesgos

- Sobre-diseñar ops y herencia antes de tener familias reales: mantener las cuatro ops y la profundidad 4 como límites duros; #35 pide más si lo demuestra.
- S5 negativo (hashes distintos): elegir una canonicalización y migrar el hash de contenido solo cuando el Plan 02 compile el pack; documentar.
- `IsAotCompatible` puede sacar advertencias en código existente ajeno a GDO: corregir solo lo trivial, registrar el resto en #42.

## Preguntas para la revisión previa

1. ¿`module:path` con `/` en la ruta, o ids planos como hoy (`bridge.reinforce`)? Propuesta: nuevo esquema para GDO; los ids actuales se conservan como `patriark:<id-actual>` al compilar (Plan 02).
2. ¿Componentes por id de tipo como propone D4, o campos planos por familia? Propuesta: componentes; cada familia decide en #35 qué va dentro.
3. ¿Hash sobre resuelto (propuesto) o sobre archivos fuente? Resuelto permite refactorizar abstractos sin invalidar guardados.
4. ¿Se acepta `IsAotCompatible` ahora, aunque iOS sea #42? Propuesta: sí, es barato y evita sorpresas.
