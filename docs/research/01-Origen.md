# 01 — Origen de KEDO y decisiones heredadas

KEDO nació en octubre de 2026 de las necesidades de contenido de Project Patriark, el primer juego de Kaux Studios que lo consume. Este documento conserva lo que de aquella investigación sirve a cualquier proyecto. El análisis del código del juego y la síntesis completa quedaron en su repositorio, que es privado.

## Requisitos del creador

- Contenido que no quede encapsulado en puro dato: heredar DTO de DTO más genéricos, importar, mixins, funciones y triggers junto al objeto, y referencias a animaciones, sonidos y cualquier otro asset.
- **Rendimiento antes que facilidad de uso**; escalable a decenas de miles de objetos y flexible para varios juegos.
- Repositorio aislado y reutilizable, con SemVer por major: un juego fija `[1.0, 2.0)` y debe funcionar con 1.9.99 ([ADR-0001](../../spec/adr/0001-versionado-semver.md), [ADR-0002](../../spec/adr/0002-repositorio-aislado.md)).
- Plugin, servidor MCP y skills para que los agentes de IA escriban contenido y extiendan el toolchain.
- Requisitos del primer juego que KEDO asume como generales: dominio sin motor, guardado como snapshot determinista, iOS con NativeAOT (sin reflexión), CLI y tests sin Godot.

## Decisiones heredadas que siguen vigentes

La investigación original proponía "JSON neutral por objeto" (D1). Esa decisión quedó superada por el lenguaje propio ([ADR-0003](../../spec/adr/0003-json-solo-como-artefacto.md)); las demás siguen siendo la base de KEDO y los [planes](../plans/) las citan por número.

- **D2. Contrato común.** Ids estables con namespace para definiciones e ids de instancia asignados en runtime: las instancias guardan id y versión de la definición, nunca índices. Tres capas: definición de autoría, definición resuelta (inmutable, hasheada) e instancia. El hash es del objeto resuelto, sin metadatos editoriales. Versiones separadas para el formato, cada definición, el contenido compilado y las reglas. Tags jerárquicos con coincidencia por prefijo.
- **D3. Registro de tipos sin reflexión.** Registro explícito por módulo (factoría, serialización source-generated, validador) compatible con trimming y NativeAOT. Cada tipo publica un esquema que usan los formularios del editor y los agentes. Añadir un tipo no toca la UI común.
- **D4. Composición antes que herencia profunda.** Un padre, profundidad acotada, ciclos rechazados. Escalares reemplazan, componentes se mezclan por id, tags por unión, listas de objetos por clave. Nunca concatenación posicional ni borrado por texto (las dos fuentes de errores de RimWorld y Dwarf Fortress, [anexo D](anexos/D-Patrones-de-datos-en-otros-juegos.md)). Operaciones explícitas en orden fijo y procedencia de cada valor visible en el editor.
- **D5. Assets por clave lógica.** Las definiciones referencian presentación por clave (`asset:…`); un manifiesto generado por el editor traduce clave a recurso del motor y la CLI valida que toda clave exista.
- **D6. Addon con cáscara GDScript y la CLI como autoridad.** El addon no carga el ensamblado del juego (evita la recarga de ensamblados C# del editor, [anexo A](anexos/A-Resources-tres-y-headless.md) §6): edita texto en memoria y delega validación y compilación a la CLI, la misma que corre en CI.
- **D7. Undo y cambios pendientes en el addon.** `UndoRedo` por documento, fusión de ediciones por campo, estado sucio integrado al diálogo de cierre del editor y borradores en `user://` ([anexo B](anexos/B-Plugins-de-editor-y-undo-redo.md) §1.4, §3). Nada del addon entra al ensamblado del juego.
- **D8. Contrato de comportamientos.** Ningún intérprete de terceros compatible con NativeAOT ofrece continuaciones serializables ([anexo E](anexos/E-Comportamientos-y-scripting.md)). KEDO compila a un IR propio con etiquetas estables y valores enteros, interpretado por una VM cuyos frames se guardan. Hooks de catálogo cerrado y versionado, señales encoladas, efectos idempotentes por clave, presupuesto de pasos, azar y reloj inyectados, y `StalePolicy` para frames suspendidos de versiones viejas.

## Trade-off JSON vs DSL (15/10/2026)

Supuesto: se mantiene la separación dominio sin Godot y guardado como snapshot determinista. "JSON" = sobre + resolver con lógica aparte; "DSL" = lenguaje de texto propio con `extends`, `import`, mixins, expresiones, funciones y triggers compilados a IR, refs de assets.

### Implicancias inmediatas

| Aspecto | JSON | DSL |
|---|---|---|
| Construir | Resolver + esquema + compilador a pack; STJ y JsonSchema.Net ya existen | Gramática + lexer/parser + modelo semántico + compilador + diagnósticos línea/columna; la sintaxis de funciones se diseña ya o se acaban teniendo dos lenguajes |
| Esfuerzo base estimado | 2–4 semanas | 5–9 semanas; parser y mensajes de error son la mitad |
| Riesgo técnico | Bajo | Medio-alto: un error de diseño con contenido escrito cuesta migraciones |
| Formularios del editor | Directos, ida y vuelta sin pérdida | Editan AST y reimprimen; hace falta formateador que preserve comentarios |
| Edición en texto | Verboso, sin comentarios ni expresiones | Compacto, comentarios, expresiones |
| Dos formatos a mantener | Sí a futuro (datos + lenguaje de lógica) | No |

### Posibilidades futuras

| Capacidad | JSON | DSL |
|---|---|---|
| Herencia de valores DTO→DTO | Sí (`parent`) | Sí (`extends`), más legible |
| Expresiones en datos | No sin strings con mini-lenguaje | Sí, tipadas |
| Plantillas parametrizadas | Torpe | Natural |
| Funciones y triggers junto al objeto | No | Sí, compiladas a IR guardable |
| Techo demostrado | RimWorld, CDDA, Minecraft (lógica en código o JSON-lógica) | Paradox, Ink, Yarn (datos y lógica juntos, herramientas propias) |
| Generación por herramientas/IA | Estándar | Posible, necesita gramática y ejemplos |
| Costo de propiedad | Bajo | Lenguaje propio: spec, versiones, migraciones, formateador, tooling |
| Riesgo "encapsulados en puro dato" | Real | Bajo |

### Conclusión

No son excluyentes: AST serializable (JSON como intercambio y compilado) + sintaxis de superficie (DSL). La decisión real es qué se escribe a mano y cuándo se construye el parser. El creador eligió el DSL propio, aislado en su repositorio, con los nodos de lógica en el AST desde 1.0 (ADR-0003).
