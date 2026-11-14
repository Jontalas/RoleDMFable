using Godot;
using RoleDMFable.Core;

namespace RoleDMFable;

/// <summary>
/// Dibuja la mesa a partir del <see cref="TableState"/> y deja arrastrar las fichas.
/// No guarda posiciones propias: cada movimiento se le pide al estado, que decide si vale.
/// </summary>
public partial class MapView : Control
{
    private TableState? _table;
    private string? _dragId;
    private string? _selectedId;
    private Vector2 _mouse;
    private Font _labelFont = null!;

    /// <summary>Ficha movida: id y pies recorridos en línea recta.</summary>
    public event Action<string, int>? TokenMoved;

    /// <summary>Movimiento rechazado: id y motivo ya redactado.</summary>
    public event Action<string, string>? MoveRefused;

    public event Action<string?>? SelectionChanged;

    public override void _Ready()
    {
        _labelFont = Ui.Body(800);
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.Click;
        ClipContents = true;
    }

    public void SetTable(TableState table)
    {
        if (_table is not null) _table.Changed -= QueueRedraw;
        _table = table;
        _table.Changed += QueueRedraw;
        _dragId = null;
        _selectedId = null;
        QueueRedraw();
    }

    public void Select(string? tokenId)
    {
        if (_selectedId == tokenId) return;
        _selectedId = tokenId;
        SelectionChanged?.Invoke(tokenId);
        QueueRedraw();
    }

    public override void _ExitTree()
    {
        if (_table is not null) _table.Changed -= QueueRedraw;
    }

    // ---------- Geometría ----------

    /// <summary>Altura de los muros en pantalla, en casillas: lo que da el aire de maqueta con volumen.</summary>
    private const float WallRise = 0.32f;

    private float Cell => _table is null
        ? 0
        : Mathf.Floor(Mathf.Min((Size.X - 16) / _table.Width, (Size.Y - 20) / (_table.Height + WallRise)));

    private Vector2 Origin => _table is null
        ? Vector2.Zero
        : ((Size - new Vector2(_table.Width, _table.Height - WallRise) * Cell) / 2f - new Vector2(0, 3)).Floor();

    private Rect2 CellRect(int x, int y) => new(Origin + new Vector2(x, y) * Cell, new Vector2(Cell, Cell));

    public Vector2 CellCenter(int x, int y) => Origin + new Vector2(x + 0.5f, y + 0.5f) * Cell;

    private Vector2I CellAt(Vector2 point)
    {
        var local = (point - Origin) / Cell;
        return new Vector2I(Mathf.FloorToInt(local.X), Mathf.FloorToInt(local.Y));
    }

    // ---------- Entrada ----------

