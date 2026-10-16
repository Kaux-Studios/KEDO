# ADR-0004 — Sintaxis de bloques tipados con palabra clave inicial

Fecha: 15/10/2026 · Estado: propuesto (issue #1) · Base: anexo H.

## Contexto

Se compararon tres familias de sintaxis sobre el mismo ejemplo (centinela abstracto, variante con componentes sobrescritos y borrados, asset, trigger con espera): **A** bloques tipados con palabra clave (`actor x extends y { k = v  component t { } remove component p  on e(ctx) { } }`, forma Clausewitz/HCL), **B** nodos uniformes (KDL: `actor "x" extends="y" { ... }`), **C** expresiones de amend (Pkl/Jsonnet: `x = (y) { ... super ... }`).

## Decisión

Sintaxis **A**:

- Toda declaración empieza con palabra clave o `identificador =`: un token de anticipación, una pasada, sin precedencia fuera de paréntesis.
- Los ids se declaran en la cabecera, con tipo, únicos por tipo y módulo; el nombre del archivo no significa nada; duplicado = error, nunca "último gana" (Paradox LIOS y Factorio como ejemplos negativos).
- Operaciones semánticas visibles al parser: `=`, `=!`, `+=`, `-=`, `k[clave] { }`, `component`, `remove`, `with`, `extends`, `abstract`, `on`, `function`. Nada de convenciones en strings (`mode="append"`, `delete=#true`) ni valores centinela (`null`, `"*delete"`).
- Referencias tipadas `tipo:id` (prefijos Clausewitz); `asset:id` resuelto por manifiesto.
- Expresiones compuestas solo entre paréntesis en cuerpos de declaración; en hooks y funciones las sentencias terminan en salto de línea o `;`.
- Duraciones como literal de un token (`30min`), sin flotantes.

## Alternativas rechazadas

- **B (KDL):** gramática más pequeña, pero empuja mezcla, borrado, referencias y expresiones a convenciones y strings: donde más se equivocan modders y agentes. Viola "una sola forma".
- **C (Pkl/Jsonnet):** máxima composición, pero exige evaluador con `super`/enlace tardío; "late binding inconsistente" documentado en Jsonnet; `null` como borrado es la ambigüedad que Pkl rechazó; ids como variables sin namespace de tipo. Peor ajuste para parser de una pasada e IR entero.

## Consecuencias

- Unas pocas palabras clave más que aprender (`component`, `remove`, `with`, `on`).
- El formateador tiene una forma canónica obvia (bloques cortos en una línea, largos expandidos).
- El resaltado y los formularios del editor se generan de una sola tabla de tokens y del esquema.
