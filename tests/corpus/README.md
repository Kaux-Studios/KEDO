# Golden corpus

Cada caso: `input.kedo`, `ast.json` esperado, `diagnostics.json` esperado, `formatted.kedo` esperado y, si ejecuta, `trace.json`. Los casos nunca se borran dentro de un major (ADR-0001).

Única excepción, previa a 1.0: el caso `patriark-prototype-9` (pack real de Patriark importado) se retiró del repositorio y de su historial al hacerlo público el 16/10/2026, porque era contenido privado del juego ([ADR-0002](../../spec/adr/0002-repositorio-aislado.md), enmienda). Ese contenido se compila ahora con KEDO en la CI de Patriark.
