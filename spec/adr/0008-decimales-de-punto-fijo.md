# ADR-0008 — Decimales de punto fijo

Fecha: 16/10/2026 · Estado: aceptado 16/10/2026 · Origen: hallazgo 3 de la integración con Patriark (decimales de generación de mapas guardados como centésimas y una lista de rutas en el compilador del juego).

## Contexto

KEDO 1.0 nació sin flotantes para que los valores sean deterministas y la VM trabaje sólo con enteros. El primer contenido real ya trae decimales de datos (`biome_cost = 1.25`, `climb_surcharge = 0.5`): escribirlos como centésimas obliga a cada juego a recordar qué rutas se escalan, y el editor y los agentes no pueden saberlo.

Cómo lo resuelven otros:

- **Paradox (Clausewitz):** los decimales del contenido son de punto fijo (`CFixedPoint`, tres decimales, más en motores nuevos) precisamente para que la simulación sea idéntica en todas las máquinas del multijugador sincronizado.
- **SQL `DECIMAL(p, s)`** y **.NET `decimal`:** escala declarada, aritmética exacta.
- **CUE:** decimales de precisión arbitraria; exactos pero caros y sin un tamaño fijo.
- **Pkl, Dhall, TOML, Factorio, Godot:** `double`. Simple, pero el resultado depende de la plataforma y del orden de las operaciones, y el texto de un número no vuelve igual.
- **JSON Schema:** `"type": "number"` con `multipleOf` para expresar la escala.

## Decisión

- **Literal decimal:** `[0-9_]+ '.' [0-9_]+`, con signo opcional en posición de valor como los enteros (`-0.5`), hasta 9 decimales (más es K1006). No se reconoce después de un `.` (dentro de un id calificado `place.1.5` siguen siendo segmentos).
- **Valor:** entero de 64 bits de unidades más una escala (`RDecimal(Units, Scale)`, valor = `Units / 10^Scale`). Nada de coma flotante en el compilador ni en el pack.
- **Esquema:** `kind = decimal  scale = N` (0 a 9, por defecto 2). Un literal con menos decimales se lleva a la escala (`1.5` con escala 2 son 150 unidades); con más es error K4009, nunca redondeo silencioso. Un entero también vale (`2` → `2.00`). `min` y `max` son enteros y se comparan contra el valor.
- **Sin esquema** (modo permisivo) el decimal conserva la escala escrita: `1.0` y `1.00` son valores distintos para el hash. El modo permisivo es para migrar, no para publicar.
- **Forma canónica y hash:** el texto canónico es el número con exactamente `Scale` decimales (`1.25`); el hash binario escribe unidades y escala.
- **Pack binario:** etiqueta nueva `Decimal` (9): unidades `i64` + escala `u8`. El formato del pack pasa a 2 (`KedoPack.FormatVersion`); el lector acepta 1 y 2, así que los packs anteriores siguen cargando.
- **Comportamientos:** en 1.x los decimales son datos. Un literal decimal dentro de un hook o una función es K5007; el host lee el campo como unidades y escala. La aritmética decimal en la VM, si hace falta, llega en un minor con su propio ADR.
- **JSON Schema generado:** `{"type": "number", "multipleOf": 10^-scale}`.

## Consecuencias

- Patriark escribe `biome_cost = { plains = 1.0  mountain = 2.25 }` y su compilador deja de escalar rutas a mano.
- El compilador de un juego decide cómo imprimir el decimal en sus artefactos (Patriark conserva el texto que esperaba su pack, `1.0` y no `1.00`).
- Un decimal no puede compararse ni sumarse en expresiones de datos `( … )` en 1.x (K5007 también allí): evita reglas de redondeo a medio definir.
