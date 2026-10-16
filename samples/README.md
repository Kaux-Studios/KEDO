# Muestras

## outpost

Mini-juego ficticio: una estación aislada con salas (`room`), tripulación (`crew`) y crisis con opciones (`crisis`). Tiene su propio esquema cerrado (`schema/outpost.kedo`), mixins, herencia de valores, componentes, borrados, listas keyed, una función y hooks con esperas de tiempo, decisión y tirada. No comparte vocabulario con Patriark: sirve para comprobar que el núcleo no filtra supuestos de un juego y como ejemplo dorado para personas y agentes.

```
dotnet run --project src/Kedo.Cli -- check --schema=samples/outpost/schema samples/outpost
dotnet run --project src/Kedo.Cli -- compile --schema=samples/outpost/schema samples/outpost
dotnet run --project src/Kedo.Cli -- build --schema=samples/outpost/schema samples/outpost --out=outpost.kpk
```

`tests/Kedo.Tests/SamplesTests.cs` valida, compila, empaqueta y ejecuta un hook de la muestra en cada corrida de tests.
