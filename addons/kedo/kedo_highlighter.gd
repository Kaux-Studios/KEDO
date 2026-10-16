@tool
class_name KedoHighlighter
extends CodeHighlighter
## Keyword and region highlighting for .kedo files, generated from the same token table as the lexer (KEDO-01 §2).

const KEYWORDS := [
	"kedo", "module", "manifest", "abstract", "mixin", "extends", "with", "component", "remove",
	"on", "function", "let", "set", "if", "else", "wait", "apply", "emit", "stop", "return",
	"decision", "roll", "signal", "option", "as", "and", "or", "not", "true", "false",
]


func _init() -> void:
	var keyword_color := Color(0.86, 0.55, 0.85)
	var string_color := Color(0.98, 0.76, 0.45)
	var comment_color := Color(0.5, 0.56, 0.6)
	var number_color := Color(0.6, 0.85, 0.65)
	var reference_color := Color(0.55, 0.78, 0.98)
	number_color = number_color
	for kw in KEYWORDS:
		add_keyword_color(kw, keyword_color)
	add_color_region("\"", "\"", string_color, false)
	add_color_region("///", "", comment_color, true)
	add_color_region("//", "", comment_color, true)
	add_color_region("/*", "*/", comment_color, false)
	add_color_region("@", "(", reference_color, true)
	symbol_color = Color(0.8, 0.8, 0.8)
	function_color = reference_color
	member_variable_color = Color(0.9, 0.9, 0.9)
