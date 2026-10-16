# GDO-Plan-05 — Undo/rehacer y cambios pendientes

Preparado: 15/10/2026 · Issue [#3](https://github.com/Kaux-Studios/Project-Patriark/issues/3) · Estado: **plan para revisión previa**. Base: [GDO-01](../research/01-Investigacion-origen-Patriark.md) decisión D7; anexo [B](../research/anexos/B-Plugins-de-editor-y-undo-redo.md) §1.4, §3.

## Objetivo

Que toda operación de edición en la pantalla Contenido sea reversible, que el estado "sin guardar" sea visible y coherente con el editor de Godot (diálogos de cierre de pestaña y de salida), que un reinicio inesperado del editor no pierda trabajo, y que nada se escriba en disco sin una acción explícita de guardar.

## Dependencias

- Plan 03 (plugin) y Spike S1. Se diseña junto con el 03 y se integra como módulo `editing/`.

## Alcance

**Incluye**

1. **Documento** (`gdo_document.gd`): un objeto por GDO abierto con `data` (Dictionary), `saved_version`, `undo: UndoRedo`, `dirty` derivado (`undo.get_version() != saved_version`), señales `changed(pointer)` y `dirty_changed`.
2. **Acciones**: `set_value(pointer, value)` con `MERGE_ENDS` y nombre de acción estable por puntero ("Editar actors/sentinel: /components/presence/range") para fusionar teclas dentro de la ventana de 800 ms del motor; `add_item`, `remove_item`, `move_item` para listas; `set_ref`, `set_asset`; `create`, `duplicate`, `delete` (a nivel de biblioteca, con su propio historial global del plugin).
3. **Atajos**: Ctrl+Z / Ctrl+Shift+Z sobre el documento con foco; menú contextual "Deshacer <acción>".
4. **Pendientes**: indicador en la lista y en la pestaña; `_get_unsaved_status(for_scene)` devuelve el resumen ("3 objetos de contenido sin guardar") solo cuando `for_scene` está vacío (salida del editor) o cuando se cierra la escena del plugin, con la guarda `is_inside_tree()` y un flag propio para evitar la doble llamada documentada en "Save & Quit".
5. **`_save_external_data`**: guarda todos los documentos sucios; devuelve diagnósticos de forma si alguno no es válido (no bloquea el guardado: se guarda y se marca).
6. **Borrador automático**: cada N segundos o en cada acción commiteada, el documento sucio se escribe a `user://contenido/drafts/<id>.json` con `saved_version`; al abrir el plugin, si existe un borrador más nuevo que el archivo, se ofrece restaurar o descartar.
7. **Guardar**: escritura atómica (archivo temporal + rename), `saved_version = undo.get_version()`, borrado del borrador, `EditorFileSystem.update_file` para que el dock vea el cambio.
8. **Validación de forma** antes de guardar (Plan 03); guardado permitido con advertencias, bloqueado solo si el JSON no es serializable.

**Excluye**

- Historial compartido con `EditorUndoRedoManager` (no se editan Resources ni nodos; ver diseño). Undo de operaciones de asset en el FileSystem (fuera del plugin).

## Diseño

### Por qué `UndoRedo` del núcleo y no el `EditorUndoRedoManager`

- El manager destina objetos externos al historial global y marca `Resource.set_edited`; también lo hace el `UndoRedo` del núcleo cuando el objetivo es un Resource. Como los documentos son Dictionaries GDScript, ninguna de las dos cosas pasa: Ctrl+S de una escena no escribe nuestros archivos y no hay interacción con el guardado de sub-recursos (propuesta #7168).
- `UndoRedo` acepta Callables (lambdas GDScript), lo que simplifica las acciones. Ctrl+Z global del editor no alcanza al plugin; se maneja el atajo en el plugin (patrón de Dialogic y Dialogue Manager).

### Semántica de fusión

| Operación | Merge | Nombre de acción |
|---|---|---|
| Teclear en un campo | `MERGE_ENDS` | fijo por puntero |
| Slider/numérico | `MERGE_ENDS` | fijo por puntero |
| Toggle, enum, lista, ref, asset | `MERGE_DISABLE` | descriptivo |
| Crear/duplicar/borrar objeto | `MERGE_DISABLE` | descriptivo, historial de biblioteca |

### Reglas de estado

- Un documento sucio no se cierra sin confirmar (guardar / descartar / cancelar).
- Guardar no compila; Validar/Play sí (Plan 03). Guardar deja el archivo listo para la CLI.
- Deshacer tras guardar vuelve a marcar sucio (versión distinta).

## Pasos

1. S1 confirma hooks de cierre.
2. `gdo_document.gd` con tests GDScript headless: set/undo/redo/merge, dirty, saved_version.
3. Integración con el formulario del Plan 03 (todo cambio pasa por `set_value`).
4. Pendientes: indicadores, `_get_unsaved_status`, `_save_external_data`, confirmación al cerrar documento.
5. Borrador automático y restauración.
6. Guardado atómico + `update_file`.
7. Prueba manual del diálogo de salida del editor con cambios pendientes; captura para TECH-02.

## Verificación

- Criterio del issue: deshacer/rehacer y tratamiento explícito de cambios pendientes.
- Test headless: 50 ediciones, 50 undos, estado igual al inicial; redo completo igual al final; `dirty` correcto en cada paso.
- Cerrar Godot con cambios → aparece el diálogo con el mensaje del plugin; "Save & Quit" guarda una sola vez (log).
- Matar el proceso del editor con cambios → al reabrir se ofrece el borrador y restaura.

## Criterios de cierre

- [ ] Documento con undo/redo, fusión y dirty.
- [ ] Integración con diálogos de cierre del editor.
- [ ] Borrador automático.
- [ ] Guardado atómico.
- [ ] Tests headless y prueba manual registrada.

## Riesgos

- Ventana de 800 ms del motor fija: para campos de texto largos, la fusión puede cortar en pausas; aceptable, documentado.
- `_save_external_data` llamado aunque el usuario elija "Don't Save" (advertencia del anexo B §1.4): el flag propio evita escribir en ese caso.
- Borradores huérfanos en `user://`: limpieza al guardar y purga de borradores con más de 30 días.

## Preguntas para la revisión previa

1. ¿Guardado explícito (propuesto) o autoguardado al editar como hace el Inspector? Explícito evita escribir contenido inválido en la fuente.
2. ¿Historial único por documento (propuesto) o historial global del plugin que mezcle objetos?
3. ¿Intervalo de borrador: por acción o cada 10 s?
