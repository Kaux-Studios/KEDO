# Desarrollo de KEDO

Proyecto propietario de Kaux Studios; estas pautas son para colaboradores autorizados.

## Entorno

.NET SDK según `global.json`. Restaurar con `dotnet restore --locked-mode` y conservar los `packages.lock.json`. Para el addon de Godot, Godot 4.6.x (sin .NET: el addon es GDScript).

## Antes de modificar

- Leer `AGENTS.md`, `spec/KEDO-00-Principios-y-Alcance.md` y los ADR.
- Un cambio de gramática, AST, IR o API pública empieza por la spec y, si cambia una decisión, por un ADR.
- Un cambio incompatible solo entra en una rama de siguiente major.

## Verificar

```powershell
dotnet restore --locked-mode
dotnet build Kedo.sln -c Release
dotnet test tests/Kedo.Tests/Kedo.Tests.csproj
```

Al tocar parser, resolver o VM: correr los benchmarks (cuando existan en `tests/Kedo.Benchmarks`) y comparar con el último resultado registrado.

## Registrar

Describir en el commit qué cambió y qué se verificó. Actualizar spec, docs y `PublicAPI.Shipped.txt` en el mismo cambio. La historia queda en Git; no agregar bitácoras fechadas en los documentos.
