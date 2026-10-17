@tool
class_name KedoPanel
extends Control
## Main screen: object library (left), form and source editor of the selected object (right), diagnostics (bottom).
## Everything is built in code so the addon has no scene files to keep in sync. The form is generated from the schema
## through `kedo describe`; its edits go through `kedo edit` and land in the source editor as one undoable step (#14).

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
var _form: KedoForm
var _tabs: TabContainer
var _refresh_timer: Timer
var current_key := ""


func _ready() -> void:
	if cli == null:
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
	_add_button(toolbar, "New object", _show_new_dialog)
	_add_button(toolbar, "Duplicate", _show_duplicate_dialog)
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
	_tabs = TabContainer.new()
	_tabs.size_flags_vertical = Control.SIZE_EXPAND_FILL
	right.add_child(_tabs)
	var scroll := ScrollContainer.new()
	scroll.name = "Form"
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_tabs.add_child(scroll)
	_form = KedoForm.new()
	_form.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_form.edit_requested.connect(func(args: PackedStringArray) -> void: apply_edit(args))
	scroll.add_child(_form)
	_refresh_timer = Timer.new()
	_refresh_timer.one_shot = true
	_refresh_timer.wait_time = 0.5
	_refresh_timer.timeout.connect(refresh_form)
	add_child(_refresh_timer)
	_editor = CodeEdit.new()
	_editor.name = "Source"
	_editor.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_editor.syntax_highlighter = KedoHighlighter.new()
	_editor.gutters_draw_line_numbers = true
	_editor.indent_size = 4
	_editor.indent_use_spaces = true
	_editor.auto_brace_completion_enabled = true
	_editor.text_changed.connect(_on_editor_changed)
	_tabs.add_child(_editor)
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
	var data: Dictionary = {}
	var raw: Variant = cli.run_json("catalog", _override_args())
	if raw is Dictionary:
		data = raw
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
	select_object(str(obj.get("key", "")))


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
	if current_key != "" and _refresh_timer != null and _refresh_timer.is_inside_tree():
		_refresh_timer.start()


# ----- form (#14) -----

## Copies the source editor's text into the document now. TextEdit reports changes deferred, and an edit or an undo
## must be visible to the next describe in the same frame.
func sync_from_editor() -> void:
	if current != null and current.text != _editor.text:
		current.set_text(_editor.text)
		_update_status()

## Shows the form of an object of the current document.
func select_object(key: String) -> void:
	current_key = key
	refresh_form()


func refresh_form() -> void:
	if current_key == "":
		_form.show_object({})
		return
	_form.catalog_objects = catalog_objects
	_form.show_object(describe(current_key))


## `kedo describe` of an object, reading the unsaved text of the current document.
func describe(key: String) -> Dictionary:
	var extra := _override_args()
	extra.append(key)
	var data: Variant = cli.run_json("describe", extra)
	return data if data is Dictionary and data.has("fields") else {}


## Runs `kedo edit` on a copy of the current document and puts the result in the source editor as one undoable step.
## args: ["set", key, path, literal], ["unset", key, path], ["new", type, id, "--extends=p"] or ["duplicate", key, new_id].
func apply_edit(args: PackedStringArray) -> bool:
	if current == null:
		_status.text = "open a file first"
		return false
	var buffer := _buffer_path()
	_write_file(buffer, current.text)
	var full := PackedStringArray([buffer])
	full.append_array(args)
	if args.size() >= 4 and args[0] == "set":
		# The literal travels in a file: command lines drop or mangle its quotes on Windows.
		var value_file := ProjectSettings.globalize_path("user://kedo_edit_value.txt")
		_write_file(value_file, args[3])
		full[4] = "--value-file=" + value_file
	var data: Variant = cli.run_json("edit", full, false)
	if not (data is Dictionary) or not bool(data.get("ok", false)):
		_status.text = "edit refused: " + (str(data.get("error", "")) if data is Dictionary else cli.last_error)
		return false
	var new_text := _read_file(buffer)
	if new_text != current.text:
		var caret := _editor.get_caret_line()
		_editor.begin_complex_operation()
		_editor.select_all()
		_editor.insert_text_at_caret(new_text)
		_editor.end_complex_operation()
		_editor.set_caret_line(min(caret, _editor.get_line_count() - 1))
		sync_from_editor()
	if args.size() > 0 and args[0] in ["new", "duplicate"]:
		var type := args[1] if args[0] == "new" else args[1].get_slice(":", 0)
		var id := args[2]
		refresh_catalog()
		select_object(type + ":" + id)
	else:
		refresh_form()
	return true


func _override_args() -> PackedStringArray:
	var extra := PackedStringArray()
	if current != null and current.is_dirty():
		var buffer := _buffer_path()
		_write_file(buffer, current.text)
		extra.append("--text=%s=%s" % [current.path, buffer])
	return extra


func _buffer_path() -> String:
	return ProjectSettings.globalize_path("user://kedo_edit_buffer.kedo")


func _write_file(path: String, content: String) -> void:
	var f := FileAccess.open(path, FileAccess.WRITE)
	if f != null:
		f.store_string(content)
		f.close()


func _read_file(path: String) -> String:
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null:
		return ""
	var text := f.get_as_text()
	f.close()
	return text


func _show_new_dialog() -> void:
	var types: Array = []
	for obj in catalog_objects:
		var t := str(obj.get("type", ""))
		if not types.has(t):
			types.append(t)
	var dialog := ConfirmationDialog.new()
	dialog.title = "New object"
	var box := VBoxContainer.new()
	var type_pick := OptionButton.new()
	for t in types:
		type_pick.add_item(t)
	box.add_child(type_pick)
	var id_line := LineEdit.new()
	id_line.placeholder_text = "id"
	box.add_child(id_line)
	var parent_line := LineEdit.new()
	parent_line.placeholder_text = "extends (optional)"
	box.add_child(parent_line)
	dialog.add_child(box)
	dialog.confirmed.connect(func() -> void:
		var args := PackedStringArray(["new", type_pick.get_item_text(type_pick.selected), id_line.text.strip_edges()])
		if parent_line.text.strip_edges() != "":
			args.append("--extends=" + parent_line.text.strip_edges())
		apply_edit(args)
		dialog.queue_free())
	dialog.canceled.connect(dialog.queue_free)
	add_child(dialog)
	dialog.popup_centered(Vector2i(320, 160))


func _show_duplicate_dialog() -> void:
	if current_key == "":
		_status.text = "select an object first"
		return
	var dialog := ConfirmationDialog.new()
	dialog.title = "Duplicate " + current_key
	var id_line := LineEdit.new()
	id_line.placeholder_text = "new id"
	dialog.add_child(id_line)
	dialog.confirmed.connect(func() -> void:
		apply_edit(PackedStringArray(["duplicate", current_key, id_line.text.strip_edges()]))
		dialog.queue_free())
	dialog.canceled.connect(dialog.queue_free)
	add_child(dialog)
	dialog.popup_centered(Vector2i(320, 100))


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
