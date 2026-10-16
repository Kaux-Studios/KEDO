@tool
extends EditorPlugin
## KEDO Content: main-screen plugin (GDScript shell, ADR-0007 D9 / Plan-03). Talks to the kedo CLI; never loads the game assembly.

var _panel: KedoPanel


func _enter_tree() -> void:
	_panel = KedoPanel.new()
	_panel.name = "KedoContent"
	EditorInterface.get_editor_main_screen().add_child(_panel)
	_make_visible(false)
	add_tool_menu_item("KEDO: Check content", func() -> void: _panel.run_check())


func _exit_tree() -> void:
	remove_tool_menu_item("KEDO: Check content")
	if _panel != null:
		_panel.queue_free()
		_panel = null


func _has_main_screen() -> bool:
	return true


func _get_plugin_name() -> String:
	return "KEDO"


func _get_plugin_icon() -> Texture2D:
	return EditorInterface.get_editor_theme().get_icon("FileList", "EditorIcons")


func _make_visible(visible: bool) -> void:
	if _panel != null:
		_panel.visible = visible


func _get_unsaved_status(_for_scene: String) -> String:
	return _panel.unsaved_status() if _panel != null and is_inside_tree() else ""


func _save_external_data() -> void:
	if _panel != null and is_inside_tree():
		_panel.save_all()


func _build() -> bool:
	if _panel == null:
		return true
	if _panel.has_errors():
		push_error("KEDO content has errors; see the KEDO screen. Play blocked.")
		return false
	return true
