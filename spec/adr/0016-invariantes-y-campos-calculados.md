# ADR-0016 — Invariantes del esquema y campos calculados

Fecha: 10/11/2026 · Estado: aceptado (11/11/2026, decisión D-126 del creador de survivors-like) · Origen: issue #22 (extensión K2 pedida por survivors-like) · Plan: [Plan-08](../../docs/plans/Plan-08-Invariantes-y-Campos-Calculados.md).

## Contexto

KEDO-02 R24 promete "invariantes declaradas por el esquema del juego (K4008)" en la etapa B, y R15 fija el diseño de los campos calculados para v0.2 (`computed max_hp = base_hp * level`, ciclos K3012). Hasta este cambio K4008 y K3012 figuraban en la tabla de KEDO-02 §11 y ningún archivo los emitía; el metaesquema (`MetaSchema.Build`) no tenía dónde declararlos; las expresiones `( … )` de los cuerpos sólo operan sobre literales al resolver (`ConstEvaluator`, R14) y los decimales no se suman ni se comparan en ellas (ADR-0008, K5007).

Lo que pide el juego (survivors-like, catálogo de contenido, columna INV) se agrupa en seis formas:

| Forma | Ejemplos del juego |
|---|---|
| Suma o cuenta sobre una lista | el reparto del pago por misión y por escape suma 100 %; el escape cae en 30–40 % |
| Unicidad y orden | los sectores del contrato van 1, 2, 3… sin saltos y el jefe va justo después del último; el orden es único |
| Agrupación | cada sub-rama del árbol: capacidad ≈ 25 puntos, un solo final, el requisito se cuenta en su sub-rama |
| Relación entre objetos | `4 × (15 + costo máximo del final) ≤ puntos máximos` del ruleset; velocidad del frente < carrera del jugador |
| Monotonía | la curva de experiencia de la run es estrictamente creciente |
| Prohibición y cuantificación | ningún rol inteligente es agrupable; toda carta tiene peso > 0 |

Y campos derivados que hoy se copian a mano (totales del árbol, costos acumulados).

Cómo lo resuelven otros:

- **CUE:** las restricciones son valores; `list.Sum`, `list.UniqueItems`, comparaciones y referencias a otros campos, todo puro y evaluado sobre el valor final.
- **JSON Schema:** `uniqueItems`, `minContains`/`maxContains`, `if`/`then`; sin sumas ni relaciones entre documentos.
- **Pkl:** `constraints` en tipos (`List<Int>(isDistinct)`, `(this.sum == 100)`), con mensajes por restricción.
- **Dhall / Jsonnet:** `assert` con expresión arbitraria; Jsonnet además tiene enlace tardío, que KEDO rechazó (ADR-0005).
- **Factorio / Paradox:** validación en el código del juego al cargar; los errores llegan tarde y no los ve el editor.

## Respuestas del creador (D-126, 11/11/2026)

1. Las reglas se escriben **sin comillas**, en el lenguaje (no como texto).
2. Hace falta **ya** un cuantificador sobre todos los objetos de un tipo.
3. `increasing` es **estricto**; no hace falta la variante no estricta.

## Decisión

Cada decisión dice por qué no rompe 1.x (ADR-0001).

- **D1. Un lenguaje de expresiones de esquema, puro, exacto y sin bucles.** Se usa en invariantes (D2) y en campos calculados (D4). Gramática: la de KEDO-01 §7, sin cambios (aritmética, comparaciones, `and`/`or`/`not`, miembros y llamadas), con este significado:
  - **nombres:** un nombre suelto es un campo del registro actual (el objeto, o el elemento dentro de un agregado); `it` es el elemento; `index` su posición (desde 0); `root` el cuerpo del objeto, desde una regla de un componente o de un record anidado o desde dentro de un agregado. Un nombre que no es campo, comparado con `==` o `!=` contra un enum, es un valor de ese enum (`role == smart`), comprobado contra el enum.
  - **desreferencia:** una ruta que pasa por un campo `reference` sigue al objeto resuelto (`ruleset.max_points`), y `tipo:id.campo` lee un objeto concreto.
  - **agregados:** `count(lista[, condición])`, `sum|min|max|unique|increasing(lista[, valor[, filtro]])`, `all|any(lista, condición)`, `has(ruta)` (el campo está presente), `len(texto o lista)` y `subsequence(lista_a, valor_a, lista_b, valor_b)`. Sin el argumento de valor, el elemento mismo (listas de escalares: `increasing(xp_curve)`). El filtro deja fuera los elementos que no lo cumplen (`max(nodes, cost, final)`).
  - **cuantificador:** `forall(tipo, condición)` recorre los objetos concretos del tipo; dentro, los nombres sueltos son campos de cada uno.
  - **valores:** enteros de 64 bits, decimales de punto fijo (suma, resta y comparación exactas a la mayor escala, y producto por un entero; `/` y `%` sobre un decimal son K2013: no hay regla de redondeo), duraciones (en milisegundos, exactas; suma, resta, comparación y producto por un entero; un literal `15s` vale en cualquier `base_unit`), booleanos, textos, enums y referencias (igualdad).

  El desborde de 64 bits y la división por cero son errores de evaluación (K4016), nunca un resultado. *1.x:* la gramática no cambia; las expresiones del contenido (R14) se siguen evaluando igual.
