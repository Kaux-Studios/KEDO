extends SceneTree
## Headless test of the addon's data layer: run with
##   godot --headless --path tests/godot -s res://test_runner.gd -- <kedo command> <content path> [schema path]
## The addon is copied into tests/godot/addons/kedo by tools/godot-test.ps1 before running.

var failures := 0


func _init() -> void:
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

	print("godot addon tests: %d failure(s)" % failures)
	quit(1 if failures > 0 else 0)


func check(condition: bool, label: String) -> void:
	if condition:
		print("  ok   " + label)
	else:
		failures += 1
		printerr("  FAIL " + label)
