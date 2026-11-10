# ADR-0015 — Entrada rápida de comportamientos

Fecha: 10/11/2026 · Estado: propuesto · Origen: issue #20 (extensión K4 pedida por survivors-like, segundo consumidor de KEDO).

## Contexto

Un juego de acción corre hooks de fases de misión y de jefe, del escape y de pasivas únicas: de decenas a cientos de activaciones por segundo, muchas sobre N instancias del mismo programa. Su regla de corte (D-38 del juego) es medir el costo por activación y mover al motor lo que no entre en 0,3 ms por frame.

Verificado en el código de 1.x: la instrucción de la VM no asigna memoria (2,5 ns), la activación sí.

- `BehaviorVm.Activate(string hookId, string instanceId)` busca el hook en un diccionario y devuelve un `RunResult` nuevo.
- `IBehaviorHost.Context`, `Query`, `Apply` y `Raise` reciben `string`. Cada `Query`, `Apply` y `Emit` asigna un `HostValue[]`; cada `Apply` arma un `EffectKey`, su texto, y busca el recibo en una `List<string>`; cada `LoadSymbol` interna por nombre en un diccionario; cada `Call` asigna un `CallFrame`.
- Medido con el hook típico del benchmark (dos lecturas de contexto, una consulta, una llamada a función, un efecto y una señal; 19 pasos): **170 ns y 465 B por activación**.
- `Query` y `Apply` no se validan contra nada: un nombre mal escrito en un hook llega al host en tiempo de ejecución.

Cómo lo resuelven otros:

- **WebAssembly:** el módulo declara sus imports por nombre; el host los resuelve una vez al instanciar y desde ahí cada llamada es por índice. Un import que falta es un error de enlace, no de ejecución.
- **Lua:** `luaL_ref` y los upvalues evitan buscar globales por cadena en cada llamada; el patrón idiomático es resolver una vez y guardar la referencia.
- **ECS con jobs (Unity DOTS, Bevy):** el sistema se ejecuta sobre un lote de entidades con sus datos en columnas, y el planificador reparte lotes entre hilos.
- **Ink y las corrutinas de Unity:** el estado suspendido es un objeto que el runtime reutiliza; la serialización se hace sólo al guardar.

## Decisión

Cada decisión indica por qué no rompe 1.x (ADR-0001). Nada existente cambia de significado: `BehaviorVm`, `IBehaviorHost`, `Activate(string, string)`, la gramática, el IR y el formato de frames quedan iguales.

- **D1. Enlace en la carga.** `BindingTable` traduce los nombres que el host expone a ids enteros que el host elige (su enum o su índice), en cinco espacios: campos de contexto, consultas, efectos, señales (de `emit` y de `wait signal`) y flujos de tirada (`wait roll`). Los nombres son los que ya ve un `IBehaviorHost`: `effect:alarm_raised`, `signal:phase_two`. Registrar dos veces un nombre o un id en el mismo espacio lanza `ArgumentException`: la tabla es código del juego, no contenido (como `SchemaRegistry.Add`). `BehaviorBinder.Bind(program, bindings, out diagnostics)` (o con un `DiagnosticBag`, para enlazar un pack entero) devuelve un `BoundProgram` inmutable: el id de cada instrucción que habla con el host, los símbolos de `LoadSymbol` ya internados en `bindings.Symbols` y la etiqueta de cada sitio de espera. `bound.Hook("on_kill")` devuelve un `HookHandle`. `BindingTable.FromPrograms` registra todo lo que usan unos programas, con ids por orden de aparición (herramientas, tests y el adaptador de D9). *1.x:* tipos nuevos; el programa no cambia y el enlace se rehace en cada carga, así que un repack no invalida nada.
- **D2. Diagnósticos del enlace, familia nueva K6 (carga del runtime).** **K6001, error:** un nombre que el programa usa y el host no registró, con su ubicación (`arena.kedo:8:13`) y una sugerencia (`did you mean`) o el método de la tabla que lo registra. Sólo fallan las activaciones que llegan a esa instrucción (`RunError.Unbound`); el resto del programa corre. **K6002, error:** un programa que no pasa la verificación (la misma de `BehaviorVm`, más el operando de `Call` y las opciones de una decisión); toda activación falla con `RunError.InvalidProgram`. El binder nunca lanza por contenido. *1.x:* códigos nuevos; `BehaviorVm` sigue lanzando `InvalidOperationException` como en 1.0 (su verificación ahora vive en `ProgramVerifier`, con los mismos mensajes).
- **D3. Host por ids: `IFastBehaviorHost`.**

  ```csharp
  public interface IFastBehaviorHost {
      long Now(DurationUnit unit);
      HostValue Context(long instance, int slot);
      HostValue Query(int queryId, ReadOnlySpan<HostValue> args);
      bool Apply(in FastEffectKey key, int effectId, ReadOnlySpan<HostValue> args);
      void Raise(int signalId, ReadOnlySpan<HostValue> args);
  }
  ```

  Cambios respecto de la propuesta del juego: **`Now(DurationUnit)` en vez de `NowTicks`**, porque los ticks no entran en KEDO (ADR-0014 D2) y el programa ya tiene unidad; **`Context` recibe la instancia**, porque en un lote la VM recorre instancias sin que el host pueda cambiar de estado entre una y otra; **sin `Roll`**, porque ninguna de las dos VM lo llama (`wait roll` suspende y el host reanuda con el valor, también en 1.0): ponerlo cambiaría la semántica de una VM respecto de la otra. Una tirada síncrona, si un juego la pide, entra en un minor como opción. Sin `Trace`: lo cubre `VmOptions.Trace` (D5). Los argumentos son un span sobre un buffer que la VM reutiliza. *1.x:* interfaz nueva; `IBehaviorHost` no cambia.
