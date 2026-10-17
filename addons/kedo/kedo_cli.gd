@tool
class_name KedoCli
extends RefCounted
## Runs the `kedo` command line and parses its JSON output. The addon never loads the game
## assembly: the CLI (same core as the MCP server) is the single authority for diagnostics.
##
## Configuration: Project Settings (project.godot) with these names, overridden per machine by non-empty Editor Settings:
##   kedo/command        executable or command prefix, e.g. "kedo", "dotnet kedo" or
##                        "dotnet run --project C:/dev/KEDO/src/Kedo.Cli -c Release --no-build --"
##   kedo/content_paths  paths passed to every command (default: ["res://content"])
##   kedo/schema_paths   paths of the KEDO-written schema (default: [])

const SETTING_COMMAND := "kedo/command"
const SETTING_CONTENT := "kedo/content_paths"
const SETTING_SCHEMA := "kedo/schema_paths"

var command: PackedStringArray = PackedStringArray(["kedo"])
var content_paths: PackedStringArray = PackedStringArray(["res://content"])
var schema_paths: PackedStringArray = PackedStringArray()
var last_error: String = ""


static func from_editor_settings() -> KedoCli:
	# Project Settings (committed in project.godot) give the project's defaults; a non-empty Editor Setting of the same
	# name overrides them on one machine (for example a different path to the CLI).
	var cli := KedoCli.new()
	var command := str(ProjectSettings.get_setting(SETTING_COMMAND, "kedo"))
	var content: Variant = ProjectSettings.get_setting(SETTING_CONTENT, PackedStringArray(["res://content"]))
	var schema: Variant = ProjectSettings.get_setting(SETTING_SCHEMA, PackedStringArray())
	if Engine.is_editor_hint():
		var settings := EditorInterface.get_editor_settings()
		for key in [SETTING_COMMAND, SETTING_CONTENT, SETTING_SCHEMA]:
			if not settings.has_setting(key):
				settings.set_setting(key, "" if key == SETTING_COMMAND else PackedStringArray())
		# Empty or the built-in default ("kedo", res://content) means "not set here": earlier versions wrote those defaults.
		var editor_command := str(settings.get_setting(SETTING_COMMAND))
		if editor_command != "" and editor_command != "kedo":
			command = editor_command
		var editor_content := settings.get_setting(SETTING_CONTENT) as PackedStringArray
		if editor_content.size() > 0 and editor_content != PackedStringArray(["res://content"]):
			content = editor_content
		if (settings.get_setting(SETTING_SCHEMA) as PackedStringArray).size() > 0:
			schema = settings.get_setting(SETTING_SCHEMA)
	cli.command = KedoCli.split_command(command)
	cli.content_paths = PackedStringArray(content)
	cli.schema_paths = PackedStringArray(schema)
	return cli


static func split_command(text: String) -> PackedStringArray:
	var parts := PackedStringArray()
	for part in text.split(" ", false):
		parts.append(part)
	return parts


## Converts res:// paths to OS paths for the CLI.
static func to_os_path(path: String) -> String:
	return ProjectSettings.globalize_path(path) if path.begins_with("res://") or path.begins_with("user://") else path


## Runs `kedo <subcommand> <content paths> --json [--schema=...] extra` and returns the parsed JSON (Dictionary) or null.
func run_json(subcommand: String, extra: PackedStringArray = PackedStringArray(), include_content: bool = true) -> Variant:
	var args := PackedStringArray()
	for i in range(1, command.size()):
		args.append(command[i])
	args.append(subcommand)
	if include_content:
		for p in content_paths:
			args.append(KedoCli.to_os_path(p))
	args.append("--json")
	if schema_paths.size() > 0:
		var joined := PackedStringArray()
		for p in schema_paths:
			joined.append(KedoCli.to_os_path(p))
		args.append("--schema=" + ";".join(joined))
	for e in extra:
		args.append(e)
	var output: Array = []
	var code := OS.execute(command[0], args, output, true, false)
	if code == -1:
		last_error = "cannot run `%s`: set Editor Settings > kedo/command to the kedo CLI (e.g. `kedo`, `dotnet kedo`)" % command[0]
		return null
	var text := ""
	for chunk in output:
		text += str(chunk)
	var json := JSON.new()
	# The CLI prints one JSON document on stdout; diagnostics text goes to stderr (merged when read_stderr is true),
	# so find the first `{` and parse from there.
	var start := text.find("{")
	if start < 0:
		last_error = "kedo returned no JSON (exit %d): %s" % [code, text.left(400)]
		return null
	var err := json.parse(text.substr(start))
	if err != OK:
		last_error = "cannot parse kedo output: %s (exit %d)" % [json.get_error_message(), code]
		return null
	last_error = ""
	return json.data


## Catalog of objects: [{type, id, key, abstract, file, line, parent, mixins, hooks, hash, errors}].
func catalog() -> Dictionary:
	var data: Variant = run_json("catalog")
	return data if data is Dictionary else {}


## Diagnostics of the whole content set: {ok, diagnostics: [{code, severity, message, file, line, column}]}.
func check() -> Dictionary:
	var data: Variant = run_json("check")
	return data if data is Dictionary else {}


## Canonical formatting of one file; returns the formatted text or "" on failure (the CLI rewrites the file).
func format_file(os_path: String) -> bool:
	var data: Variant = run_json("fmt", PackedStringArray([os_path]), false)
	return data is Dictionary and data.get("ok", false)
