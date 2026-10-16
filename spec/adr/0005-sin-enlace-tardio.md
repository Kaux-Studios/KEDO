# ADR-0005 — Sin enlace tardío en la mezcla; campos calculados en pasada posterior

Fecha: 15/10/2026 · Estado: propuesto (issue #1) · Base: anexo I §5.

## Contexto

Jsonnet, Pkl, Nickel, CUE y Nix evalúan los campos contra el objeto final (`self`, `super`, fixpoint). Da campos derivados que siguen las sobreescrituras del hijo, a costa de clausuras por campo y objeto, recomputación de fixpoint, ciclos dinámicos con malas ubicaciones y pérdida de la garantía "un valor concreto es el final". Jsonnet necesitó cachés por objeto (20–450x); CUE reescribió su evaluador por la contabilidad de cierre; Nickel documenta la recomputación como costo.

## Decisión

- La mezcla (KEDO-02 §2–3) no evalúa expresiones contra el objeto final. Las capas aportan literales, referencias y expresiones constantes evaluadas una vez. `Resolver(O)` depende solo de `Resuelto(P)`, los mixins y la declaración de O.
- Los **campos calculados** se declaran en el esquema del tipo (`computed max_hp = base_hp * level`), se evalúan una vez después de resolver todos los objetos, en orden estático, sobre campos resueltos propios y de objetos referenciados; ciclos son error al cargar el esquema; entran al hash; el autor no los sobrescribe. Llegan en v0.2 con este diseño ya fijado.

## Consecuencias

- Cada objeto se resuelve una vez y comparte estructura con su padre; incrementalidad por objeto, no por campo.
- Los autores que esperan `self` deberán usar campos calculados del esquema o parámetros explícitos. `kedo explain` muestra de dónde sale cada valor.
- Si un juego necesita plantillas parametrizadas (`extends template(level = 3)`), se evalúan como una función pura cacheable por tupla de argumentos; no se decide en v0.1.
