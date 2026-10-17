# KEDO-02 — Resolución v0.1

Revisado: 15/10/2026 · Estado: **v0.1 aceptada por el creador el 15/10/2026** (issue [#1](https://github.com/Kaux-Studios/KEDO/issues/1)); las decisiones de §12 quedan como propuestas aceptadas salvo cambio posterior. Base: anexo [I](../docs/research/anexos/I-Semantica-de-herencia-y-mezcla.md) (CUE, Nickel, Jsonnet, Pkl, Dhall, Kubernetes, Nix, Terraform, CSS, Unreal, Unity, C3) y anexo [D](../docs/research/anexos/D-Patrones-de-datos-en-otros-juegos.md). Gramática en [KEDO-01](KEDO-01-Gramatica.md).

Define cómo un conjunto de declaraciones se convierte en un **catálogo resuelto**: objetos inmutables, completos, validados y hasheados, que el juego consume sin volver a interpretar herencia ni mixins.

## 1. Modelo

- **Declaración:** lo que el autor escribió (AST de un objeto, mixin, función o asset) con su ubicación.
- **Objeto resuelto:** el resultado de aplicar capas (§2) sobre los valores por defecto del esquema; solo literales y referencias por id; sin expresiones pendientes.
- **Catálogo:** todos los objetos resueltos no abstractos de un módulo y sus dependencias, indexados por `(tipo, id)`, con hash por objeto y hash global.
- **Instancia:** estado de partida creado por el juego a partir de un objeto resuelto; guarda `(tipo, id, versión del módulo, hash del objeto)`. Fuera del alcance de KEDO salvo por los frames de comportamiento.

## 2. Capas y orden

Para un objeto `O` con padre `P` y mixins `M1..Mn`:

```
Defaults(esquema del tipo) → Resuelto(P) → M1 … Mn (aplanados) → Propio(O)
```

- R1. `extends` es único y del mismo tipo. Resuelto(P) es el valor final de P, con sus propios mixins ya aplicados.
- R2. La lista de mixins se **aplana** en profundidad, de izquierda a derecha, eliminando repetidos y conservando la **primera** aparición. No hay relación padre-hijo entre mixins, así que no hace falta C3. Si dos mixins escriben el mismo escalar, gana el posterior y se emite K3010 con ambas ubicaciones.
- R3. Las aristas `extends` y `with` forman un DAG entre todos los módulos cargados; la resolución es en orden topológico; cada objeto se resuelve una sola vez; el resultado es inmutable y comparte estructura con el padre donde no cambió.
- R4. Los defaults del esquema son la capa inferior virtual: cualquier valor explícito gana a un default; la procedencia registra "default" vs "autoría".
- R4b. Variantes ([ADR-0012](adr/0012-variantes.md)): en la vista de una variante, `override tipo id { }` agrega una capa después de la propia: `… → Propio(O) → Override₁(O) … Overrideₙ(O)` (en orden de archivo). La procedencia es `variant nombre`. Un objeto que extiende a uno sobrescrito hereda el resultado sobrescrito. Cada variante es un catálogo completo (base + objetos nuevos de la variante + overrides), con su propio hash; la base no ve los archivos de variantes.

## 3. Reglas de mezcla por clase de valor

| Clase | `k = v` | `k =! v` | `k += [..]` | `k -= [..]` | `k[clave] { }` |
|---|---|---|---|---|---|
| Escalar (int, decimal, bool, text, duration, enum, referencia, asset) | reemplaza | — (K3005) | — | — | — |
| Record | mezcla recursiva campo a campo (`= { }` sobre uno heredado no vacío: advertencia K3015) | reemplaza entero | — | — | — |
| Mapa (`kind = map`, ADR-0009) | mezcla clave a clave | reemplaza entero | — | — | — |
| Lista `atomic` (default si el esquema no dice otra cosa) | reemplaza entera (K3011 advierte si la heredada no era vacía) | reemplaza | — (K3005) | — | — |
| Lista `set` (escalares únicos) | agrega los ausentes (K3014 advierte si la heredada no era vacía) | reemplaza | agrega los ausentes | quita por valor exacto (K3004 si falta) | — |
| Lista `keyed(k)` (records con clave única) | mezcla por clave; claves nuevas al final (K3014 advierte cuando agrega a una heredada) | reemplaza | agrega (clave existente: K3006) | quita por clave (K3004 si falta) | mezcla sobre el elemento |
| Lista `append` | concatena | reemplaza | concatena | — (K3005) | — |
| Componentes (`keyed(tipo)`) | `component t { }` mezcla o agrega | — | — | `remove component t` | — |
| Tags (`set`) | agrega | reemplaza | agrega | quita | — |

- R5. Escalares: gana la última capa. Un solo eje de precedencia (el orden de capas): sin prioridades, sin `force`, sin `!important`.
- R6. Records: mezcla recursiva por defecto porque el caso dominante es sobrescribir un subcampo de un bloque de stats. `=!` para reemplazo total.
- R7. Listas: la estrategia la declara el **esquema del campo**. Sin estrategia es `atomic`. Nunca hay concatenación por defecto (lección de RimWorld). No hay edición por índice.
- R8. Orden resultante en `set`/`keyed`/`append`: elementos heredados primero en su orden, luego los agregados en orden de autoría. Para el hash, el esquema decide si el orden es significativo; si no, se canonicaliza por clave.
- R8b. Posición fija ([ADR-0011](adr/0011-posicion-fija-en-listas.md)): `k += first [..]` y `k += last [..]` anclan los elementos agregados. El orden final es: anclados `first`, no anclados (R8), anclados `last`; cada grupo conserva su orden. El ancla viaja con el elemento por herencia, mixins y mezclas por clave; `-=` y un nuevo `+=` sin palabra la quitan. Agregar con ancla a un `set` un valor que ya está no lo mueve (K3016). El ancla no entra al hash: sólo el orden resultante.
- R9. Las referencias son escalares: se resuelven a ids, nunca se inlinean ni mezclan el objeto apuntado.
- R10. Tags: unión por defecto; es el único caso donde reemplazar por defecto sorprendería. Revisado el 16/10/2026 ([ADR-0010](adr/0010-diagnosticos-de-mezcla-y-orden-de-declaracion.md)): la unión con `=` sigue igual pero advierte (K3014); la forma canónica de agregar es `+=` y la de reemplazar `=!`.

## 4. Borrado

- R11. El borrado es explícito y tipado: `remove component t`, `remove campo.sub`, `-= [..]`. No existe `null` como valor ni como borrado.
- R12. Borrar algo que ninguna capa anterior aportó es error K3004 (no un no-op silencioso). Borrar un campo obligatorio de un objeto concreto es K3007.
- R13. Un borrado persiste en los descendientes salvo que lo vuelvan a declarar; la procedencia registra la capa que borró.

## 5. Sin enlace tardío; campos calculados

- R14. Ninguna capa evalúa expresiones contra el objeto final. No hay `self` ni `super`. Las expresiones en cuerpos se evalúan al resolver sobre literales y constantes; `Resolver(O)` depende solo de Resuelto(P), de los mixins y de la declaración de O.
- R15. **Campos calculados** (v0.2, diseño fijado ahora): el esquema declara `computed max_hp = base_hp * level`; se evalúan una vez, después de resolver todos los objetos, en orden estático, sobre campos resueltos del propio objeto y de los referenciados; ciclos son error al cargar el esquema (K3012); entran al hash; el autor no los puede sobrescribir.

Razón (anexo I §5): el enlace tardío obliga a cerrar cada campo en una clausura por objeto final, impide reutilizar el resultado del padre, vuelve dinámicos los ciclos y rompe el "si ves un valor concreto, es el final". Los campos calculados dan el beneficio práctico sin ese costo.

## 6. Procedencia y `explain`

- R16. Cada objeto resuelto conserva, fuera del hash, `ruta → { archivo, span, objeto, capa ∈ {default, parent, mixin(nombre), own, removed} }` para cada hoja, elemento de lista y componente, apuntando al **sitio de autoría original**.
- R17. `kedo explain tipo:id.ruta` imprime el valor ganador, su origen y cada candidato sombreado en orden de capas; en listas y mapas, además, el origen de cada elemento (lo heredado aparece como `parent <objeto>`). Es la herramienta principal para "¿de dónde salió este valor?" tanto para personas como para agentes.
- R18. Representación compacta: arrays `(índice de ruta → id de origen)` compartidos con el padre; alternativa de recálculo bajo demanda (la resolución de un objeto es barata y determinista).

## 7. Ciclos, límites y determinismo

- R19. Ciclo en `extends`/`with`: K3001 con la ruta completa `A → B → C → A` y la ubicación de cada arista; los miembros quedan sin resolver; los dependientes reportan K3002 una sola vez (sin cascada).
- R20. Ciclos de **referencia** entre objetos están permitidos; las referencias no participan del orden de resolución.
- R21. Límites como errores, nunca truncamiento silencioso: profundidad de herencia ≤ 32 (K3008), mixins aplanados por objeto ≤ 64 (K3009), anidamiento de records ≤ 16 (K3013).
- R22. Iteración en orden de declaración dentro de un archivo y por ruta de archivo ordinal entre archivos; diagnósticos ordenados por `(archivo, span, código)`; nunca se itera un mapa hash hacia la salida.

## 8. Validación en dos etapas

- R23. **Etapa A, por declaración** (sin resolver): errores de parseo; tipo, componente, hook o campo desconocidos (esquemas cerrados, con sugerencia); clase de valor incorrecta (K4001); operación de lista no permitida para la estrategia (K3005); padre o mixin ausente; id duplicado. Ubicación exacta de autoría.
- R24. **Etapa B, por objeto resuelto concreto** (los `abstract` se saltan): campos obligatorios (K4002, salvo si el campo se escribió y su valor ya fue rechazado), rangos (K4004), escala decimal (K4009), cantidad de elementos (K4010), largo de texto (K4011), texto fuera de `one_of` (K4012), enums (K4003), referencia inexistente (K4005) o de tipo equivocado (K4006), asset de otra clase que la que pide el campo (`asset_kind` contra el campo `kind` del objeto asset, K4013), asset ausente del manifiesto (K4007, advertencia en modo rápido), invariantes declaradas por el esquema del juego (K4008).
- R25. Todo diagnóstico de etapa B cita el objeto resuelto y la ruta, el origen de autoría por procedencia y la cadena de herencia. Errores idénticos con el mismo origen se agrupan: "`max` fuera de rango, definido en base.kedo:12, afecta a 37 objetos".
- R26. Los validadores **acumulan**, no lanzan; el resolver devuelve el catálogo con su lista de diagnósticos aunque haya errores; el juego rechaza cargar con algún error.

## 9. Hash e incrementalidad

- R27. `Hash(O) = XxHash128(canónico(Resuelto(O)))`, donde canónico serializa en orden de campos del esquema, listas en orden resuelto o canonicalizado (R8), referencias por id, sin procedencia, sin anotaciones ni documentación. `Hash(catálogo)` sobre las líneas `tipo:id=hash` ordenadas. Un hash profundo opcional sobre la clausura de referencias sirve al pipeline de assets.
- R28. Un cambio en B no vuelve a hashear A por referenciar a B.
- R29. Incremental por archivo (modelo Salsa): parseo por archivo → hash por declaración → corte temprano (hash de declaración igual ⇒ nada río abajo se re-resuelve) → re-resolución del objeto y sus descendientes por herencia → re-validación de referentes solo si cambió el tipo o el conjunto de componentes. Índices inversos `padre → hijos` e `id → referentes`.
- R29b. Implementado el 17/10/2026 (`IncrementalCatalog`, #15): un archivo con los mismos bytes no se parsea de nuevo; un objeto se resuelve de nuevo sólo si cambió la huella de su declaración (texto **y posición**, porque la procedencia es parte de lo resuelto), si se resolvió de nuevo su padre o si cambió uno de sus mixins; si no, se reutiliza con sus diagnósticos. La etapa B se repite para esos objetos y para los que referenciaban una clave que cambió, apareció o desapareció (índice `objeto → claves referidas`). Cada objeto guarda todos los diagnósticos que informaría y la unión se deduplica, así el conjunto no depende de qué se reutilizó: el resultado es igual al de una resolución completa (catálogo, hashes, procedencia, diagnósticos, `HasErrors`, orden). Con variantes (ADR-0012) se resuelve completo.

## 10. Ejemplo resuelto

Esquema (abreviado) del tipo `unit`: componentes `stats { hp: int[0..9999] obligatorio; spd: int default 5 }`, `loot { table: keyed(id) de { id; weight: int } }`; `tags: set`; `abilities: set de referencias`.

```kedo
abstract unit creature {
    component stats { hp = 100 }
    component loot  { table = [ { id = gold  weight = 10 }, { id = hide  weight = 3 } ] }
    tags = [animal]
}

mixin elite {
    component stats { hp = 250  spd = 7 }
    component loot  { table += [ { id = trophy  weight = 1 } ]  table -= [hide] }
    tags += [elite]
    abilities = [ability:roar]
}

unit wolf extends creature {
    component stats { hp = 120 }
    abilities = [ability:bite]
    tags += [pack]
}

unit alpha_wolf extends wolf with elite {
    component stats { spd = 9 }
    component loot  { table[gold] { weight = 25 } }
    remove component vision          // K3004: ninguna capa aportó vision
}
```

Orden: `creature → wolf → alpha_wolf`.

- `wolf.stats.hp = 120` (propio), `spd = 5` (default); `loot.table = [gold:10, hide:3]`; `tags = {animal, pack}`; `abilities = {bite}`.
- `alpha_wolf.stats.hp = 250` (mixin `elite`; sombreados: wolf 120, creature 100); `spd = 9` (propio); `loot.table = [gold:25, trophy:1]` (hide borrado por `elite`; `gold.weight` propio); `tags = {animal, pack, elite}`; `abilities = {bite, roar}`; K3004 en `remove component vision` con la lista de capas consultadas.
- `kedo explain unit:alpha_wolf.stats.hp` → `250 desde mixin elite (mixins.kedo:3); sombreados: 120 wolf (wolves.kedo:2), 100 creature (base.kedo:2)`.
- Editar solo `wolves.kedo` re-resuelve `wolf` y `alpha_wolf`; cambiar un comentario en `base.kedo` re-parsea pero no re-resuelve nada (hash de declaración igual).

## 11. Códigos de diagnóstico

| Código | Significado |
|---|---|
| K3001 | Ciclo de herencia o mixins (ruta completa) |
| K3002 | Base sin resolver por un error anterior (una vez, sin cascada) |
| K3004 | Borrado de algo que ninguna capa aportó |
| K3005 | Operación no permitida para la clase o estrategia del campo |
| K3006 | Clave duplicada en una lista `keyed` dentro de una capa |
| K3007 | Borrado de un campo obligatorio en un objeto concreto |
| K3008 | Profundidad de herencia > 32 |
| K3009 | Mixins aplanados > 64 |
| K3010 | Dos mixins escriben el mismo escalar (advertencia) |
| K3011 | Lista `atomic` heredada no vacía reemplazada sin `=!` (advertencia) |
| K3012 | Ciclo entre campos calculados del esquema |
| K3013 | Anidamiento de records > 16 |
| K3014 | `=` agrega a una lista `set` o `keyed` heredada no vacía (advertencia; `+=` o `=!`) |
| K3015 | `= { }` sobre un record heredado no vacío no cambia nada (advertencia; `=! { }`) |
| K3016 | `+= first` o `+= last` de un valor que ya está en una lista `set`: no se mueve (advertencia; `-=` antes) |
| K4001 | Clase de valor incorrecta |
| K4002 | Campo obligatorio ausente en objeto concreto |
| K4003 | Valor de enumeración inválido |
| K4004 | Valor fuera de rango |
| K4005 | Referencia a objeto inexistente |
| K4006 | Referencia a objeto de otro tipo |
| K4007 | Asset ausente del manifiesto |
| K4008 | Invariante del esquema del juego incumplida |
| K4009 | Decimal con más decimales que la escala del campo |
| K4010 | Lista o mapa fuera de `min_items`/`max_items` |
| K4011 | Texto fuera de `min_length`/`max_length` |
| K4012 | Texto que no está en `one_of` |
| K4013 | Referencia `asset:` a un asset de otra clase: el campo declara `asset_kind` y el objeto asset tiene otro `kind` |
| K5001 | Hook: variable o campo de contexto desconocido |
| K5002 | Función recursiva o ciclo de llamadas |
| K5003 | Tipo incompatible en expresión |
| K5004 | `wait` en un hook que no admite suspensión |
| K5005 | `apply` en un hook que no admite efectos |
| K5007 | Decimal dentro de un hook, una función o una expresión (los decimales son datos en 1.x) |

## 12. Decisiones para la revisión

1. Mezcla recursiva de records por defecto (propuesto) frente a reemplazo por defecto (Terraform, Jsonnet `:`).
2. Listas `atomic` por defecto con advertencia K3011, frente a exigir estrategia explícita en todo esquema.
3. Mixins con "último gana" + advertencia, frente a error duro cuando dos mixins chocan y el propio no decide.
4. Campos calculados diferidos a v0.2 con el diseño ya fijado.
5. XxHash128 sobre forma canónica binaria (anexo F) en lugar de SHA-256 sobre JSON canónico como hace hoy Patriark. Implica que Patriark migre su hash de contenido al adoptar KEDO; el compilador al pack puede seguir emitiendo el SHA-256 actual durante la transición.
