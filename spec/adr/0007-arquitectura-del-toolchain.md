# ADR-0007 — Arquitectura del toolchain orientada a rendimiento

Fecha: 15/10/2026 · Estado: aceptado (decisión tomada con delegación del creador, issue #2) · Base: anexo [F](../../docs/research/anexos/F-Rendimiento-y-arquitectura-del-toolchain.md); prioridades de [KEDO-00](../KEDO-00-Principios-y-Alcance.md).

## Contexto

Rendimiento es la primera prioridad. Hay que fijar la forma del lexer, el parser, el AST, el modelo incremental, la VM, el artefacto compilado, el hash y la medición antes de escribir el parser (#3), porque cambiar cualquiera de ellos después cuesta reescribir.

## Decisiones

### D1. Lexer sobre bytes UTF-8, a mano

`ReadOnlySpan<byte>` sin transcodificar; `SearchValues<byte>` para espacios, comentarios y cadenas; tokens como structs de 12 bytes (`kind:u16, flags:u16, start:int, length:int`) en un array agrupado; identificadores internados a `SymbolId:int` con una tabla propia indexada por span; flag `newlineBefore` en cada token para terminar sentencias sin comas. Evidencia: combinadores (Sprache ~23x, Superpower ~10x, Pidgin 2–4x más lentos que un parser a mano en JSON) y ANTLR C# 2–9x más lento que ANTLR Java.

### D2. Parser recursivo descendente + Pratt, resiliente, a mano

LL(1–2): cada constructo empieza por palabra clave o `ident =` (ADR-0004). Expresiones con tabla de binding power. Recuperación de errores al estilo matklad (nodos de error, conjuntos de recuperación por ancestros, combustible en debug): el parser siempre produce un árbol y diagnósticos, nunca aborta. Parseo por archivo en paralelo; fusión determinista por ruta ordenada.

### D3. AST como arena plana con trivia

Eventos `Open/Close/Advance` → arena `Node { kind:u16, mainToken:int, lhs:int, rhs:int }` + `extra int[]`; comentarios y espacios conservados como tokens (estilo rowan) para que el formateador sea un reescritor de espacios y nunca pierda comentarios. Sin árboles rojo/verde en v1 (su beneficio es la reparse incremental dentro de un archivo, innecesaria para un compilador de contenido); un array de padres y un cursor `ref struct` cubren a las herramientas. Serialización del AST a JSON (source-gen) solo para tooling y MCP.

### D4. Incrementalidad por archivo con corte temprano

Modelo Salsa simplificado: tablas de memoización por `XxHash128(bytes del archivo)`; etapas como funciones puras sobre records pequeños con igualdad por valor (`Parse(file) → DeclIndex → grafo → Resolved(obj) → IR`); corte temprano cuando el resultado es igual (nada río abajo se recalcula); dos niveles de durabilidad (contenido base vs módulo de campaña); caché persistida en disco direccionada por contenido. Resolución en niveles de Kahn para paralelismo con orden determinista. No se construye un sistema de queries de grano fino en v1; las etapas puras permiten cambiarlo después.

### D5. Catálogo en columnas

Struct-of-arrays por esquema; `FrozenDictionary<SymbolId,int>` para id→fila (~2x más rápido en lectura que `Dictionary`); listas en pools `int[]` compartidos; IR en sección aparte de carga diferida; objetos resueltos inmutables que comparten estructura con el padre.

### D6. VM de registros, instrucciones de 32 bits, sin asignaciones

Máquina de registros; instrucción fija `op:8|a:8|b:8|c:8` o `op:8|a:8|imm:16`; un solo `switch` denso; acceso con `ref`/`Unsafe.Add` solo tras un verificador de bytecode en carga; sin `try/catch` ni `async` en el bucle; la suspensión devuelve un estado (`Suspended`, `Completed`, `Failed`). Valores `int` (64 bits en registros lógicos; handles para texto, ids, listas; punto fijo para fracciones). Frames `{functionId, pc, regBase, regCount}` sobre un único `int[]`; el estado serializa a tres arrays más una tabla de ids de función por nombre (sobreviven al re-enlace). Cero asignaciones por instrucción, verificado en CI. Mantener la VM monomórfica y sin genéricos pesados porque Android corre Mono, no NativeAOT (anexo F §4.4).

### D7. Artefacto compilado: "KEDO Pack" binario seccionado

Cabecera `{magic, formatVersion, langVersion, toolHash, tabla de secciones}` + secciones `{strings, symbols, objectTable (columnas), irCode (uint[]), irConstants, debugInfo opcional, hash}`; carga con un `ReadAllBytes` y `MemoryMarshal.Cast` por sección; solo primitivos blittables, little-endian explícito; tests de ida y vuelta y golden files; rechazo de `formatVersion` desconocido y recompilación desde fuente. Sin MemoryPack ni MessagePack por defecto (MemoryPack como alternativa si mantener layouts a mano pesa; MessagePack solo si aparecen consumidores en otros lenguajes). `System.Text.Json` source-gen solo para diagnósticos, AST, índices de caché y salida de herramientas.

### D8. Hash: XxHash128 sobre la forma binaria canónica

`System.IO.Hashing.XxHash128` (oficial, no criptográfico, ~10x más rápido que SHA-256 en .NET) para hash de contenido y claves de caché; SHA-256 opcional del pack final para integridad de distribución. Nunca SHA-256 sobre JSON canónico en el camino caliente. Patriark migra su hash de contenido al adoptar KEDO (KEDO-02 §12).

### D9. Registro explícito sin reflexión

`KedoRuntimeBuilder` donde el juego registra componentes (`descriptor` como datos: ids de campo y tipos), tipos, hooks, validadores y funciones nativas (`delegate*` indexado por id entero). Generador de código opcional (`Kedo.Generators`) que emite esas llamadas desde atributos; sin `ModuleInitializer` por defecto (explícito antes que implícito; no soportado en algunos hosts). El esquema es dato, no jerarquía de clases: un juego nuevo añade descriptores, no subclases.

### D10. Medición y presupuestos

BenchmarkDotNet con jobs JIT y NativeAOT y `MemoryDiagnoser`; en PR: puertas **exactas de asignación** (0 B por instrucción de VM; `Allocated` no crece en parse) y puertas **laxas de tiempo** (alerta > 120 %, fallo > 150 % frente al último baseline, porque los runners hospedados son ruidosos); nocturno: tests estadísticos (Welch, Mann-Whitney) en una máquina fija; además tamaño del artefacto y tiempo de carga en frío; mediciones esporádicas en Android real.

Presupuestos iniciales (a refinar con las primeras mediciones reales):

| Métrica | Escritorio x64 .NET 8 | Android medio arm64 (Mono) |
|---|---|---|
| Lexer + parser | ≥ 50 MB/s de fuente, un hilo | n/a (se compila fuera) |
| Parse + resolve de 10k objetos (~5 MB), 8 núcleos | ≤ 500 ms en frío; ≤ 50 ms incremental por archivo | n/a |
| Carga del pack compilado, 10k objetos | ≤ 20 ms | ≤ 100 ms |
| Instrucciones enteras de VM por segundo | ≥ 50 M | ≥ 10 M |
| Asignaciones por instrucción | 0 B | 0 B |
| Guardar/restaurar 100 frames | ≤ 50 µs, sin asignaciones fuera del buffer | ≤ 250 µs |
| Heap gestionado con 100k objetos cargados | ≤ 64 MB | ≤ 96 MB |

Medición del 17/10/2026 (#16; escritorio x64, .NET 8, un hilo, corpus sintético de 10k objetos, job corto): parse 18 ms y 60 MB asignados (antes 57 ms y 96 MB: los nodos abiertos del parser son structs sobre una pila de hijos compartida, sin una lista por nodo), parse + declaraciones 76 ms (antes 101 ms), resolve 235 ms y 121 MB (antes 243 ms y 151 MB: un record mezclado se copia una vez, la etapa B arma la ruta del campo sólo para un diagnóstico y el hash codifica en la pila), carga del pack 7,4 ms y 7 MB (antes 50 ms y 38 MB: los cuerpos se decodifican al primer acceso; cargar y decodificar todo, 35 ms). Parse + resolve en frío queda en 311 ms, dentro de los 500 ms; la carga del pack, dentro de los 20 ms. `tests/Kedo.Benchmarks/baseline.json` guarda esta medición y `tools/bench-gate.ps1` falla el benchmark nocturno si una asignación crece más de 10 % o un tiempo más de 4 veces.

**Medido el 16/10/2026** (escritorio x64, .NET 8, `tests/Kedo.Benchmarks`, corpus sintético de 10k objetos ≈ 5 MB, un hilo):

| Métrica | Medido | Presupuesto | Estado |
|---|---|---|---|
| Lexer 10k objetos | 10,1 ms (~500 MB/s) | ≥ 50 MB/s | cumple |
| Parser 10k objetos | 55,5 ms, 95,7 MB asignados | — | tiempo bien; asignación alta (listas de tokens y nodos, una lista por frame del árbol): optimizar |
| Parser + declaraciones 10k | 90,4 ms | — | — |
| Resolver 10k | 198 ms, 146 MB asignados | parse + resolve ≤ 500 ms | cumple en tiempo (≈ 290 ms en total); `OrderedFields.With` copia por escritura: optimizar |
| Formateador 10k | 23,5 ms | — | — |
| Escritura del pack 10k | 71 ms | — | — |
| Carga del pack 10k | 45,9 ms, 37,6 MB | ≤ 20 ms | **no cumple**: la decodificación es ansiosa; pasar a decodificación por objeto bajo demanda |
| Carga del pack 1k | 0,9 ms | — | — |
| VM, instrucciones enteras | 2,4 ns por instrucción (~410 M/s) | ≥ 50 M/s | cumple |
| VM, asignaciones por instrucción | 0 B | 0 B | cumple (la primera versión asignaba 56 B por el `with` del frame; corregido) |

Android y NativeAOT: sin medir todavía.

### D11. Diagnósticos y formateador

Diagnóstico como `readonly record struct` con código `Kxxxx`, severidad, span primario, etiquetas secundarias, notas y arreglos con aplicabilidad (`MachineApplicable`, `MaybeIncorrect`, `HasPlaceholders`) al estilo rustc; mensajes en minúscula, sin punto final, identificadores entre comillas invertidas; acumulados por etapa y cacheados con el resultado. Formateador sin opciones (estilo gofmt) sobre el array de tokens sin pérdida; respeta los saltos de línea del autor; `fmt(fmt(x)) == fmt(x)` y `parse(fmt(x)) ≡ parse(x)` módulo trivia, en CI.

## Alternativas rechazadas

- Generadores de parser (ANTLR, Pidgin, Superpower): 2–20x más lentos y asignan mucho; peor recuperación de errores.
- Árboles rojo/verde: coste de memoria y complejidad sin beneficio para compilación por lotes.
- Sistema de queries de grano fino (Salsa completo) en v1: inversión grande; la memoización por archivo con corte temprano da la mayor parte del beneficio.
- Máquina de pila o árbol-walker (Ink, Yarn): más simple, pero más despacho y más asignaciones; Ink y Yarn no tienen presupuesto de rendimiento comparable.
- Serializadores de terceros para el pack: dependencia y caveats de layout entre runtimes; el pack ya son columnas blittables.
- SHA-256 sobre JSON canónico: coste de serialización y ordenación innecesario.

## Consecuencias y riesgos

- Código `unsafe`/`Unsafe.Add` en el bucle de la VM exige verificador de bytecode, build de depuración con comprobaciones y fuzzing del cargador del pack.
- Formato binario propio = versionado propio; mitigado con primitivos blittables, endianness explícita y golden files.
- Valores enteros limitan ergonomía (texto y listas como handles); se añaden operaciones de texto/listas en la VM solo con benchmarks que prueben que el camino entero no se degrada.
- Android con Mono: medir en dispositivo temprano; evitar despacho virtual genérico.
- La incrementalidad simple puede quedarse corta con módulos enormes; las etapas puras permiten subir a un sistema de queries sin rehacer el compilador.
