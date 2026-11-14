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

    private float Cell => _table is null
        ? 0
        : Mathf.Floor(Mathf.Min((Size.X - 16) / _table.Width, (Size.Y - 20) / _table.Height));

    private Vector2 Origin => _table is null
        ? Vector2.Zero
        : ((Size - new Vector2(_table.Width, _table.Height) * Cell) / 2f - new Vector2(0, 3)).Floor();

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

    public override void _Draw()
    {
        if (_table is null || Cell <= 4) return;
        var cell = Cell;
        var board = new Rect2(Origin, new Vector2(_table.Width, _table.Height) * cell);
        var line = Mathf.Max(3f, cell / 15f);

        // Sombra dura y marco del tablero.
        DrawRect(new Rect2(board.Position + new Vector2(-line, -line + 6), board.Size + new Vector2(line * 2, line * 2)), Ui.Ink);
        DrawRect(board.Grow(line), Ui.Ink);

        for (var x = 0; x < _table.Width; x++)
        for (var y = 0; y < _table.Height; y++)
            DrawTerrain(x, y, cell);

        // Rejilla fina.
        var grid = new Color(Ui.Ink, 0.16f);
        for (var x = 1; x < _table.Width; x++)
            DrawLine(board.Position + new Vector2(x * cell, 0), board.Position + new Vector2(x * cell, board.Size.Y), grid, 2);
        for (var y = 1; y < _table.Height; y++)
            DrawLine(board.Position + new Vector2(0, y * cell), board.Position + new Vector2(board.Size.X, y * cell), grid, 2);

        DrawTerrainOutlines(cell, line);

        for (var x = 0; x < _table.Width; x++)
        for (var y = 0; y < _table.Height; y++)
            if (_table.TerrainAt(x, y) == Terrain.Rock)
                DrawRock(CellCenter(x, y), cell, line);

        var dragged = _dragId is null ? null : _table.FindToken(_dragId);
        if (dragged is not null) DrawDragPreview(dragged, cell, line);

        foreach (var token in _table.Tokens)
        {
            if (token == dragged || _table.IsUnexplored(token.X, token.Y)) continue;
            DrawToken(token, CellCenter(token.X, token.Y), cell, line, token.Id == _selectedId);
        }

        DrawFog(cell, line);

        if (dragged is not null) DrawToken(dragged, _mouse, cell, line, true, lifted: true);
    }

    private static Color TerrainColor(Terrain terrain) => terrain switch
    {
        Terrain.Wall => new Color("4a3b2e"),
        Terrain.Water => new Color("2d4a5e"),
        Terrain.Bridge => new Color("b07a3f"),
        _ => new Color("cdbf9f"),
    };

    private void DrawTerrain(int x, int y, float cell)
    {
        var terrain = _table!.TerrainAt(x, y);
        var rect = CellRect(x, y);
        DrawRect(rect, TerrainColor(terrain));

        switch (terrain)
        {
            case Terrain.Floor or Terrain.Rock:
                // Motas de piedra, siempre las mismas en cada casilla.
                var seed = (uint)(x * 73856093 ^ y * 19349663);
                for (var i = 0; i < 2; i++)
                {
                    seed = seed * 1664525u + 1013904223u;
                    var px = (seed >> 8) % 1000 / 1000f;
                    seed = seed * 1664525u + 1013904223u;
                    var py = (seed >> 8) % 1000 / 1000f;
                    DrawCircle(rect.Position + new Vector2(0.15f + px * 0.7f, 0.15f + py * 0.7f) * cell, cell * 0.035f, new Color("b9aa88"));
                }
                break;
            case Terrain.Wall:
                DrawRect(new Rect2(rect.Position + new Vector2(cell * 0.12f, cell * 0.2f), new Vector2(cell * 0.4f, cell * 0.2f)), new Color("5a4939"));
                DrawRect(new Rect2(rect.Position + new Vector2(cell * 0.5f, cell * 0.58f), new Vector2(cell * 0.38f, cell * 0.2f)), new Color("5a4939"));
                break;
            case Terrain.Water:
                var wave = new Color("7fb2c9");
                var phase = (x + y) % 2 == 0 ? 0.3f : 0.62f;
                var start = rect.Position + new Vector2(cell * 0.2f, cell * phase);
                DrawArc(start + new Vector2(cell * 0.15f, 0), cell * 0.15f, Mathf.Pi, Mathf.Tau, 10, wave, Mathf.Max(2f, cell / 18f), true);
                DrawArc(start + new Vector2(cell * 0.45f, 0), cell * 0.15f, 0, Mathf.Pi, 10, wave, Mathf.Max(2f, cell / 18f), true);
                break;
            case Terrain.Bridge:
                var horizontal = IsWater(x, y - 1) || IsWater(x, y + 1);
                for (var i = 1; i < 3; i++)
                {
                    var t = i / 3f * cell;
                    if (horizontal) DrawLine(rect.Position + new Vector2(t, 0), rect.Position + new Vector2(t, cell), Ui.Ink, Mathf.Max(2f, cell / 20f));
                    else DrawLine(rect.Position + new Vector2(0, t), rect.Position + new Vector2(cell, t), Ui.Ink, Mathf.Max(2f, cell / 20f));
                }
                break;
        }
    }

    private bool IsWater(int x, int y) => _table!.InBounds(x, y) && _table.TerrainAt(x, y) == Terrain.Water;

    /// <summary>Roca y suelo cuentan como lo mismo; el resto se separa con trazo grueso.</summary>
    private Terrain OutlineClass(int x, int y)
    {
        var terrain = _table!.TerrainAt(x, y);
        return terrain == Terrain.Rock ? Terrain.Floor : terrain;
    }

    private void DrawTerrainOutlines(float cell, float line)
    {
        for (var x = 0; x < _table!.Width; x++)
        for (var y = 0; y < _table.Height; y++)
        {
            var here = OutlineClass(x, y);
            var corner = Origin + new Vector2(x, y) * cell;
            var half = new Vector2(line / 2f, 0);
            if (x + 1 < _table.Width && OutlineClass(x + 1, y) != here)
                DrawLine(corner + new Vector2(cell, -line / 2f), corner + new Vector2(cell, cell + line / 2f), Ui.Ink, line);
            if (y + 1 < _table.Height && OutlineClass(x, y + 1) != here)
                DrawLine(corner + new Vector2(0, cell) - half, corner + new Vector2(cell, cell) + half, Ui.Ink, line);
        }
    }

    private static readonly float[] RockShape = { 0.36f, 0.30f, 0.33f, 0.26f, 0.31f, 0.35f, 0.29f, 0.34f, 0.27f, 0.32f };

    private void DrawRock(Vector2 center, float cell, float line)
    {
        var points = new Vector2[10];
        for (var i = 0; i < points.Length; i++)
        {
            var angle = Mathf.Tau * i / points.Length;
            var radius = cell * RockShape[i];
            points[i] = center + new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius * 0.85f);
        }
        DrawColoredPolygon(points, new Color("8c8273"));
        DrawPolyline(points.Append(points[0]).ToArray(), Ui.Ink, line, true);
        DrawLine(center + new Vector2(-cell * 0.12f, -cell * 0.08f), center + new Vector2(cell * 0.02f, -cell * 0.14f), new Color("b3aa9b"), line * 0.8f, true);
    }

    private void DrawFog(float cell, float line)
    {
        var fog = new Color("1d1712", 0.93f);
        for (var x = 0; x < _table!.Width; x++)
        for (var y = 0; y < _table.Height; y++)
        {
            if (!_table.IsUnexplored(x, y)) continue;
            DrawRect(CellRect(x, y), fog);
            if (x > 0 && !_table.IsUnexplored(x - 1, y))
                DrawDashedLine(CellRect(x, y).Position, CellRect(x, y).Position + new Vector2(0, cell), new Color("8c8273"), line, cell / 5f);
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

    private void DrawToken(Token token, Vector2 center, float cell, float line, bool selected, bool lifted = false)
    {
        var radius = cell * 0.38f;
        var color = new Color(token.Color);
        var drop = lifted ? cell * 0.16f : cell * 0.07f;
        if (lifted) center -= new Vector2(0, cell * 0.08f);

        if (selected) DrawArc(center, radius + line * 1.9f, 0, Mathf.Tau, 40, Ui.Gold, line * 1.3f, true);
        DrawCircle(center + new Vector2(0, drop), radius + line / 2f, lifted ? new Color(Ui.Ink, 0.45f) : Ui.Ink);

        if (token.Icon is "goblin" or "goblin_boss")
        {
            foreach (var side in new[] { -1f, 1f })
            {
                var ear = new[]
                {
                    center + new Vector2(side * radius * 1.45f, -radius * 0.75f),
                    center + new Vector2(side * radius * 0.55f, -radius * 0.45f),
                    center + new Vector2(side * radius * 0.85f, radius * 0.25f),
                };
                DrawColoredPolygon(ear, color.Darkened(0.15f));
                DrawPolyline(ear.Append(ear[0]).ToArray(), Ui.Ink, line, true);
            }
        }

        DrawCircle(center, radius, color);
        DrawArc(center, radius, 0, Mathf.Tau, 40, Ui.Ink, line, true);
        DrawIcon(token.Icon, center, radius, line);

        if (lifted || token.MaxHp <= 0) return;
        var bar = new Rect2(center + new Vector2(-radius * 0.9f, radius + line * 1.4f), new Vector2(radius * 1.8f, Mathf.Max(6f, cell * 0.11f)));
        DrawRect(bar, Ui.Ink);
        var inner = bar.Grow(-Mathf.Max(1.5f, line * 0.45f));
        DrawRect(inner, Ui.Parchment);
        DrawRect(new Rect2(inner.Position, new Vector2(inner.Size.X * token.Hp / token.MaxHp, inner.Size.Y)), Ui.HpRed);
    }

    private void DrawIcon(string icon, Vector2 c, float r, float line)
    {
        var white = Colors.White;
        switch (icon)
        {
            case "sword":
                DrawColoredPolygon(new[] { c + new Vector2(0, -r * 0.78f), c + new Vector2(r * 0.17f, -r * 0.55f), c + new Vector2(r * 0.17f, r * 0.15f), c + new Vector2(-r * 0.17f, r * 0.15f), c + new Vector2(-r * 0.17f, -r * 0.55f) }, white);
                DrawRect(new Rect2(c + new Vector2(-r * 0.4f, r * 0.15f), new Vector2(r * 0.8f, r * 0.17f)), white);
                DrawRect(new Rect2(c + new Vector2(-r * 0.1f, r * 0.32f), new Vector2(r * 0.2f, r * 0.4f)), white);
                break;
            case "hat":
                DrawColoredPolygon(new[] { c + new Vector2(0, -r * 0.75f), c + new Vector2(r * 0.4f, r * 0.25f), c + new Vector2(-r * 0.4f, r * 0.25f) }, white);
                DrawRect(new Rect2(c + new Vector2(-r * 0.68f, r * 0.25f), new Vector2(r * 1.36f, r * 0.24f)), white);
                break;
            case "sun":
                DrawCircle(c, r * 0.3f, white);
                for (var i = 0; i < 8; i++)
                {
                    var dir = Vector2.Right.Rotated(Mathf.Tau * i / 8f);
                    DrawLine(c + dir * r * 0.46f, c + dir * r * 0.72f, white, Mathf.Max(2f, line * 0.8f), true);
                }
                break;
            case "goblin" or "goblin_boss":
                var eye = new Color("f2e24a");
                foreach (var side in new[] { -1f, 1f })
                {
                    var p = c + new Vector2(side * r * 0.36f, -r * 0.12f);
                    DrawCircle(p, r * 0.2f, Ui.Ink);
                    DrawCircle(p, r * 0.2f - Mathf.Max(1.5f, line * 0.4f), eye);
                }
                DrawArc(c + new Vector2(0, r * 0.2f), r * 0.38f, Mathf.Pi * 0.2f, Mathf.Pi * 0.8f, 12, Ui.Ink, Mathf.Max(2f, line * 0.7f), true);
                if (icon == "goblin_boss")
                {
                    var crown = new[]
                    {
                        c + new Vector2(-r * 0.6f, -r * 0.62f), c + new Vector2(-r * 0.6f, -r * 1.2f), c + new Vector2(-r * 0.3f, -r * 0.9f),
                        c + new Vector2(0, -r * 1.3f), c + new Vector2(r * 0.3f, -r * 0.9f), c + new Vector2(r * 0.6f, -r * 1.2f),
                        c + new Vector2(r * 0.6f, -r * 0.62f),
                    };
                    foreach (var triangle in Geometry2D.TriangulatePolygon(crown) is { Length: > 0 } indices
                                 ? indices.Chunk(3).Select(t => new[] { crown[t[0]], crown[t[1]], crown[t[2]] })
                                 : Array.Empty<Vector2[]>())
                        DrawColoredPolygon(triangle, Ui.Gold);
                    DrawPolyline(crown.Append(crown[0]).ToArray(), Ui.Ink, Mathf.Max(2f, line * 0.8f), true);
                }
                break;
        }
    }
}
