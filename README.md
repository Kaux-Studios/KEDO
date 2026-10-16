# KEDO — Kaux Enriched Data Object

Lenguaje y toolchain propios de Kaux Studios para definir contenido de juego enriquecido: objetos declarativos con herencia de valores, importación, mixins y componentes; expresiones; funciones y triggers que compilan a un IR determinista con continuaciones guardables; referencias a assets de cualquier clase. Independiente del motor. El primer cliente es Project Patriark, un juego de Kaux Studios (repositorio privado).

Estado: **alpha** (16/10/2026). Spec v0.1 aceptada; lexer, parser, formateador, resolver (capas, mezcla, borrado, procedencia, validación, hash), esquema como dato (`kedo_type`…) con `kedo schema`, compilador de hooks y funciones a IR, VM de registros con frames guardables, KEDO Pack binario (`kedo build`) y CLI completa, benchmarks, servidor MCP y skills, addon de Godot y el mini-juego `samples/outpost`. Desde el 16/10/2026: decimales de punto fijo, mapas con claves libres, restricciones de esquema (tamaño de listas, largo y valores de textos), advertencias de mezcla y orden de declaración ([ADR-0008](spec/adr/0008-decimales-de-punto-fijo.md) a [ADR-0010](spec/adr/0010-diagnosticos-de-mezcla-y-orden-de-declaracion.md)). Patriark compila todo su contenido con KEDO desde su propio repositorio. Pendientes: [backlog](https://github.com/Kaux-Studios/KEDO/issues).

## Principios (en orden)

1. **Rendimiento** antes que facilidad de uso. Lexer sobre spans, AST compacto, resolución incremental, VM sin asignaciones por instrucción, serialización por source generators, cero reflexión, compatible con NativeAOT y trimming.
2. **Facilidad de uso**: sintaxis pequeña y explícita, una sola forma de hacer cada cosa, diagnósticos con código, posición y sugerencia de arreglo, formateador canónico.
3. **Escalable**: decenas de miles de objetos, módulos, compilación incremental por archivo, catálogos inmutables.
4. **Flexible**: tipos, componentes, hooks y validadores se registran por extensión sin tocar el núcleo. Varios juegos sobre el mismo núcleo.
5. **Determinista y guardable**: valores enteros, azar y reloj inyectados por el host, frames de ejecución serializables, efectos idempotentes.
6. **Pensado para agentes de IA**: CLI con salida JSON, servidor MCP sobre el mismo núcleo, skills y ejemplos dorados desde el inicio.

Detalle en [spec/KEDO-00-Principios-y-Alcance.md](spec/KEDO-00-Principios-y-Alcance.md).

## Versionado

SemVer. **1.x nunca rompe**: gramática, AST serializado, IR, frames y API pública solo crecen; lo obsoleto avisa y sigue funcionando. Un cambio incompatible es 2.0 y trae `kedo migrate`. Patriark depende de `[1.0, 2.0)`: debe funcionar con 1.9.99. Ver [ADR-0001](spec/adr/0001-versionado-semver.md).

## Estructura

```
spec/            especificación del lenguaje, principios, ADR
src/Kedo.Core    valores, diagnósticos, lector del pack, VM determinista, host  (net8.0, AOT, superficie mínima)
src/Kedo.Compiler lexer, parser, AST, resolver, formateador, esquema, escritor del pack  (net8.0, AOT)
src/Kedo.Cli     herramienta `kedo`: parse | check | resolve | explain | compile | build | schema | fmt (todo con --json)
addons/kedo      addon de Godot (GDScript, sin cargar el ensamblado del juego): biblioteca de objetos, editor con resaltado, check/fmt vía CLI, diagnósticos con salto, cambios pendientes y borradores
tests/           golden corpus + xUnit + tests GDScript headless
samples/         mini-juego de muestra, distinto de Patriark, para probar que el núcleo es genérico
docs/research    investigación (origen y anexos A, B, D–I); docs/plans planes por bloque
```

## Comandos

```powershell
dotnet restore --locked-mode
dotnet build Kedo.sln -c Release
dotnet test tests/Kedo.Tests/Kedo.Tests.csproj
dotnet run --project src/Kedo.Cli -- version
dotnet run --project src/Kedo.Cli -- check spec/ejemplos            # parse + resolve + validate (modo permisivo)
dotnet run --project src/Kedo.Cli -- resolve spec/ejemplos          # catálogo resuelto
dotnet run --project src/Kedo.Cli -- explain spec/ejemplos event:road.sentinel.depot "options[risk].check.difficulty"
dotnet run --project src/Kedo.Cli -- fmt spec/ejemplos --check
dotnet run --project src/Kedo.Cli -- check --schema=spec/ejemplos/schema spec/ejemplos   # con esquema cerrado
dotnet run --project src/Kedo.Cli -- compile --schema=spec/ejemplos/schema spec/ejemplos # IR de hooks y funciones
dotnet run --project src/Kedo.Cli -- build --schema=spec/ejemplos/schema spec/ejemplos --out=patriark.kpk
dotnet run --project src/Kedo.Cli -- schema spec/ejemplos/schema --json              # JSON Schema para formularios y agentes
```

## Addon de Godot

`addons/kedo` es un plugin de pantalla principal escrito en GDScript que habla con la CLI (`Editor Settings > kedo/command`, `kedo/content_paths`, `kedo/schema_paths`): lista los objetos del catálogo por tipo con filtro, abre el archivo en un `CodeEdit` con resaltado KEDO, ejecuta Check y Format, muestra diagnósticos con salto a línea, integra los cambios pendientes en el diálogo de cierre del editor, guarda borradores en `user://` y bloquea Play si el contenido tiene errores. Los formularios generados desde el esquema quedan para una entrega posterior. Tests headless: `tools/godot-test.ps1 -GodotPath <godot 4.6>`.

## Herramientas para agentes de IA

- **Skills** en `.agents/skills/` (Codex, Cursor) espejadas en `.claude/skills/` (Claude Code; `tools/sync-skills.ps1` las sincroniza y un test impide que diverjan): `kedo-syntax` (gramática y ejemplos), `kedo-author-content` (bucle escribir → `kedo check` → corregir → `kedo fmt` → `kedo resolve`), `kedo-toolchain` (cómo extender el compilador).
- **Servidor MCP** `kedo mcp` (stdio, sin estado, SDK oficial 2.2): herramientas `kedo_check`, `kedo_format`, `kedo_resolve`, `kedo_explain`, `kedo_catalog`, `kedo_compile`, `kedo_schema`. Registrado para Claude Code en `.mcp.json` y para Codex en `.codex/config.toml` (requiere `dotnet build -c Release` previo).
- Toda la CLI acepta `--json`; los diagnósticos llevan código, posición y sugerencia para cerrar el bucle de corrección sin intervención humana.

## Documentación

- [Principios y alcance](spec/KEDO-00-Principios-y-Alcance.md)
- [ADR](spec/adr/) · [API pública y paquetes](spec/KEDO-03-API-Publica-y-Paquetes.md) · [Gramática](spec/KEDO-01-Gramatica.md) · [Resolución](spec/KEDO-02-Resolucion.md)
- [Investigación](docs/research/00-LEEME.md) y [planes](docs/plans/)
- [Cómo contribuir](CONTRIBUTING.md) · [AGENTS.md](AGENTS.md)

Repositorio público desde el 16/10/2026; el código sigue siendo propietario de Kaux Studios: verlo no concede permisos de uso, copia ni distribución. Ver [LICENSE](LICENSE).
