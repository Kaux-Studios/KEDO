# Addon de Godot para KEDO

Plugin de pantalla principal en GDScript. No carga el ensamblado del juego: toda validación y toda edición estructural pasan por la CLI `kedo` (misma autoridad que CI y el servidor MCP).

## Qué hace

- **Biblioteca:** objetos del contenido agrupados por tipo, con filtro, marca de abstractos y de errores.
- **Formulario** (pestaña *Form*): al elegir un objeto, `kedo describe` devuelve los campos de su tipo en el orden del esquema, con el valor resuelto y la capa de la que viene (propio, padre, mixin, default, variante o ausente). Cada campo tiene un control según su clase: casilla para `bool`, lista para `enum`, `one_of`, referencias y assets (con los objetos del catálogo del tipo pedido), texto para `text` y el literal KEDO para números, listas y mapas; los records se anidan. *Reset* quita el valor que escribe el objeto para volver a heredarlo.
- **Edición:** un cambio del formulario se aplica con `kedo edit` (`set`, `unset`, `new`, `duplicate`) sobre una copia del documento abierto, que el formateador reimprime; el resultado entra en el editor de texto como un solo paso, así que Ctrl+Z y Ctrl+Shift+Z deshacen y rehacen igual lo tipeado y lo editado en el formulario. Un valor que no es un literal válido se rechaza sin tocar el texto. Una lista o un mapa escrito en el formulario reemplaza el valor entero (`=!`).
- **Listas por clave:** cada elemento de una lista de records con `strategy = keyed` tiene su grupo plegable (`item_<lista>[<clave>]`) con una fila por campo; una edición escribe un parche `lista[clave] { campo = … }` (`kedo edit set … 'lista[clave].campo'`), así los elementos heredados siguen heredados y el resto de la lista no se reescribe. Un *Reset* quita el parche cuando era su última entrada. Las listas atómicas siguen como literal.
- **Fuente** (pestaña *Source*): `CodeEdit` con resaltado; los cambios se reflejan en el formulario medio segundo después (`kedo describe --text=<archivo>=<copia>` lee el texto sin guardar).
- **Diagnósticos** con salto a archivo y línea; *Check*, *Format*, *Save*, *Save all*, *New object* y *Duplicate* en la barra.
- **Pendientes:** `_get_unsaved_status` para los diálogos de cierre, `_save_external_data` al guardar el proyecto, borradores en `user://kedo_drafts` que sobreviven a un cierre inesperado, guardado atómico.
- **Play:** `_build()` bloquea la ejecución si el contenido tiene errores.

## Fichas por tipo

Un juego puede reemplazar el formulario genérico de un tipo:

```gdscript
KedoForm.register_sheet("room", func(desc: Dictionary, form: KedoForm) -> void:
    for field in desc.fields:
        if field.name in ["title", "kind"]:
            form.add_field_row(field, field.name, form))
```

La ficha recibe la descripción del objeto y el formulario; puede construir su propia UI y usar `add_field_row` para los campos que deja genéricos. Las ediciones salen por la señal `edit_requested`, así que deshacer y validar funcionan igual.

## Configuración

`kedo/command` (por defecto `kedo`; también `dotnet kedo`, `dotnet <ruta>/kedo.dll` o `dotnet run --project ... -c Release --no-build --`), `kedo/content_paths` (por defecto `res://content`) y `kedo/schema_paths`. El proyecto los fija en Project Settings (`project.godot`, sección `[kedo]`); un Editor Setting no vacío con el mismo nombre los reemplaza en una máquina. El directorio de trabajo de los comandos es el del proyecto, así que una ruta relativa como `../KEDO/...` funciona.

## Archivos

`kedo_plugin.gd` (EditorPlugin), `kedo_panel.gd` (pantalla: biblioteca, formulario, fuente, diagnósticos), `kedo_form.gd` (formulario y registro de fichas), `kedo_cli.gd` (subproceso y JSON), `kedo_document.gd` (texto, sucio, guardado atómico, borradores), `kedo_highlighter.gd`. Pruebas headless: `tools/godot-test.ps1`.

Pendiente: vista de tabla para editar muchos objetos a la vez; selector con vista previa de assets (`EditorResourcePicker`) cuando el juego publique su manifiesto.
