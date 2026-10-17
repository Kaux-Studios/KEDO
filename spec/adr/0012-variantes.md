# ADR-0012 — Variantes: archivos `variant` y declaraciones `override`

Fecha: 16/10/2026 · Estado: aceptado 16/10/2026 · Origen: hallazgo 10 y propuesta P10 de la integración con Patriark.

## Contexto

Patriark compila dos campañas desde la misma fuente: `prototype-9` y `test-1`, que es la primera con un gremio de prueba, otro inventario y un recluta con todos los atributos altos. Los ids son únicos por tipo (KEDO-01 §4), así que la variante no podía redeclarar `inventory guild`: usó la convención `test_1.guild`, y el compilador del juego quita el prefijo y decide qué objeto entra a cada campaña. Es conocimiento fuera del lenguaje: el editor, `kedo explain` y los agentes no saben que `test_1.guild` reemplaza a `guild`.

Lo mismo hace falta para mods, parches de balance y campañas derivadas. Cómo lo resolvieron otros:

- **Datapacks de Minecraft:** un pack posterior reemplaza el archivo con la misma ruta; las etiquetas se pueden unir con `replace: false`. Un error de tipeo en la ruta crea un archivo nuevo en silencio.
- **Paradox (`replace_path`, sobrescritura por nombre de objeto):** el mod redefine el objeto entero; el último cargado gana, con advertencias de conflicto poco claras.
- **Parches de RimWorld (`PatchOperation*` sobre XPath):** editan la base en su lugar; potentes, frágiles cuando la base cambia.
- **Kustomize (overlays sobre una base):** cada overlay es un directorio que referencia la base y aplica parches estratégicos por nombre de recurso; la base no sabe de los overlays y cada overlay produce una salida completa.

## Decisión

- **Archivo de variante:** `variant nombre` en la línea siguiente a `module` hace que todas las declaraciones del archivo pertenezcan a esa variante. La variante es una capa sobre el contenido base del módulo (como un overlay de Kustomize): los archivos sin `variant` forman la base, y cada variante se resuelve como base + sus archivos.
- **`override tipo id { … }`** dentro de un archivo de variante aplica sus entradas sobre el objeto base con ese id, como una **capa más después de la propia** (`Defaults → padre → mixins → propio → override`). Las reglas de mezcla son las de siempre: `=` mezcla listas `set`/`keyed` y records (con K3014/K3015), `=!` reemplaza, `+=`/`-=`/`k[clave] { }` editan. Un `override` no cambia `extends`, `with` ni `abstract` (K2008).
- **Los descendientes ven el override:** en la vista de la variante, un objeto base que extiende a uno sobrescrito hereda la versión sobrescrita (`override character mara { level = 5 }` sube también a quienes extienden `mara`). Es lo que distingue una variante de una copia.
- **Objetos nuevos:** una declaración normal en un archivo de variante es un objeto que sólo existe en esa variante. Repetir un id de la base es K2002, con la pista de escribir `override`; nunca hay reemplazo implícito.
- **Errores:** `override` fuera de un archivo de variante (K2009); `override` de un id que no existe en la base (K2010, con sugerencia por cercanía): el error de tipeo de los datapacks no puede pasar en silencio.
- **Palabras contextuales:** `variant` sólo cuenta en la cabecera (después de `module`, seguida de un identificador y sin `{`, `extends` ni `with`); `override` sólo al inicio de una declaración seguida de tipo e id. Siguen siendo válidas como tipos, ids y campos.
- **API:** `Resolver.Resolve` devuelve el catálogo base y resuelve cada variante en `ResolvedCatalog.Variants` (nombre → catálogo completo, con su hash); los diagnósticos de las variantes que la base no informó se suman a la misma bolsa. `ResolvedCatalog.Variant`, `FileDeclarations.Variant`, `ObjectDeclaration.IsOverride` y `LayerKind.Variant` (procedencia `variant nombre (archivo:línea)`). La CLI acepta `--variant=nombre` en `resolve`, `explain`, `catalog`, `compile` y `build`; `check` valida todas. El MCP suma el parámetro `variant` a `kedo_resolve`, `kedo_explain` y `kedo_catalog` (y `kind = variants`).

Una variante no extiende otra variante ni sobrescribe mixins o funciones: cuando un juego lo necesite se agrega (`variant b extends a`), sin romper lo anterior.

## Consecuencias

- Las variantes de campaña, los parches de balance y los mods se escriben con la misma herramienta y `kedo explain` muestra qué capa de qué variante puso cada valor.
- Resolver N variantes cuesta N+1 resoluciones del módulo. Con el presupuesto actual (ADR-0007) es aceptable para decenas de variantes; la resolución incremental (#15) lo reduce a los objetos afectados.
- Los adaptadores de juego dejan de inventar convenciones de prefijo: Patriark compila `test-1` desde la vista de su variante.
