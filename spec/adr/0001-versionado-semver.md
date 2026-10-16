# ADR-0001 — Versionado SemVer con majors incompatibles y 1.x aditivo

Fecha: 15/10/2026 · Estado: aceptado · Decisor: creador (Kaux Studios).

## Contexto

Varios juegos consumirán KEDO a lo largo de años. Patriark empieza con 1.0. Un proyecto futuro puede necesitar cambios incompatibles sin obligar a Patriark a migrar.

## Decisión

- SemVer estricto en lenguaje, AST serializado, IR, formato de frames, códigos de diagnóstico, CLI y API pública de `Kedo.Core` y `Kedo.Runtime`.
- **Minor y patch son aditivos.** Nada que compilaba o parseaba deja de hacerlo; nada cambia de significado. Lo obsoleto se marca (`[Obsolete]`, diagnóstico de advertencia con código) y sobrevive hasta el siguiente major.
- **Major rompe** y publica `kedo migrate N→N+1` para archivos `.kedo`, AST y frames guardados cuando sea posible; cuando no, documenta la política de abandono.
- Los clientes fijan `[N.0, N+1.0)`. Patriark: `[1.0, 2.0)`; debe funcionar con 1.9.99.
- Prerelease durante la inicialización: `1.0.0-alpha.N`; no hay garantía de compatibilidad entre alphas. La garantía empieza en `1.0.0`.

## Mecanismos

- `Microsoft.CodeAnalysis.PublicApiAnalyzers` con `PublicAPI.Shipped.txt` por proyecto; un cambio de API pública exige editar el archivo y pasa revisión.
- `Microsoft.DotNet.ApiCompat` contra el último paquete publicado del mismo major en CI.
- Golden corpus del lenguaje: cada versión conserva los casos anteriores y sus salidas esperadas; un cambio de salida en minor es error.
- `KedoVersion.LanguageMajor` en el núcleo; los archivos `.kedo` pueden declarar `kedo 1` en cabecera para que el toolchain rechace majors desconocidos con un mensaje claro.

## Consecuencias

- Diseñar con cuidado antes de 1.0: lo que entra queda hasta 2.0. La spec v0.1 y la conversión del contenido de Patriark (que corre en ese repositorio) son la prueba previa.
- El costo de mantener dos majors en paralelo se acepta solo cuando exista un segundo juego que lo necesite.