    public override void _GuiInput(InputEvent @event)
    {
        if (_table is null || Cell <= 0) return;

        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            _mouse = button.Position;
            var cell = CellAt(button.Position);
            if (button.Pressed)
            {
                var token = _table.InBounds(cell.X, cell.Y) && !_table.IsUnexplored(cell.X, cell.Y)
                    ? _table.TokenAt(cell.X, cell.Y)
                    : null;
                Select(token?.Id);
                _dragId = token?.Id;
            }
            else if (_dragId is not null)
            {
                var id = _dragId;
                _dragId = null;
                var result = _table.MoveToken(id, cell.X, cell.Y);
                if (!result.Ok) MoveRefused?.Invoke(id, result.Explain());
                else if (result.Feet > 0) TokenMoved?.Invoke(id, result.Feet);
            }
            QueueRedraw();
            AcceptEvent();
        }
        else if (@event is InputEventMouseMotion motion)
        {
            _mouse = motion.Position;
            if (_dragId is not null) QueueRedraw();
        }
    }

    // ---------- Dibujo ----------
    //
    // La mesa se ve desde arriba, pero los muros tienen altura, el agua está hundida y las
    // figuritas están de pie. Se pinta de fondo a frente, fila a fila, para que lo que está
    // más cerca tape a lo que está detrás.

    private static readonly Color Shade = new("1d1712");

    public override void _Draw()
    {
        if (_table is null || Cell <= 4) return;
        var cell = Cell;
        var rise = cell * WallRise;
        var board = new Rect2(Origin, new Vector2(_table.Width, _table.Height) * cell);
        var line = Mathf.Max(3f, cell / 15f);

        // Sombra dura y marco del tablero (con sitio arriba para la altura de los muros).
        var frame = new Rect2(board.Position - new Vector2(line, line + rise), board.Size + new Vector2(line * 2, line * 2 + rise));
        DrawRect(new Rect2(frame.Position + new Vector2(0, 6), frame.Size), Ui.Ink);
        DrawRect(frame, Ui.Ink);
        DrawRect(new Rect2(board.Position - new Vector2(0, rise), new Vector2(board.Size.X, rise)), Shade);

        // 1. Todo lo que está a ras de suelo.
        for (var y = 0; y < _table.Height; y++)
        for (var x = 0; x < _table.Width; x++)
            DrawGround(x, y, cell, line);

        foreach (var exit in _table.Exits)
            for (var x = exit.X; x < exit.X + exit.W; x++)
            for (var y = exit.Y; y < exit.Y + exit.H; y++)
                if (!_table.IsUnexplored(x, y))
                    DrawExit(x, y, cell, line);

        for (var y = 0; y < _table.Height; y++)
        for (var x = 0; x < _table.Width; x++)
            DrawCastShadows(x, y, cell);

        // Charcos de luz cálida bajo las antorchas.
        for (var y = 0; y < _table.Height; y++)
        for (var x = 1; x < _table.Width; x += 3)
        {
            if (!IsWall(x, y) || !HasTorch(x, y)) continue;
            var pool = Origin + new Vector2((x + 0.5f) * cell, (y + 1.4f) * cell);
            for (var i = 0; i < 5; i++)
                Fill(Ellipse(pool, cell * (1.5f - i * 0.25f), cell * (0.95f - i * 0.16f), 32), new Color("ffc45a", 0.07f));
        }

        DrawFog(cell, line, rise);

        var dragged = _dragId is null ? null : _table.FindToken(_dragId);
        if (dragged is not null) DrawDragPreview(dragged, cell, line);

        // 2. Lo que tiene altura, de la fila del fondo a la de delante.
        for (var y = 0; y < _table.Height; y++)
        {
            for (var x = 0; x < _table.Width; x++)
            {
                if (_table.IsUnexplored(x, y)) continue;
                var terrain = _table.TerrainAt(x, y);
                if (terrain == Terrain.Wall)
                {
                    DrawWall(x, y, cell, line, rise);
                    if (x % 3 == 1 && HasTorch(x, y)) DrawTorch(x, y, cell, line, rise);
                }
                else if (terrain == Terrain.Rock) DrawRock(CellCenter(x, y), cell, line);
            }
            foreach (var token in _table.Tokens.Where(t => t.Y == y).OrderBy(t => t.X))
            {
                if (token == dragged || _table.IsUnexplored(token.X, token.Y)) continue;
                DrawToken(token, CellCenter(token.X, token.Y), cell, line, token.Id == _selectedId);
            }
        }

        if (dragged is not null) DrawToken(dragged, _mouse, cell, line, true, lifted: true);
    }

    // ---------- Ayudantes de forma ----------

    private static Vector2[] Ellipse(Vector2 center, float rx, float ry, int points = 28, float from = 0, float to = Mathf.Tau)
    {
        var result = new Vector2[points];
        for (var i = 0; i < points; i++)
        {
            var angle = from + (to - from) * i / (points - (to - from >= Mathf.Tau - 0.001f ? 0 : 1));
            result[i] = center + new Vector2(Mathf.Cos(angle) * rx, Mathf.Sin(angle) * ry);
        }
        return result;
    }

    private void Fill(Vector2[] points, Color color)
    {
        if (points.Length >= 3) DrawColoredPolygon(points, color);
    }

    private void Outline(Vector2[] points, float width, bool closed = true)
    {
        DrawPolyline(closed ? points.Append(points[0]).ToArray() : points, Ui.Ink, width, true);
    }

    private void Shape(Vector2[] points, Color color, float width)
    {
        Fill(points, color);
        Outline(points, width);
    }

    /// <summary>Rectángulo con degradado de arriba abajo.</summary>
    private void GradientDown(Rect2 rect, Color top, Color bottom)
    {
        DrawPolygon(
            new[] { rect.Position, rect.Position + new Vector2(rect.Size.X, 0), rect.End, rect.Position + new Vector2(0, rect.Size.Y) },
            new[] { top, top, bottom, bottom });
    }

    /// <summary>Rectángulo con degradado de izquierda a derecha.</summary>
    private void GradientRight(Rect2 rect, Color left, Color right)
    {
        DrawPolygon(
            new[] { rect.Position, rect.Position + new Vector2(rect.Size.X, 0), rect.End, rect.Position + new Vector2(0, rect.Size.Y) },
            new[] { left, right, right, left });
    }

    private static uint Hash(int x, int y) => (uint)(x * 73856093 ^ y * 19349663) * 1664525u + 1013904223u;

    private bool Is(int x, int y, Terrain terrain) => _table!.InBounds(x, y) && _table.TerrainAt(x, y) == terrain;

    private bool IsWet(int x, int y) => Is(x, y, Terrain.Water) || Is(x, y, Terrain.Bridge);

    /// <summary>Un muro que se ve: existe y ya está explorado.</summary>
    private bool IsWall(int x, int y) => Is(x, y, Terrain.Wall) && !_table!.IsUnexplored(x, y);

    // ---------- Suelo, agua y puente ----------

    private void DrawGround(int x, int y, float cell, float line)
    {
        var terrain = _table!.TerrainAt(x, y);
        var rect = CellRect(x, y);
        switch (terrain)
        {
            case Terrain.Water:
                DrawWater(x, y, rect, cell, line);
                break;
            case Terrain.Bridge:
                DrawWater(x, y, rect, cell, line);
                DrawBridge(x, y, rect, cell, line);
                break;
            default:
                DrawSlab(x, y, rect, cell);
                break;
        }
    }

    /// <summary>Losa de piedra con bisel: luz arriba a la izquierda, sombra abajo a la derecha.</summary>
    private void DrawSlab(int x, int y, Rect2 rect, float cell)
    {
        var seed = Hash(x, y);
        var tone = ((seed >> 8) % 7 - 3f) * 0.014f;
        var stone = new Color("cdbf9f");
        stone = tone >= 0 ? stone.Lightened(tone) : stone.Darkened(-tone);
        DrawRect(rect, stone);

        var bevel = Mathf.Max(2f, cell / 26f);
        DrawRect(new Rect2(rect.Position, new Vector2(cell, bevel)), stone.Lightened(0.22f));
        DrawRect(new Rect2(rect.Position, new Vector2(bevel, cell)), stone.Lightened(0.14f));
        DrawRect(new Rect2(rect.Position + new Vector2(0, cell - bevel), new Vector2(cell, bevel)), stone.Darkened(0.2f));
        DrawRect(new Rect2(rect.Position + new Vector2(cell - bevel, 0), new Vector2(bevel, cell)), stone.Darkened(0.14f));

        // Motas y alguna grieta, siempre las mismas en cada casilla.
        for (var i = 0; i < 2; i++)
        {
            seed = seed * 1664525u + 1013904223u;
            var px = (seed >> 8) % 1000 / 1000f;
            seed = seed * 1664525u + 1013904223u;
            var py = (seed >> 8) % 1000 / 1000f;
            DrawCircle(rect.Position + new Vector2(0.18f + px * 0.64f, 0.18f + py * 0.64f) * cell, cell * 0.03f, stone.Darkened(0.13f));
        }
        if ((seed >> 12) % 6 == 0)
        {
            var start = rect.Position + new Vector2(cell * 0.2f, cell * 0.72f);
            DrawPolyline(new[] { start, start + new Vector2(cell * 0.14f, -cell * 0.1f), start + new Vector2(cell * 0.2f, -cell * 0.02f), start + new Vector2(cell * 0.32f, -cell * 0.13f) },
                stone.Darkened(0.22f), Mathf.Max(1.5f, cell / 40f), true);
        }
    }

    /// <summary>Agua hundida: se ve la orilla de piedra por arriba y la sombra que cae dentro.</summary>
    private void DrawWater(int x, int y, Rect2 rect, float cell, float line)
    {
        GradientDown(rect, new Color("2f5a78"), new Color("23445d"));

        var wave = new Color("7fb9d3");
        var width = Mathf.Max(2f, cell / 18f);
        var phase = (x + y) % 2 == 0 ? 0.42f : 0.68f;
        var start = rect.Position + new Vector2(cell * 0.2f, cell * phase);
        DrawArc(start + new Vector2(cell * 0.15f, 0), cell * 0.15f, Mathf.Pi, Mathf.Tau, 10, wave, width, true);
        DrawArc(start + new Vector2(cell * 0.45f, 0), cell * 0.15f, 0, Mathf.Pi, 10, wave, width, true);
        DrawLine(rect.Position + new Vector2(cell * 0.62f, cell * (phase - 0.24f)), rect.Position + new Vector2(cell * 0.8f, cell * (phase - 0.24f)), new Color(wave, 0.5f), width * 0.8f, true);

        if (_table!.InBounds(x, y - 1) && !IsWet(x, y - 1))
        {
            // Pared de la orilla y espuma al pie.
            var bank = new Rect2(rect.Position, new Vector2(cell, cell * 0.2f));
            GradientDown(bank, new Color("7a6851"), new Color("3d3024"));
            DrawLine(bank.Position + new Vector2(0, bank.Size.Y), bank.End, Ui.Ink, line * 0.6f);
            DrawLine(bank.Position + new Vector2(cell * 0.08f, bank.Size.Y + line), bank.End + new Vector2(-cell * 0.08f, line), new Color("cfe6ee", 0.7f), Mathf.Max(2f, line * 0.5f), true);
            DrawLine(rect.Position, rect.Position + new Vector2(cell, 0), Ui.Ink, line);
        }
        if (_table.InBounds(x - 1, y) && !IsWet(x - 1, y))
        {
            GradientRight(new Rect2(rect.Position, new Vector2(cell * 0.3f, cell)), new Color(Shade, 0.5f), new Color(Shade, 0f));
            DrawLine(rect.Position, rect.Position + new Vector2(0, cell), Ui.Ink, line);
        }
        if (_table.InBounds(x + 1, y) && !IsWet(x + 1, y))
            DrawLine(rect.Position + new Vector2(cell, 0), rect.End, Ui.Ink, line);
        if (_table.InBounds(x, y + 1) && !IsWet(x, y + 1))
            DrawLine(rect.Position + new Vector2(0, cell), rect.End, Ui.Ink, line);
        // Sombra del puente sobre el agua de debajo.
        if (Is(x, y - 1, Terrain.Bridge))
            GradientDown(new Rect2(rect.Position, new Vector2(cell, cell * 0.34f)), new Color(Shade, 0.55f), new Color(Shade, 0f));
    }

    /// <summary>Tablones levantados sobre el agua, con el canto de la viga a la vista.</summary>
    private void DrawBridge(int x, int y, Rect2 rect, float cell, float line)
    {
        var wood = new Color("c08a4a");
        var dark = new Color("7d5428");
        var thin = Mathf.Max(2f, cell / 22f);
        var alongX = IsWet(x, y - 1) || IsWet(x, y + 1) || !(IsWet(x - 1, y) && IsWet(x + 1, y)) && !Is(x, y - 1, Terrain.Bridge) && !Is(x, y + 1, Terrain.Bridge);

        if (alongX)
        {
            var deck = new Rect2(rect.Position + new Vector2(0, cell * 0.04f), new Vector2(cell, cell * 0.76f));
            var beam = new Rect2(rect.Position + new Vector2(0, cell * 0.8f), new Vector2(cell, cell * 0.14f));
            GradientDown(deck, wood.Lightened(0.08f), wood);
            for (var i = 0; i < 3; i++)
                DrawLine(deck.Position + new Vector2(cell * i / 3f, 0), deck.Position + new Vector2(cell * i / 3f, deck.Size.Y), dark, thin);
            DrawLine(deck.Position + new Vector2(0, thin), deck.Position + new Vector2(cell, thin), wood.Lightened(0.3f), thin);
            GradientDown(beam, dark, dark.Darkened(0.35f));
            DrawLine(deck.Position, deck.Position + new Vector2(cell, 0), Ui.Ink, line);
            DrawLine(beam.Position, beam.Position + new Vector2(cell, 0), Ui.Ink, line * 0.6f);
            DrawLine(beam.Position + new Vector2(0, beam.Size.Y), beam.End, Ui.Ink, line);
            if (!Is(x - 1, y, Terrain.Bridge) && IsWet(x - 1, y)) DrawLine(deck.Position, beam.Position + new Vector2(0, beam.Size.Y), Ui.Ink, line);
            if (!Is(x + 1, y, Terrain.Bridge) && IsWet(x + 1, y)) DrawLine(deck.Position + new Vector2(cell, 0), beam.End, Ui.Ink, line);
        }
        else
        {
            var deck = new Rect2(rect.Position + new Vector2(cell * 0.1f, 0), new Vector2(cell * 0.8f, cell));
            GradientRight(deck, wood.Lightened(0.08f), wood.Darkened(0.08f));
            for (var i = 0; i < 3; i++)
                DrawLine(deck.Position + new Vector2(0, cell * i / 3f), deck.Position + new Vector2(deck.Size.X, cell * i / 3f), dark, thin);
            DrawLine(deck.Position, deck.Position + new Vector2(0, cell), Ui.Ink, line);
            DrawLine(deck.Position + new Vector2(deck.Size.X, 0), deck.End, Ui.Ink, line);
            if (!Is(x, y + 1, Terrain.Bridge))
            {
                var beam = new Rect2(rect.Position + new Vector2(cell * 0.1f, cell * 0.88f), new Vector2(cell * 0.8f, cell * 0.12f));
                DrawRect(beam, dark);
                DrawRect(beam, Ui.Ink, false, line * 0.6f);
            }
        }
    }

    /// <summary>Sombra que los muros echan sobre el suelo, con la luz arriba a la izquierda.</summary>
    private void DrawCastShadows(int x, int y, float cell)
    {
        if (_table!.TerrainAt(x, y) == Terrain.Wall || _table.IsUnexplored(x, y)) return;
        var rect = CellRect(x, y);
        if (IsWall(x, y - 1))
            GradientDown(new Rect2(rect.Position, new Vector2(cell, cell * 0.42f)), new Color(Shade, 0.5f), new Color(Shade, 0f));
        if (IsWall(x - 1, y))
            GradientRight(new Rect2(rect.Position, new Vector2(cell * 0.34f, cell)), new Color(Shade, 0.42f), new Color(Shade, 0f));
    }

    // ---------- Muros y rocas ----------

    /// <summary>Muro con volumen: la cara de arriba sube <paramref name="rise"/> y deja ver la cara de delante.</summary>
    private void DrawWall(int x, int y, float cell, float line, float rise)
    {
        var corner = Origin + new Vector2(x, y) * cell;
        bool north = IsWall(x, y - 1), south = IsWall(x, y + 1), west = IsWall(x - 1, y), east = IsWall(x + 1, y);
        var thin = Mathf.Max(2f, cell / 24f);
        var seed = Hash(x, y);

        if (!south)
        {
            // Cara de delante: sillares en sombra.
            var front = new Rect2(corner + new Vector2(0, cell - rise), new Vector2(cell, rise));
            GradientDown(front, new Color("54402f"), new Color("261c14"));
            var joint = new Color("221912");
            DrawLine(front.Position + new Vector2(0, rise * 0.5f), front.Position + new Vector2(cell, rise * 0.5f), joint, thin);
            var shift = (x & 1) == 0 ? 0.3f : 0.7f;
            DrawLine(front.Position + new Vector2(cell * shift, 0), front.Position + new Vector2(cell * shift, rise * 0.5f), joint, thin);
            DrawLine(front.Position + new Vector2(cell * (1 - shift), rise * 0.5f), front.Position + new Vector2(cell * (1 - shift), rise), joint, thin);
            DrawLine(front.Position + new Vector2(0, rise), front.End, Ui.Ink, line);
            if (!(west && !IsWall(x - 1, y + 1))) DrawLine(front.Position, front.Position + new Vector2(0, rise), Ui.Ink, line);
            if (!(east && !IsWall(x + 1, y + 1))) DrawLine(front.Position + new Vector2(cell, 0), front.End, Ui.Ink, line);
        }

        // Cara de arriba: más clara, porque le da la luz.
        var top = new Rect2(corner - new Vector2(0, rise), new Vector2(cell, cell));
        var stone = new Color("6f5c49");
        var tone = ((seed >> 8) % 5 - 2f) * 0.02f;
        stone = tone >= 0 ? stone.Lightened(tone) : stone.Darkened(-tone);
        DrawRect(top, stone);
        var block = stone.Lightened(0.13f);
        var gap = cell * 0.07f;
        if ((seed >> 5) % 2 == 0)
        {
            DrawRect(new Rect2(top.Position + new Vector2(gap, gap), new Vector2(cell * 0.52f - gap, cell * 0.5f - gap * 1.5f)), block);
            DrawRect(new Rect2(top.Position + new Vector2(cell * 0.52f + gap * 0.5f, gap), new Vector2(cell * 0.48f - gap * 1.5f, cell * 0.5f - gap * 1.5f)), block.Darkened(0.05f));
            DrawRect(new Rect2(top.Position + new Vector2(gap, cell * 0.5f + gap * 0.5f), new Vector2(cell - gap * 2, cell * 0.5f - gap * 1.5f)), block.Darkened(0.03f));
        }
        else
        {
            DrawRect(new Rect2(top.Position + new Vector2(gap, gap), new Vector2(cell - gap * 2, cell * 0.5f - gap * 1.5f)), block.Darkened(0.04f));
            DrawRect(new Rect2(top.Position + new Vector2(gap, cell * 0.5f + gap * 0.5f), new Vector2(cell * 0.4f - gap, cell * 0.5f - gap * 1.5f)), block);
            DrawRect(new Rect2(top.Position + new Vector2(cell * 0.4f + gap * 0.5f, cell * 0.5f + gap * 0.5f), new Vector2(cell * 0.6f - gap * 1.5f, cell * 0.5f - gap * 1.5f)), block.Darkened(0.06f));
        }

        if (!north) DrawLine(top.Position, top.Position + new Vector2(cell, 0), Ui.Ink, line);
        if (!west) DrawLine(top.Position, top.Position + new Vector2(0, cell), Ui.Ink, line);
        if (!east) DrawLine(top.Position + new Vector2(cell, 0), top.End, Ui.Ink, line);
        if (!south)
        {
            DrawLine(top.Position + new Vector2(0, cell - thin), top.End - new Vector2(0, thin), stone.Lightened(0.35f), thin);
            DrawLine(top.Position + new Vector2(0, cell), top.End, Ui.Ink, line * 0.7f);
        }
    }

    /// <summary>Marca dorada con una flecha hacia el borde más cercano: por ahí se sale del mapa.</summary>
    private void DrawExit(int x, int y, float cell, float line)
    {
        var rect = CellRect(x, y).Grow(-line * 1.2f);
        DrawRect(CellRect(x, y), new Color(Ui.Gold, 0.3f));
        GradientDown(rect, new Color(Ui.Gold.Lightened(0.3f), 0.85f), new Color(Ui.Gold, 0.7f));
        DrawRect(rect, Ui.Gold.Darkened(0.25f), false, line);

        var distances = new[] { x, _table!.Width - 1 - x, y, _table.Height - 1 - y };
        var nearest = Array.IndexOf(distances, distances.Min());
        var dir = nearest switch { 0 => Vector2.Left, 1 => Vector2.Right, 2 => Vector2.Up, _ => Vector2.Down };
        var side = new Vector2(-dir.Y, dir.X);
        var c = CellCenter(x, y);
        var arrow = new[]
        {
            c + dir * cell * 0.28f,
            c - dir * cell * 0.1f + side * cell * 0.26f,
            c - dir * cell * 0.1f - side * cell * 0.26f,
        };
        DrawColoredPolygon(arrow, Ui.Ink);
        DrawLine(c - dir * cell * 0.1f, c - dir * cell * 0.3f, Ui.Ink, line * 1.6f);
    }

    /// <summary>Las antorchas cuelgan de los muros que dan de frente a suelo pisable.</summary>
    private bool HasTorch(int x, int y) =>
        _table!.InBounds(x, y + 1) && !_table.IsUnexplored(x, y + 1) && _table.TerrainAt(x, y + 1) is Terrain.Floor or Terrain.Rock;

    private void DrawTorch(int x, int y, float cell, float line, float rise)
    {
        var foot = Origin + new Vector2((x + 0.5f) * cell, (y + 1) * cell - rise * 0.25f);
        var flame = foot - new Vector2(0, rise * 0.62f);
        DrawLine(foot, flame, Ui.Ink, line * 1.4f, true);
        DrawLine(foot, flame, new Color("8a5f30"), line * 0.6f, true);
        Shape(new[] { flame + new Vector2(-cell * 0.08f, 0), flame + new Vector2(-cell * 0.05f, -cell * 0.12f), flame + new Vector2(0, -cell * 0.24f), flame + new Vector2(cell * 0.06f, -cell * 0.1f), flame + new Vector2(cell * 0.08f, 0), flame + new Vector2(0, cell * 0.05f) },
            new Color("f28a2b"), Mathf.Max(2f, line * 0.6f));
        Fill(new[] { flame + new Vector2(-cell * 0.035f, 0), flame + new Vector2(0, -cell * 0.13f), flame + new Vector2(cell * 0.035f, 0), flame + new Vector2(0, cell * 0.025f) }, new Color("ffe27a"));
    }

    private static readonly float[] RockShape = { 0.36f, 0.33f, 0.35f, 0.3f, 0.34f, 0.38f, 0.33f, 0.36f, 0.31f, 0.35f };

    /// <summary>Peñasco con su sombra en el suelo, la cara de la derecha oscura y un brillo arriba.</summary>
    private void DrawRock(Vector2 center, float cell, float line)
    {
        Fill(Ellipse(center + new Vector2(cell * 0.08f, cell * 0.2f), cell * 0.4f, cell * 0.17f), new Color(Shade, 0.35f));
        center -= new Vector2(0, cell * 0.08f);
        var points = new Vector2[RockShape.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var angle = Mathf.Tau * i / points.Length;
            points[i] = center + new Vector2(Mathf.Cos(angle) * cell * RockShape[i], Mathf.Sin(angle) * cell * RockShape[i] * 0.82f);
        }
        var stone = new Color("9a9082");
        Fill(points, stone);
        // De la derecha hacia abajo queda en sombra.
        Fill(new[] { points[0], points[1], points[2], points[3], points[4], center + new Vector2(-cell * 0.04f, cell * 0.08f), center + new Vector2(cell * 0.12f, -cell * 0.04f) }, stone.Darkened(0.28f));
        Fill(new[] { points[6], points[7], points[8], center + new Vector2(cell * 0.02f, -cell * 0.14f), center + new Vector2(-cell * 0.14f, -cell * 0.04f) }, stone.Lightened(0.2f));
        Outline(points, line);
        DrawLine(center + new Vector2(cell * 0.12f, -cell * 0.04f), center + new Vector2(-cell * 0.04f, cell * 0.08f), Ui.Ink, Mathf.Max(2f, line * 0.5f), true);
    }

    private void DrawFog(float cell, float line, float rise)
    {
        for (var x = 0; x < _table!.Width; x++)
        for (var y = 0; y < _table.Height; y++)
        {
            if (!_table.IsUnexplored(x, y)) continue;
            var rect = CellRect(x, y);
            DrawRect(y == 0 ? new Rect2(rect.Position - new Vector2(0, rise), rect.Size + new Vector2(0, rise)) : rect, Shade);
            if (x > 0 && !_table.IsUnexplored(x - 1, y))
            {
                // La penumbra se come un poco de lo que sí se ve.
                GradientRight(new Rect2(rect.Position - new Vector2(cell * 0.6f, 0), new Vector2(cell * 0.6f, cell)), new Color(Shade, 0f), new Color(Shade, 0.85f));
                DrawDashedLine(rect.Position, rect.Position + new Vector2(0, cell), new Color("8c8273"), line, cell / 5f);
            }
        }
    }

    private void DrawDragPreview(Token token, float cell, float line)
    {
        var target = CellAt(_mouse);
        var check = _table!.CheckMove(token.Id, target.X, target.Y);
        if (!_table.InBounds(target.X, target.Y)) return;

        var color = check.Ok ? new Color("5b9a3c") : Ui.Red;
        var rect = CellRect(target.X, target.Y).Grow(-line);
        DrawRect(rect, new Color(color, 0.35f));
        DrawRect(rect, color, false, line);

        var from = CellCenter(token.X, token.Y);
        var to = CellCenter(target.X, target.Y);
        DrawDashedLine(from, to, Ui.Ink, line, cell / 6f);
        DrawCircle(from, cell * 0.12f, Ui.Ink);

        if (check.Feet <= 0) return;
        var text = $"{check.Feet} pies";
        var fontSize = Mathf.Clamp((int)(cell * 0.26f), 12, 22);
        var size = _labelFont.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize);
        var pill = new Rect2((from + to) / 2f - size / 2f - new Vector2(10, 4), size + new Vector2(20, 8));
        DrawPill(pill, check.Ok ? Ui.Gold : new Color("f3b5a8"), line * 0.8f);
        DrawString(_labelFont, pill.Position + new Vector2(10, 4 + _labelFont.GetAscent(fontSize)), text, HorizontalAlignment.Left, -1, fontSize, Ui.Ink);
    }

    private void DrawPill(Rect2 rect, Color fill, float border)
    {
        var box = new StyleBoxFlat { BgColor = fill, BorderColor = Ui.Ink, AntiAliasing = true };
        box.SetBorderWidthAll(Mathf.RoundToInt(border));
        box.SetCornerRadiusAll((int)(rect.Size.Y / 2f));
        DrawStyleBox(box, rect);
    }

    private static readonly Color[] Skins = { new("f2c9a0"), new("d9a273"), new("a8744d"), new("f6d8b8") };

    /// <summary>
    /// Figurita de pie sobre su peana: la peana lleva el color de la ficha y la figura se levanta
    /// desde el centro de la casilla, así que asoma sobre la casilla de detrás.
    /// </summary>
    private void DrawToken(Token token, Vector2 center, float cell, float line, bool selected, bool lifted = false)
    {
        var s = cell;
        var color = new Color(token.Color);
        var thin = Mathf.Max(2f, line * 0.7f);
        var goblin = token.Icon is "goblin" or "goblin_boss";
        float rx = s * 0.36f, ry = s * 0.15f, thick = s * 0.07f;

        var floor = center - new Vector2(0, s * 0.04f);
        var stand = lifted ? floor - new Vector2(0, s * 0.22f) : floor;

        // Sombra en el suelo: más lejos y más tenue si la figurita está en el aire.
        Fill(Ellipse(floor + new Vector2(s * (lifted ? 0.14f : 0.07f), thick + s * 0.03f), rx * 1.06f, ry * 1.1f), new Color(Shade, lifted ? 0.25f : 0.4f));
        if (selected && !lifted) Shape(Ellipse(stand + new Vector2(0, thick), rx + line * 1.7f, ry + line * 1.3f), Ui.Gold, thin);

        // Peana: canto oscuro y cara de arriba con un aro claro.
        Shape(Ellipse(stand + new Vector2(0, thick), rx, ry), color.Darkened(0.4f), line);
        DrawRect(new Rect2(stand + new Vector2(-rx, 0), new Vector2(rx * 2, thick)), color.Darkened(0.4f));
        DrawLine(stand + new Vector2(-rx, 0), stand + new Vector2(-rx, thick), Ui.Ink, line);
        DrawLine(stand + new Vector2(rx, 0), stand + new Vector2(rx, thick), Ui.Ink, line);
        Shape(Ellipse(stand, rx, ry), color, line);
        Fill(Ellipse(stand, rx * 0.8f, ry * 0.8f), color.Lightened(0.22f));
        Fill(Ellipse(stand + new Vector2(s * 0.03f, s * 0.015f), rx * 0.62f, ry * 0.7f), new Color(Shade, 0.3f));

        var f = stand - new Vector2(0, s * 0.02f);
        // La figura es más grande que su casilla: cabezona, como una miniatura de caricatura.
        var fs = s * 1.42f;
        Vector2 P(float x, float y) => f + new Vector2(x, y) * fs;

        // Lo que lleva a la espalda o en la mano de detrás va antes que el cuerpo.
        if (token.Icon == "hat")
        {
            DrawLine(P(0.25f, 0.02f), P(0.25f, -0.66f), Ui.Ink, line * 1.5f, true);
            DrawLine(P(0.25f, 0.02f), P(0.25f, -0.66f), new Color("8a5f30"), line * 0.7f, true);
            Shape(Ellipse(P(0.25f, -0.7f), fs * 0.065f, fs * 0.065f, 16), new Color("7fe0ff"), thin);
        }
        if (goblin)
        {
            foreach (var side in new[] { -1f, 1f })
                Shape(new[] { P(side * 0.11f, -0.52f), P(side * 0.34f, -0.6f), P(side * 0.13f, -0.4f) }, color.Darkened(0.12f), thin);
        }

        // Cuerpo: una túnica acampanada, con el lado derecho en sombra.
        var cloth = goblin ? new Color("8a6238") : color;
        var body = new[] { P(-0.2f, 0), P(-0.17f, -0.2f), P(-0.11f, -0.35f), P(0.11f, -0.35f), P(0.17f, -0.2f), P(0.2f, 0), P(0.1f, 0.035f), P(-0.1f, 0.035f) };
        Fill(body, cloth);
        Fill(new[] { P(0.03f, -0.35f), P(0.11f, -0.35f), P(0.17f, -0.2f), P(0.2f, 0), P(0.1f, 0.035f), P(0.06f, 0.035f) }, cloth.Darkened(0.28f));
        Fill(new[] { P(-0.17f, -0.2f), P(-0.11f, -0.35f), P(-0.06f, -0.35f), P(-0.12f, -0.02f), P(-0.19f, -0.02f) }, cloth.Lightened(0.18f));
        Outline(body, line);
        DrawLine(P(-0.165f, -0.15f), P(0.165f, -0.15f), Ui.Ink, thin);
        Shape(Ellipse(P(0, -0.15f), fs * 0.035f, fs * 0.035f, 10), Ui.Gold, Mathf.Max(1.5f, thin * 0.6f));

        // Cabeza.
        var head = P(0, -0.49f);
        var hr = fs * 0.17f;
        var skin = goblin ? color : Skins[(int)((uint)token.Id.GetHashCode(StringComparison.Ordinal) % (uint)Skins.Length)];
        Shape(Ellipse(head, hr, hr, 24), skin, line);
        Fill(Ellipse(head + new Vector2(hr * 0.42f, hr * 0.2f), hr * 0.5f, hr * 0.72f, 14), skin.Darkened(0.16f));
        Fill(Ellipse(head + new Vector2(-hr * 0.4f, -hr * 0.42f), hr * 0.3f, hr * 0.22f, 12), new Color(Colors.White, 0.4f));

        if (goblin)
        {
            foreach (var side in new[] { -1f, 1f })
            {
                var eye = head + new Vector2(side * hr * 0.42f, -hr * 0.1f);
                DrawCircle(eye, hr * 0.27f, Ui.Ink);
                DrawCircle(eye, hr * 0.27f - Mathf.Max(1.5f, line * 0.35f), new Color("f2e24a"));
                DrawCircle(eye, hr * 0.08f, Ui.Ink);
            }
            DrawArc(head + new Vector2(0, hr * 0.2f), hr * 0.45f, Mathf.Pi * 0.2f, Mathf.Pi * 0.8f, 12, Ui.Ink, thin, true);
            if (token.Icon == "goblin_boss")
                Shape(new[] { P(-0.13f, -0.58f), P(-0.15f, -0.76f), P(-0.07f, -0.67f), P(0, -0.8f), P(0.07f, -0.67f), P(0.15f, -0.76f), P(0.13f, -0.58f) }, Ui.Gold, thin);
        }
        else
        {
            DrawCircle(head + new Vector2(-hr * 0.38f, hr * 0.05f), hr * 0.13f, Ui.Ink);
            DrawCircle(head + new Vector2(hr * 0.38f, hr * 0.05f), hr * 0.13f, Ui.Ink);
            DrawArc(head + new Vector2(0, hr * 0.3f), hr * 0.3f, Mathf.Pi * 0.25f, Mathf.Pi * 0.75f, 8, Ui.Ink, Mathf.Max(1.5f, thin * 0.7f), true);
        }

        switch (token.Icon)
        {
            case "sword":
                // Yelmo, escudo redondo y espada en alto.
                Shape(Ellipse(head - new Vector2(0, hr * 0.12f), hr * 1.08f, hr * 1.02f, 16, Mathf.Pi, Mathf.Tau), new Color("c3c7cf"), thin);
                DrawLine(head - new Vector2(0, hr * 1.1f), head + new Vector2(0, hr * 0.15f), Ui.Ink, thin);
                Shape(new[] { P(0.2f, -0.24f), P(0.2f, -0.6f), P(0.235f, -0.68f), P(0.27f, -0.6f), P(0.27f, -0.24f) }, new Color("e6e9ee"), thin);
                DrawLine(P(0.15f, -0.24f), P(0.32f, -0.24f), Ui.Ink, line * 1.3f, true);
                DrawLine(P(0.235f, -0.24f), P(0.235f, -0.14f), Ui.Ink, line * 1.3f, true);
                Shape(Ellipse(P(-0.2f, -0.17f), fs * 0.11f, fs * 0.12f, 18), color.Lightened(0.3f), line);
                Shape(Ellipse(P(-0.2f, -0.17f), fs * 0.035f, fs * 0.04f, 10), Ui.Gold, Mathf.Max(1.5f, thin * 0.6f));
                break;
            case "hat":
                // Sombrero de pico.
                Shape(new[] { P(-0.13f, -0.57f), P(0.05f, -0.86f), P(0.13f, -0.57f) }, color.Darkened(0.12f), thin);
                Shape(Ellipse(P(0, -0.57f), fs * 0.23f, fs * 0.055f, 20), color.Darkened(0.12f), thin);
                DrawLine(P(-0.1f, -0.61f), P(0.1f, -0.61f), Ui.Gold, thin, true);
                break;
            case "sun":
                // Capucha y símbolo solar en el pecho.
                Shape(Ellipse(head - new Vector2(0, hr * 0.1f), hr * 1.12f, hr * 1.08f, 16, Mathf.Pi * 0.95f, Mathf.Pi * 2.05f), color.Lightened(0.25f), thin);
                for (var i = 0; i < 8; i++)
                {
                    var dir = Vector2.Right.Rotated(Mathf.Tau * i / 8f);
                    DrawLine(P(0, -0.26f) + dir * fs * 0.05f, P(0, -0.26f) + dir * fs * 0.085f, Ui.Gold, thin, true);
                }
                Shape(Ellipse(P(0, -0.26f), fs * 0.045f, fs * 0.045f, 12), Ui.Gold, Mathf.Max(1.5f, thin * 0.6f));
                break;
            case "mushroom":
                // Sombrero de seta.
                Shape(Ellipse(P(0, -0.55f), fs * 0.24f, fs * 0.17f, 18, Mathf.Pi, Mathf.Tau), new Color("c9503f"), thin);
                DrawCircle(P(-0.1f, -0.62f), fs * 0.03f, Colors.White);
                DrawCircle(P(0.06f, -0.66f), fs * 0.035f, Colors.White);
                break;
            case "goblin" or "goblin_boss":
                break;
            default:
                Shape(Ellipse(head - new Vector2(0, hr * 0.15f), hr * 1.05f, hr * 0.95f, 14, Mathf.Pi, Mathf.Tau), new Color("5a4030"), thin);
                break;
        }

        if (lifted || token.MaxHp <= 0) return;
        // Vida: flota sobre la cabeza para que no la tape nada.
        var top = token.Icon is "hat" ? -0.97f : token.Icon is "goblin_boss" ? -0.91f : -0.79f;
        var bar = new Rect2(P(-0.22f, top), new Vector2(fs * 0.44f, Mathf.Max(7f, s * 0.11f)));
        DrawRect(bar, Ui.Ink);
        var inner = bar.Grow(-Mathf.Max(1.5f, line * 0.45f));
        DrawRect(inner, Ui.Parchment);
        DrawRect(new Rect2(inner.Position, new Vector2(inner.Size.X * token.Hp / token.MaxHp, inner.Size.Y)), Ui.HpRed);
    }
}
