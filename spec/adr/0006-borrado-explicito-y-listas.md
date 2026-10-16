# ADR-0006 — Borrado explícito y estrategias de lista declaradas por el esquema

Fecha: 15/10/2026 · Estado: aceptado 15/10/2026 (issue #1) · Base: anexos D §1, H, I §3–4.

## Contexto

Las fuentes de error recurrentes en lenguajes de contenido: listas que se concatenan por defecto y exigen una escotilla para no hacerlo (RimWorld `Inherit="False"`); borrado por valor centinela (`null` en RFC 7386, `"*delete"` en Caves of Qud, documentado como roto); imposibilidad de borrar (CUE, Nickel, Pkl); edición posicional que se rompe al cambiar la base (RFC 6902, Pkl `[0] {}`); prioridades no locales (Nickel `force`, CSS `!important`).

## Decisión

- **Borrado explícito y tipado:** `remove component t`, `remove campo.sub`, `k -= [valores o claves]`. Borrar algo que ninguna capa aportó es error (K3004), no un no-op. No existe `null`.
- **Estrategia de lista en el esquema**, no en el archivo: `atomic` (default: reemplaza entera, con advertencia K3011 si la heredada no era vacía), `set` (agrega sin duplicar, quita por valor), `keyed(k)` (mezcla por clave, `k[clave] { }` edita un elemento, claves nuevas al final), `append` (concatena). Nunca concatenación por defecto; nunca edición por índice.
- **Un solo eje de precedencia:** el orden de capas (defaults → padre → mixins → propio). Sin prioridades numéricas ni `force`.
- **Reemplazo total con `=!`** para records y listas cuando la mezcla no es lo deseado.

## Consecuencias

- Los esquemas del juego deben anotar la estrategia de cada lista que se herede de forma habitual (opciones de evento: `keyed(id)`; tags: `set`). KEDO-00 lo registra como responsabilidad del juego.
- Renombrar una clave en una base convierte los `-=` de los descendientes en K3004: es deliberado; se agrupan por origen para no inundar.
- Los agentes de IA no pueden confundir "vaciar" con "quitar": cada operación tiene una sola escritura.
