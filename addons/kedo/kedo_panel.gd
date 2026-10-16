@tool
class_name KedoPanel
extends Control
## Main screen: object library (left), editor with highlighting (right), diagnostics (bottom).
## Everything is built in code so the addon has no scene files to keep in sync.

signal unsaved_changed()

var cli: KedoCli
var documents: Dictionary = {}   # os path -> KedoDocument
var current: KedoDocument = null
var catalog_objects: Array = []

var _filter: LineEdit
var _tree: Tree
var _editor: CodeEdit
var _diagnostics: Tree
var _status: Label
var _suppress_text_signal := false


func _ready() -> void:
	cli = KedoCli.from_editor_settings()
	_build_ui()
	refresh_catalog()


func _build_ui() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	var root := VBoxContainer.new()
	root.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(root)

	var toolbar := HBoxContainer.new()
	root.add_child(toolbar)
	_add_button(toolbar, "Refresh", refresh_catalog)
	_add_button(toolbar, "Check", run_check)
	_add_button(toolbar, "Format", format_current)
	_add_button(toolbar, "Save", save_current)
	_add_button(toolbar, "Save all", save_all)
	_status = Label.new()
	_status.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	toolbar.add_child(_status)

	var split := HSplitContainer.new()
	split.size_flags_vertical = Control.SIZE_EXPAND_FILL
	root.add_child(split)

	var left := VBoxContainer.new()
	left.custom_minimum_size.x = 280
	split.add_child(left)
	_filter = LineEdit.new()
	_filter.placeholder_text = "Filter objects..."
	_filter.clear_button_enabled = true
	_filter.text_changed.connect(func(_t: String) -> void: _rebuild_tree())
	left.add_child(_filter)
	_tree = Tree.new()
	_tree.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_tree.hide_root = true
	_tree.item_activated.connect(_on_tree_activated)
	_tree.item_selected.connect(_on_tree_activated)
	left.add_child(_tree)

	var right := VSplitContainer.new()
	right.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	split.add_child(right)
	_editor = CodeEdit.new()
	_editor.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_editor.syntax_highlighter = KedoHighlighter.new()
	_editor.gutters_draw_line_numbers = true
	_editor.indent_size = 4
	_editor.indent_use_spaces = true
	_editor.auto_brace_completion_enabled = true
	_editor.text_changed.connect(_on_editor_changed)
	right.add_child(_editor)
	_diagnostics = Tree.new()
	_diagnostics.custom_minimum_size.y = 120
	_diagnostics.columns = 4
	_diagnostics.set_column_titles_visible(true)
	_diagnostics.set_column_title(0, "Severity")
	_diagnostics.set_column_title(1, "Code")
	_diagnostics.set_column_title(2, "Location")
	_diagnostics.set_column_title(3, "Message")
	_diagnostics.set_column_expand(3, true)
	_diagnostics.hide_root = true
	_diagnostics.item_activated.connect(_on_diagnostic_activated)
	right.add_child(_diagnostics)


func _add_button(parent: Node, label: String, callback: Callable) -> void:
	var b := Button.new()
	b.text = label
	b.pressed.connect(callback)
	parent.add_child(b)


# ----- catalog -----

func refresh_catalog() -> void:
	var data := cli.catalog()
	if data.is_empty():
		_status.text = cli.last_error if cli.last_error != "" else "no catalog"
		catalog_objects = []
	else:
		catalog_objects = data.get("objects", [])
		_status.text = "%d object(s), catalog %s" % [catalog_objects.size(), str(data.get("catalog_hash", "")).left(8)]
		_show_diagnostics(data.get("diagnostics", []))
	_rebuild_tree()


func _rebuild_tree() -> void:
	_tree.clear()
	var root := _tree.create_item()
	var by_type: Dictionary = {}
	var needle := _filter.text.to_lower()
	for obj in catalog_objects:
		var key := str(obj.get("key", ""))
		if needle != "" and key.to_lower().find(needle) < 0:
			continue
		var type := str(obj.get("type", ""))
		if not by_type.has(type):
			var t := _tree.create_item(root)
			t.set_text(0, type)
			t.set_selectable(0, false)
			by_type[type] = t
		var item := _tree.create_item(by_type[type])
		var label := str(obj.get("id", ""))
		if obj.get("abstract", false):
			label = "(abstract) " + label
		if obj.get("errors", false):
			label += "  !"
		item.set_text(0, label)
		item.set_tooltip_text(0, str(obj.get("doc", "")) if obj.get("doc") != null else key)
		item.set_metadata(0, obj)


