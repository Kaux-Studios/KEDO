# Addon de Godot para KEDO

Plugin de pantalla principal en GDScript. No carga el ensamblado del juego: toda validación pasa por la CLI `kedo` (misma autoridad que CI y el servidor MCP).

Archivos: `kedo_plugin.gd` (EditorPlugin: pantalla, menú Tools, `_get_unsaved_status`, `_save_external_data`, `_build`), `kedo_panel.gd` (UI construida en código: lista con filtro, `CodeEdit`, diagnósticos), `kedo_cli.gd` (subproceso y JSON), `kedo_document.gd` (texto, sucio, guardado atómico, borrador en `user://kedo_drafts`), `kedo_highlighter.gd` (palabras clave y regiones).

Configuración en Editor Settings: `kedo/command` (por defecto `kedo`; también `dotnet kedo` o `dotnet run --project ... -c Release --no-build --`), `kedo/content_paths` (por defecto `res://content`), `kedo/schema_paths`.

Pendiente: formularios generados desde `kedo schema`, registro de fichas por tipo, vista de tabla.
