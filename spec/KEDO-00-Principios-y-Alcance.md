# KEDO-00 — Principios y alcance

Revisado: 15/10/2026 · Autoridad: decisiones del creador (Kaux Studios). Este documento fija qué es KEDO, qué no es, en qué orden se priorizan las cualidades y qué garantiza cada versión.

## Qué es

KEDO (Kaux Enriched Data Object) es un lenguaje de texto y su toolchain para definir **objetos de datos enriquecidos** de un juego: definiciones de contenido con herencia de valores entre objetos, importación y mixins, componentes, expresiones, funciones y triggers, y referencias a assets. Un archivo `.kedo` describe uno o varios objetos. El toolchain los parsea, resuelve, valida, formatea, compila a un **catálogo resuelto** (inmutable, hasheado) y a un **IR de comportamientos** que ejecuta una VM determinista con estado guardable.

El núcleo no conoce ningún motor. Un juego lo consume como paquete .NET, registra sus tipos, componentes, hooks y validadores, y conecta el host de la VM a su simulación.

## Qué no es

- No es un motor de reglas del juego: las reglas (dados, inventario, guardado) viven en el juego. KEDO provee datos resueltos y ejecuta lógica de contenido contra una API restringida que el juego define.
- No es un lenguaje de propósito general: sin flotantes, sin E/S, sin reloj real, sin azar propio, sin carga dinámica de código. Las capacidades nuevas se agregan como primitivas, no como facilidades generales.
- No es un formato de guardado: los frames de la VM son datos que el juego guarda dentro de su propio snapshot.

## Prioridades, en orden

1. **Rendimiento.** Medido, con presupuestos y regresiones en CI. Parseo y resolución de decenas de miles de objetos en tiempos de carga de móvil; VM sin asignaciones por instrucción; cero reflexión; NativeAOT y trimming limpios.
2. **Facilidad de uso.** Sintaxis pequeña, explícita, con una sola forma de expresar cada cosa; diagnósticos con código estable, posición exacta y sugerencia; formateador canónico; herramientas para personas y para agentes de IA.
3. **Escalabilidad.** Módulos, compilación incremental por archivo con caché por hash, catálogos inmutables, carga diferida de IR.
4. **Flexibilidad.** Tipos, componentes, hooks, validadores y fichas de editor por extensión, sin tocar el núcleo. Varios juegos sobre el mismo núcleo, cada uno fijado a un major.

Cuando dos prioridades chocan, gana la de número menor, y la decisión se registra en un ADR.

## Garantías por versión

- **1.x es aditivo.** Un archivo `.kedo` válido en 1.0 es válido e idéntico en significado en cualquier 1.x. El AST serializado, el IR, el formato de frames, los códigos de diagnóstico y la API pública solo crecen. Lo obsoleto emite advertencia y sigue funcionando hasta el siguiente major.
- **Un major rompe.** 2.0 puede cambiar gramática, IR o API; trae `kedo migrate` de N a N+1 y un período en que ambos majors se publican.
- **Compatibilidad de clientes.** Project Patriark depende de `[1.0, 2.0)`. Un proyecto futuro puede nacer en 2.x sin obligar a Patriark a migrar.
- **API pública vigilada.** Analizadores de API pública y comparación binaria entre versiones en CI; un cambio incompatible no compila sin bump de major.

## Qué entra en el núcleo y qué queda en cada juego

| KEDO | Juego |
|---|---|
| Gramática, parser, AST, formateador | Tipos de objeto del juego registrados por extensión |
| Resolver: herencia, import, mixins, componentes, ops, referencias | Catálogo de hooks del juego y sus contextos tipados |
| Diagnósticos con código, posición y arreglo sugerido | Validadores semánticos del juego |
| IR, VM determinista, frames serializables, presupuestos | Host: consultas, efectos, azar y reloj conectados a la simulación |
| Modelo de valores: enteros, booleanos, texto, ids, enums, listas, records | Significado de cada id y de cada efecto |
| Claves de asset abstractas | Manifiesto y resolución de assets del motor |
| Registro de extensiones sin reflexión, contextos JSON source-generated | Compilación del catálogo resuelto al modelo del juego |
| CLI `kedo` y servidor MCP | Integración en el pipeline del juego |
| Addon genérico de Godot (lista, formularios desde esquema, diagnósticos, undo) | Fichas específicas por familia |
| Corpus de tests del lenguaje y mini-juego de muestra | Tests de integración con contenido real |

## Decisiones iniciales fijadas

- Lenguaje de texto propio con extensión `.kedo`. El AST se serializa a JSON como artefacto de intercambio y compilado; nadie edita ese JSON a mano. [ADR-0003](adr/0003-json-solo-como-artefacto.md).
- Repositorio aislado de Patriark, consumido como paquete NuGet privado con SemVer. [ADR-0002](adr/0002-repositorio-aislado.md).
- Valores enteros en el modelo de la VM. Porcentajes en puntos básicos. Sin flotantes en 1.x.
- Hooks: catálogo cerrado y versionado del juego; señales encoladas, nunca recursivas; efectos idempotentes por clave; presupuesto de pasos en el bucle de despacho.
- Herramientas para IA desde el inicio: `--json` en toda la CLI, servidor MCP sobre el mismo núcleo, skills y ejemplos dorados en el repo.

## Lo que la investigación de origen dejó resuelto

La investigación hecha para Patriark (ver [docs/research](../docs/research/00-LEEME.md)) descartó: `.tres` como fuente canónica, JSON puro como formato de autoría, intérpretes de terceros (MoonSharp, Lua-CSharp, Jint, NLua, Roslyn scripting, GDScript en runtime) como ejecutor de comportamientos, y plugins de editor en C# por la recarga de ensamblados de Godot 4.x. Fijó: resolver con mezcla por id y cuatro ops en orden fijo; dos espacios de id; hash del objeto resuelto; registro explícito AOT-safe; diagnóstico con código y puntero; IR propio con frames como datos; addon de Godot en GDScript que habla con la CLI.

## Preguntas abiertas al inicializar

Las responde la investigación F (rendimiento y toolchain) y G (herramientas para IA), y después la spec v0.1 de la gramática:

1. Representación del AST (árbol inmutable estilo Roslyn vs arrays compactos) y modelo incremental.
2. Máquina de registros vs pila y representación de valores en la VM.
3. Formato binario del catálogo compilado y del IR (STJ source-gen vs MemoryPack vs propio) y hash.
4. Superficie del servidor MCP y relación con un LSP.
5. Sintaxis concreta: delimitadores, declaración de objetos, `extends`/`import`/`mixin`, expresiones, funciones, triggers, refs de assets.