- **D2. Invariantes declarativas.** `kedo_type`, `kedo_component` y `kedo_record` ganan `invariants`, una lista `keyed` por `id`:

  ```kedo
  kedo_type contract {
      fields = [ … ]
      invariants = [
          { id = sectors_in_order  check = (all(sectors, sector == index + 1))  message = "the sectors go 1, 2, 3... without gaps" }
          { id = boss_after_last  check = (boss_sector == count(sectors) + 1) }
          { id = payout_total  check = (sum(payouts, share) == 100)  message = "the payout shares add up to {sum(payouts, share)}, not 100" }
          { id = escape_band  check = (all(payouts, id != escape or (share >= 30 and share <= 40)))  severity = warning }
      ]
  }
  ```

  `message` admite `{expr}` para mostrar valores (`{{` es una llave literal); `severity = warning` es opcional (error por defecto). *1.x:* son campos nuevos del metaesquema, que rechazaba cualquier campo desconocido (K2005), así que sólo aceptan lo que antes era error.
- **D3. Las expresiones van sin comillas** (respuesta 1). `check = (…)` y `computed = (…)` son expresiones entre paréntesis que el resolver **no evalúa**: el metaesquema declara esos campos de clase `expression` (valor nuevo de `ValueKind`, sólo para el metaesquema) y el resolver los guarda como `RExpression` (clase de valor nueva) con su texto canónico; el cargador del esquema los compila. El formateador los formatea, el editor los resalta y los diagnósticos tienen posición exacta en el archivo del esquema. Escribirlas entre comillas es K4001 con la sugerencia de quitarlas. Los descriptores de `Kedo.Core` guardan el texto canónico (`FieldSchema.Computed`, `InvariantSchema.Check`), así que un esquema registrado en C# escribe el mismo texto y se compila igual (sin posición). *1.x:* `ValueKind.Expression`, `RExpression` y `LayerKind.Computed` son miembros nuevos; el contenido nunca ve la clase `expression` (no figura en `kind = …`).
- **D4. Campos calculados (R15).** Un `kedo_field` de un tipo o de un componente gana `computed = (expr)`: el campo es de sólo lectura y se evalúa una vez por objeto concreto en una pasada nueva **después de resolver y antes de la etapa B y del hash**, en el orden de sus dependencias (los ciclos son **K3012** al cargar el esquema, con la ruta), sobre los campos resueltos del objeto y de los objetos referenciados, que se calculan primero si hace falta. Los calculados van al final del cuerpo, en ese orden. El resultado entra al catálogo como un campo más, con procedencia `computed` (`kedo explain` muestra la expresión), y por eso al hash y al pack: los consumidores del pack no cambian. Escribir un calculado en el contenido es **K4015**, con la ubicación. Un campo de un `kedo_record` no puede ser calculado (K2013: se calcula en el tipo que tiene los records, con un agregado). La expresión debe dar la clase del campo (K2013): un decimal con no más decimales que la escala del campo, una duración que sea entera en la `base_unit` del módulo (K4016 si no). *1.x:* sin `computed`, la pasada no hace nada y los hashes no cambian.
- **D5. Cuantificador (respuesta 2).** `forall(tipo, condición)` vale en cualquier regla. Además, `kedo_invariant` declara una **invariante del catálogo** cuyo `check` es `forall(tipo, condición)`: se evalúa objeto por objeto y cada objeto que la incumple tiene su K4008, en su posición:

  ```kedo
  kedo_invariant cards_weighted {
      check = (forall(card, weight > 0))
      message = "card weight is {weight}; a card with weight 0 never shows up"
  }
  ```

  Un `check` de `kedo_invariant` que no es `forall` es K2013 (una regla sobre un objeto va en su tipo). *1.x:* tipo nuevo del metaesquema.
