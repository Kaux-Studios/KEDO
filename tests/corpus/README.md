# Golden corpus

Cada caso: `input.kedo`, `ast.json` esperado, `diagnostics.json` esperado, `formatted.kedo` esperado y, si ejecuta, `trace.json`. Los casos nunca se borran dentro de un major (ADR-0001).

Casos de la API del host y la entrada rápida ([ADR-0015](../../spec/adr/0015-entrada-rapida.md), `host-api-*`): `schema/*.kedo` (esquema del juego) y `content/*.kedo`, con `diagnostics.txt` (esquema, resolución, compilación y enlace) y, si no hay errores, `trace.txt` (cada hook corrido en `FastBehaviorVm` hasta el final, con las llamadas al host y los frames guardados; `BehaviorVm` debe hacer las mismas llamadas). Un `unbound.txt` opcional lista los nombres (`kind name`) que el host no registra.

Única excepción, previa a 1.0: el caso `patriark-prototype-9` (pack real de Patriark importado) se retiró del repositorio y de su historial al hacerlo público el 16/10/2026, porque era contenido privado del juego ([ADR-0002](../../spec/adr/0002-repositorio-aislado.md), enmienda). Ese contenido se compila ahora con KEDO en la CI de Patriark.
