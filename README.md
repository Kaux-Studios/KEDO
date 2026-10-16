# KEDO — Kaux Enriched Data Object

Lenguaje y toolchain propios de Kaux Studios para definir contenido de juego enriquecido: objetos declarativos con herencia de valores, importación, mixins y componentes; expresiones; funciones y triggers que compilan a un IR determinista con continuaciones guardables; referencias a assets de cualquier clase. Independiente del motor. El primer cliente es [Project Patriark](https://github.com/Kaux-Studios/Project-Patriark).

Estado: **alpha** (16/10/2026). Spec v0.1 aceptada; lexer, parser, formateador, resolver (capas, mezcla, borrado, procedencia, validación, hash) y CLI `parse | check | resolve | explain | fmt` funcionando sobre los ejemplos de Patriark. Pendientes: `kedo schema`, IR/VM, MCP, addon de Godot, benchmarks.

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
src/Kedo.Cli     herramienta `kedo`: parse | check | fmt | build | schema | mcp
addons/kedo      addon genérico de Godot (GDScript): biblioteca de contenido, formularios, diagnósticos, undo
tests/           golden corpus + xUnit + tests GDScript headless
samples/         mini-juego de muestra, distinto de Patriark, para probar que el núcleo es genérico
docs/research    investigación (origen Patriark y posteriores); docs/plans planes por bloque
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
```

## Documentación

- [Principios y alcance](spec/KEDO-00-Principios-y-Alcance.md)
- [ADR](spec/adr/) · [API pública y paquetes](spec/KEDO-03-API-Publica-y-Paquetes.md) · [Gramática](spec/KEDO-01-Gramatica.md) · [Resolución](spec/KEDO-02-Resolucion.md)
- [Investigación](docs/research/00-LEEME.md) y [planes](docs/plans/)
- [Cómo contribuir](CONTRIBUTING.md) · [AGENTS.md](AGENTS.md)

Propietario. Ver [LICENSE](LICENSE).
