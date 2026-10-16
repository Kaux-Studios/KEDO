# Instrucciones para agentes

KEDO es un lenguaje y toolchain propios. Antes de cambiar algo, leer [spec/KEDO-00-Principios-y-Alcance.md](spec/KEDO-00-Principios-y-Alcance.md) y los [ADR](spec/adr/).

## Reglas

- Prioridad: rendimiento > facilidad de uso > escalabilidad > flexibilidad. Un cambio que empeora un benchmark necesita justificación en un ADR.
- `Kedo.Core` y `Kedo.Compiler` no dependen de ningún motor ni de paquetes externos. Cero reflexión; serialización por source generators; `IsAotCompatible` y `JsonSerializerIsReflectionEnabledByDefault=false` se mantienen y el build no admite advertencias.
- Nada específico de un juego entra al núcleo. Si Patriark necesita algo, se resuelve con un punto de extensión y se prueba también en `samples/`.
- 1.x es aditivo (ADR-0001): no quitar ni cambiar significado de sintaxis, AST, IR, frames, códigos de diagnóstico ni API pública. Marcar obsoleto, no borrar.
- Toda capacidad del lenguaje entra con: caso en la spec, golden test (entrada `.kedo`, AST esperado, diagnósticos esperados, salida formateada) y, si ejecuta, traza esperada de la VM.
- Diagnósticos: código estable `KEDOxxxx`, posición exacta, mensaje con sugerencia. Nunca lanzar excepciones por errores de contenido; acumular.
- La CLI expone todo con `--json`. El servidor MCP usa el mismo núcleo que la CLI; nada se implementa dos veces.
- Documentación en español; código, identificadores, mensajes de diagnóstico y nombres de archivo en inglés.
- Verificar antes de afirmar: `dotnet build Kedo.sln -c Release` sin advertencias, `dotnet test`, y los benchmarks cuando se toca parser, resolver o VM.

## Flujo de trabajo

Issue → investigación si algo no está claro (cómo lo resolvieron otros; proponer algo mejor si se puede) → ADR si cambia una decisión → implementación con golden tests → actualizar spec y docs en el mismo cambio.
