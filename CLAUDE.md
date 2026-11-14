# RoleDMFable

Mesa de rol virtual 2D con máster IA. Godot 4.7.1 (.NET) + C# sobre .NET 10 (LTS; .NET 8 caducó en noviembre de 2026). El usuario habla español y no tiene experiencia en desarrollo de juegos: textos de interfaz, README y mensajes en español.

## Reglas de arquitectura

- `core/` es C# puro, sin referencias a Godot, y es la única autoridad sobre el estado (posiciones, puntos de vida, tiradas). La interfaz, la IA y la red solo le piden cambios. Todo lo que tenga lógica va aquí y lleva pruebas en `tests/`.
- `master/` es C# puro y habla con Claude (paquete NuGet `Anthropic`). Cada turno es una conversación nueva con las instrucciones fijas, el estado de la mesa y el registro reciente (`MasterPrompt`); las herramientas las define y ejecuta `MasterToolbox` en `core/`. Se prueba contra un servidor local de imitación, sin clave. La clave del usuario vive en `user://ajustes.json`: nunca en el repositorio ni en los registros.
- `rules/` es C# puro y lee reglamentos (PDF con PdfPig, .docx, texto) a `Rulebook`; el troceado en pasajes y el buscador BM25 (`RulesLibrary`) están en `core/Rules.cs`. Los libros leídos se guardan en `user://reglamentos/`. Nunca subir manuales con derechos de autor al repositorio: las pruebas usan los ejemplos inventados de `tests/samples/`.
- `game/` solo dibuja y recoge la entrada. La interfaz se construye en código (`Main.cs`, `Ui.cs`), no en escenas `.tscn` editadas a mano.
- El mapa es siempre visible y se genera a partir de datos (`SceneSpec`), nunca de una imagen suelta: así lo que narra el máster y lo que se ve coinciden. Se usa igual dentro y fuera de combate.
- La IA no calcula números ni tira dados: pedirá acciones mediante herramientas y `core/` las ejecuta.
- Estilo visual: contorno grueso `Ui.Ink`, colores planos, tipografías Baloo 2 (títulos) y Nunito (texto). Usar los ayudantes de `Ui.cs`.

## Comandos

```bash
dotnet test tests                      # pruebas de core, master y rules
dotnet build RoleDMFable.csproj        # compilar el juego
godot --headless --import              # tras añadir recursos
# Captura para revisar el aspecto (necesita pantalla; en Linux sin ella, xvfb-run):
godot --rendering-driver opengl3 --resolution 1440x900 -- --captura=/ruta/a.png [--arrastrar] [--viajar] [--ajustes] [--api=URL --decir="texto"] [--reglas="a.pdf;b.docx"]
```
