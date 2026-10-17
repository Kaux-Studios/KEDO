# ADR-0013 — Valor por defecto vacío para listas y mapas

Fecha: 16/10/2026 · Estado: aceptado 16/10/2026 · Origen: reescritura del contenido de Patriark con herencia y mixins (Plan 02).

## Contexto

El esquema admite `default` para enteros, duraciones, booleanos, textos y enums (KEDO-02 R4). Las listas y los mapas no tenían default, así que un campo obligatorio de ese tipo había que escribirlo siempre, aunque esté vacío: en Patriark cada opción de evento repite `requirements = []` y `effects = []`, y cada servicio `costs = { }` y `rewards = { }`. Es ruido para quien escribe y para los agentes, y no expresa nada.

JSON Schema tiene `default` como anotación (no cambia la validación); Pkl y CUE dan a los objetos y listas un valor inicial vacío; Protobuf 3 trata el campo ausente como vacío. En todos el vacío es el único default que no inventa datos.

## Decisión

- `default = []` en un campo `kind = list` y `default = { }` en un campo `kind = map`: el campo empieza vacío en la capa de defaults. Un campo obligatorio con ese default queda presente aunque el contenido no lo escriba.
- Sólo el vacío: `default = [1]` o `default = { agua = 1 }` es error del esquema. Los records no tienen default: un default inventaría sus campos obligatorios y un record que se contiene a sí mismo no terminaría; un record que puede faltar es un campo opcional.
- Como cualquier default, no provoca K3011, K3014 ni K3015 cuando el contenido escribe el campo (`requirements = [...]` sobre el default vacío reemplaza sin advertir).
- `kedo schema --json` escribe `"default": []` o `"default": {}`.
- API: `FieldSchema.DefaultEmpty` (aditivo).

## Consecuencias

- Los juegos marcan con `default = []`/`{ }` las listas y mapas que suelen estar vacíos; el contenido escribe sólo lo que tiene.
- El orden de los campos del record resuelto sigue el esquema para los que vienen del default; un adaptador que escribe JSON puede cambiar el orden de claves respecto de una fuente que los escribía a mano (los hashes canónicos con claves ordenadas no cambian).
