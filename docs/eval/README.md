# Evaluación de agentes con KEDO

Estado: vigente desde el 17/10/2026 (issue [#17](https://github.com/Kaux-Studios/KEDO/issues/17); anexo G §7.1, punto 6).

`eval/tasks.json` tiene 30 tareas de autoría sobre la muestra Outpost: agregar salas, personajes y crisis, heredar en vez de copiar, sobrescribir valores de mixins, agregar y quitar elementos de listas heredadas, opciones ancladas al final, variantes, assets, hooks y funciones, y corregir errores que informa `kedo check`. Cada tarea trae una solución de referencia y una lista de comprobaciones que se evalúan sobre el catálogo resuelto y la VM; ninguna depende de cómo se escribió la respuesta, sólo de su efecto.

## Comandos

```powershell
# El suite en sí: cada solución de referencia pasa (y las pruebas verifican que ninguna tarea pasa sin cambios).
kedo eval eval/tasks.json --reference

# Un agente: el comando recibe {prompt_file} (TASK.md en el espacio de trabajo), {prompt} y {workspace}.
kedo eval eval/tasks.json --agent="claude -p --permission-mode acceptEdits < {prompt_file}" --json > sin-ayuda.json

# Las cuatro configuraciones del anexo G: con o sin skills, con o sin servidor MCP.
kedo eval eval/tasks.json --agent="..." --skills=.agents/skills            > skills.json
kedo eval eval/tasks.json --agent="..." --mcp                              > mcp.json
kedo eval eval/tasks.json --agent="..." --skills=.agents/skills --mcp      > ambos.json
```

`--skills=<dir>` copia las skills al espacio de trabajo (`.claude/skills` y `.agents/skills`); `--mcp` escribe un `.mcp.json` que levanta `kedo mcp` con el mismo ejecutable. Otras opciones: `--only=id1,id2`, `--workdir=<dir>`, `--keep` (no borra los espacios de trabajo; el del agente guarda `agent.log`), `--timeout=<segundos>` (600 por defecto), `--json`.

Correr un agente real consume su cuota o su API: no forma parte de la CI. La CI corre `EvalTests`, que comprueba el suite con las soluciones de referencia.

## Formato de una tarea

```json
{
  "id": "add-tag",
  "prompt": "Tag the greenhouse as `organic` without losing any tag it already has.",
  "setup": [ { "file": "rooms.kedo", "find": "...", "replace": "..." } ],
  "solution": [ { "file": "rooms.kedo", "find": "    kind = greenhouse\n", "replace": "    kind = greenhouse\n    tags += [organic]\n" } ],
  "checks": [
    { "kind": "clean" },
    { "kind": "value", "key": "room:greenhouse", "path": "tags", "expected": "[interior, pressurized, organic]" }
  ]
}
```

Pasos (`setup`, `solution`): `content` (archivo entero), `append` (al final) o `find`/`replace` (primera aparición). El espacio de trabajo parte de una copia de `base` (contenido y su esquema en `schema/`).

Comprobaciones:

| `kind` | Pasa cuando |
|---|---|
| `clean` | Parseo, resolución, validación y compilación de hooks sin errores |
| `formatted` | Todos los archivos están en forma canónica |
| `value` | El valor en `path` (`campo.sub`, `componente.campo`, `lista[clave].campo`) es el literal `expected` |
| `present` | Existe un valor en `path` |
| `layer` | El valor viene de la capa `expected`: `own`, `parent`, `mixin`, `default`, `variant` |
| `exists`, `absent` | El objeto `key` existe o no |
| `parent`, `mixin` | El objeto extiende `expected` o usa el mixin `expected` |
| `keys` | Las claves de la lista en `path`, en orden, son `expected` (`patch,vent,wait`) |
| `no_component` | El objeto no tiene el componente `expected` |
| `compiles` | El objeto tiene comportamiento compilado sin errores K5 |
| `effect`, `no_effect` | Al correr `hook` con `context` (enteros o símbolos `tipo:id`) y `queries`, se aplica (o no) un efecto que contiene `expected` |

Toda comprobación admite `"variant": "<nombre>"` para mirar la vista de una variante (ADR-0012).

## Herramientas para agentes que mide el suite

El servidor MCP (`kedo mcp`) ofrece `kedo_check`, `kedo_format`, `kedo_resolve`, `kedo_describe`, `kedo_explain`, `kedo_catalog`, `kedo_compile`, `kedo_run_trace` y `kedo_schema`; el recurso `kedo://grammar` (la skill de sintaxis) y el prompt `new-object`. Las skills están en `.agents/skills` (copia para Claude Code en `.claude/skills`).