- **D4. Clave de efecto: `FastEffectKey(long Instance, int Site, int Sequence)`.** `Site` es el pc del `apply`, como el `pc17` de la etiqueta de 1.0, y `Sequence` el mismo contador; su texto es el de `EffectKey` (`7/pc17#0`), así que los recibos de un frame son iguales con cualquiera de las dos VM. La instancia es un `long`; en el frame se guarda su texto decimal. *1.x:* misma identidad de efecto que 1.0.
- **D5. `FastBehaviorVm`.** La propuesta del juego sobrecargaba `new BehaviorVm(bound, fastHost, budget, options)`; se adapta a una **clase aparte**, porque `BehaviorVm` es una clase sellada atada a un `IBehaviorHost` y una clase con dos modos obligaría a validar el modo en cada miembro. API:

  ```csharp
  var vm = new FastBehaviorVm(bound, host, budget, new VmOptions { Trace = false });
  vm.Activate(onKill, instance, ref outcome);                    // RunOutcome: struct reutilizable
  vm.ActivateBatch(onKill, instances, context, outcomes);         // Span<RunOutcome>
  vm.Resume(outcome.Frame, AwaitKind.Minutes, 0, ref outcome);   // frame del pool
  PendingAwait p = vm.Pending(frame); vm.Release(frame);
  BehaviorFrame saved = vm.Save(frame); vm.Load(saved, ref outcome); vm.Resume(saved, kind, value, ref outcome, stalePolicy);
  string text = vm.Describe(outcome);                            // sólo cuando hace falta el texto
  ```

  `RunOutcome` lleva `Status`, `Error` (`RunError`, un enum en vez del texto de `RunResult.Error`), `Frame`, `Pc` y `Steps`. Un `HookHandle` inválido o de otro `BoundProgram` falla con `RunError.NoSuchHook`. Misma semántica que `BehaviorVm`: instrucciones, presupuestos (pasos, profundidad, registros: aquí fallan en vez de lanzar), saturación de `wait`, recibos y políticas de frame obsoleto. Con `Trace = false` no se escribe nada; con `true`, `vm.Trace` guarda las líneas de `trace` y de falla de la última llamada. *1.x:* tipos nuevos.
