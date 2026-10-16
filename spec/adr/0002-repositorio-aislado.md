# ADR-0002 — Repositorio aislado, consumido como paquete

Fecha: 15/10/2026 · Estado: aceptado · Decisor: creador (Kaux Studios).

## Contexto

El lenguaje nació como necesidad de Project Patriark (issue #3: base arquitectónica de Game Data Objects). El creador quiere un lenguaje orientado a sus necesidades, reutilizable en proyectos futuros, y mantenerlo aislado del juego.

## Decisión

- KEDO vive en `Kaux-Studios/KEDO` (privado hasta el 16/10/2026, ver la enmienda) con su propio backlog, CI y versiones.
- Se consume como paquete NuGet privado (GitHub Packages) `Kedo.Core`, `Kedo.Runtime`, herramienta `kedo` (`dotnet tool`), y el addon de Godot como carpeta versionada.
- Durante la fase alpha, Patriark puede referenciar los proyectos por ruta local para iterar; al primer uso en `main` de Patriark, pasa a paquete.
- **Frontera:** nada específico de un juego entra en KEDO. Lo que necesita un juego se resuelve con puntos de extensión (tipos, componentes, hooks, validadores, fichas). `samples/` contiene un mini-juego ficticio para probar que el núcleo no filtra supuestos de Patriark.
- Patriark es el **primer corpus de pruebas**: su contenido convertido se ejecuta en la CI de KEDO como test externo, con la versión de Patriark fijada.

## Consecuencias

- Dos repos en paralelo al principio: cada cambio de gramática puede romper la conversión de Patriark. Se mitiga con el corpus en CI y con la política de ADR-0001.
- Riesgo de generalizar para proyectos hipotéticos: toda capacidad entra con un caso de uso real (Patriark o el sample) y un test.
- Licencia propietaria de Kaux Studios, igual que Patriark.

## Enmienda (16/10/2026): repositorio público

- `Kaux-Studios/KEDO` pasa a ser **público** para que la CI de los juegos clone o restaure KEDO sin credenciales. La licencia no cambia: el código es propietario y verlo no concede permisos.
- Antes de publicarlo se retiró del repositorio y de su historial todo el contenido privado de Patriark: el pack real importado como corpus, el mapa de su código (anexo C) y la síntesis de su investigación. El historial público se reescribió sin esos archivos; el repositorio anterior quedó archivado y privado. Queda [01 — Origen](../../docs/research/01-Origen.md) con lo genérico.
- El "primer corpus de pruebas" se invierte: la CI de Patriark compila su contenido real con KEDO fijado a un commit (y, desde `1.0.0-alpha.1`, a un paquete). Un cambio de KEDO que rompa a Patriark se detecta allí al actualizar el commit fijado; en KEDO lo cubren el corpus sintético, `samples/outpost` y los ejemplos de la spec.
- Hasta `1.0.0-alpha.1` (#18) el consumo es por código fuente (clon hermano o propiedad `KedoRoot`), no por paquete.
