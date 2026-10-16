# ADR-0003 — Lenguaje de texto propio; JSON solo como artefacto

Fecha: 15/10/2026 · Estado: aceptado · Decisor: creador (Kaux Studios).

## Contexto

La investigación de origen (docs/research/01) propuso JSON por objeto con un resolver. El creador lo rechazó: ya se había descartado en Patriark porque hace falta heredar DTO de DTO más genéricos, importar, escribir funciones y triggers junto al contenido y referenciar animaciones, sonidos y otros assets, sin quedar encapsulados en puro dato. `.tres` de Godot tampoco lo da (sin herencia de valores entre archivos, sin funciones por objeto). El trade-off JSON vs DSL (docs/research/01 §D1 revisada) mostró que la arquitectura estándar de compilador los hace complementarios: AST serializable + sintaxis de superficie.

## Decisión

- La **fuente canónica** es texto `.kedo` con sintaxis propia: declaración de objetos, `extends`, `import`, `mixin`, componentes, expresiones, funciones, triggers, refs de assets, comentarios.
- El **AST** es el modelo central; se serializa a JSON (source generators) como formato de intercambio y como salida compilada (catálogo resuelto, IR). Existe un formateador canónico AST→texto y un parser texto→AST; la ida y vuelta preserva comentarios.
- Nadie edita el JSON a mano. Las herramientas (addon de Godot, MCP, generadores) editan el AST y escriben `.kedo`.
- Los nodos de lógica (funciones, triggers, IR) forman parte del AST desde 1.0, aunque la VM se complete después, para que el formato nunca quede "solo datos".

## Consecuencias

- Hay que diseñar y mantener un lenguaje: spec versionada, parser, formateador, diagnósticos, migraciones. Se acepta por la prioridad de flexibilidad y por ADR-0001.
- El rendimiento del parser es presupuesto de primera clase (ADR futuro sobre arquitectura del toolchain tras la investigación F).
- Las herramientas para IA necesitan gramática, ejemplos y validador en bucle (investigación G).