- **D6. 0 B por activación tras el calentamiento.** Registros, pila de llamadas (structs), buffer de argumentos y recibos se reutilizan y sólo crecen durante el calentamiento; los símbolos están internados desde el enlace; el resultado va por referencia. Un efecto sólo puede repetir un recibo restaurado de un frame (dentro de una corrida la secuencia sólo crece), así que una activación nueva no busca recibos. Cada registro tiene 256 posiciones de holgura, así que ningún operando de 8 bits sale del array. Lo verifica un test con `GC.GetAllocatedBytesForCurrentThread` (activaciones, lotes y suspensiones con reanudación) y la puerta de D10.
- **D7. Frames en un pool, sólo al suspenderse.** Un hook que llega a `wait` copia su estado a un frame del pool de la VM y `RunOutcome.Frame` devuelve un `FrameHandle` (índice y generación: un handle viejo no reanuda otra cosa, falla con `RunError.NotSuspended`). `Resume(handle, kind, value, ref outcome)` lo continúa; si vuelve a suspenderse conserva el handle, si termina vuelve al pool. `Pending(handle)` dice qué espera sin asignar (las cadenas son del programa; las opciones son un `ReadOnlyMemory<string>`). `Save` produce el `BehaviorFrame` de siempre: el JSON es **idéntico byte a byte** al que escribe `BehaviorVm` para la misma activación (test), y un frame guardado por cualquiera de las dos VM se reanuda en la otra. `Load` y `Resume(BehaviorFrame, …)` siguen las mismas reglas de 1.0 (formato, frame obsoleto, `StalePolicy`); una instancia que no es un entero es `RunError.UnsupportedFrame`. *1.x:* el formato de frames no cambia (`CurrentFormat` sigue en 1).
- **D8. Lote.** `ActivateBatch(hook, instances, outcomes)` corre el hook sobre N instancias; la sobrecarga con `ReadOnlySpan<HostValue> context` lee los campos de contexto de columnas (el valor del slot `s` para la instancia `i` está en `context[s * N + i]`; un slot fuera del span se pide al host). El `BoundProgram` es inmutable: una `FastBehaviorVm` por hilo de trabajo permite repartir lotes entre hilos sin bloqueo. `Load`, `Save` y la reanudación de un `BehaviorFrame` guardado usan la `SymbolTable` de la sesión, que no es segura entre hilos: se hacen en un solo hilo (al cargar y al guardar la partida). *1.x:* aditivo.
- **D9. Adaptador.** `BehaviorHostAdapter(IBehaviorHost, BindingTable)` corre un host de 1.0 sobre `FastBehaviorVm`: traduce ids a nombres y claves a `EffectKey`, y reenvía las trazas. Exige que la tabla use `host.Symbols`. Asigna como el contrato de 1.0; existe para que un host viejo no necesite reescribirse. *1.x:* aditivo.
- **D10. Puertas de rendimiento.** Benchmarks nuevos en `tests/Kedo.Benchmarks/FastEntryBenchmarks.cs`, con puertas en `baseline.json`: `FastActivate` (ns y B por activación), `FastActivateBatch1024` (por activación dentro de un lote de 1024: el rendimiento del lote), `FastSuspendResume` (suspensión con frame del pool más reanudación) y `LegacyActivate` (referencia de 1.0). `tools/bench-gate.ps1` gana una regla: **una línea base de 0 B es exacta** (BenchmarkDotNet escribe `-` y antes se leía como dato ausente, y la tolerancia de 1 KB dejaba pasar asignaciones). Medido (job corto, la máquina de desarrollo): 56 ns y 0 B por activación, 57 ns por activación en lote, 62 ns por suspensión y reanudación, frente a 170 ns y 465 B en `BehaviorVm`. Con la regla del juego, 0,3 ms por frame alcanzan para unas 5.000 activaciones de ese hook. *1.x:* sólo agrega entradas a la línea base.
- **D11. API del host declarada en el esquema: `kedo_query` y `kedo_effect`.**

  ```kedo
  kedo_query hp_percent {
      params = [ { name = target  kind = reference  target = enemy } ]
      returns = { kind = int }
  }
  kedo_effect set_phase {
      params = [ { name = target  kind = reference  target = enemy }, { name = phase  kind = int } ]
  }
  ```

  `params` es una lista `keyed` por nombre de descriptores `kedo_field`, en orden; en una llamada de miembro (`ctx.boss.hp_percent()`, `ctx.boss.hp`) el receptor es el primer parámetro. `returns` es un descriptor sin nombre. Desde C#: `SchemaRegistry.AddQuery(QuerySchema)` y `AddEffect(EffectSchema)`. El cargador del esquema rechaza con K2001 un parámetro o resultado de una clase que un hook no puede pasar (`list`, `record`, `map`, `decimal`) y un enum inexistente.

  Si el esquema declara al menos una consulta, la compilación de hooks (`kedo compile`, `kedo build`, el MCP y `kedo eval`) exige que cada consulta esté declarada, y lo mismo para los efectos, por separado:

  - **K5008, error, código nuevo:** consulta o efecto no declarado, con sugerencia (`did you mean `hp_percent`?`); un `apply` que no es `effect:<id>` también.
  - **K5003, existente:** aridad (`takes 1 argument(s), got 2 (the receiver of a member call is the first)`) y clase de cada argumento (`argument 2 (`to`) of query `distance` must be a reference to `place`, got a reference to `enemy``).

  La clase de un argumento sale de lo que se sabe sin ejecutar: literales, `tipo:id` (referencia a ese tipo), `enum.valor`, campos del contexto del hook según su `kedo_hook`, resultados de consultas declaradas, retornos y parámetros `int`/`bool`/`text`/`duration` de funciones, y las variables `let` (un `set` con otra clase la vuelve desconocida). Lo desconocido no se comprueba: sin falsos positivos. `int` y `duration` son intercambiables (una duración es un entero en la unidad base, ADR-0014 D5). Si el esquema no declara consultas ni efectos (Patriark, el esquema permisivo), se mantiene el comportamiento actual. `kedo schema` agrega `queries` y `effects` al JSON sólo si hay alguno, y `kedo_catalog` del MCP gana `kind=queries` y `kind=effects`. *1.x:* `kedo_query` y `kedo_effect` eran tipos desconocidos del metaesquema (K2001, error), así que declararlos sólo acepta lo que antes se rechazaba; las comprobaciones sólo rigen para quien declara; el JSON de un esquema sin declaraciones no cambia.