- **D6. `increasing` es estricto** (respuesta 3): cada valor es mayor que el anterior; una lista vacía o de un elemento cumple.
- **D7. Diagnósticos.**
  - **K4008** (existente en la spec, primera implementación): invariante incumplida, ``invariant `id` fails: <mensaje>`` (sin mensaje: ``` `check` is false```). La posición es la del primer campo escrito en el contenido que leyó la regla; las etiquetas muestran los otros campos leídos con su valor y, si el valor se heredó, el objeto que lo hereda. Errores iguales con el mismo origen se agrupan (R25): dos objetos que heredan los mismos valores dan uno solo. Con `severity = warning` es advertencia y no bloquea la exportación.
  - **K3012** (existente en la spec): ciclo entre campos calculados, al cargar el esquema.
  - **K2013**, nuevo: expresión de esquema inválida al cargar (campo, función o tipo desconocido, con sugerencia; clase incompatible; decimal o duración sin resultado exacto; `it`, `index` o `root` fuera de lugar; check que no es booleano), con posición exacta.
  - **K4015**, nuevo: el contenido escribe un campo calculado.
  - **K4016**, nuevo: una regla o un calculado no se puede evaluar (desborde, división por cero, `min`/`max` de una lista vacía, un campo opcional ausente: el mensaje sugiere `has(campo)`). Sin cascada (ADR-0010): un campo obligatorio ausente (K4002) o una referencia rota (K4005) no dan K4016 ni K4008.
- **D8. Incrementalidad.** Una regla que desreferencia otro objeto registra esa clave como dependencia en el mismo índice que usa `IncrementalCatalog` para las referencias; `forall(tipo, …)` registra `*tipo`, que cambia cuando cambia, aparece o desaparece cualquier objeto del tipo. Los calculados se recalculan en cada actualización y un objeto cuyos calculados cambiaron cuenta como clave cambiada para sus lectores. Las invariantes del catálogo corren en cada actualización. `InvariantTests` compara cada actualización con una resolución completa, como `IncrementalTests`. *1.x:* misma garantía de hoy.
- **D9. Herramientas.** `kedo describe` marca los campos calculados (`computed: true`, `expression`) y el addon los muestra como texto de sólo lectura, con la expresión en el tooltip; `kedo schema --json` agrega `readOnly` y `x-kedo.computed` a cada calculado (que no figura en `required`) y `x-kedo.invariants` al registro; `kedo_catalog kind=types` del MCP lista los calculados y las invariantes de cada tipo. *1.x:* sólo agrega campos JSON.
- **D10. Rendimiento.** Las expresiones se compilan una vez por registro a un árbol tipado (sin reflexión ni `dynamic`) y se evalúan con valores `struct`, sin asignar por elemento; los agregados son lineales salvo `unique` (cuadrático sobre listas cortas, sin asignar). Una regla que falla se evalúa otra vez para recoger los campos leídos, así que una regla que se cumple no paga las etiquetas. Un esquema sin reglas no compila ni recorre nada. Puertas: `ResolveWithInvariants|10000` (dos calculados y tres invariantes por objeto) frente a `ResolveTyped|10000` (el mismo contenido con esos valores escritos a mano); presupuesto 115 %. Medido (`--job short`, 10 000 objetos): 115,6 ms y 63,3 MB con reglas frente a 110,1 ms y 69,9 MB sin ellas (1,05× en tiempo, 0,91× en memoria); `Resolve|10000` sin esquema de reglas no cambia.
- **D11. Fuera de alcance por ahora:** validadores en C# (`IObjectValidator`): las reglas que pide el juego caben en el lenguaje, y lo que se declara en el esquema lo ven la CLI, el MCP y el addon; se agregan después si hace falta, como interfaces nuevas. También quedan fuera las funciones definidas por el autor, las expresiones en hooks y los campos calculados en records anidados.

## Consecuencias

- Las reglas de balance del juego viven en el esquema, las ve el addon (bloquea Play), el MCP y la CI, y se comprueban en cada objeto concreto con el valor final.
- Los campos derivados dejan de copiarse a mano y entran al hash.
- Un esquema que no use nada de esto produce el mismo catálogo, los mismos hashes y el mismo pack byte a byte (ningún archivo esperado de `tests/corpus` cambió).
- Casos en `tests/corpus/invariants-contract`, `invariants-curve-forall`, `computed-fields` e `invariants-errors`.
