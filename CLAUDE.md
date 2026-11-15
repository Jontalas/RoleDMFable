# RoleDMFable

Mesa de rol virtual 2D con máster IA. Godot 4.7.1 (.NET) + C# sobre .NET 10 (LTS; .NET 8 caducó en noviembre de 2026). El usuario habla español y no tiene experiencia en desarrollo de juegos: textos de interfaz, README y mensajes en español.

## Reglas de arquitectura

- `core/` es C# puro, sin referencias a Godot, y es la única autoridad sobre el estado (posiciones, puntos de vida, tiradas). La interfaz, la IA y la red solo le piden cambios. Todo lo que tenga lógica va aquí y lleva pruebas en `tests/`.
- `master/` es C# puro y habla con Claude (paquete NuGet `Anthropic`). Cada turno es una conversación nueva con las instrucciones fijas, el estado de la mesa y el registro reciente (`MasterPrompt`); las herramientas las define y ejecuta `MasterToolbox` en `core/`. Se prueba contra un servidor local de imitación, sin clave. La clave del usuario vive en `user://ajustes.json`: nunca en el repositorio ni en los registros.
- `rules/` es C# puro y lee reglamentos (PDF con PdfPig, .docx, texto) a `Rulebook`; el troceado en pasajes y el buscador BM25 (`RulesLibrary`) están en `core/Rules.cs`. Los libros leídos se guardan en `user://reglamentos/`. Nunca subir manuales con derechos de autor al repositorio: las pruebas usan los ejemplos inventados de `tests/samples/`.
- `game/` solo dibuja y recoge la entrada. La interfaz se construye en código (`Main.cs`, `Ui.cs`), no en escenas `.tscn` editadas a mano.
- El combate (`CombatState`: ronda, orden, turno) y las hojas de personaje (`SheetEntry`, datos libres nombre/valor para no atarse a un reglamento) viven en `TableState` y se guardan con la escena.
- La historia (`core/Story.cs`, `StoryState` en `Campaign`): escenas, objetivos, PNJ, notas, cuentas atrás, oráculo de sí/no con d10+d8+d6, eventos inesperados y puntos de historia hasta el punto de giro. Es un motor propio inspirado en los juegos de rol en solitario con oráculo; las tablas están redactadas de cero. Los principios de dirección del máster viven en `MasterPrompt.Instructions`, escritos con palabras propias.
- Los libros del usuario (reglamentos y guías de dirección) son obras con derechos de autor: se cargan en su PC (`BookKind.Rules` / `BookKind.Guide`) y nunca se copian al repositorio, ni su texto ni sus tablas literales.
- El mapa es siempre visible y se genera a partir de datos (`SceneSpec`), nunca de una imagen suelta: así lo que narra el máster y lo que se ve coinciden. Se usa igual dentro y fuera de combate.
- El máster construye los lugares de la aventura con herramientas (`crear_lugar` con un plano de texto `. # ~ = o`, `ir_a_lugar`, `poner_ficha`, `traer_ficha`, `quitar_ficha`, `cambiar_terreno`, `revelar_zona`): todo pasa por `Campaign`/`TableState`, que validan antes de tocar la partida.
- Una partida propia nace en `CampaignSetup` (`core/Setup.cs`): nombre, idea de la aventura y héroes con lo mínimo (nombre, qué es, aspecto, si lo lleva un jugador). No pide nada que dependa de un reglamento: la hoja y los puntos de vida los completa el máster con el jugador según las reglas cargadas (`anotar_ficha`, `fijar_pv_maximos`). El grupo empieza en «Punto de partida» con los héroes sin crear (`Token.Created`): la creación es obligatoria y `crear_lugar`/`ir_a_lugar` se niegan hasta que el máster marque a todos con `personaje_listo`. Entonces abre la aventura creando el primer lugar.
- Los personajes viven fuera de la partida (`core/Heroes.cs`: `SavedHero`, `HeroStore`, carpeta `user://personajes/`): al guardar una partida propia, cada héroe ya creado se copia a su archivo con su hoja y sus PV máximos, y en «Nueva partida» se pueden elegir para otra aventura, donde entran ya creados. Un héroe nuevo no puede pisar a uno guardado con el mismo nombre.
- La IA no calcula números ni tira dados: pedirá acciones mediante herramientas y `core/` las ejecuta.
- Estilo visual: contorno grueso `Ui.Ink`, colores planos, tipografías Baloo 2 (títulos) y Nunito (texto). Usar los ayudantes de `Ui.cs`.

## Comandos

```bash
dotnet test tests                      # pruebas de core, master y rules
dotnet build RoleDMFable.csproj        # compilar el juego
godot --headless --import              # tras añadir recursos
# Captura para revisar el aspecto (necesita pantalla; en Linux sin ella, xvfb-run):
godot --rendering-driver opengl3 --resolution 1440x900 -- --captura=/ruta/a.png [--arrastrar] [--viajar] [--ajustes] [--api=URL --decir="texto"] [--reglas="a.pdf;b.docx"] [--combate [--ficha]] [--historia] [--lugar] [--nueva] [--propia]
```