- **D12. Fuera de alcance:** señales declaradas (`kedo_signal`), tirada síncrona, reescribir `BehaviorVm` sobre el núcleo nuevo (hoy son dos bucles de interpretación con la misma semántica, comprobada por los tests de paridad y el golden de D13; unirlos queda para un minor si vale la pena), el reparto de lotes entre hilos (lo hace el juego) y la medición de p99 (BenchmarkDotNet da la media; el juego mide p99 en su propio benchmark de D-38).
- **D13. Golden.** Caso nuevo de corpus: `tests/corpus/<caso>/schema/*.kedo` + `content/*.kedo`, con `diagnostics.txt` (esquema, resolución, compilación y enlace) y, si no hay errores, `trace.txt`: cada hook de cada programa corrido en `FastBehaviorVm` con un host guionado hasta el final, con cada llamada al host, cada suspensión y el frame guardado. La misma corrida en `BehaviorVm` debe hacer las mismas llamadas. Casos: `host-api-boss` (declarado, `ms`, espera de tiempo, de señal y de decisión), `host-api-errors` (K5008 y K5003) y `host-api-undeclared` (sin declaraciones; con `unbound.txt` el host deja dos nombres sin registrar: K6001 y `RunError.Unbound`).

Códigos nuevos: **K5008** (error), **K6001** (error), **K6002** (error). Ninguno existía antes.

## Consecuencias

- Un juego enlaza su pack una vez al cargar, con su propia tabla de ids, y activa hooks por handle sin asignar memoria: 3 veces más rápido que la entrada por nombre en el benchmark y sin presión sobre el GC.
- Los nombres que el host no conoce se ven al cargar (K6001), no cuando un jugador llega al hook; con `kedo_query` y `kedo_effect` declarados, se ven al compilar (K5008, K5003).
- Un guardado con frames suspendidos sobrevive a un repack y a un cambio de host: el frame sigue guardando etiquetas y nombres, y el enlace se rehace al cargar.
- Patriark no cambia: su esquema no declara consultas ni efectos y sigue usando `BehaviorVm`.
- Hay dos bucles de interpretación que mantener con la misma semántica; los tests de paridad y el golden comparan llamadas al host, efectos y frames, de modo que una divergencia rompe la CI.
