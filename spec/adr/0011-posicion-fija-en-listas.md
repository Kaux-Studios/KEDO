# ADR-0011 — Posición fija de elementos agregados a una lista (`+= first`, `+= last`)

Fecha: 16/10/2026 · Estado: aceptado 16/10/2026 · Origen: hallazgo 7 y propuesta P4 de la integración con Patriark.

## Contexto

Un mixin se aplica después del padre y antes del cuerpo propio (KEDO-02 §2), y los elementos agregados van al final en orden de capas (R8). Un mixin que agrega una opción común a muchos eventos (`leave`, "irse sin hacer nada") la deja **antes** de las opciones que el evento agrega después:

```kedo
mixin leavable { options += [{ id = leave }] }
event ambush with leavable { options += [{ id = fight }, { id = flee }] }   // [leave, fight, flee]
```

En Patriark el orden de las opciones es el orden visible en pantalla, así que la importación del pack quedó plana: cada evento repite su opción `leave`.

Cómo lo resolvieron otros:

- **Parches posicionales** (RFC 6902 `add /options/-`, `PatchOperationInsert order="Append|Prepend"` de RimWorld, índices de Factorio): la posición se decide en el momento de aplicar el parche; un parche posterior puede quedar después. Se rompen cuando cambia la base (ADR-0006 prohíbe editar por índice).
- **Directivas de orden en la mezcla** (`$setElementOrder` de los parches estratégicos de Kubernetes): el que mezcla reescribe el orden completo; obliga a repetir todas las claves.
- **Propiedad de orden en el elemento** (`order` de CSS flexbox, `[DefaultExecutionOrder]` de Unity, `order` de los menús de Unity y Godot): un número decide la posición final. Funciona, pero los números son globales y se pelean (`999`, `1000`).
- **Restricciones relativas** (`before`/`after` en los sistemas de Bevy, `requires` de Paradox): generales; piden orden topológico y diagnosticar ciclos.

## Decisión

- **Sintaxis:** `k += first [..]` y `k += last [..]`. La palabra va entre `+=` y la lista. Son palabras **contextuales**: sólo significan algo inmediatamente antes de `[` después de un operador, así que `first` y `last` siguen siendo válidos como nombres de campo, ids y valores de enumeración (`first = last` es un campo `first` con el valor `last`). Después de `=`, `=!` o `-=` son error K1007.
- **Semántica (ancla pegajosa):** cada elemento de una lista ordenada (`set`, `keyed`, `append`) tiene un ancla: ninguna, `first` o `last`. El orden resuelto es: anclados `first`, luego los no anclados (en el orden de R8) y por último los anclados `last`; cada grupo conserva su orden relativo. El ancla viaja con el elemento a través de herencia, mixins y mezclas por clave (`options[leave] { }` no la cambia), así que el elemento sigue al final aunque los descendientes agreguen más.
- **Cambiar el ancla:** quitar y volver a agregar (`options -= [leave]` y `options += [{ id = leave … }]`); el elemento agregado sin palabra queda sin ancla. Agregar con ancla a una lista `set` un valor que ya está no lo mueve: advertencia K3016.
- **Listas `atomic`:** no admiten `+=` (K3005), tampoco con ancla.
- **Hash y procedencia:** el ancla es metadato de resolución: el hash ve sólo el orden resultante. `kedo explain` y el volcado de `kedo resolve` muestran `(pinned first|last)` en cada elemento anclado.
- **API:** `ListAnchor`, `FieldEntry.Anchor` y `ResolvedValue.Anchor` (aditivo).

`first`/`last` cubre el caso real (encabezados y pies comunes: "irse", "volver", "cancelar") sin números ni restricciones. Las restricciones relativas (`+= before leave [..]`) quedan como extensión aditiva si un juego las necesita.

## Consecuencias

- Un mixin puede aportar el elemento final o inicial de una lista sin que el objeto que lo usa sepa nada de él; el objeto escribe `options += [..]` como siempre.
- `-=` sobre un elemento anclado y `+=` del mismo en el mismo cuerpo lo deja sin ancla: es la forma de "desanclar"; no hay sintaxis de reubicación.
- Los formateadores y editores deben conservar la palabra; `kedo fmt` la escribe `+= last [`.
- Diagnósticos nuevos: K1007 (palabra de posición fuera de `+=`) y K3016 (advertencia: valor ya presente en un `set`).
