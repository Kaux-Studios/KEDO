@tool
class_name KedoDocument
extends RefCounted
## One .kedo file open in the panel: text, saved text, dirty flag, atomic save and draft autosave
## (drafts survive an editor restart; see Plan-05).

signal dirty_changed(dirty: bool)

var path: String = ""
var text: String = ""
var saved_text: String = ""


static func open(file_path: String) -> KedoDocument:
	var doc := KedoDocument.new()
	doc.path = file_path
	var f := FileAccess.open(file_path, FileAccess.READ)
	if f != null:
		doc.text = f.get_as_text()
		f.close()
	doc.saved_text = doc.text
	var draft := doc.draft_path()
	if FileAccess.file_exists(draft):
		var d := FileAccess.open(draft, FileAccess.READ)
		if d != null:
			var draft_text := d.get_as_text()
			d.close()
			if draft_text != doc.saved_text:
				doc.text = draft_text
	return doc


func is_dirty() -> bool:
	return text != saved_text


func set_text(new_text: String) -> void:
	var was_dirty := is_dirty()
	text = new_text
	if is_dirty():
		_write(draft_path(), text)
	elif FileAccess.file_exists(draft_path()):
		DirAccess.remove_absolute(draft_path())
	if was_dirty != is_dirty():
		dirty_changed.emit(is_dirty())


## Atomic save: write a temporary file next to the target, then rename over it.
func save() -> bool:
	var tmp := path + ".tmp"
	if not _write(tmp, text):
		return false
	var err := DirAccess.rename_absolute(tmp, path)
	if err != OK:
		return false
	saved_text = text
	if FileAccess.file_exists(draft_path()):
		DirAccess.remove_absolute(draft_path())
	dirty_changed.emit(false)
	return true


func discard() -> void:
	set_text(saved_text)


func draft_path() -> String:
	var base := "user://kedo_drafts/"
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(base))
	return base + path.md5_text() + ".kedo"


func _write(target: String, content: String) -> bool:
	var f := FileAccess.open(target, FileAccess.WRITE)
	if f == null:
		return false
	f.store_string(content)
	f.close()
	return true
