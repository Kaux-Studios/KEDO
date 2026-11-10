# ADR-0016 — Invariantes del esquema y campos calculados

Fecha: 10/11/2026 · Estado: propuesto · Origen: issue #22 (extensión K2 pedida por survivors-like) · Plan: [Plan-08](../../docs/plans/Plan-08-Invariantes-y-Campos-Calculados.md).

## Contexto

KEDO-02 R24 promete "invariantes declaradas por el esquema del juego (K4008)" en la etapa B, y R15 fija el diseño de los campos calculados para v0.2 (`computed max_hp = base_hp * level`, ciclos K3012). Verificado en el código: K4008 y K3012 figuran en la tabla de KEDO-02 §11 y ningún archivo los emite; el metaesquema (`MetaSchema.Build`) no tiene dónde declararlos; las expresiones `( … )` de los cuerpos sólo operan sobre literales al resolver (`ConstEvaluator`, R14) y los decimales no se suman ni se comparan en ellas (ADR-0008, K5007).

Lo que pide el juego (survivors-like, catálogo de contenido, columna INV) se agrupa en seis formas:

| Forma | Ejemplos del juego |
|---|---|
| Suma o cuenta sobre una lista | el reparto del pago por misión y por escape suma 100 %; el escape cae en 30–40 % |
| Unicidad y orden | los sectores del contrato son una subsecuencia del orden fijo del mapa; el orden es único |
| Agrupación | cada sub-rama del árbol: capacidad ≈ 25 puntos, un solo final, el requisito se cuenta en su sub-rama |
| Relación entre objetos | `4 × (15 + costo máximo del final) ≤ puntos máximos` del ruleset; velocidad del frente < carrera del jugador; los extremos de un atajo existen |
| Monotonía | la curva de experiencia de la run es estrictamente creciente |
| Prohibición | ningún rol inteligente es agrupable; ningún enemigo tiene campo de dinero; no hay conversión entre monedas |

Y campos derivados que hoy se copian a mano (totales del árbol, costos acumulados).

Cómo lo resuelven otros:

- **CUE:** las restricciones son valores; `list.Sum`, `list.UniqueItems`, comparaciones y referencias a otros campos, todo puro y evaluado sobre el valor final.
- **JSON Schema:** `uniqueItems`, `minContains`/`maxContains`, `if`/`then`; sin sumas ni relaciones entre documentos.
- **Pkl:** `constraints` en tipos (`List<Int>(isDistinct)`, `(this.sum == 100)`), con mensajes por restricción.
- **Dhall / Jsonnet:** `assert` con expresión arbitraria; Jsonnet además tiene enlace tardío, que KEDO rechazó (ADR-0005).
- **Factorio / Paradox:** validación en el código del juego al cargar; los errores llegan tarde y no los ve el editor.

## Decisión (propuesta)

Cada decisión dice por qué no rompe 1.x (ADR-0001).

- **D1. Un lenguaje de expresiones de esquema, puro, exacto y sin bucles.** Se usa en invariantes (D2) y en campos calculados (D4). Gramática: la de KEDO-01 §7 (aritmética, comparaciones, `and`/`or`/`not`), más:
  - **rutas** sobre el objeto o el elemento actual (`payouts`, `stats.hp`), `root.` para el objeto desde dentro de un agregado, e `it` para el elemento de una lista de escalares;
  - **desreferencia:** una ruta que pasa por un campo `reference` sigue al objeto resuelto (`ruleset.max_points`), y `tipo:id.campo` lee un objeto concreto;
  - **agregados:** `sum(lista, expr)`, `count(lista[, cond])`, `all(lista, cond)`, `any(lista, cond)`, `unique(lista, expr)`, `increasing(lista, expr)` (estricto), `min(lista, expr)`, `max(lista, expr)`, `has(ruta)` (el campo está presente), `len(texto)`, y `subsequence(lista_a, expr_a, lista_b, expr_b)` para el orden de los sectores; dentro de `expr` los nombres sueltos son campos del elemento;
  - **valores:** enteros de 64 bits, decimales de punto fijo (suma, resta y comparación exactas a la mayor escala; multiplicar o dividir un decimal es error al cargar el esquema: sin reglas de redondeo), duraciones en la unidad base, booleanos, textos, enums y referencias (igualdad).

  El desborde de 64 bits es un error de evaluación con su diagnóstico, nunca un resultado. *1.x:* es un lenguaje nuevo dentro del esquema; las expresiones del contenido (R14) no cambian.
- **D2. Invariantes declarativas.** `kedo_type`, `kedo_component` y `kedo_record` ganan `invariants`, una lista `keyed` por `id`:

  ```kedo
  kedo_type contract {
      fields = [ … ]
      invariants = [
          { id = payout_total  check = "sum(payouts, share) == 100"  message = "the payout shares add up to {sum(payouts, share)}, not 100" }
          { id = escape_band  check = "escape_share >= 30 and escape_share <= 40" }
          { id = sector_order  check = "subsequence(sectors, it, map.sectors, id)" }
      ]
  }
  ```

  `check` es texto (ver D3); `message` admite `{expr}` para mostrar valores; `severity = warning` es opcional (error por defecto). *1.x:* son campos nuevos del metaesquema, que hoy rechaza cualquier campo desconocido (K2005), así que sólo aceptan lo que antes era error.