func _on_tree_activated() -> void:
	var item := _tree.get_selected()
	if item == null or item.get_metadata(0) == null:
		return
	var obj: Dictionary = item.get_metadata(0)
	open_file(str(obj.get("file", "")), int(obj.get("line", 1)))


# ----- editing -----

func open_file(os_path: String, line: int = 1) -> void:
	if os_path == "":
		return
	if current != null and current.path == os_path:
		_goto(line)
		return
	if not documents.has(os_path):
		var doc := KedoDocument.open(os_path)
		doc.dirty_changed.connect(func(_d: bool) -> void: unsaved_changed.emit())
		documents[os_path] = doc
	current = documents[os_path]
	_suppress_text_signal = true
	_editor.text = current.text
	_suppress_text_signal = false
	_goto(line)
	_update_status()


func _goto(line: int) -> void:
	_editor.set_caret_line(max(0, line - 1))
	_editor.center_viewport_to_caret()
	_editor.grab_focus()


func _on_editor_changed() -> void:
	if _suppress_text_signal or current == null:
		return
	current.set_text(_editor.text)
	_update_status()


func _update_status() -> void:
	if current == null:
		return
	_status.text = "%s%s" % [current.path.get_file(), " *" if current.is_dirty() else ""]


func save_current() -> void:
	if current != null and current.save():
		_update_status()
		run_check()


func save_all() -> void:
	for doc in documents.values():
		if doc.is_dirty():
			doc.save()
	_update_status()
	run_check()


func format_current() -> void:
	if current == null:
		return
	if current.is_dirty():
		current.save()
	if cli.format_file(current.path):
		var reloaded := KedoDocument.open(current.path)
		current.text = reloaded.text
		current.saved_text = reloaded.saved_text
		_suppress_text_signal = true
		_editor.text = current.text
		_suppress_text_signal = false
		_update_status()


func run_check() -> void:
	var data := cli.check()
	if data.is_empty():
		_status.text = cli.last_error
		return
	_show_diagnostics(data.get("diagnostics", []))
	var errors := 0
	for d in data.get("diagnostics", []):
		if str(d.get("severity", "")) == "error":
			errors += 1
	_status.text = "check: %d error(s), %d diagnostic(s)" % [errors, data.get("diagnostics", []).size()]
	refresh_catalog_quiet()


func refresh_catalog_quiet() -> void:
	var data := cli.catalog()
	if not data.is_empty():
		catalog_objects = data.get("objects", [])
		_rebuild_tree()


func _show_diagnostics(list: Array) -> void:
	_diagnostics.clear()
	var root := _diagnostics.create_item()
	for d in list:
		var item := _diagnostics.create_item(root)
		item.set_text(0, str(d.get("severity", "")))
		item.set_text(1, str(d.get("code", "")))
		item.set_text(2, "%s:%s:%s" % [str(d.get("file", "")).get_file(), str(d.get("line", "")), str(d.get("column", ""))])
		item.set_text(3, str(d.get("message", "")))
		item.set_metadata(0, d)


func _on_diagnostic_activated() -> void:
	var item := _diagnostics.get_selected()
	if item == null or item.get_metadata(0) == null:
		return
	var d: Dictionary = item.get_metadata(0)
	open_file(str(d.get("file", "")), int(d.get("line", 1)))


## Summary for the editor's close/quit dialogs, or "" when nothing is pending.
func unsaved_status() -> String:
	var dirty := 0
	for doc in documents.values():
		if doc.is_dirty():
			dirty += 1
	return "" if dirty == 0 else "%d KEDO file(s) with unsaved changes" % dirty


## Compile check used by the plugin's _build(): false blocks Play.
func has_errors() -> bool:
	var data := cli.check()
	return not data.is_empty() and not bool(data.get("ok", true))
