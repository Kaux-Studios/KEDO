@tool
class_name KedoForm
extends VBoxContainer
## Form of one object, generated from `kedo describe` (#14): a row per schema field with a control for its kind,
## the layer the value comes from and a reset button for values the object writes itself. Edits are requested as
## structural edits (`kedo edit ... set|unset`), which the panel applies to the open document as one undoable step,
## so Ctrl+Z works the same for typing and for the form. A type can replace the generic form with its own sheet:
## KedoForm.register_sheet("room", func(desc: Dictionary, form: KedoForm) -> void: ...).

## Emitted with ["set", key, path, literal] or ["unset", key, path].
signal edit_requested(args: PackedStringArray)

static var _sheets: Dictionary = {}   # type -> Callable(desc: Dictionary, form: KedoForm)

## Objects of the catalog ([{type, id, key, ...}]) to offer in reference and asset pickers.
var catalog_objects: Array = []
var description: Dictionary = {}


## Registers a sheet for a type: it receives the description and this form, and may call add_field_row for the
## fields it keeps generic. Registering again replaces the sheet; an empty Callable removes it.
static func register_sheet(type: String, sheet: Callable) -> void:
	if sheet.is_valid():
		_sheets[type] = sheet
	else:
		_sheets.erase(type)


static func has_sheet(type: String) -> bool:
	return _sheets.has(type)


func show_object(desc: Dictionary) -> void:
	description = desc
	for child in get_children():
		remove_child(child)
		child.queue_free()
	if desc.is_empty():
		return
	var header := Label.new()
	header.text = str(desc.get("key", ""))
	if desc.has("parent"):
		header.text += "  extends " + str(desc["parent"])
	var mixins: Array = desc.get("mixins", [])
	if mixins.size() > 0:
		header.text += "  with " + ", ".join(PackedStringArray(mixins))
	header.tooltip_text = str(desc.get("doc", desc.get("type_doc", "")))
	add_child(header)
	var type := str(desc.get("type", ""))
	if _sheets.has(type):
		(_sheets[type] as Callable).call(desc, self)
	else:
		for field in desc.get("fields", []):
			add_field_row(field, str(field.get("name", "")), self)


## One row for a field (recursing into records). `path` is the dotted field path the edit will name.
func add_field_row(field: Dictionary, path: String, parent: Control) -> void:
	var kind := str(field.get("kind", "any"))
	if kind == "record" and field.has("fields"):
		var title := Label.new()
		title.text = path
		title.tooltip_text = str(field.get("doc", ""))
		parent.add_child(title)
		var box := VBoxContainer.new()
		var margin := MarginContainer.new()
		margin.add_theme_constant_override("margin_left", 16)
		margin.add_child(box)
		parent.add_child(margin)
		for nested in field.get("fields", []):
			add_field_row(nested, path + "." + str(nested.get("name", "")), box)
		return
	var row := HBoxContainer.new()
	row.name = "field_" + path.replace(".", "_")
	parent.add_child(row)
	var label := Label.new()
	label.text = str(field.get("name", "")) + (" *" if field.get("required", false) else "")
	label.custom_minimum_size.x = 180
	label.tooltip_text = str(field.get("doc", ""))
	row.add_child(label)
	var editor := _editor_for(field, path)
	editor.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row.add_child(editor)
	var layer := str(field.get("layer", "absent"))
	var source := Label.new()
	source.text = layer if not field.has("from") or layer == "own" else "%s %s" % [layer, field["from"]]
	source.tooltip_text = str(field.get("origin", ""))
	source.modulate = Color(1, 1, 1, 1.0 if layer == "own" else 0.55)
	row.add_child(source)
	if layer == "own":
		var reset := Button.new()
		reset.text = "Reset"
		reset.tooltip_text = "Remove the value written here and inherit it again"
		reset.pressed.connect(func() -> void: edit_requested.emit(PackedStringArray(["unset", str(description.get("key", "")), path])))
		row.add_child(reset)


func _editor_for(field: Dictionary, path: String) -> Control:
	var kind := str(field.get("kind", "any"))
	var value: Variant = field.get("value")
	var text := "" if value == null else str(value)
	match kind:
		"bool":
			var check := CheckBox.new()
			check.button_pressed = text == "true"
			check.toggled.connect(func(on: bool) -> void: _set_value(path, "true" if on else "false"))
			return check
		"enum":
			if field.has("enum"):
				return _choice(path, text, field.get("enum", []), "")
		"reference", "asset":
			var target := "asset" if kind == "asset" else str(field.get("target", ""))
			var ids: Array = []
			for obj in catalog_objects:
				if (target == "" or str(obj.get("type", "")) == target) and not obj.get("abstract", false):
					ids.append(str(obj.get("key", "")))
			return _choice(path, text, ids, "")
		"text":
			if field.has("one_of"):
				var quoted: Array = []
				for v in field.get("one_of", []):
					quoted.append(KedoForm.quote(str(v)))
				return _choice(path, text, quoted, "")
			var line := _line(path, text.substr(1, text.length() - 2).c_unescape() if text.begins_with("\"") else text)
			line.set_meta("quote", true)
			return line
	# int, decimal, duration, lists, maps and anything else: the KEDO literal itself.
	return _line(path, text)


func _choice(path: String, current: String, values: Array, _none: String) -> OptionButton:
	var option := OptionButton.new()
	if current == "" or not values.has(current):
		option.add_item(current if current != "" else "(none)")
		option.set_item_disabled(0, true)
	for v in values:
		option.add_item(str(v))
	for i in range(option.item_count):
		if option.get_item_text(i) == current:
			option.select(i)
	option.item_selected.connect(func(index: int) -> void: _set_value(path, option.get_item_text(index)))
	return option


func _line(path: String, text: String) -> LineEdit:
	var line := LineEdit.new()
	line.text = text
	line.set_meta("committed", text)
	var commit := func() -> void:
		if line.text == str(line.get_meta("committed")):
			return
		line.set_meta("committed", line.text)
		_set_value(path, KedoForm.quote(line.text) if line.get_meta("quote", false) else line.text)
	line.text_submitted.connect(func(_t: String) -> void: commit.call())
	line.focus_exited.connect(commit)
	return line


func _set_value(path: String, literal: String) -> void:
	var args := PackedStringArray(["set", str(description.get("key", "")), path, literal])
	# A list or map typed in the form is the whole value, not something to merge with what is inherited.
	if literal.begins_with("[") or literal.begins_with("{"):
		args.append("--op==!")
	edit_requested.emit(args)


## A KEDO text literal.
static func quote(text: String) -> String:
	return "\"" + text.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", "\\n") + "\""
