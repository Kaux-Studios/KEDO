# GDO-Plan-03 — Plugin de editor y biblioteca de contenido

Preparado: 15/10/2026 · Issue [#3](https://github.com/Kaux-Studios/Project-Patriark/issues/3) · Estado: **plan para revisión previa**. Base: [GDO-01](../research/01-Investigacion-origen-Patriark.md) decisión D6; anexo [B](../research/anexos/B-Plugins-de-editor-y-undo-redo.md) §1, §2, §4, §5, §6, §8.

## Objetivo

Pantalla **Contenido** dentro de Godot, en GDScript, que lista, busca y filtra GDO por tipo, módulo y tag; crea, edita, duplica y guarda objetos con formularios generados desde los esquemas del Plan 02; muestra diagnósticos de la CLI con salto al objeto; bloquea Play con errores; y expone puntos de extensión para fichas por familia. Quien no programa no toca escenas, scripts ni archivos.

## Dependencias

- Plan 02 (fuente JSON, `build-content --json`, esquemas por tipo).
- Spikes S1 (plugin + JSON + undo + diálogo de cierre), S2 (latencia del subproceso), S4 (formulario desde esquema).
- Plan 05 define undo y pendientes; aquí se integra su API.

## Alcance

**Incluye**

1. `addons/contenido/` en GDScript: `plugin.cfg`, `contenido_plugin.gd` (`_has_main_screen`, `_get_plugin_name`, `_make_visible`, `_build`, `_get_unsaved_status`, `_save_external_data`, menú `Project > Tools > Validar contenido`).
2. Capa de datos: `gdo_store.gd` (escaneo de `content/gdo/**`, índice por tipo/módulo/tag, lectura/escritura JSON con orden de claves estable y `$editor`), `gdo_schema.gd` (carga de `content/gdo/schema/*.schema.json`, defaults, validación de forma).
3. UI: `HSplitContainer` → izquierda `LineEdit` filtro + `OptionButton` tipo/módulo + `Tree` (icono de estado, sucio, errores); derecha `ScrollContainer` con formulario generado + barra de acciones (Nuevo, Duplicar, Guardar, Validar, Compilar) + panel inferior de diagnósticos (`Tree` con severidad, código, mensaje; doble clic enfoca el campo).
4. Formulario genérico desde esquema: string, int, bool, enum, lista de strings, objeto anidado (sección plegable), referencia a otro GDO (selector con búsqueda por tipo), referencia a asset (control del Plan 04), tags (editor de lista). Etiquetas de `title`, ayuda de `description`, límites de `minimum`/`maximum`/`enum`.
5. Validación en vivo (forma) en GDScript; validación semántica y compilación mediante `OS.execute` sobre `tools` (`build-content --json`), parseo de diagnósticos y mapeo a objetos/campos por `GdoLocation.Pointer`.
6. `_build()`: compila; si hay errores, muestra el panel y devuelve `false` (Play bloqueado).
7. Punto de extensión: `extensions/registry.gd` con diccionario `tipo → PackedScene` para fichas específicas; la ficha recibe el documento y el esquema y emite `changed(pointer, value)`. Ejemplo mínimo: una ficha para `ambient` que solo reordena campos, para demostrar el mecanismo.
8. Preferencias del plugin persistidas con `_get_window_layout`/`_set_window_layout` (split, filtro, último objeto).
9. Test de contrato: script GDScript ejecutado headless (`--headless -s`, es GDScript, sin restricción C#) que carga esquemas, crea los cuatro ejemplos desde defaults, guarda, relee y compara.

**Excluye**

- Undo/pendientes (Plan 05, integrados aquí vía API). Assets (Plan 04). Fichas reales por familia (#35). Grafos (#36). Tablas masivas (evaluar "Edit Resources as Table 2" después; no aplica a JSON).

## Diseño

### Por qué no carga el ensamblado del juego

El plugin trabaja con JSON + esquema + CLI. No instancia tipos C#, no sufre la recarga de ensamblados (#78513), no necesita `#if TOOLS`, y la autoridad de validación es la misma que corre en CI. Si S2 mide latencia inaceptable (> 1,5 s por validación con binario precompilado), alternativa: un puente C# mínimo `[Tool]` en `addons/contenido/bridge/` que exponga `Validate(json) → Array` con la disciplina del anexo B §4.3; decisión en la revisión previa del plan con la medición en mano.

### Ejecutable de `tools`

`dotnet build tools/Patriark.Tools.csproj` deja `tools/bin/Debug/net8.0/Patriark.Tools.exe`; el plugin lo localiza por ruta relativa al proyecto y, si falta, ofrece compilarlo (`OS.execute("dotnet", ["build", ...])`) con salida en el panel. Ruta configurable en `EditorSettings` del plugin.

### Flujo del colaborador

1. Abrir pestaña Contenido → lista filtrada.
2. Nuevo → elegir tipo → formulario con defaults del esquema → completar.
3. Guardar → JSON escrito → validación de forma inmediata; Validar → CLI → diagnósticos.
4. Corregir con doble clic en el diagnóstico.
5. Play → `_build` compila; si falla, no arranca.

### Convenciones del JSON escrito

Claves en el orden del esquema, 2 espacios, `\n`, UTF-8 sin BOM, `$editor` al final. Idéntico a lo que emite la CLI al formatear, para que editar en Godot o en un editor de texto no produzca diffs de formato (test de ida y vuelta).

## Pasos

1. S1, S2, S4 (descartables, en `tmp/spikes/`).
2. Esqueleto del plugin con pantalla vacía, menú Tools, `_build` que ejecuta la CLI y muestra salida cruda.
3. `gdo_store` + lista con filtro; abrir un objeto muestra JSON crudo en `TextEdit` (hito intermedio verificable).
4. `gdo_schema` + formulario genérico para los tipos escalares; luego anidados, listas, referencias.
5. Diagnósticos estructurados con salto a campo.
6. Nuevo/Duplicar/Guardar con la API del Plan 05.
7. Registro de extensiones y ficha de ejemplo.
8. Test headless de contrato; captura de pantalla PC de la pantalla con los cuatro ejemplos para TECH-02.

## Verificación

- Criterio del issue: crear y modificar campaña, actor, lugar y ambiente desde la UI sin atributos de gameplay.
- Referencias inválidas e ids duplicados aparecen como diagnósticos con objeto y problema (vienen del Plan 01 vía CLI).
- Añadir un tipo (clase C# + registro + `gdo-schema`) lo hace aparecer en Nuevo sin tocar el plugin.
- Play bloqueado con un error deliberado; desbloqueado al corregir.
- Exportación Windows y Android: el PCK no contiene `addons/contenido` (filtro de exclusión) y el juego arranca.

## Criterios de cierre

- [ ] Pantalla Contenido operativa con lista, filtro, formulario genérico, diagnósticos y `_build`.
- [ ] Punto de extensión documentado con ejemplo.
- [ ] Test headless de ida y vuelta.
- [ ] Preset de exportación excluye el addon; verificado en Windows y Android.
- [ ] Captura y nota de uso para colaboradores (borrador; se consolida en Plan 07).

## Riesgos

- `EditorDock`/`add_dock` son experimentales en 4.6 y `add_control_to_dock` está deprecado: usar pantalla principal, no docks, y aislar cualquier creación de dock en una función.
- Latencia del subproceso en máquinas lentas: validación de forma en vivo mitiga; la semántica solo al Validar/Guardar/Play.
- Formulario genérico insuficiente para familias complejas: es el objetivo del punto de extensión; no intentar cubrir todo.
- GDScript tipado dinámico frente al esquema: el test de contrato y los esquemas commiteados son la red.

## Preguntas para la revisión previa

1. ¿Confirmar GDScript para el plugin (D6) tras S1/S2? Alternativa con costo conocido: C# con reinicios.
2. ¿Pantalla principal (propuesto) o dock lateral?
3. ¿La compilación al Play (`_build`) es aceptable para el flujo diario o debe ser opcional en EditorSettings?
4. ¿Se quiere ya una vista de tabla para balance en esta entrega? Propuesta: no; evaluar en #35.
