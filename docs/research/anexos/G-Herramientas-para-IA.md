# Research G — AI tooling for KEDO (MCP server, LSP, skills, LLM-friendly DSL design)

Date: 2026-10-15. Target: KEDO (Kaux Enriched Data Object), private C# / .NET 8 DSL + toolchain for game content.
Claims are tagged **VERIFIED** (primary source fetched this session, URL given) or **INFERRED** (reasoning / secondary sources / prior knowledge).

---

## 1. Model Context Protocol (MCP): spec, C# SDK, best practices, pitfalls

### 1.1 Current spec revision: 2026-07-28 (large, breaking change)

VERIFIED — https://modelcontextprotocol.io/specification/versioning and https://modelcontextprotocol.io/specification/2026-07-28/changelog

- The **current** revision is `2026-07-28`; `2025-11-25` and earlier are "handshake-based" revisions kept for backward compatibility.
- Major changes relevant to a tooling server:
  1. **Stateless core.** The `initialize`/`notifications/initialized` handshake is removed. Every request carries protocol version + client capabilities in `_meta` (`io.modelcontextprotocol/protocolVersion`, `.../clientCapabilities`). Servers SHOULD identify themselves in each result's `_meta` (`.../serverInfo`).
  2. **`server/discover`** is a mandatory RPC returning supported protocol versions, capabilities and identity. Clients MAY call it first; on stdio it doubles as a back-compat probe.
  3. **No protocol sessions** (`Mcp-Session-Id` removed from Streamable HTTP). `tools/list` etc. MUST NOT vary per connection. Cross-call state uses **explicit, server-minted handles passed as ordinary tool arguments** (the spec's "Stateful Tools" non-normative guidance: opaque IDs, stated lifetime, clear "expired handle" error).
  4. **Multi Round-Trip Requests (MRTR)** replace server-initiated `elicitation/create`, `sampling/createMessage`, `roots/list`. A tool returns `resultType: "input_required"` with `inputRequests`; the client retries the same call with `inputResponses` (+ opaque `requestState`). All results now carry `resultType` (`"complete"` | `"input_required"`).
  5. `subscriptions/listen` replaces HTTP GET stream + `resources/subscribe`; `ping`, `logging/setLevel`, `notifications/roots/list_changed` removed; log level per-request via `_meta`.
  6. Tasks moved to an extension (`io.modelcontextprotocol/tasks`), polling via `tasks/get`.
  7. **Deprecated: Roots, Sampling, Logging** (12-month window). Migration: pass paths via tool params/resource URIs/config; log to **stderr** on stdio or OpenTelemetry.
  8. `inputSchema`/`outputSchema` may use any JSON Schema 2020-12 keywords; `structuredContent` may be any JSON value.
  9. `tools/list`, `resources/list`, etc. now require `ttlMs` and `cacheScope` (`CacheableResult`). Servers SHOULD return tools in **deterministic order** (prompt-cache hits).
  10. Error-code policy: `-32020..-32099` reserved for the spec; `-32000..-32019` implementation-defined. Resource-not-found now `-32602`.
  11. HTTP+SSE transport and OAuth Dynamic Client Registration deprecated (Client ID Metadata Documents preferred). `iss` validation required.

Implication for KEDO (INFERRED): design the server as **stateless-by-default** from day one — no "open document" sessions. Any state (loaded workspace, VM trace handle) must be an explicit handle argument with documented expiry. Don't adopt Roots/Sampling/Logging.

### 1.2 Tools: data model, structured output, errors, annotations

VERIFIED — https://modelcontextprotocol.io/specification/2026-07-28/server/tools

- Tool = `name` (1–128 chars, `[A-Za-z0-9_.-]`, case-sensitive, unique per server), `title`, `description`, `icons`, `inputSchema` (valid JSON Schema object; use `{"type":"object","additionalProperties":false}` for no-arg tools), optional `outputSchema`, `annotations` (hints such as read-only/destructive/idempotent — clients MUST treat annotations as untrusted unless the server is trusted).
- Results: `content[]` (text / image / audio / `resource_link` / embedded `resource`) and optional `structuredContent`. If `outputSchema` is given, servers MUST conform; clients SHOULD validate. For back-compat a tool returning `structuredContent` SHOULD also put the serialized JSON in a `TextContent` block.
- **Two error channels**: protocol errors (JSON-RPC `error`, e.g. unknown tool: models rarely recover) vs **tool execution errors** (`isError: true` with actionable text: "Clients SHOULD provide tool execution errors to language models to enable self-correction"). KEDO diagnostics are *data*, not errors — return them in `structuredContent` with `isError: false`; reserve `isError` for "file not found / invalid handle".
- Security: servers MUST validate inputs, rate-limit, sanitize outputs; clients SHOULD implement timeouts and show inputs before calling.

### 1.3 Official C# SDK (`ModelContextProtocol` on NuGet)

VERIFIED — https://github.com/modelcontextprotocol/csharp-sdk and https://github.com/modelcontextprotocol/csharp-sdk/releases

- Maintained with Microsoft; Apache-2.0 (repo) / docs say MIT for docs site. Packages: `ModelContextProtocol.Core` (minimal deps, client + low-level server), `ModelContextProtocol` (hosting + DI; the default for stdio servers), `ModelContextProtocol.AspNetCore` (Streamable HTTP in ASP.NET Core), `ModelContextProtocol.Extensions.Apps`, `ModelContextProtocol.Extensions.Tasks`.
- Release line: **v2.0.0 (28 Jul 2026) = stable, aligned with spec 2026-07-28** ("discovery-first negotiation, stateless-by-default HTTP, multi-round-trip requests"); v2.1.0 (5 Aug) adds opt-in `subscriptions/listen` handler; **v2.2.0 (13 Aug 2026, latest)** adds hybrid stateful/stateless HTTP so 2025-11-25 and 2026-07-28 clients share one endpoint. 1.x line ends at v1.4.1 (9 Jul 2026).
- Breaking in 2.0: HTTP stateless by default (`Stateless = false` for legacy); Tasks in separate package; tools with non-object return types emit raw structured values; deserializing a `Tool` without `inputSchema` throws; Roots/Sampling/Logging APIs produce `MCP9005` deprecation warnings.
- Auth: OAuth (PKCE S256 required), Client ID Metadata, Enterprise Managed Authorization (ID-JAG flow, `IdentityAssertionGrantProvider`). Irrelevant for a local stdio tooling server.
- Programming model (INFERRED from v1.x docs, not re-verified this session for v2 but packages/attributes persisted): `Host.CreateApplicationBuilder(args).Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`; tools are static/instance methods in `[McpServerToolType]` classes marked `[McpServerTool]` with `[Description]` on method and parameters; prompts `[McpServerPrompt]`, resources `[McpServerResource]`. Returning a POCO yields `outputSchema` + `structuredContent` automatically. **Rule for stdio: never write to stdout except JSON-RPC** — route ILogger to stderr.
- Target frameworks (INFERRED): packages ship netstandard2.0 / net8.0 / net9.0+; .NET 8 is fine for KEDO.

Third-party reference (secondary, 2026-08-21): an awesome-copilot "dotnet-mcp-builder" skill lists `ModelContextProtocol` 2.2.0 + `Microsoft.Extensions.Hosting` 10.0.11 as current pins — https://git.cynarski.dev/Awesome/awesome-copilot/raw/branch/main/skills/dotnet-mcp-builder/references/packages.md (INFERRED reliability).

### 1.4 How compilers / LSPs are exposed over MCP today

| Project | Tools exposed | Notes |
|---|---|---|
| **mcp-language-server** (isaacphi, Go) VERIFIED https://github.com/isaacphi/mcp-language-server | `definition`, `references`, `diagnostics` (per file), `hover`, `rename_symbol`, `edit_file` (line-number based) | Wraps any stdio LSP. Only 6 tools. Claims line-based `edit_file` is "more reliable and uses less context than search-and-replace". Beta. |
| **Serena** (oraios) VERIFIED https://github.com/oraios/serena | find symbol, symbols overview, referencing symbols, declaration, implementations, diagnostics; rename; `replace_symbol_body`, `insert_before/after_symbol`, safe delete; search_for_pattern, read_file, shell (usually disabled inside Claude Code/Codex); memories | "Agent-first tool design built on high-level abstractions" (symbols, not line numbers). Two backends: LSP (40+ languages, C# included) or JetBrains plugin. Configurable **contexts/modes** to shrink the tool set per host. |
| **JetBrains IDE MCP server** (IntelliJ 2025.2+) INFERRED from community skill pages | `get_file_problems`, `get_project_problems`, `rename_refactoring`, find usages, etc. | Settings → Tools → MCP Server → Exposed Tools. Shows the "diagnostics after edit" pattern. |
| **GitHub MCP server** VERIFIED https://github.com/github/github-mcp-server | ~90+ tools split into **toolsets** (`--toolsets`, `GITHUB_TOOLSETS`, default = context,repos,issues,pull_requests,users); `--read-only` skips write tools | Rationale quoted: "Enabling only the toolsets that you need can help the LLM with tool choice and reduce the context size." |
| **Roslyn MCP servers** (pvasek/csharp-mcp, Darylmcd.RoslynMcp, MCPRoslyn, carquiza/RoslynMCP) VERIFIED https://github.com/pvasek/csharp-mcp (others via search) | find definition/references/implementations, signature, list members, inheritance tree, callers/callees, rename, generate/implement interface, **diagnostics**, unused code | pvasek's is **dual-mode: same operations as CLI (`find-references`) and MCP (`csharp_find_references`)**, solution loaded once at startup in MCP mode. 16–18 tools each; MCPRoslyn markets "token-efficient" read-only tools. |

Takeaways (INFERRED): language servers over MCP converge on ~6–20 tools: *diagnostics, definition/hover (explain), references, rename, symbol-level edit*. Nobody exposes raw parse trees; they expose **questions an agent asks**. Dual CLI/MCP over one core is an established pattern.

### 1.5 Pitfalls: context size, tool count, descriptions, idempotency

VERIFIED — https://code.claude.com/docs/en/mcp (Claude Code MCP reference)
- **Tool search is on by default**: only tool *names* and *server instructions* load at session start; definitions are deferred and fetched via `ToolSearch`. "Claude Code doesn't impose a fixed per-server tool cap; the practical limit is your context window budget." `ENABLE_TOOL_SEARCH=auto` loads upfront until definitions reach 10% of context.
- **Server instructions matter more under tool search** ("help Claude understand when to search for your tools, similar to how skills work"). Limits: server instructions and upfront tool descriptions cut at **4,096 chars**; descriptions of deferred tools at **16,384 chars**. Put searchable keywords near the start.
- Per-tool `_meta`: `anthropic/alwaysLoad: true` (load upfront), `anthropic/maxResultSizeChars` (raise persist-to-disk threshold up to 500k), `anthropic/requiresUserInteraction: true` (always prompt).
- **Output limits**: warning at 10,000 tokens; default cap `MAX_MCP_OUTPUT_TOKENS` = 25,000; successful text results > 50,000 chars are written to a file and Claude gets a path. Error text > ~11k chars is middle-truncated. Transport cap 16 MiB. **Paginate / truncate server-side.**
- Tool calls > 2 min move to a background task. Tool full name (`mcp__server__tool`) must be ≤ 128 chars; invalid input schemas (non-2020-12, bad property names) get the tool silently dropped; root-level `anyOf/oneOf` is flattened.
- MCP prompts appear as `/server:prompt` slash commands; MCP resources are `@server:uri` mentions (automatically fetched as attachments). Both are useful for KEDO: a `kedo://grammar` resource and a `/kedo:new-object` prompt.
- Elicitation: on 2026-07-28 connections Claude Code declares `elicitation: {form, url}` and shows a dialog.

VERIFIED — https://anthropic.com/engineering/writing-tools-for-agents (Anthropic engineering): "More tools don't always lead to better outcomes"; consolidate steps a human would do together; namespace with prefixes; return "only high signal information"; offer a `response_format` enum (`concise`/`detailed`); use pagination/range/filter/truncation with sensible defaults and tell the agent how to narrow; error messages specific and actionable; describe tools "as you would to a new teammate"; unambiguous param names (`user_id` not `user`); build evals from real tasks with verifiable outcomes and read transcripts.

Secondary (INFERRED): GitHub MCP's 93 tool definitions were measured at ~55k tokens on clients without deferral (https://workos.com/blog/mcp-server-token-cost); Cursor loads all definitions upfront per request (client comparison June 2026). So keep KEDO's tool count ≤ ~12 for hosts without tool search.

Idempotency / determinism (INFERRED from spec): because `tools/list` must be deterministic and connection-independent, and because any state is a handle, every KEDO tool should be a pure function of (workspace path, file content or path, options). Apply-fix should take an explicit `fix_id` + expected file hash and refuse on mismatch (optimistic concurrency), so retries after client-side timeouts are safe.

---

## 2. LSP vs MCP — what to build first

### 2.1 Frameworks for a C# language server

| Framework | Status (VERIFIED on NuGet unless noted) | Verdict |
|---|---|---|
| `OmniSharp.Extensions.LanguageServer` | 0.19.11 released **2026-10-07** (previous 0.19.9 was 2023-09-21); 2.7M total downloads; targets netstandard2.0/2.1, net8.0, net10.0 — https://www.nuget.org/packages/OmniSharp.Extensions.LanguageServer | Still the de-facto community choice; code-generated handler plumbing, DI, JSON-RPC. Mature but heavyweight; long gaps between releases. |
| `Draco.Lsp` (Draco-lang) | 0.4.14-pre, last updated 2024-10-23, net8.0, ~35k downloads — https://www.nuget.org/packages/Draco.Lsp | Lean, metamodel-generated, attribute-based handlers. Prerelease and quiet for 2 years; fine to **vendor/fork** (MIT), risky as a dependency. |
| `Microsoft.CommonLanguageServerProtocol.Framework` (CLaSP) | 5.0.0-1.25277.114 prerelease (2025-06-06), netstandard2.0 — https://www.nuget.org/packages/Microsoft.CommonLanguageServerProtocol.Framework ; README: "not recommended or supported for use outside of specific teams/projects" (Roslyn, Razor) — https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Microsoft.CommonLanguageServerProtocol.Framework/README.md | Do not depend on it. |

Hosting notes: Zed registers a language server through `extension.toml [language_servers.<id>]` + a Rust `language_server_command` (VERIFIED https://zed.dev/docs/extensions/languages). VS Code uses the `vscode-languageclient` npm package (INFERRED). Neovim has built-in `vim.lsp` (INFERRED).

### 2.2 Build LSP first and bridge to MCP?

INFERRED, built on the verified facts above:
- The LSP→MCP bridges (mcp-language-server, mcpls, Serena's LSP backend) exist precisely because those languages *already had* an LSP. For a brand-new language, bridging is a detour: LSP is **editor-session-shaped** (open documents, incremental sync, pull diagnostics per open file, UI-oriented completions), while MCP 2026-07-28 is **stateless and batch-shaped** (one call = one answer, explicit handles). Forcing MCP through LSP means re-adding sessions the protocol just removed.
- Both should sit on the **same in-process core**: parse → bind → check → format → query → explain → VM. The core API should take *immutable snapshots* (workspace + overrides for unsaved buffers) and return immutable results. The CLI, the MCP server and the LSP server are three thin adapters (same way `pvasek/csharp-mcp` exposes one Roslyn core as CLI + MCP).
- Order: **CLI (`kedo check --json`) → MCP → LSP**. The CLI is needed for CI and for agents without MCP (Codex sandbox, Cursor without tool search, shell-only agents); MCP adds structured results and handles; LSP is a human-DX feature that can come after v1 ships, when the language is stable enough that incremental re-analysis is worth building.
- One caveat: design the core's incremental story (file-level invalidation keyed on import graph) early, because LSP latency expectations (<100 ms per keystroke) will not be met by a whole-workspace recompile and retrofitting incrementality is the expensive part.

### 2.3 Tree-sitter and editor highlighting

- **VS Code still highlights with TextMate grammars**; tree-sitter does not replace them (May 2026 PR evidence, INFERRED from https://github.com/timfennis/andy-cpp/pull/165; community extensions use the Semantic Tokens API as a workaround). So KEDO needs a TextMate `.tmLanguage.json` for VS Code *and* a tree-sitter grammar for Zed/Neovim/Helix. Alternatively, ship LSP semantic tokens and keep the TextMate grammar minimal.
- **Zed** (VERIFIED https://zed.dev/docs/extensions/languages): `extension.toml [grammars.kedo] repository/rev`, `languages/kedo/config.toml` (`name`, `grammar`, `path_suffixes`, `line_comments`), queries `highlights.scm`, `brackets.scm`, `indents.scm`, `outline.scm`, `injections.scm`.
- **Neovim** (VERIFIED https://github.com/nvim-treesitter/nvim-treesitter/blob/main/README.md): `main` branch is a full rewrite requiring Neovim 0.12 nightly + tree-sitter-cli ≥ 0.26.1; register a parser via `require('nvim-treesitter.parsers').kedo = { install_info = { url, revision, queries } }` in a `User TSUpdate` autocmd; queries live under `queries/kedo/` on the runtimepath; filetype via `vim.filetype.add()`.
- **Godot** (VERIFIED https://docs.godotengine.org/en/stable/classes/class_codehighlighter.html and class_syntaxhighlighter.html, class_codeedit.html): `CodeHighlighter` supports only keyword colors, member-keyword colors, `add_color_region(start,end,color,line_only)`, number/symbol/function/member-variable colors — **no regex, no TextMate, no tree-sitter**. `SyntaxHighlighter` is subclassable: override `_get_line_syntax_highlighting(line)` → `Dictionary{column: {"color": Color}}` with `_update_cache` / `_clear_highlighting_cache` hooks. `CodeEdit` has code completion (`add_code_completion_option`, `_request_code_completion`, `_filter_code_completion_candidates`), symbol lookup/validate signals, delimiters, folding/regions, auto-brace, line-length guidelines, breakpoints. The docs don't state whether `SyntaxHighlighter` subclasses run outside the editor; `CodeHighlighter` existing separately from `EditorSyntaxHighlighter` suggests runtime use is fine (INFERRED; test in an export).
  - Practical answer: nothing in Godot consumes tree-sitter or TextMate. For an in-engine KEDO editor, either (a) populate `CodeHighlighter` from the **same keyword/delimiter table** the KEDO lexer uses (generate `keyword_colors` + `color_regions` from a single source of truth), or (b) write a `SyntaxHighlighter` subclass in C# that calls the KEDO **lexer** per line (tokens → colors) and feed completion/symbol lookup from the same core. No tree-sitter GDExtension for CodeEdit was found (search returned none).

---

## 3. Skills and agent instructions for a custom language

### 3.1 Formats and discovery (all VERIFIED)

- **Agent Skills open standard** — https://agentskills.io/specification: a folder with `SKILL.md` (YAML frontmatter: required `name` ≤ 64 chars lowercase/digits/hyphens matching the directory, required `description` ≤ 1024 chars; optional `license`, `compatibility`, `metadata` (string map), experimental `allowed-tools`) + optional `scripts/`, `references/`, `assets/`. Progressive disclosure: metadata (~100 tokens) at startup → body (< 5000 tokens recommended, < 500 lines) on activation → referenced files on demand; keep references one level deep. Validator: `skills-ref validate ./my-skill`. Adopters listed on https://agentskills.io/home include Claude Code, Claude, ChatGPT & Codex, Cursor, GitHub Copilot, VS Code, Gemini CLI, OpenCode, OpenHands, Amp, Goose, Kiro, Roo Code, JetBrains Junie, Factory, Tabnine and more.
- **Claude Code** — https://code.claude.com/docs/en/skills: extra frontmatter (`when_to_use`, `argument-hint`, `arguments`, `disable-model-invocation`, `user-invocable`, `allowed-tools`, `disallowed-tools`, `model`/`effort`, `context: fork` + `agent`, `hooks`, `paths` globs, `shell`). Locations: `~/.claude/skills/`, `.claude/skills/` (commit it), nested `<subdir>/.claude/skills/`, plugins (`/plugin:skill`). `description`+`when_to_use` truncated at 1,536 chars in the listing. `` !`cmd` `` dynamic injection (e.g. `` !`kedo check --json --summary` ``). Compaction keeps ≤ 5,000 tokens per skill, 25,000 combined. Outside Claude Code only spec fields are allowed.
- **Codex / ChatGPT** — https://learn.chatgpt.com/docs/build-skills: discovery in `.agents/skills` (cwd and each parent up to repo root), `$HOME/.agents/skills`, `/etc/codex/skills`, bundled. Optional `agents/openai.yaml` (`policy.allow_implicit_invocation`, `dependencies.tools` incl. MCP servers, UI metadata). Invoke with `$` or `/skills`. Skill list budget ≈ 2% of context (8,000 chars), up to 10,000 tokens via `skills.max_context_tokens`.
- **Cursor** — https://cursor.com/docs/context/skills: reads `.agents/skills/`, `.cursor/skills/` (nested ok), `~/.agents/skills/`, `~/.cursor/skills/`, plus compatibility paths `.claude/skills/`, `.codex/skills/`. Optional `paths` globs, `disable-model-invocation`. Rules (`.cursor/rules/*.mdc` with `alwaysApply`/`globs`/`description`) remain for always-on or glob-scoped guidance; "Apply Intelligently" rules can be migrated to skills.
- **AGENTS.md**: Codex, Cursor and others read it as repo-level standing instructions (INFERRED from docs navigation + community sources; Claude Code reads CLAUDE.md and, in this repo, AGENTS.md via project config). Keep it short — one source calls a 300-line AGENTS.md "an Always Apply rule wearing a different name" (https://thepromptshelf.dev/blog/cursorrules-vs-mdc-format-guide-2026/, INFERRED).

Practical layout for KEDO (INFERRED): put the canonical skills in **`.agents/skills/kedo-*/`** (read natively by Codex and Cursor), and symlink/copy into `.claude/skills/` or ship a Claude Code plugin that points at the same folders. Keep frontmatter to spec fields; add Claude-only fields (`paths`, `when_to_use`) only in the Claude copy.

### 3.2 What to put in the skills (INFERRED, grounded in 3.3)

1. `kedo-syntax` (user-invocable: false; `paths: ["**/*.kedo"]`): one-page grammar cheat-sheet in EBNF-ish form + 5–8 golden examples covering every construct (object, inheritance, import, component, expression, embedded function), each followed by "what `kedo check` says about it". Reference files: `references/grammar.ebnf`, `references/diagnostics.md` (every code: cause, fix, example), `references/stdlib.md`.
2. `kedo-add-type` / `kedo-add-component`: step-by-step recipe for extending the **toolchain** (where the C# lives, how to add a node kind, a binder rule, a diagnostic code, a test corpus case, and a formatter rule), with the verification command at the end.
3. `kedo-author-content`: the loop — write file → `kedo check --json` → read `fixes` → apply → `kedo fmt` → `kedo run --trace`. Include the "do not invent keywords; query `kedo explain <keyword>` or the catalog tool if unsure" rule.
4. `kedo-debug-diagnostic`: how to read a diagnostic JSON object, how to use `explain`, common false paths.
5. A `scripts/` folder with wrappers (`check.ps1`/`check.sh`) so agents without MCP still get JSON.

### 3.3 Evidence on what makes LLMs reliable with custom DSLs

- **Few-shot examples > grammar prose.** MTOB ("Machine Translation from One Book", ICLR 2024) baselines stayed below a human learner; the follow-up "Can LLMs Really Learn to Translate a Low-Resource Language from One Grammar Book?" (https://arxiv.org/abs/2409.19151) argues almost all gains come from the book's **parallel examples**, not the grammatical explanations (VERIFIED abstracts via search). Analogy: golden `.kedo` examples paired with intent matter more than the EBNF.
- **Grammar in the loop helps when the model derives a task-specific grammar** — Grammar Prompting (Wang et al., NeurIPS 2023, https://arxiv.org/abs/2305.19234): the LLM first writes a specialized BNF subset, then generates; competitive on semantic parsing, PDDL, SMILES with 16–32 demos. DocCGen (https://arxiv.org/abs/2406.11925): domain schemas/grammars are "generally unseen during pretraining", mitigations are in-context examples and fine-tuning.
- **Unseen languages are learnable in context but accuracy drops a lot.** "In-Context Learning for Esoteric Programming Languages" (NeurIPS 2025): accuracy with docs + sparse examples is well below common languages; **inserting verified solutions into context** ("self-scaffolding") raised Pyth EsoEval 16.67% → 30.82% and Minipy HumanEval 51% → 65%. A 2025 preprint on robot languages introduced after cutoff reports 88.5% pass@1 with **documentation-based retrieval**, and warns compression-style retrieval biases models toward Python. (VERIFIED via search snippets; full papers not read.)
- **Constrained decoding removes syntax errors but can hurt reasoning.** SynCode (TMLR 2025, https://arxiv.org/abs/2403.01632): grammar-augmented decoding eliminated all JSON syntax errors and cut Python/Go syntax errors by 96%. "Let Me Speak Freely?" (Tam et al., EMNLP 2024 Industry): stricter format constraints reduced reasoning performance; a two-step "reason freely, then convert" approach recovers it. For KEDO this argues for: let the agent write KEDO text freely, then **validate** (not constrain), and give it a canonical formatter.
- **Feedback quality is the bottleneck of repair loops.** "Is Self-Repair a Silver Bullet?" (Olausson et al., ICLR 2024, https://arxiv.org/abs/2306.09896): gains from self-repair are modest and depend on feedback quality; stronger/human feedback produces much larger gains. Translation: invest in **precise, actionable diagnostics with fix suggestions** — the compiler is the "strong feedback model".
- **Format familiarity matters.** ConCodeEval (https://arxiv.org/html/2407.03387v1): models comprehend constraints better in JSON/YAML/natural language than XML or Python. JSONSchemaBench (https://arxiv.org/abs/2501.10868) is the reference benchmark for structured output. No study was found directly comparing a novel DSL to JSON for agent accuracy (searched twice) — **INFERRED gap**: KEDO should build its own small eval (see §7).
- Error taxonomies disagree on how common syntax errors are (≈40% in an older HumanEval study vs <10% in a 2024 seven-model study; semantic errors dominate in the newer one) — INFERRED from search; either way, syntax errors are the cheap part to eliminate with a formatter + checker, semantic errors need the type/catalog tools.

---

## 4. LLM-friendly language design

All items INFERRED (design reasoning anchored on §3.3 evidence and rustc precedent), except where noted.

### 4.1 Syntax choices
- **Explicit delimiters, no significant whitespace.** Braces/`end` keywords survive copy-paste, re-indentation and token-budget-driven formatting; indentation errors are a documented LLM failure class and formatting whitespace costs tokens (https://arxiv.org/abs/2508.13666, "The Hidden Cost of Readability"). Keep indentation purely cosmetic and let `kedo fmt` own it.
- **One way to do each thing.** No optional commas/semicolons, no two spellings for inheritance, no implicit conversions. Every optional surface is a place the model can guess wrong.
- **Unambiguous keywords** that don't collide with identifiers models already associate with other languages (`class`, `def`, `import` carry Python/C# priors — fine if semantics match, dangerous if they differ slightly). Prefer `object`, `extends`, `uses`, `component`.
- **Stable, explicit ids**: every object has a declared id (`object hero.sword : Weapon { ... }`), referenced by id, never by position or file. Models are excellent at copying ids and terrible at computing positions.
- **Local reasoning**: an object's meaning should be determinable from its file + explicit imports. Avoid global implicit scope/lookup order.
- **Expressions: small, familiar, C-like** (`+ - * / && || ! == !=`, `a ? b : c`), explicit units/types for numbers if the domain has them. Embedded functions use the same expression grammar with explicit `fn name(params) -> type { ... }` signatures.
- **Comments are structured**: a `///` doc-comment convention that `explain` and the catalog surface, so agents can read intent.
- **Reserved extension points**: `@attribute(...)` syntax reserved from v1 so new features don't need new keywords (1.x compat promise).

### 4.2 Machine-readable diagnostics (modelled on rustc) — VERIFIED reference https://doc.rust-lang.org/rustc/json.html
rustc's `--error-format=json` emits one JSON object per line on stderr with `$message_type`, a parent diagnostic + `children` (notes/help), `code`, `level`, `spans[]` (file, 0-based byte offsets with exclusive end, 1-based line/col, `is_primary`, `label`, `suggested_replacement`, `suggestion_applicability` ∈ `MachineApplicable | MaybeIncorrect | HasPlaceholders | Unspecified`), and `rendered` (human text). Docs tell consumers to parse defensively because fields/enum values may be added.

Proposed `kedo check --json` shape (INFERRED):
```json
{ "version": "1", "tool": "kedo 1.3.0", "ok": false,
  "summary": {"errors": 1, "warnings": 2, "files": 14},
  "diagnostics": [{
    "code": "KEDO2041", "level": "error", "title": "Unknown component",
    "message": "Component 'Inventry' is not defined. Did you mean 'Inventory'?",
    "spans": [{"file": "content/items/sword.kedo", "start": {"line": 12, "col": 9, "offset": 240}, "end": {"line": 12, "col": 17, "offset": 248}, "primary": true, "label": "not found"}],
    "notes": ["Components in scope: Inventory, Equippable, Stackable"],
    "fixes": [{"id": "fx_7f3a", "title": "Rename to 'Inventory'", "applicability": "machine_applicable",
               "edits": [{"file": "...", "start": {...}, "end": {...}, "new_text": "Inventory"}]}],
    "explain_url": "kedo://diagnostics/KEDO2041", "rendered": "error[KEDO2041]: Unknown component\n  --> content/items/sword.kedo:12:9 ..." }]}
```
Rules: stable numeric codes never reused; one `title` per code; `message` always names the wrong thing and the expected thing; `notes` list the valid alternatives (bounded, e.g. top 10) because "what *is* valid" is the single most useful fact for a model; `fixes` carry concrete edits; errors sorted by file then offset; `--json` output goes to **stdout** as a single document (not JSON-lines) with exit code 0 = ok / 1 = diagnostics / 2 = tool failure.

### 4.3 Formatter and canonical form
- `kedo fmt` is opinionated, has no options, is idempotent, and is what `apply_fix` and MCP edits emit. Golden examples in skills are fmt-canonical so model output converges on one style and diffs stay small.
- `kedo fmt --check --json` reports non-canonical files as diagnostics with a machine-applicable fix (whole-file replacement).

### 4.4 Explain tooling
- `kedo explain KEDO2041` → long-form Markdown (like `rustc --explain`), and `kedo explain <symbol-or-id>` → resolved view of an object: final merged properties after inheritance/components, where each value came from (file:line), imports resolved. This is the "hover" of a data language and is what an agent needs to reason about *semantics* instead of guessing.

---

## 5. Content-generation loops

### 5.1 Validator-in-the-loop (INFERRED)
```
agent drafts file(s) → kedo check --json (or MCP check) → if diagnostics:
   for each fix with applicability=machine_applicable → apply_fix
   else → agent edits using message + notes → re-check (bounded, e.g. 3 rounds)
→ kedo fmt → kedo run --trace (optional, sandboxed) → agent compares trace to intent → commit
```
Evidence for bounding the loop: self-repair gains are modest when feedback is weak and compute is counted (Olausson 2024); gains come from feedback quality, so spend effort on diagnostics not on more loop iterations.

Hook option (VERIFIED mechanism, https://code.claude.com/docs/en/plugins/components): a plugin `hooks/hooks.json` `PostToolUse` hook with matcher `Write|Edit` can run `"${CLAUDE_PLUGIN_ROOT}/scripts/check.sh"` automatically after each edit, surfacing diagnostics without the agent having to remember. Equivalent on Codex: instruct via skill (no hook system verified).

### 5.2 Test corpora as examples
- Keep `tests/corpus/{ok,err}/**.kedo` with sidecar `.expected.json` (diagnostics) and, for `ok`, `.expected.trace.json`. The corpus doubles as (a) golden examples referenced by skills, (b) regression tests, (c) the eval set for the AI-tooling itself. "Self-scaffolding" evidence (§3.3) says verified solutions in context are the strongest lever, so the corpus *is* the prompt material.
- Each diagnostic code must have at least one `err/` case that triggers it and one `fix` case showing the machine-applicable repair.

### 5.3 How narrative/mod communities document for tooling
- **Ink / inklecate** (VERIFIED https://raw.githubusercontent.com/inkle/ink/master/inklecate/CommandLineTool.cs): `-j` JSON mode prints `{"compile-success": bool}`, `{"issues": ["ERROR: ...", "WARNING: ..."]}` (bare strings, type implied by prefix/order, **no spans**), `{"export-complete": true}`, and `-s -j` → `{"stats": {words, knots, stitches, functions, choices, gathers, diverts}}`. Lesson: it's enough for an IDE plugin (Inky) but too weak for agents — KEDO should do strictly better (codes, spans, fixes).
- **Yarn Spinner** (VERIFIED partial, https://docs.yarnspinner.dev/llms-full.txt): `ysc` console (`ysc upgrade`, compile etc., "in heavy development"); VS Code extension provides highlighting and a graph view of nodes/jumps; docs are published as `llms-full.txt` / `llms.txt` — a cheap, worthwhile convention for KEDO docs too.
- **Paradox / Clausewitz** (INFERRED from forum/GitHub via search): no official tooling ("we don't have any"); community **CWTools** provides an LSP + VS Code extension with **community-maintained `.cwt` rule files** per game, a GitHub Action (`cwtools-action`) for CI, and derived Rust LSPs (Foch, hearts-of-modding). Lesson: a declarative, versioned **rules/catalog file** that tools consume is what lets third parties (and agents) validate content; KEDO's type catalog export (`kedo catalog --json`) plays that role.

### 5.4 Sandboxing when an agent runs the VM (INFERRED, backed by general guidance)
- KEDO's IR VM is the ideal place for safety: it's an interpreter, so impose a **deterministic step budget** (like WASM fuel), a memory/allocation cap, a wall-clock timeout enforced by the host (not the script), bounded output size, and no I/O or reflection primitives in the VM. Run `kedo run` in-process only when the IR has those limits; otherwise spawn a child process with timeout.
- MCP side: the `run_trace` tool should declare a destructive/open-world hint of false but still be gated by the client (Claude Code tool permissions; `_meta["anthropic/requiresUserInteraction"]` if ever running user-supplied native plugins). Codex has `tool_timeout_sec` (default 60) and `tools.<tool>.output_token_limit` (VERIFIED https://learn.chatgpt.com/docs/extend/mcp.md?surface=cli); Claude Code defaults 25k tokens output, 2-min auto-background.
- Treat VM output and loaded content as untrusted data when echoing into the model (prompt-injection via content files).

---

## 6. Distribution

- **dotnet tool packaging** (VERIFIED https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools-how-to-create): csproj `<PackAsTool>true</PackAsTool>`, `<ToolCommandName>kedo</ToolCommandName>`, `<PackageOutputPath>`; `dotnet pack` → `.nupkg`; install `dotnet tool install -g kedo` (or local with a `.config/dotnet-tools.json` manifest created by `dotnet new tool-manifest`, then `dotnet tool run kedo` / `dotnet kedo`). Applies to .NET 8 SDK+. Local manifests pin versions per repo (preferred for the game repo so agents and CI use the same version). .NET 10 adds RID-specific/self-contained tool packages (INFERRED from skills listing).
- **`kedo mcp` subcommand** (INFERRED pattern, matches pvasek/csharp-mcp and Darylmcd.RoslynMcp which ship MCP servers as global .NET tools): same binary, `kedo mcp` starts stdio server; `kedo mcp --http :port` optional later via `ModelContextProtocol.AspNetCore`. Registration:
  - Claude Code project scope `.mcp.json`: `{"mcpServers": {"kedo": {"type": "stdio", "command": "dotnet", "args": ["kedo", "mcp"]}}}` (or `claude mcp add --transport stdio kedo -- dotnet kedo mcp`) — VERIFIED shape https://code.claude.com/docs/en/mcp.
  - Codex `.codex/config.toml` (trusted projects only): `[mcp_servers.kedo] command = "dotnet" args = ["kedo","mcp"] tool_timeout_sec = 120` — VERIFIED https://learn.chatgpt.com/docs/extend/mcp.md?surface=cli.
  - Cursor: `.cursor/mcp.json` (INFERRED, same JSON shape as Claude's).
- **Claude Code plugin** (VERIFIED https://code.claude.com/docs/en/plugins and /plugins/components): directory with `.claude-plugin/plugin.json` (name, version, description), `skills/<name>/SKILL.md`, `agents/`, `hooks/hooks.json`, `.mcp.json` at plugin root using `${CLAUDE_PLUGIN_ROOT}` (per-version install dir, don't write state there) and `${CLAUDE_PLUGIN_DATA}` (survives updates). Plugin MCP tools are named `mcp__plugin_<plugin>_<server>__<tool>`. Distribute via a marketplace repo with `.claude-plugin/marketplace.json`; develop with `--plugin-dir`; `claude plugin validate` checks `.mcp.json`. Marketplace cards show a "Context cost" estimate — keep skill descriptions tight. The same plugin format also installs on claude.ai/Cowork with a different component subset.
- **Codex**: skills from `.agents/skills` in-repo; MCP from `.codex/config.toml`; "Plugins" bundle skills + connectors (VERIFIED https://learn.chatgpt.com/docs/build-skills).
- **Versioning the tool surface with the 1.x language promise** (INFERRED):
  - Every JSON output carries `"version"` (schema) and `"tool"` (semver). Schema version bumps only on breaking shape changes; adding fields is non-breaking (rustc precedent: "parse defensively").
  - MCP tools: never rename; add `_v2` tools instead of changing input shapes; keep `tools/list` order stable (spec SHOULD, cache hits); `title` for display, `name` for stability. Return `ttlMs` large (catalog rarely changes) and `cacheScope: "private"`.
  - Diagnostic codes are append-only; deprecate by leaving the code and changing level to `warning` with a note.
  - Skills reference the CLI by version-agnostic commands; the dynamic-injection line `` !`kedo --version` `` tells the agent what it's running.
  - Pin the MCP SDK minor (2.2.x) and the spec revision; the C# SDK's hybrid mode (2.2.0) means clients on 2025-11-25 still work.

---

## 7. Recommended AI-tooling architecture for KEDO v1

### 7.1 Build order
1. **Core library** (`Kedo.Core`): immutable `Workspace` snapshot (root path + in-memory overrides), `Parse`, `Bind/Check` producing `Diagnostic[]` with spans/codes/fixes, `Format`, `Resolve(id)` (effective object after inheritance/components, with provenance), `Catalog` (types, components, keywords, stdlib), `Explain(code|id)`, `Compile→IR`, `Vm.Run(ir, budget)` returning a trace. No console I/O inside.
2. **CLI** (`kedo`, dotnet tool): `check`, `fmt`, `resolve`, `catalog`, `explain`, `run --trace`, `fix --apply`, all with `--json` (single JSON document on stdout, exit 0/1/2; human text by default). Golden tests on the JSON schema. This alone makes Codex/Cursor/CI/agents-without-MCP productive.
3. **MCP server** (`kedo mcp`, stdio, `ModelContextProtocol` 2.2.x): thin adapter over the same core; stateless; explicit handles only for traces. Server instructions (< 4,096 chars) say *when* to use it.
4. **Skills + plugin**: `.agents/skills/kedo-*` (spec-only frontmatter) + Claude Code plugin wrapping skills, `.mcp.json`, and a `PostToolUse` check hook; corpus-derived golden examples; `llms.txt` for the docs site.
5. **Editor surface** (after language stabilizes): TextMate grammar (VS Code) + tree-sitter grammar (Zed/Neovim) generated from one token table; LSP (OmniSharp.Extensions or vendored Draco.Lsp) on the same core; Godot `CodeHighlighter` fed from the same token table.
6. **Eval harness**: 30–50 authoring tasks with verifiable outcomes (file must pass `check` and `resolve` to expected JSON), run with/without skills and with/without MCP, per Anthropic's tool-eval guidance. This is also how to decide whether grammar-in-prompt or examples-only works better for KEDO specifically (no external benchmark answers that).

### 7.2 MCP tool list (≤ 10 tools; names prefixed `kedo_`)

| Tool | Input | Output (`structuredContent`) | Hints |
|---|---|---|---|
| `kedo_check` | `{paths?: string[], content?: {path, text}[], max_diagnostics?: int (default 50), response_format?: "concise"\|"detailed"}` | `{ok, summary, diagnostics[], truncated, next_offset?}` (schema §4.2) | read-only, idempotent |
| `kedo_format` | `{path?, text?, check_only?: bool}` | `{formatted_text?, changed: bool, diagnostics[]}` | read-only (never writes); the agent writes |
| `kedo_apply_fix` | `{fix_ids: string[], expected_hashes: {path: sha256}}` | `{applied: [{path, edits}], skipped: [{fix_id, reason}], new_hashes}` | writes files; non-idempotent; refuses on hash mismatch |
| `kedo_resolve` | `{id: string, response_format?}` | `{id, type, effective: {...}, provenance: [{prop, file, line}], components[], base_chain[]}` | read-only |
| `kedo_catalog` | `{kind: "types"\|"components"\|"keywords"\|"functions"\|"objects", query?: string, limit?: int, cursor?}` | `{items: [{name, kind, summary, signature?, doc?}], next_cursor?}` | read-only; paginated |
| `kedo_explain` | `{code?: string, id?: string, keyword?: string}` | `{markdown}` | read-only; `maxResultSizeChars` modest |
| `kedo_references` | `{id, limit?, cursor?}` | `{refs: [{file, line, col, kind}], next_cursor?}` | read-only |
| `kedo_run_trace` | `{entry: string, inputs?: object, step_budget?: int (default 100k), max_events?: int}` | `{trace_id, status: "ok"\|"budget_exceeded"\|"error", events[], outputs, diagnostics[]}` | sandboxed; may be slow → consider `_meta` requiresUserInteraction only if plugins are enabled |
| `kedo_trace_events` | `{trace_id, offset, limit}` | `{events[], next_offset?}` | read-only; handle expires after N minutes (stated in description) |
| `kedo_new_object` (prompt, not tool) | `type, id` | prompt text with golden example | exposed as `/kedo:new-object` |
| Resources | `kedo://grammar`, `kedo://diagnostics/{code}`, `kedo://catalog/types` | Markdown/JSON | `@kedo:kedo://grammar` mentions |

Design rules: every text result also has a concise human line (agents without structured-output support); every list is paginated with a default page; errors in `isError` only for tool misuse (bad handle, missing file), never for KEDO diagnostics; all outputs sorted deterministically; `description` leads with when-to-use keywords ("KEDO content validate check diagnostics").

### 7.3 Top risks
1. **Spec churn**: MCP 2026-07-28 removed sessions/handshake and deprecated Roots/Sampling/Logging; clients lag (Claude Code supports both; Cursor/others unknown). Mitigation: C# SDK 2.2 hybrid mode, stateless design, no deprecated features.
2. **Context budget**: hosts without tool search load every definition each request. Mitigation: ≤ 10 tools, short descriptions, paginated outputs, `response_format`.
3. **Weak diagnostics → useless loops** (Olausson): if messages lack "what was expected", agents thrash. Mitigation: every diagnostic names valid alternatives and ships a fix where possible; corpus case per code.
4. **Model priors clash with KEDO syntax**: keywords that look like Python/C# but behave differently cause silent semantic errors. Mitigation: choose distinctive keywords, one-way syntax, `kedo_resolve` to show effective semantics, eval harness.
5. **Skill drift vs. language 1.x**: skills hard-coding examples that later fail. Mitigation: skills reference corpus files that CI validates; `!`kedo --version`` injection.
6. **Unsafe VM execution from agents**: Mitigation: step/memory/time budgets inside the VM, no I/O primitives, host timeout, treat output as untrusted.
7. **Three adapters drifting** (CLI/MCP/LSP): Mitigation: one core API with snapshot semantics; adapters contain no logic; shared JSON schema and golden tests.
8. **Editor fragmentation**: TextMate (VS Code) vs tree-sitter (Zed/Neovim) vs Godot's keyword-only `CodeHighlighter`. Mitigation: generate all three from one token/keyword table; defer LSP until after v1.

---

## Sources (primary, fetched this session)
- MCP versioning: https://modelcontextprotocol.io/specification/versioning
- MCP 2026-07-28 changelog: https://modelcontextprotocol.io/specification/2026-07-28/changelog
- MCP tools (2026-07-28): https://modelcontextprotocol.io/specification/2026-07-28/server/tools
- C# SDK repo/releases: https://github.com/modelcontextprotocol/csharp-sdk , https://github.com/modelcontextprotocol/csharp-sdk/releases
- Claude Code MCP reference: https://code.claude.com/docs/en/mcp
- Claude Code skills: https://code.claude.com/docs/en/skills ; plugins: https://code.claude.com/docs/en/plugins , https://code.claude.com/docs/en/plugins/components
- Anthropic, Writing effective tools for agents: https://anthropic.com/engineering/writing-tools-for-agents
- Agent Skills spec/home: https://agentskills.io/specification , https://agentskills.io/home
- Codex skills: https://learn.chatgpt.com/docs/build-skills ; Codex MCP: https://learn.chatgpt.com/docs/extend/mcp.md?surface=cli
- Cursor skills: https://cursor.com/docs/context/skills
- rustc JSON diagnostics: https://doc.rust-lang.org/rustc/json.html
- NuGet: https://www.nuget.org/packages/OmniSharp.Extensions.LanguageServer , https://www.nuget.org/packages/Draco.Lsp , https://www.nuget.org/packages/Microsoft.CommonLanguageServerProtocol.Framework ; CLaSP README: https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Microsoft.CommonLanguageServerProtocol.Framework/README.md
- Godot: https://docs.godotengine.org/en/stable/classes/class_codehighlighter.html , .../class_syntaxhighlighter.html , .../class_codeedit.html
- Zed languages: https://zed.dev/docs/extensions/languages ; nvim-treesitter: https://github.com/nvim-treesitter/nvim-treesitter/blob/main/README.md
- mcp-language-server: https://github.com/isaacphi/mcp-language-server ; Serena: https://github.com/oraios/serena ; GitHub MCP: https://github.com/github/github-mcp-server ; csharp-mcp: https://github.com/pvasek/csharp-mcp
- dotnet tools: https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools-how-to-create
- inklecate: https://raw.githubusercontent.com/inkle/ink/master/inklecate/CommandLineTool.cs ; Yarn Spinner docs: https://docs.yarnspinner.dev/llms-full.txt
- Papers (abstract-level via search): Grammar Prompting https://arxiv.org/abs/2305.19234 ; DocCGen https://arxiv.org/abs/2406.11925 ; MTOB critique https://arxiv.org/abs/2409.19151 ; SynCode https://arxiv.org/abs/2403.01632 ; Self-Repair https://arxiv.org/abs/2306.09896 ; JSONSchemaBench https://arxiv.org/abs/2501.10868 ; ConCodeEval https://arxiv.org/html/2407.03387v1 ; Let Me Speak Freely (EMNLP 2024 Industry, 2408.02442); Esoteric languages ICL (NeurIPS 2025); Hidden Cost of Readability https://arxiv.org/abs/2508.13666
