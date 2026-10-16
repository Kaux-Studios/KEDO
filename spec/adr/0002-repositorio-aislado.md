# ADR-0002 — Repositorio aislado, consumido como paquete

Fecha: 15/10/2026 · Estado: aceptado · Decisor: creador (Kaux Studios).

## Contexto

El lenguaje nació como necesidad de Project Patriark (issue #3: base arquitectónica de Game Data Objects). El creador quiere un lenguaje orientado a sus necesidades, reutilizable en proyectos futuros, y mantenerlo aislado del juego.

## Decisión

- KEDO vive en `Kaux-Studios/KEDO` (privado) con su propio backlog, CI y versiones.
- Se consume como paquete NuGet privado (GitHub Packages) `Kedo.Core`, `Kedo.Runtime`, herramienta `kedo` (`dotnet tool`), y el addon de Godot como carpeta versionada.
- Durante la fase alpha, Patriark puede referenciar los proyectos por ruta local para iterar; al primer uso en `main` de Patriark, pasa a paquete.
- **Frontera:** nada específico de un juego entra en KEDO. Lo que necesita un juego se resuelve con puntos de extensión (tipos, componentes, hooks, validadores, fichas). `samples/` contiene un mini-juego ficticio para probar que el núcleo no filtra supuestos de Patriark.
- Patriark es el **primer corpus de pruebas**: su contenido convertido se ejecuta en la CI de KEDO como test externo, con la versión de Patriark fijada.

## Consecuencias

- Dos repos en paralelo al principio: cada cambio de gramática puede romper la conversión de Patriark. Se mitiga con el corpus en CI y con la política de ADR-0001.
- Riesgo de generalizar para proyectos hipotéticos: toda capacidad entra con un caso de uso real (Patriark o el sample) y un test.
- Licencia propietaria de Kaux Studios, igual que Patriark.
