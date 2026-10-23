extends SceneTree
## Headless test of the addon's data layer: run with
##   godot --headless --path tests/godot -s res://test_runner.gd -- <kedo command> <content path> [schema path]
## The addon is copied into tests/godot/addons/kedo by tools/godot-test.ps1 before running.

var failures := 0
var _pending_form: Array = []


func _initialize() -> void:
	var user_args := OS.get_cmdline_user_args()
	if user_args.size() < 2:
		push_error("usage: -- <kedo command> <content path> [schema path]")
		quit(2)
		return
	var cli := KedoCli.new()
	cli.command = KedoCli.split_command(user_args[0])
	cli.content_paths = PackedStringArray([user_args[1]])
	if user_args.size() > 2:
		cli.schema_paths = PackedStringArray([user_args[2]])

	var catalog := cli.catalog()
	check(not catalog.is_empty(), "catalog returned JSON: " + cli.last_error)
	var objects: Array = catalog.get("objects", [])
	check(objects.size() > 0, "catalog has objects")
	if objects.size() > 0:
		check(objects[0].has("key") and objects[0].has("file") and objects[0].has("line"), "objects carry key, file and line")

	var result := cli.check()
	check(result.has("ok"), "check returned ok flag")
	check(bool(result.get("ok", false)), "sample content has no errors: " + str(result.get("diagnostics", [])))

	var tmp := ProjectSettings.globalize_path("user://doc_test.kedo")
	var f := FileAccess.open(tmp, FileAccess.WRITE)
	f.store_string("kedo 1\nmodule t\nactor a { x = 1 }\n")
	f.close()
	var doc := KedoDocument.open(tmp)
	check(not doc.is_dirty(), "fresh document is clean")
	doc.set_text("kedo 1\nmodule t\nactor a { x = 2 }\n")
	check(doc.is_dirty(), "edited document is dirty")
	check(FileAccess.file_exists(doc.draft_path()), "draft written while dirty")
	var reopened := KedoDocument.open(tmp)
	check(reopened.text.find("x = 2") >= 0, "draft restored on reopen")
	check(doc.save(), "save succeeds")
	check(not doc.is_dirty(), "saved document is clean")
	check(not FileAccess.file_exists(doc.draft_path()), "draft removed after save")
	var h := KedoHighlighter.new()
	check(h.has_keyword_color("extends"), "highlighter knows keywords")
	var preparer := KedoCli.new()
	check(not preparer.prepare() and preparer.last_error.contains(KedoCli.SETTING_PREPARE), "prepare without a command says so")
	preparer.prepare_command = KedoCli.split_command(user_args[0] + " version")
	check(preparer.prepare(), "prepare runs its command: " + preparer.last_error)
	preparer.prepare_command = KedoCli.split_command(user_args[0] + " no-such-command")
	check(not preparer.prepare() and preparer.last_error.begins_with("prepare command failed"), "a failing prepare command reports it: " + preparer.last_error)

	if user_args.size() > 2:
		# The form needs a running tree (nodes get _ready): finish on the first frame.
		_pending_form = [cli, user_args[1], user_args[2]]
		return
	_finish()


func _process(_delta: float) -> bool:
	if _pending_form.size() > 0:
		var args := _pending_form
		_pending_form = []
		_form_tests(args[0], args[1], args[2])
		_finish()
	return false


func _finish() -> void:
	print("godot addon tests: %d failure(s)" % failures)
	quit(1 if failures > 0 else 0)


