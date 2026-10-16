# ADR-0010 — Diagnósticos de mezcla, procedencia por elemento y orden de declaración

Fecha: 16/10/2026 · Estado: aceptado 16/10/2026 · Origen: hallazgos 6, 11 y 15 de la integración con Patriark.

## Contexto

Tres problemas aparecieron al escribir contenido real:

1. `roster = [tester]` en un hijo de `menders` dio `[mara, ilan, tester]`: `=` sobre una lista `set` heredada une, en silencio, y lo mismo pasa con `keyed`. `resources = { }` sobre un record heredado no borra nada. Ambas son reglas de KEDO-02 §3, pero quien escribe `=` casi siempre espera "esto es el valor". `kedo explain` además atribuía la lista unida a la capa propia.
2. Tras un error de tipo o de enum en un campo obligatorio, KEDO informaba también "falta el campo obligatorio": dos diagnósticos para un error.
3. El catálogo resuelto se ordena por `(tipo, id)`. Los compiladores de juego que necesitan el orden de autoría (Patriark: arrays del pack) tenían que reordenar por archivo y línea.

Referencias: Kustomize distingue `patchesStrategicMerge` de reemplazo con directivas explícitas (`$patch: replace`); RimWorld exige `Inherit="False"` para no concatenar y es la fuente de errores más citada de sus mods; Terraform y Jsonnet eligen reemplazo por defecto (`:`) y mezcla explícita (`+:`). Roslyn y rustc suprimen diagnósticos en cascada sobre un nodo ya marcado con error.

## Decisión

- **K3014 (advertencia):** `=` sobre una lista `set` heredada no vacía, o sobre una `keyed` heredada cuando agrega claves nuevas. Mensaje con el arreglo: `+=` para agregar, `=!` para reemplazar, `k[clave] { }` para cambiar un elemento. Actualizar por clave elementos existentes de una `keyed` (`costs = [{ resource = parts  amount = 1 }]`) no advierte. La semántica no cambia (sigue uniendo); cambia que deja de ser silenciosa. `+=` es la forma canónica de agregar. Una entrada de un mixin advierte una sola vez aunque se aplique a muchos objetos.
- **K3015 (advertencia):** `= { }` sobre un record heredado no vacío: no cambia nada; `=!` para vaciarlo.
- **`kedo explain` por elemento:** para listas y mapas lista cada elemento con su capa de origen.
- **Sin cascada:** si un campo se escribió y su valor fue rechazado (K4001, K4003, K4009…), la etapa B no informa K4002 para ese campo, ni en el objeto ni en sus descendientes.
- **Un diagnóstico por valor escrito** (KEDO-02 R25): un error de etapa B sobre un valor heredado se informa una vez, en el primer objeto, y no una vez por cada descendiente. Lo mismo para K3014 y K3015 cuando una entrada de un mixin se aplica a muchos objetos.
- **Orden de declaración:** `ResolvedObject.Order` es la posición de la declaración en la entrada del resolver (archivos en el orden recibido, declaraciones en orden de archivo), y `ResolvedCatalog.InDeclarationOrder` devuelve los objetos en ese orden. El orden de `Objects` no cambia (sigue siendo el canónico para el hash).

## Consecuencias

- El contenido existente que une listas heredadas con `=` empieza a mostrar advertencias; se corrige escribiendo `+=` (o `=!` si la intención era reemplazar). Las advertencias no rompen la compilación.
- Los adaptadores de juego dejan de depender de archivo y línea para el orden.
