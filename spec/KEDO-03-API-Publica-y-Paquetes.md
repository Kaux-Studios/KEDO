# KEDO-03 — API pública, paquetes y versiones

Revisado: 15/10/2026 · Estado: vigente (issue [#12](https://github.com/Kaux-Studios/KEDO/issues/12)). Mecanismos que hacen cumplir [ADR-0001](adr/0001-versionado-semver.md) y [ADR-0002](adr/0002-repositorio-aislado.md).

## Paquetes

| Paquete | Contenido | Quién lo consume | Superficie |
|---|---|---|---|
| `Kedo.Core` | Modelo de valores, diagnósticos, lector del pack compilado, VM determinista, interfaces del host | El juego en runtime | **Mínima y estricta.** Todo `sealed` o `static` salvo puntos de extensión diseñados; sin miembros `virtual` nuevos tras 1.0 |
| `Kedo.Compiler` | Lexer, parser, AST, resolver, formateador, esquema, escritor del pack | Herramientas del juego, CLI, addon, MCP | Más amplia; evoluciona más rápido, siempre aditiva dentro del major |
| `kedo` (tool `Kedo.Cli`) | CLI `parse check fmt build schema explain mcp` | Personas, CI, editor, agentes | Comandos y `--json` estables dentro del major |
| `Kedo.Tooling` (futuro) | LSP, servidor MCP como biblioteca | Editores | — |
| `Kedo.Generators` (futuro) | Generador de código para registros | Juegos | — |

Razón de la división (anexo F §6.2): lo que el juego embarca en el dispositivo (`Kedo.Core`) debe ser pequeño y casi inmutable; lo que corre en herramientas puede crecer.

## Tres números de versión

| Versión | Dónde | Regla |
|---|---|---|
| Paquete NuGet (SemVer) | `Directory.Build.props`, tags `vX.Y.Z` | Major = incompatibilidad; minor y patch aditivos |
| Lenguaje | `kedo N` en cada archivo, `KedoVersion.LanguageMajor` | El compilador N acepta lenguaje N; `kedo migrate` entre majors |
| Formato del pack compilado | Cabecera del pack (`formatVersion`) | El runtime N carga formatos ≤ N; un artefacto de otro major se rechaza y se recompila desde fuente (la fuente es la verdad, el pack es caché) |

Prerelease durante la inicialización: `1.0.0-alpha.N`. Sin garantía entre alphas. La garantía empieza en `1.0.0`.

## Mecanismos en el repositorio

1. **`Microsoft.CodeAnalysis.PublicApiAnalyzers`** (3.3.4, compatible con el compilador del SDK 8) en todo proyecto empaquetable: cada miembro público se declara en `PublicAPI.Shipped.txt` (publicado) o `PublicAPI.Unshipped.txt` (pendiente de publicar). RS0016 (miembro no declarado) y RS0017 (miembro eliminado) son errores por `TreatWarningsAsErrors`. Un cambio de API es un diff revisable en esos archivos. Al publicar una versión, lo de `Unshipped` pasa a `Shipped`.
2. **Package validation** (`EnablePackageValidation`) en `dotnet pack`: compara con `PackageValidationBaselineVersion` (se fija al publicar `1.0.0`); en ramas de servicio, `EnableStrictModeForBaselineValidation`. Rupturas intencionales (solo en un nuevo major) se registran con `ApiCompatGenerateSuppressionFile`.
3. **Reglas de cambio** (dotnet/runtime `breaking-change-rules.md`): permitido añadir miembros, implementaciones de interfaz, ampliar visibilidad; prohibido quitar o renombrar, cambiar parámetros, añadir miembros abstractos a tipos extensibles, cambiar struct↔class, cambiar valores de enum, añadir `virtual`/`static`.
4. **Golden corpus**: los casos de `tests/corpus` nunca se borran dentro de un major; un cambio de salida en minor es error.
5. **Release por tag**: `git tag vX.Y.Z` dispara `.github/workflows/release.yml`: build, tests, `pack` (con validación), push a GitHub Packages de `Kaux-Studios` (privado, `GITHUB_TOKEN`) y GitHub Release con los `.nupkg`.

## Consumo desde un juego

`nuget.config` del juego:

```xml
<configuration>
  <packageSources>
    <add key="kaux" value="https://nuget.pkg.github.com/Kaux-Studios/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <kaux>
      <add key="Username" value="%KAUX_NUGET_USER%" />
      <add key="ClearTextPassword" value="%KAUX_NUGET_TOKEN%" />
    </kaux>
  </packageSourceCredentials>
</configuration>
```

Referencia con rango de major: `<PackageReference Include="Kedo.Core" Version="[1.0.0,2.0.0)" />`. Durante alpha, Patriark puede usar `<ProjectReference>` a `C:\dev\KEDO` mediante una propiedad condicional; al primer uso en `main`, paquete.

Credenciales: token personal con permiso `read:packages`, fuera de Git (variables de entorno), como manda `CONTRIBUTING.md` de Patriark.

## Decisión sobre el riesgo "1.0 demasiado pronto"

El anexo F recomienda quedarse en 0.x hasta tener dos juegos. El creador fijó 1.0 para Patriark. Mitigación adoptada: `Kedo.Core` nace mínimo (lo que el runtime necesita y nada más); todo lo discutible vive en `Kedo.Compiler`, cuya superficie puede crecer sin romper; `1.0.0` se publica recién cuando Patriark consuma KEDO en `main`, no antes.
