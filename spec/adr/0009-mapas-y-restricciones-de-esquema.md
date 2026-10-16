# ADR-0009 — Mapas y restricciones de esquema

Fecha: 16/10/2026 · Estado: aceptado 16/10/2026 · Origen: hallazgos 5 y 13 de la integración con Patriark (tabla de escenas con claves `role:city_b`, 12 registros abiertos sin tipo para sus valores, reglas de `pack.schema.json` que el editor no ve).

## Contexto

Los datos de juego usan objetos con claves libres: recursos por id, costos, entrenamiento por habilidad, tablas de presentación con claves compuestas (`role:bridge_west`, `biome:mountain`). En KEDO 1.0 sólo había records de campos declarados: o se cerraban con una lista fija de claves, o se abrían sin ningún tipo para los valores, y las claves que no son identificadores no podían escribirse. Además, reglas comunes de los esquemas (tamaño de una lista, largo o valores permitidos de un texto) sólo existían en el JSON Schema del juego.

Cómo lo resuelven otros:

- **CUE:** structs con restricción de patrón `[string]: int`; claves entre comillas cuando no son identificadores.
- **Pkl:** `Mapping<String, Int>`; entradas con `["role:x"] = 1`.
- **HCL / Terraform:** `map(number)`; claves entre comillas.
- **Protocol Buffers:** `map<string, int32>` con claves escalares.
- **JSON Schema:** `additionalProperties` (valor), `propertyNames` (clave), `minItems`/`maxItems`, `minLength`/`maxLength`, `enum`.

## Decisión

- **`kind = map`:** un record de claves libres. `element = { … }` describe los valores con el mismo descriptor que las listas; `keys = <enum>` restringe las claves a un enum (clave fuera del enum: K4003). Sin `keys`, una clave es un identificador o un texto.
- **Claves entre comillas:** en cualquier cuerpo `{ }`, una entrada puede empezar con un texto: `scene_table = { "role:bridge_west" = riverbed }`. Es la forma de escribir claves que no son identificadores; el formateador no quita las comillas. En un record cerrado la clave sigue teniendo que ser un campo declarado.
- **Mezcla:** un mapa se mezcla como un record (clave a clave, recursivo); `=!` lo reemplaza; `remove campo.clave` quita una clave identificador. La forma canónica escribe entre comillas las claves que no son identificadores.
- **Restricciones nuevas del descriptor de campo:**
  - listas y mapas: `min_items`, `max_items` (K4010);
  - textos: `min_length`, `max_length`, medidos en caracteres Unicode (K4011), y `one_of = ["refuge-1", …]` (K4012).
- **Pack binario:** un mapa es un record (sin etiqueta nueva).
- **JSON Schema generado:** `object` con `additionalProperties` y, si hay `keys`, `propertyNames` con el enum; `minItems`/`maxItems`, `minProperties`/`maxProperties`, `minLength`/`maxLength` y `enum` para `one_of`.

## Consecuencias

- La tabla de escenas de Patriark vuelve a ser un objeto (`scene_table = { "role:bridge_west" = riverbed }`) y su compilador deja de traducir una lista keyed.
- Los registros abiertos sin tipo desaparecen del esquema de Patriark: costos, recursos y entrenamiento pasan a `map` con valores `int`.
- Un esquema puede expresar todo lo que el JSON Schema de Patriark valida hoy, lo que permite invertir la dirección más adelante (KEDO como fuente del esquema del juego).
- Uniones ("identificador o texto") siguen fuera: `any` cubre esos campos mientras no haya un caso que justifique un tipo unión.