- **D3. Las expresiones van como texto.** `check = "…"` y `computed = "…"` son cadenas que el cargador del esquema parsea con el parser de expresiones de KEDO, con posiciones exactas dentro del literal para los diagnósticos. Alternativa descartada por ahora: una expresión sin comillas `( … )` que el resolver no evalúe, que exige una clase de valor nueva (`RExpression`) y cambia lo que `ConstEvaluator` hace con los paréntesis en el metaesquema. El texto no toca el resolver ni la API de valores; el costo es que el formateador no formatea dentro de la cadena. *1.x:* sin cambios en la gramática ni en `ResolvedValue`.
- **D4. Campos calculados (R15).** Un `kedo_field` gana `computed = "expr"`: el campo es de sólo lectura, se evalúa una vez por objeto concreto en una pasada nueva **después de resolver y antes de la etapa B y del hash**, en el orden estático que dan sus dependencias (los ciclos son **K3012** al cargar el esquema, con la ruta), sobre los campos resueltos del objeto y de los objetos referenciados (que ya tienen sus calculados si los hay; el grafo entre tipos también se ordena). El resultado entra al catálogo como un campo más, con procedencia `computed` (`kedo explain` muestra la expresión), y por eso al hash y al pack: los consumidores del pack no cambian. Escribir un campo calculado en el contenido es **K4015**, error nuevo, con la ubicación. *1.x:* sin `computed`, la pasada no hace nada y los hashes no cambian.
- **D5. Diagnósticos.**
  - **K4008** (existente en la spec, primera implementación): invariante incumplida, en el objeto concreto, con el mensaje del esquema, el `id` de la invariante, los valores calculados y etiquetas a la procedencia de los campos que leyó. Errores iguales con el mismo origen se agrupan (R25).
  - **K3012** (existente en la spec): ciclo entre campos calculados.
  - **K2013**, nuevo: expresión de esquema inválida al cargar (sintaxis, campo o lista desconocida, agregado sobre algo que no es lista, aritmética decimal no exacta, clase incompatible), con posición dentro del literal.
  - **K4015**, nuevo: el contenido escribe un campo calculado.
  - **K4016**, nuevo: error al evaluar (desborde, división por cero, referencia sin resolver por un error anterior: una vez, sin cascada, ADR-0010).
- **D6. Incrementalidad.** Una invariante o un calculado que desreferencia otro objeto registra esa clave como dependencia en el mismo índice que hoy usa `IncrementalCatalog` para las referencias (`References`), así que editar el ruleset vuelve a validar los contratos que lo leen y a calcular sus campos. Un test compara cada actualización incremental con una resolución completa (como el resto de `IncrementalTests`). *1.x:* misma garantía de hoy.
- **D7. Validadores en C#.** Para lo que no entre en D1, `IObjectValidator` en `Kedo.Compiler` (el modelo resuelto vive ahí, no en `Kedo.Core`), sin reflexión: `void Validate(ResolvedObject obj, ResolvedCatalog catalog, ValidationSink sink)`, registrado por tipo en un `ValidatorSet` que recibe una sobrecarga nueva de `Resolver.Resolve` (y de `IncrementalCatalog`); informa K4008 con su propio `id`. Límite explícito: la CLI, el MCP y el addon cargan el esquema desde `.kedo` y **no ven** validadores en C#; sólo corren donde el juego compila desde su código (su build y sus tests). Por eso la recomendación es declarar en el esquema todo lo que se pueda. *1.x:* interfaces nuevas.
- **D8. Herramientas.** `kedo describe` marca los campos calculados (`computed: true`, con la expresión) y el addon los muestra deshabilitados; `kedo schema --json` agrega `x-kedo.computed` y `x-kedo.invariants`; `kedo_catalog` del MCP los lista con el tipo; los skills documentan la sintaxis y los códigos. *1.x:* sólo agrega campos JSON.
- **D9. Rendimiento.** Una invariante se compila una vez al cargar el esquema a un árbol tipado (sin reflexión ni `dynamic`) y se evalúa sin asignar por elemento; los agregados son lineales. Puertas nuevas: `Resolve|10000` con un esquema de invariantes y calculados típico (presupuesto: no más de 15 % sobre `Resolve|10000` sin ellos), y `Resolve|10000` sin invariantes sin cambios (mismas asignaciones).
- **D10. Fuera de alcance:** invariantes sobre el catálogo entero sin tipo ancla (se anclan a un objeto: "el ruleset" o "el mapa"); cuantificadores sobre todos los objetos de un tipo (`all(type:enemy, …)`), que se agregan después si hace falta con su propio costo incremental; funciones definidas por el autor; expresiones en hooks.

Preguntas para la revisión:

1. ¿Expresión como texto (D3) o sin comillas con una clase de valor nueva? El texto es más barato y aditivo; la otra se formatea y se resalta.
2. ¿Hace falta ya el cuantificador sobre todos los objetos de un tipo (D10)? "Ningún rol inteligente es agrupable" se expresa como invariante del tipo `role` (`not (smart and groupable)`), sin cuantificador; "los extremos del atajo existen" ya lo cubre K4005.
3. ¿`increasing` estricto alcanza, o hace falta también no estricto?

## Consecuencias

- Las reglas de balance del juego viven en el esquema, las ve el addon (bloquea Play), el MCP y la CI, y se comprueban en cada objeto concreto con el valor final.
- Los campos derivados dejan de copiarse a mano y entran al hash.
- Un esquema que no use nada de esto produce el mismo catálogo, los mismos hashes y el mismo pack byte a byte.
- Las reglas que necesiten C# quedan fuera de las herramientas genéricas (D7); el juego debe correr su propio paso de validación en su CI.