## Form (#14): describe an object, edit a field through the form, undo it in the source editor, create and duplicate.
func _form_tests(base_cli: KedoCli, content: String, schema: String) -> void:
	var dir := ProjectSettings.globalize_path("user://form_test")
	_copy_dir(content, dir)
	# Drafts of earlier runs would be restored over the fresh copies (KedoDocument keeps unsaved text across restarts).
	var drafts := ProjectSettings.globalize_path("user://kedo_drafts")
	if DirAccess.dir_exists_absolute(drafts):
		for file in DirAccess.get_files_at(drafts):
			DirAccess.remove_absolute(drafts.path_join(file))
	var cli := KedoCli.new()
	cli.command = base_cli.command
	cli.content_paths = PackedStringArray([dir])
	cli.schema_paths = PackedStringArray([schema])
	var panel := KedoPanel.new()
	panel.cli = cli
	root.add_child(panel)
	panel.refresh_catalog()
	check(panel.catalog_objects.size() > 0, "panel lists the copied content")
	var rooms := dir.path_join("rooms.kedo")
	panel.open_file(rooms, 1)
	panel.select_object("room:reactor")
	var desc := panel.describe("room:reactor")
	check(desc.get("fields", []).size() > 0, "describe returns the fields of room:reactor")
	var title: Dictionary = {}
	for f in desc.get("fields", []):
		if f.get("name") == "title":
			title = f
	check(title.get("value") == "\"Reactor\"" and title.get("layer") == "own", "title is own and quoted: " + str(title))
	check(panel._form.find_child("field_title", true, false) != null, "form has a row for title")
	var before := panel.current.text
	check(panel.apply_edit(PackedStringArray(["set", "room:reactor", "title", "\"Main reactor\""])), "form edit applied")
	check(panel.current.text.find("title = \"Main reactor\"") >= 0, "source shows the edit")
	check(panel.current.is_dirty(), "edited document is dirty")
	var after_edit := panel.describe("room:reactor")
	var shown := ""
	for f in after_edit.get("fields", []):
		if f.get("name") == "title":
			shown = str(f.get("value"))
	check(shown == "\"Main reactor\"", "describe reads the unsaved text: " + shown)
	panel._editor.undo()
	panel.sync_from_editor()
	check(panel.current.text == before, "undo in the source editor reverts the form edit")
	panel._editor.redo()
	panel.sync_from_editor()
	check(panel.current.text.find("title = \"Main reactor\"") >= 0, "redo applies it again")
	panel._editor.undo()
	panel.sync_from_editor()
	check(not panel.apply_edit(PackedStringArray(["set", "room:reactor", "title", "a = 1"])), "invalid literal refused")
	check(panel.current.text == before, "refused edit leaves the text unchanged")
	check(panel.apply_edit(PackedStringArray(["duplicate", "room:reactor", "reactor_copy"])), "duplicate applied")
	check(panel.current_key == "room:reactor_copy", "duplicate selects the copy")
	check(panel.apply_edit(PackedStringArray(["new", "room", "spare", "--extends=base_room"])), "new object applied")
	check(panel.current.text.find("room spare extends base_room { }") >= 0, "new object declared")
	# Keyed lists of records: one foldable group per item, whose rows edit `list[key].field` as a patch.
	var crises := dir.path_join("crises.kedo")
	panel.open_file(crises, 1)
	panel.select_object("crisis:coolant_leak")
	var leak := panel.describe("crisis:coolant_leak")
	var keys: Array = []
	for f in leak.get("fields", []):
		if f.get("name") == "choices":
			for item in f.get("items", []):
				keys.append(item.get("key"))
	check(keys == ["patch", "vent", "wait"], "describe lists the items of a keyed list: " + str(keys))
	check(panel._form.find_child("item_choices[patch]", true, false) != null, "form has a group per item")
	check(panel._form.find_child(KedoForm.row_name("choices[patch].duration"), true, false) != null, "item rows edit list[key].field")
	check(panel._form.find_child(KedoForm.row_name("choices[patch].costs[parts].amount"), true, false) != null, "nested keyed lists get their own groups")
	var crises_before := panel.current.text
	check(panel.apply_edit(PackedStringArray(["set", "crisis:coolant_leak", "choices[patch].duration", "45min"])), "item edit applied")
	check(panel.current.text.find("choices[patch] { duration = 45min }") >= 0, "the edit is a patch of the item")
	check(panel.current.text.find("choices += last [{ id = wait") >= 0, "the inherited item is untouched")
	panel._editor.undo()
	panel.sync_from_editor()
	check(panel.current.text == crises_before, "undo reverts the item edit")
	check(KedoForm.item_key("effort.bonus") == "effort.bonus" and KedoForm.item_key("two words") == "\"two words\"", "item keys quote only when needed")
	panel.open_file(rooms, 1)
	var registered := [false]
	KedoForm.register_sheet("room", func(d: Dictionary, form: KedoForm) -> void:
		registered[0] = true
		for f in d.get("fields", []):
			if f.get("name") == "title":
				form.add_field_row(f, "title", form))
	panel.select_object("room:reactor")
	check(registered[0], "a registered sheet replaces the generic form")
	check(panel._form.find_child("field_title", true, false) != null and panel._form.find_child("field_kind", true, false) == null, "the sheet decides the rows")
	KedoForm.register_sheet("room", Callable())
	check(panel.unsaved_status() != "", "unsaved status reports the edited file")
	for doc in panel.documents.values():
		doc.discard()
	check(panel.unsaved_status() == "", "discarding leaves nothing pending")
	panel.queue_free()


func _copy_dir(from: String, to: String) -> void:
	DirAccess.make_dir_recursive_absolute(to)
	var d := DirAccess.open(from)
	for file in d.get_files():
		if file.ends_with(".kedo"):
			DirAccess.copy_absolute(from.path_join(file), to.path_join(file))


func check(condition: bool, label: String) -> void:
	if condition:
		print("  ok   " + label)
	else:
		failures += 1
		printerr("  FAIL " + label)
