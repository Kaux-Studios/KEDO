# KEDO-01 — Gramática v0.1

Revisado: 15/10/2026 · Estado: **v0.1 aceptada por el creador el 15/10/2026** (issue [#1](https://github.com/Kaux-Studios/KEDO/issues/1)); las decisiones de §11 quedan como propuestas aceptadas salvo cambio posterior. Base: anexos [H](../docs/research/anexos/H-Encuesta-de-sintaxis.md) e [I](../docs/research/anexos/I-Semantica-de-herencia-y-mezcla.md); principios en [KEDO-00](KEDO-00-Principios-y-Alcance.md). La semántica de resolución está en [KEDO-02](KEDO-02-Resolucion.md). Los ejemplos completos en [ejemplos/](ejemplos/).

## 1. Forma general

Un archivo `.kedo` contiene una cabecera y declaraciones. Las declaraciones son bloques tipados con palabra clave inicial: todo constructo empieza con una palabra clave o con `identificador =`, de modo que el parser decide con un token de anticipación y en una sola pasada.

```kedo
kedo 1
module patriark

/// Centinela genérico de las máquinas de la red.
abstract actor sentinel_machine {
    name_key = "actor.sentinel"
    tags = [machine, hostile]

    component health { max = 40  regen = 0 }
    component patrol { radius = 6  interval = 10min }
    component sense  { range = 8 }
}

actor depot_sentinel extends sentinel_machine {
    name_key = "actor.sentinel.depot"
    tags += [depot]

    component health { max = 60 }        // mezcla por tipo: solo cambia max
    remove component patrol              // borrado explícito
    component sprite { sheet = asset:sentinel_sheet }

    on arrive(ctx) {
        wait 30min
        apply effect:alarm_raised(ctx.place)
    }
}
```

## 2. Léxico

| Elemento | Regla |
|---|---|
| Codificación | UTF-8 sin BOM. Saltos de línea `\n` (el formateador normaliza `\r\n`) |
| Espacios | Insignificantes salvo como separador de tokens y como terminador de sentencias dentro de `on` y `function` (§8) |
| Comentarios | `// línea`, `/* bloque */` (sin anidar), `/// documentación` adherida a la siguiente declaración |
| Identificador | `[a-z_][a-z0-9_]*`. Minúsculas y guion bajo; el formateador no lo cambia, el parser lo exige (K1003) |
| Id calificado | `ident ('.' ident)*`, por ejemplo `bridge.reinforce`. Solo en cabeceras de declaración y en referencias |
| Ruta de módulo | `ident '/' id-calificado`, por ejemplo `core/basic_item`. Solo en referencias a otro módulo |
| Entero | `[0-9]+` con `_` opcional como separador (`1_500`). Un `-` pegado a un entero o duración en posición de valor forma un literal negativo (`mood_shift = -1`); cualquier otra expresión con `-` va entre paréntesis. Sin flotantes en 1.x |
| Duración | entero seguido sin espacio de `s`, `min`, `h` o `d` (`30min`, `2h`). Un solo token; se baja a entero en la unidad base del esquema (minutos por defecto) |
| Cadena | `"..."` con escapes `\" \\ \n \t \u{XXXX}`. Sin cadenas multilínea en v0.1 |
| Booleano | `true`, `false` |
| Referencia | `tipo ':' id-calificado` o `tipo ':' ruta-de-módulo`, sin espacios: `effect:alarm_raised`, `item:core/rope`. `asset:` es un tipo reservado |
| Puntuación | `{ } [ ] ( ) = += -= =! , ; : . @ ->` |

Palabras reservadas: `kedo module manifest abstract mixin extends with component remove on function let set if else wait apply emit stop return decision roll signal option as and or not true false`. Un tipo registrado por el juego no puede usar una palabra reservada como nombre.

## 3. Cabecera

```ebnf
file     = header { declaration } ;
header   = "kedo" integer newline "module" ident newline ;
```

- `kedo 1`: major del lenguaje. Un toolchain 1.x acepta solo `kedo 1`; un major desconocido produce K1001 con el mensaje de migración (ADR-0001).
- `module nombre`: módulo al que pertenecen los objetos del archivo. Debe coincidir con el manifiesto del directorio (§10). Todos los archivos de un módulo se cargan juntos: **no hay `import` por archivo**; la dependencia entre módulos se declara una vez en el manifiesto y las referencias cruzadas usan la ruta de módulo.

## 4. Declaraciones

```ebnf
declaration = { annotation } [ doc ] ( object | mixin | function | asset ) ;
object      = [ "abstract" ] type id [ "extends" id ] [ "with" id { "," id } ] body ;
mixin       = "mixin" id [ "with" id { "," id } ] body ;
asset       = "asset" id body ;
type        = ident ;                      (* tipo registrado por el juego: actor, event, place... *)
id          = qualified-id ;
body        = "{" { entry } "}" ;
annotation  = "@" ident [ "(" [ argument { "," argument } ] ")" ] ;
```

- Los **tipos** no los define la gramática: los registra el juego (KEDO-00 §tabla). El parser acepta cualquier identificador en posición de tipo; el resolver rechaza los no registrados (K2001).
- `abstract`: el objeto sirve solo como base; no entra al catálogo; las comprobaciones de campos obligatorios no se le aplican.
- `extends`: un único padre, del mismo tipo (K2003), declarado en cualquier archivo del módulo o de un módulo requerido.
- `with`: mixins en orden; se aplican después del padre y antes del cuerpo propio (KEDO-02 §2).
- `mixin`: cuerpo parcial reutilizable. Puede `with` otros mixins; no puede `extends`. No tiene tipo: se valida contra el tipo del objeto que lo incluye.
- `asset`: declaración de un recurso de presentación con identidad estable. Su cuerpo lo define el manifiesto de assets del juego (por ejemplo `kind = texture  path = "art/sentinel.png"`). KEDO solo garantiza identidad y existencia; el motor resuelve la ruta.
- Los ids son únicos por tipo dentro del módulo. Un duplicado es error con las dos ubicaciones (K2002); no existe "el último gana".
- El nombre del archivo no significa nada. Convención sugerida, no obligatoria: un archivo por familia o por objeto grande.

## 5. Entradas de cuerpo

```ebnf
entry        = field | component | removal | hook | function ;
field        = ident ( "=" | "+=" | "-=" | "=!" ) value
             | ident "[" key "]" body ;                       (* elemento de lista con clave *)
component    = "component" ident body ;
removal      = "remove" ( "component" ident | ident { "." ident } ) ;
key          = ident | qualified-id | string | integer ;
```

| Forma | Significado (detalle en KEDO-02) |
|---|---|
| `k = v` | Escalar: reemplaza. Record: mezcla recursiva. Lista: según estrategia del esquema (`atomic` reemplaza; `set` y `keyed` agregan) |
| `k =! v` | Reemplazo total de un record o lista, sin mezcla |
| `k += [..]` | Agrega elementos (listas `set`, `keyed`, `append`) |
| `k -= [..]` | Quita elementos por valor o por clave |
| `k[clave] { .. }` | Mezcla sobre el elemento de clave `clave` en una lista `keyed` |
| `component t { .. }` | Mezcla sobre el componente `t`; lo agrega si no existe |
| `remove component t` | Quita el componente heredado (error si no existe, K3004) |
| `remove k.sub` | Quita un campo heredado opcional |

Las entradas de un cuerpo se separan por espacio en blanco o `;`. Varias entradas en una línea son válidas (`max = 40  regen = 0`); el formateador las deja en una sola línea si el bloque es corto. No hay comas entre entradas.

## 6. Valores

```ebnf
value   = integer | duration | string | boolean | ident        (* enum *)
        | reference | list | record | "(" expression ")" ;
list    = "[" [ value { separator value } [ "," ] ] "]" ;
separator = "," | newline ;
record  = body ;
```

- Los elementos de una lista se separan con coma **o** con salto de línea (un elemento por línea no necesita comas); la coma final es opcional. El formateador conserva la elección del autor.

- Un identificador suelto en posición de valor es un **valor de enumeración** del esquema (`context = travel`, `resolution = d20`). El esquema decide si es válido (K4003). Una palabra reservada también vale como valor de enumeración (`strategy = set`), porque ningún valor empieza con palabra reservada; en cambio no puede ser nombre de objeto ni de campo.
- Un record es un bloque `{ k = v ... }`; sus claves las fija el esquema del campo (cerrado: clave desconocida es K2005 con sugerencia).
- Una expresión compuesta solo aparece entre paréntesis (§7). Así `k = v` termina sin necesidad de salto de línea significativo.

## 7. Expresiones

Solo dentro de paréntesis en un valor, o libremente dentro de sentencias de `on`/`function`.

```ebnf
expression = or ;
or         = and { "or" and } ;
and        = not { "and" not } ;
not        = [ "not" ] comparison ;
comparison = sum [ ( "==" | "!=" | "<" | "<=" | ">" | ">=" ) sum ] ;
sum        = product { ( "+" | "-" ) product } ;
product    = unary { ( "*" | "/" | "%" ) unary } ;
unary      = [ "-" ] postfix ;
postfix    = primary { "." ident | "(" [ expression { "," expression } ] ")" } ;
primary    = integer | duration | string | boolean | reference | ident | "(" expression ")" ;
```

- Tipos: `int` (64 bits), `bool`, `text`, `duration` (entero en unidad base), referencias, enums, listas y records del esquema. Sin flotantes: división entera, `%` resto; porcentajes en puntos básicos si el esquema lo pide.
- Un identificador en expresión es una variable (`ctx`, parámetros, `let`), nunca un enum; los enums en expresiones van calificados `tipo.valor` (`outcome.success`). El nombre del tipo de enum no puede ser una palabra reservada (`option`, `signal`): el juego elige nombres de enum que no choquen.
- En cuerpos de declaración, las expresiones se evalúan **una vez al resolver** sobre literales y constantes del módulo; no hay `self`, `super` ni enlace tardío (ADR-0005). Referencias a otros campos del mismo objeto no están permitidas en v0.1; los campos calculados llegan como característica del esquema (KEDO-02 §5).

## 8. Hooks y funciones

```ebnf
hook       = "on" ident "(" ident ")" block ;
function   = "function" id "(" [ param { "," param } ] ")" [ "->" typename ] block ;
param      = ident ":" typename ;
typename   = "int" | "bool" | "text" | "duration" | ident ;   (* ident: tipo de referencia o enum *)
block      = "{" { statement } "}" ;
statement  = ( "let" ident "=" expression
             | "set" target "=" expression
             | "wait" wait-spec
             | "apply" reference "(" [ args ] ")"
             | "emit" reference "(" [ args ] ")"
             | "if" expression block [ "else" ( block | "if" ... ) ]
             | "stop"                                      (* termina el hook *)
             | "return" expression                        (* solo en function *)
             | call ) terminator ;
wait-spec  = expression                                   (* duración *)
           | "decision" reference "{" { "option" ident block } "}"
           | "roll" ident "(" args ")" "as" ident
           | "signal" reference [ "(" args ")" ] [ "as" ident ] ;
target     = ident { "." ident } ;
terminator = newline | ";" ;
```

- `on nombre(ctx)`: el nombre del hook lo fija el catálogo del juego (K2006 si no existe) y determina el tipo de `ctx`. Un objeto puede declarar varios hooks; dos para el mismo nombre es error (K2007).
- Las sentencias terminan en salto de línea o `;`. Es el **único** lugar donde el salto de línea es significativo. Una expresión no puede partirse en dos líneas salvo dentro de paréntesis o corchetes.
- `wait`: suspende el comportamiento. Sus formas se corresponden con `AwaitRequest` del runtime (tiempo, decisión del jugador, tirada, señal). La VM guarda el frame en ese punto; la etiqueta de reanudación es estable por hook y posición declarada (anexo E).
- `apply effect:x(args)`: la única vía de mutación; pasa por el host con clave idempotente. `emit signal:x(args)`: encola una señal, nunca ejecuta de forma reentrante.
- `function`: funciones puras o con sentencias, llamables desde hooks del mismo módulo o de módulos que lo requieren. Sin recursión en v0.1 (K5002 al compilar).
- Lo que no existe en v0.1: bucles (`for`, `while`), cadenas de formato, flotantes, acceso a estado fuera de `ctx` y de consultas del host (`query`), definición de tipos por el autor.

## 9. Anotaciones y documentación

- `/// texto` antes de una declaración o entrada: documentación para el editor y para agentes; se conserva en el AST y en `kedo schema`.
- `@deprecated("usar actor:x")`, `@editor(label = "Centinela", group = "Amenazas")`, `@since(2)`: anotaciones registradas por KEDO o por el juego; una desconocida es advertencia (K2008). No participan del hash de contenido.

## 10. Módulos y manifiesto

Cada directorio raíz de módulo contiene `module.kedo`:

```kedo
kedo 1
module patriark

manifest {
    version = 3                  // versión de contenido del módulo
    requires = [core]            // módulos de los que se puede referenciar y heredar
    base_unit = min              // unidad de las duraciones
}
```

Todos los `.kedo` bajo ese directorio pertenecen al módulo. Un objeto puede heredar o referenciar objetos de los módulos en `requires` con la forma `tipo:modulo/id`. Ciclos entre módulos son error (K2009).

## 11. Decisiones para la revisión

1. **Sin `import` por archivo** (§3): la dependencia es por módulo. Alternativa: `use core` al inicio del archivo para referenciar sin prefijo. Propuesta: sin `use` en v0.1.
2. **Ids en minúsculas con puntos** (`bridge.reinforce`) para conservar los ids actuales de Patriark sin renombrar. Alternativa: solo `snake_case` y renombrar al migrar.
3. **Enums como identificadores sueltos** en cuerpos (`context = travel`). Alternativa: siempre calificados `context = context.travel`. Propuesta: sueltos en cuerpos, calificados en expresiones.
4. **`=!` para reemplazo total** y `remove` para borrar; sin `null`, sin `!` de limpieza. Un solo verbo por operación.
5. **Mixins en v0.1** (`with`), con advertencia cuando dos mixins escriben el mismo campo. Alternativa: diferir a v0.2.
6. **Funciones sin recursión ni bucles** en v0.1. Los bucles acotados llegan con presupuesto en v0.2 si el contenido los pide.
7. **`asset` como declaración** con id estable, resuelta por el manifiesto del motor. Alternativa: ruta literal en el campo (descartada: no sobrevive a renombres).
8. **Durations como literal de un token** y enteros sin flotantes.

## 12. Códigos de diagnóstico de esta sección

| Código | Significado |
|---|---|
| K1001 | Major de `kedo` no soportado |
| K1002 | Token inesperado |
| K1003 | Identificador con mayúsculas o caracteres inválidos |
| K1004 | Cadena sin cerrar / escape inválido |
| K1005 | Expresión compuesta fuera de paréntesis en un valor |
| K2001 | Tipo no registrado |
| K2002 | Id duplicado (dos ubicaciones) |
| K2003 | Padre de otro tipo |
| K2004 | Padre o mixin inexistente |
| K2005 | Clave desconocida en esquema cerrado (con sugerencia) |
| K2006 | Hook desconocido |
| K2007 | Hook repetido en el mismo objeto |
| K2008 | Anotación desconocida (advertencia) |
| K2009 | Ciclo entre módulos |

Los códigos de resolución (K3xxx), validación (K4xxx) y compilación de lógica (K5xxx) están en KEDO-02.
