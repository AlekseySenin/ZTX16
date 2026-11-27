using ZTX16;
using ZTX16.Authoring;
using ZTX16.Collections;

namespace ZTX16Graphics;

/// <summary>
/// The ZTX16 graphics editor. Toolbar (Open, Save, Save As, Import, Export); on the left the main screen
/// (the selected tile or tile region, zoomed, for pixel editing), the palette with its menu, the tools and their
/// settings, and (next stage) the animation timeline; on the right the whole 256×256 tilemap, where tiles and
/// regions are selected.
/// </summary>
/// <remarks>
/// Mouse: left button paints with the primary colour, right button with the secondary one. Keys: B brush,
/// E eraser, F fill, I pipette, S select, Ctrl+C/X/V, Ctrl+Z/Y, Del clears, [ ] brush size, G grid, H/V flip,
/// R rotate, Esc cancels, Ctrl+S save, Ctrl+O open. Host keys: F1 help, F2 Console, F3 code editor, F4 this editor.
/// </remarks>
public sealed class Editor : ICartridge
{
    // Layers: 0 static background (panels, checkerboards), 1 pixels, 2 overlays and interface.
    private const int Back = 0, Pixels = 1, Ui = 2;
    private const string Font = "small";
    private const int Screen = 256;
    private const int CharWidth = 4;
    private const int LineHeight = 7;

    // Layout (lower-left origin).
    private const int ToolbarY = 245;
    private const int LeftWidth = 92;
    private const int MainX = 0, MainY = 168, MainWidth = 80, MainHeight = 76;
    private const int ZoomX = 86, ZoomWidth = 6;           // zoom slider right of the main screen
    private const int AreaY = 155;                         // tile region size buttons
    private const int PaletteY = 129;
    private const int ToolsY = 106;
    private const int SettingsY = 79;
    private static readonly int[] AreaSizes = { 1, 2, 4, 8, 16, 32 };
    private const int ViewX = 93, ViewY = 5, ViewWidth = 158, ViewHeight = 240;
    private const int Bar = 5;

    // Tools and actions (icon order).
    private const int ToolBrush = 0, ToolEraser = 1, ToolFill = 2, ToolPipette = 3, ToolSelect = 4;
    private const int ActionCopy = 5, ActionPaste = 6, ActionFlipX = 7, ActionFlipY = 8, ActionRotate = 9;
    private const int IconCount = 10;

    // Panels.
    private const int PanelNone = 0, PanelExplorer = 1, PanelPrompt = 2, PanelConfirm = 3, PanelPaletteMenu = 4, PanelColor = 5;

    private static readonly string[] ToolbarLabels = { "OPEN", "SAVE", "SAVE AS", "IMPORT", "EXPORT" };
    private static readonly string[] ToolNames = { "BRUSH", "ERASER", "FILL", "PIPETTE", "SELECT", "COPY", "PASTE", "FLIP H", "FLIP V", "ROTATE" };
    private static readonly string[] PaletteMenu = { "EDIT COLOR", "LOAD PALETTE", "SAVE PALETTE", "SAVE PALETTE AS" };

    private static readonly string[] IconArt =
    {
        ".....## ....### ...###. ..###.. .###... ##..... #......",   // brush
        "....... ..####. .#..##. #..###. ####.#. #..##.. ####...",   // eraser
        "..#.... .#.#... #...#.. #####.# .###..# ..#...# .......",   // fill
        ".....## ....### ...###. ..#.#.. .#.#... #.#.... ##.....",   // pipette
        "###.### #.....# ....... #.....# ....... #.....# ###.###",   // select
        "####... #..#... #..#### ####..# ...#..# ...#### .......",   // copy
        ".##.##. #.###.# #.....# #.###.# #.....# #.###.# #######",   // paste
        "...#... .#.#.#. ##.#.## ###.### ##.#.## .#.#.#. ...#...",   // flip horizontally
        "...#... .#####. ..###.. ####### ..###.. .#####. ...#...",   // flip vertically
        ".###... #...#.. #....#. #..#### #...###. .#...#. ..###..",  // rotate
    };

    private readonly Image _image = new();
    private readonly Theme _theme = new();
    private readonly int[] _toolbarX = new int[5];
    private readonly int[] _toolbarWidth = new int[5];
    private byte[][] _icons = new byte[IconCount][];
    private bool _backgroundDirty = true;

    private string _path = "";
    private string _status = "";
    private int _statusTicks;
    private int _blink;

    // Tilemap view and the selected tile region (in tiles).
    private int _viewScrollX, _viewScrollY;
    private int _selTileX, _selTileY, _selTileWidth = 1, _selTileHeight = 1;
    private int _dragTileX = -1, _dragTileY;
    private bool _grid;

    // Main screen.
    private int _zoom = 10;
    private int _mainScrollX, _mainScrollY;

    // Tools.
    private int _tool = ToolBrush;
    private int _primary = 5, _secondary = 0;
    private int _brushSize = 1;
    private bool _fillGlobal;
    private bool _roundBrush;
    private int _hoverTicks;
    private int _hoverX = -1, _hoverY = -1;
    private bool _pasteTransparent = true;
    private bool _painting;
    private int _paintColor;
    private int _lastPaintX, _lastPaintY;

    // Pixel selection inside the region (tilemap coordinates) and the clipboard / floating paste.
    private bool _hasSelection;
    private int _selX, _selY, _selWidth, _selHeight;
    private int _selAnchorX = -1, _selAnchorY;
    private byte[] _clip = new byte[0];
    private int _clipWidth, _clipHeight;
    private bool _pasting;

    // Scrollbar dragging: 0 none, 1 view horizontal, 2 view vertical, 3 main horizontal, 4 main vertical.
    private int _dragBar;

    // Panels.
    private int _panel;
    private string _promptText = "";
    private int _promptPurpose;          // 1 save image as, 2 save palette as
    private string _pendingPath = "";
    private int _menuIndex;
    private int _colorIndex;             // palette entry being edited
    private int _colorChannel;           // 0 R, 1 G, 2 B
    private string _explorerDir = "/";
    private string _explorerFilter = ".png";
    private readonly List<string> _entries = new();
    private int _explorerIndex, _explorerScroll;
    private long _lastClickTick = -100;
    private int _lastClickIndex = -1;
    private int _dialog;                 // 1 import pending, 2 export pending

    public void Initialize()
    {
        int x = 2;
        for (int i = 0; i < ToolbarLabels.Length; i++)
        {
            _toolbarX[i] = x;
            _toolbarWidth[i] = (ToolbarLabels[i].Length * CharWidth) + 5;
            x += _toolbarWidth[i] + 2;
        }

        _theme.Parse(Workspace.ReadText("/HelloWorld/palettes/default.pal") ?? DefaultPalette());
        BuildIcons();
        Status("Open a PNG tilemap (OPEN) or EDIT file.png in the Console");
    }

    // ---- tick ---------------------------------------------------------------------------------------------

    public void Tick()
    {
        string request = Apps.Request();
        if (request.Length > 0)
        {
            RequestOpen(request);
        }

        _blink++;
        if (_statusTicks > 0)
        {
            _statusTicks--;
        }

        PollDialog();
        int mx = Input.MouseX, my = Input.MouseY;

        // Tooltips appear once the mouse rests on a control for a moment.
        if (mx == _hoverX && my == _hoverY && !Input.IsMouseButtonDown(MouseButton.Left))
        {
            _hoverTicks++;
        }
        else
        {
            _hoverTicks = 0;
            _hoverX = mx;
            _hoverY = my;
        }

        if (_panel == PanelNone)
        {
            Mouse(mx, my);
            Keys();
        }
        else
        {
            PanelInput(mx, my);
        }
    }

    private void Keys()
    {
        bool ctrl = Input.IsKeyDown(Key.LeftControl) || Input.IsKeyDown(Key.RightControl);
        bool shift = Input.IsKeyDown(Key.LeftShift) || Input.IsKeyDown(Key.RightShift);
        if (ctrl)
        {
            if (Input.WasKeyPressed(Key.Z))
            {
                Undo();
            }

            if (Input.WasKeyPressed(Key.Y))
            {
                Redo();
            }

            if (Input.WasKeyPressed(Key.C))
            {
                Action(ActionCopy);
            }

            if (Input.WasKeyPressed(Key.X))
            {
                Action(ActionCopy);
                ClearSelectionPixels();
            }

            if (Input.WasKeyPressed(Key.V))
            {
                Action(ActionPaste);
            }

            if (Input.WasKeyPressed(Key.S))
            {
                if (shift)
                {
                    SaveAs();
                }
                else
                {
                    Save();
                }
            }

            if (Input.WasKeyPressed(Key.O))
            {
                OpenExplorer(".png");
            }

            return;
        }

        if (Input.WasKeyPressed(Key.B)) _tool = ToolBrush;
        if (Input.WasKeyPressed(Key.E)) _tool = ToolEraser;
        if (Input.WasKeyPressed(Key.F)) _tool = ToolFill;
        if (Input.WasKeyPressed(Key.I)) _tool = ToolPipette;
        if (Input.WasKeyPressed(Key.S)) _tool = ToolSelect;
        if (Input.WasKeyPressed(Key.G)) _grid = !_grid;
        if (Input.WasKeyPressed(Key.H)) Action(ActionFlipX);
        if (Input.WasKeyPressed(Key.V)) Action(ActionFlipY);
        if (Input.WasKeyPressed(Key.R)) Action(ActionRotate);
        if (Input.WasKeyPressed(Key.LeftBracket) && _brushSize > 1) _brushSize--;
        if (Input.WasKeyPressed(Key.RightBracket) && _brushSize < 8) _brushSize++;
        if (Input.WasKeyPressed(Key.Delete)) ClearSelectionPixels();
        if (Input.WasKeyPressed(Key.Escape))
        {
            if (_pasting)
            {
                _pasting = false;
            }
            else if (_hasSelection)
            {
                _hasSelection = false;
            }
            else
            {
                Apps.Back();
            }
        }

        // Arrow keys move the selected tile region on the tilemap.
        if (Input.WasKeyTyped(Key.Left)) SelectTiles(_selTileX - 1, _selTileY, _selTileWidth, _selTileHeight);
        if (Input.WasKeyTyped(Key.Right)) SelectTiles(_selTileX + 1, _selTileY, _selTileWidth, _selTileHeight);
        if (Input.WasKeyTyped(Key.Up)) SelectTiles(_selTileX, _selTileY + 1, _selTileWidth, _selTileHeight);
        if (Input.WasKeyTyped(Key.Down)) SelectTiles(_selTileX, _selTileY - 1, _selTileWidth, _selTileHeight);
    }

    // ---- mouse --------------------------------------------------------------------------------------------

    private void Mouse(int mx, int my)
    {
        bool left = Input.IsMouseButtonDown(MouseButton.Left);
        bool right = Input.IsMouseButtonDown(MouseButton.Right);
        bool leftPressed = Input.WasMouseButtonPressed(MouseButton.Left);
        bool rightPressed = Input.WasMouseButtonPressed(MouseButton.Right);
        bool pressed = leftPressed || rightPressed;
        int wheel = Input.MouseWheel;

        if (!left && !right)
        {
            _painting = false;
            _dragBar = 0;
            _dragTileX = -1;
            _selAnchorX = -1;
        }

        if (_dragBar != 0)
        {
            DragScrollbar(mx, my);
            return;
        }

        // Toolbar.
        if (leftPressed && my >= ToolbarY)
        {
            for (int i = 0; i < ToolbarLabels.Length; i++)
            {
                if (mx >= _toolbarX[i] && mx < _toolbarX[i] + _toolbarWidth[i])
                {
                    ToolbarButton(i);
                }
            }

            return;
        }

        // Scrollbars.
        if (leftPressed && StartScrollbar(mx, my))
        {
            return;
        }

        // Tilemap view.
        if (In(mx, my, ViewX, ViewY, ViewWidth, ViewHeight))
        {
            int tx = _viewScrollX + (mx - ViewX), ty = _viewScrollY + (my - ViewY);
            if (wheel != 0)
            {
                bool shift = Input.IsKeyDown(Key.LeftShift) || Input.IsKeyDown(Key.RightShift);
                if (shift)
                {
                    _viewScrollX = Clamp(_viewScrollX - (wheel * 16), 0, Image.Size - ViewWidth);
                }
                else
                {
                    _viewScrollY = Clamp(_viewScrollY + (wheel * 16), 0, Image.Size - ViewHeight);
                }
            }

            if (_pasting)
            {
                if (leftPressed)
                {
                    Stamp(tx, ty);
                }

                return;
            }

            if (leftPressed)
            {
                _dragTileX = tx / 8;
                _dragTileY = ty / 8;
                SelectTiles(_dragTileX, _dragTileY, 1, 1);
            }
            else if (left && _dragTileX >= 0)
            {
                int x0 = Min(_dragTileX, tx / 8), y0 = Min(_dragTileY, ty / 8);
                SelectTiles(x0, y0, Abs((tx / 8) - _dragTileX) + 1, Abs((ty / 8) - _dragTileY) + 1);
            }

            if (rightPressed)
            {
                // Right click on the tilemap picks the colour under the cursor as secondary.
                _secondary = _image.Get(tx, ty);
            }

            return;
        }

        // Main screen.
        if (In(mx, my, MainX, MainY, MainWidth, MainHeight) || _painting || _selAnchorX >= 0)
        {
            MainMouse(mx, my, left, right, leftPressed, rightPressed, wheel);
            return;
        }

        // Palette.
        if (pressed && In(mx, my, 0, PaletteY, LeftWidth, 26))
        {
            PaletteMouse(mx, my, leftPressed);
            return;
        }

        // Tools and settings.
        if (leftPressed && In(mx, my, 0, ToolsY, LeftWidth, 23))
        {
            int col = (mx - 2) / 11, row = (ToolsY + 22 - my) / 11;
            int index = (row * 8) + col;
            if (col >= 0 && col < 8 && index < IconCount)
            {
                if (index <= ToolSelect)
                {
                    _tool = index;
                    _pasting = false;
                }
                else
                {
                    Action(index);
                }
            }

            return;
        }

        // Region size buttons: N×N tiles at the current position.
        if (leftPressed && In(mx, my, 0, AreaY, LeftWidth, 8))
        {
            int button = AreaButtonAt(mx);
            if (button >= 0)
            {
                SelectTiles(_selTileX, _selTileY, AreaSizes[button], AreaSizes[button]);
            }

            return;
        }

        if (leftPressed && In(mx, my, 0, SettingsY, LeftWidth, 27))
        {
            SettingsClick(mx, my);
        }
    }

    // Region size button under x (or -1): label "AREA" then buttons 10 pixels wide.
    private static int AreaButtonAt(int mx)
    {
        int index = (mx - 22) / 11;
        return mx >= 22 && index < AreaSizes.Length ? index : -1;
    }

    private void MainMouse(int mx, int my, bool left, bool right, bool leftPressed, bool rightPressed, int wheel)
    {
        int ox = _selTileX * 8, oy = _selTileY * 8, w = _selTileWidth * 8, h = _selTileHeight * 8;
        int px = ox + _mainScrollX + FloorDiv(mx - MainX, _zoom);
        int py = oy + _mainScrollY + FloorDiv(my - MainY, _zoom);
        if (wheel != 0)
        {
            SetZoom(Clamp(_zoom + wheel, MinZoom, MaxZoom));
        }

        if (_pasting)
        {
            if (leftPressed)
            {
                Stamp(px, py);
            }

            return;
        }

        bool inside = px >= ox && py >= oy && px < ox + w && py < oy + h;
        if (_tool == ToolPipette)
        {
            if (inside && leftPressed)
            {
                _primary = _image.Get(px, py);
            }

            if (inside && rightPressed)
            {
                _secondary = _image.Get(px, py);
            }

            return;
        }

        if (_tool == ToolFill)
        {
            if (inside && (leftPressed || rightPressed))
            {
                _image.Snapshot();
                _image.Fill(px, py, leftPressed ? _primary : _secondary, _fillGlobal, ox, oy, w, h);
            }

            return;
        }

        if (_tool == ToolSelect)
        {
            if (leftPressed && inside)
            {
                _selAnchorX = px;
                _selAnchorY = py;
                _hasSelection = false;
            }
            else if (left && _selAnchorX >= 0)
            {
                int cx = Clamp(px, ox, ox + w - 1), cy = Clamp(py, oy, oy + h - 1);
                _selX = Min(_selAnchorX, cx);
                _selY = Min(_selAnchorY, cy);
                _selWidth = Abs(cx - _selAnchorX) + 1;
                _selHeight = Abs(cy - _selAnchorY) + 1;
                _hasSelection = true;
            }

            return;
        }

        // Brush and eraser: strokes are drawn along the mouse path.
        if ((leftPressed || rightPressed) && inside)
        {
            _image.Snapshot();
            _painting = true;
            _paintColor = _tool == ToolEraser ? 0 : leftPressed ? _primary : _secondary;
            _lastPaintX = px;
            _lastPaintY = py;
            _image.Brush(px, py, _brushSize, _roundBrush, _paintColor, ox, oy, w, h);
        }
        else if (_painting && (left || right))
        {
            _image.Line(_lastPaintX, _lastPaintY, px, py, _brushSize, _roundBrush, _paintColor, ox, oy, w, h);
            _lastPaintX = px;
            _lastPaintY = py;
        }
    }

    private void PaletteMouse(int mx, int my, bool left)
    {
        if (my >= PaletteY + 18)
        {
            // Header: the menu button.
            if (mx >= LeftWidth - 30)
            {
                _panel = PanelPaletteMenu;
                _menuIndex = 0;
            }

            return;
        }

        int col = (mx - 2) / 5, row = (PaletteY + 17 - my) / 8;
        int index = (row * 16) + col;
        if (col >= 0 && col < 16 && row >= 0 && row < 2)
        {
            if (left)
            {
                _primary = index;
            }
            else
            {
                _secondary = index;
            }
        }
    }

    // Settings panel rows (bottom edge y): title row (tool name, GRID), option row, slider row.
    private const int SettingsTitleY = SettingsY + 19;
    private const int SettingsOptionY = SettingsY + 10;
    private const int SettingsSliderY = SettingsY + 2;
    private const int SliderX = 22, SliderWidth = 58;

    private void SettingsClick(int mx, int my)
    {
        if (my >= SettingsTitleY && mx >= 68)
        {
            _grid = !_grid;
            return;
        }

        bool brush = (_tool == ToolBrush || _tool == ToolEraser) && !_pasting;
        if (my >= SettingsOptionY && my < SettingsOptionY + 8)
        {
            if (brush)
            {
                if (mx >= 2 && mx < 11) _roundBrush = false;          // square
                if (mx >= 12 && mx < 21) _roundBrush = true;          // circle
            }
            else if (_tool == ToolFill && !_pasting)
            {
                _fillGlobal = mx >= 34;                               // AREA | COLOR
            }
            else if (_tool == ToolSelect || _pasting)
            {
                _pasteTransparent = mx < 46;                          // 0 CLEAR | SOLID
            }

            return;
        }

        if (brush && my >= SettingsSliderY && my < SettingsSliderY + 7 && mx >= SliderX - 2)
        {
            _dragBar = 6;
            DragScrollbar(mx, my);
        }
    }

    // ---- scrollbars ---------------------------------------------------------------------------------------

    private bool StartScrollbar(int mx, int my)
    {
        if (In(mx, my, ViewX, 0, ViewWidth, Bar)) _dragBar = 1;
        else if (In(mx, my, ViewX + ViewWidth, ViewY, Bar, ViewHeight)) _dragBar = 2;
        else if (MainScrollsX() && In(mx, my, MainX, MainY - Bar, MainWidth, Bar)) _dragBar = 3;
        else if (MainScrollsY() && In(mx, my, MainX + MainWidth, MainY, Bar, MainHeight)) _dragBar = 4;
        else if (In(mx, my, ZoomX, MainY, ZoomWidth, MainHeight)) _dragBar = 5;
        if (_dragBar != 0)
        {
            DragScrollbar(mx, my);
            return true;
        }

        return false;
    }

    // The thumb follows the mouse: position along the track → scroll offset.
    private void DragScrollbar(int mx, int my)
    {
        if (_dragBar == 1)
        {
            _viewScrollX = Clamp(((mx - ViewX) * (Image.Size - ViewWidth)) / ViewWidth, 0, Image.Size - ViewWidth);
        }
        else if (_dragBar == 2)
        {
            _viewScrollY = Clamp(((my - ViewY) * (Image.Size - ViewHeight)) / ViewHeight, 0, Image.Size - ViewHeight);
        }
        else if (_dragBar == 3)
        {
            _mainScrollX = Clamp(((mx - MainX) * MainRangeX()) / MainWidth, 0, MainRangeX());
            _backgroundDirty = true;
        }
        else if (_dragBar == 4)
        {
            _mainScrollY = Clamp(((my - MainY) * MainRangeY()) / MainHeight, 0, MainRangeY());
            _backgroundDirty = true;
        }
        else if (_dragBar == 5)
        {
            SetZoom(ZoomFromSlider(my));
        }
        else if (_dragBar == 6)
        {
            _brushSize = Clamp(1 + (((mx - SliderX) * 8) / SliderWidth), 1, 8);
        }
    }

    // Zoom slider: bottom = 1, top = 16.
    private const int MinZoom = 1, MaxZoom = 16;

    private static int ZoomFromSlider(int my) => Clamp(MinZoom + (((my - MainY) * MaxZoom) / MainHeight), MinZoom, MaxZoom);

    private static int SliderFromZoom(int zoom) => MainY + (((zoom - MinZoom) * (MainHeight - 4)) / (MaxZoom - MinZoom));

    private void SetZoom(int zoom)
    {
        if (zoom == _zoom)
        {
            return;
        }

        _zoom = zoom;
        _mainScrollX = Clamp(_mainScrollX, 0, MainRangeX());
        _mainScrollY = Clamp(_mainScrollY, 0, MainRangeY());
        _backgroundDirty = true;
    }

    private int MainRangeX() => Max(0, (_selTileWidth * 8) - (MainWidth / _zoom));

    private int MainRangeY() => Max(0, (_selTileHeight * 8) - (MainHeight / _zoom));

    private bool MainScrollsX() => MainRangeX() > 0;

    private bool MainScrollsY() => MainRangeY() > 0;

    private void ClampMainScroll()
    {
        _mainScrollX = Clamp(_mainScrollX, 0, MainRangeX());
        _mainScrollY = Clamp(_mainScrollY, 0, MainRangeY());
    }

    // ---- region, tools, actions ----------------------------------------------------------------------------

    private void SelectTiles(int x, int y, int width, int height)
    {
        width = Clamp(width, 1, 32);
        height = Clamp(height, 1, 32);
        _selTileX = Clamp(x, 0, 32 - width);
        _selTileY = Clamp(y, 0, 32 - height);
        _selTileWidth = width;
        _selTileHeight = height;
        _hasSelection = false;

        // Fit the region into the main screen (at least 2× zoom; scroll if it is larger).
        int fit = Min(MainWidth / (width * 8), MainHeight / (height * 8));
        _zoom = Clamp(fit, 2, 16);
        _mainScrollX = 0;
        _mainScrollY = 0;
        _backgroundDirty = true;

        // Keep the region visible in the tilemap view.
        int px = _selTileX * 8, py = _selTileY * 8;
        if (px < _viewScrollX) _viewScrollX = px;
        if (px + (width * 8) > _viewScrollX + ViewWidth) _viewScrollX = Min(Image.Size - ViewWidth, px + (width * 8) - ViewWidth);
        if (py < _viewScrollY) _viewScrollY = py;
        if (py + (height * 8) > _viewScrollY + ViewHeight) _viewScrollY = Min(Image.Size - ViewHeight, py + (height * 8) - ViewHeight);
    }

    // The area actions work on: the pixel selection, or else the whole region.
    private void Target(out int x, out int y, out int width, out int height)
    {
        if (_hasSelection)
        {
            x = _selX;
            y = _selY;
            width = _selWidth;
            height = _selHeight;
        }
        else
        {
            x = _selTileX * 8;
            y = _selTileY * 8;
            width = _selTileWidth * 8;
            height = _selTileHeight * 8;
        }
    }

    private void Action(int action)
    {
        int x, y, width, height;
        Target(out x, out y, out width, out height);
        if (action == ActionCopy)
        {
            _clip = _image.Read(x, y, width, height);
            _clipWidth = width;
            _clipHeight = height;
            Status("Copied " + width.ToString() + "x" + height.ToString());
            return;
        }

        if (action == ActionPaste)
        {
            if (_clipWidth == 0)
            {
                Status("Nothing to paste: copy first");
                return;
            }

            _pasting = true;
            Status("Click to place  (H/V/R transform, Esc cancels)");
            return;
        }

        // Transforms: the floating paste while pasting, otherwise the selection or region.
        if (_pasting)
        {
            if (action == ActionFlipX) _clip = Image.FlipX(_clip, _clipWidth, _clipHeight);
            if (action == ActionFlipY) _clip = Image.FlipY(_clip, _clipWidth, _clipHeight);
            if (action == ActionRotate)
            {
                _clip = Image.Rotate(_clip, _clipWidth, _clipHeight);
                int t = _clipWidth;
                _clipWidth = _clipHeight;
                _clipHeight = t;
            }

            return;
        }

        if (action == ActionRotate && width != height)
        {
            Status("Rotate needs a square area");
            return;
        }

        byte[] block = _image.Read(x, y, width, height);
        block = action == ActionFlipX ? Image.FlipX(block, width, height)
            : action == ActionFlipY ? Image.FlipY(block, width, height)
            : Image.Rotate(block, width, height);
        _image.Snapshot();
        _image.Write(block, width, height, x, y, false);
    }

    // Places the floating paste with its lower-left corner at (x, y) of the tilemap.
    private void Stamp(int x, int y)
    {
        _image.Snapshot();
        _image.Write(_clip, _clipWidth, _clipHeight, x, y, _pasteTransparent);
        Status("Placed  (click again, Esc to stop)");
    }

    private void ClearSelectionPixels()
    {
        int x, y, width, height;
        Target(out x, out y, out width, out height);
        _image.Snapshot();
        _image.Clear(x, y, width, height, 0);
    }

    private void Undo()
    {
        if (!_image.Undo())
        {
            Status("Nothing to undo");
        }
    }

    private void Redo()
    {
        if (!_image.Redo())
        {
            Status("Nothing to redo");
        }
    }

    // ---- files ------------------------------------------------------------------------------------------

    private void ToolbarButton(int index)
    {
        if (index == 0) OpenExplorer(".png");
        if (index == 1) Save();
        if (index == 2) SaveAs();
        if (index == 3) StartImport();
        if (index == 4) StartExport();
    }

    private void RequestOpen(string path)
    {
        string lower = Strings.ToLower(path);
        if (Strings.EndsWith(lower, ".pal"))
        {
            LoadPalette(path);
            return;
        }

        if (_image.Modified)
        {
            _pendingPath = path;
            _panel = PanelConfirm;
            return;
        }

        OpenImage(path);
    }

    private void OpenImage(string path)
    {
        byte[]? pixels = Workspace.Exists(path) ? Images.LoadTilemap(path) : new byte[Image.Size * Image.Size];
        if (pixels is null)
        {
            Status("Cannot open: " + Images.Error());
            return;
        }

        _image.Load(pixels);
        _path = path;
        _explorerDir = Parent(path);
        Status((Workspace.Exists(path) ? "Opened " : "New ") + FileName(path));

        // Use the project's palette when the image belongs to a project.
        string project = ProjectOf(path);
        if (project.Length > 0 && Workspace.Exists(project + "/palettes/default.pal"))
        {
            LoadPalette(project + "/palettes/default.pal");
        }

        SelectTiles(0, 0, 2, 2);                 // tile 0 is the lower-left one
    }

    private bool Save()
    {
        if (_path.Length == 0)
        {
            SaveAs();
            return false;
        }

        string error = Images.SaveTilemap(_path, _image.Pixels, _theme.Rgb8());
        if (error.Length > 0)
        {
            Status("Save failed: " + error);
            return false;
        }

        _image.Modified = false;
        Status("Saved " + FileName(_path));
        return true;
    }

    private void SaveAs()
    {
        _promptPurpose = 1;
        _promptText = _path.Length > 0 ? _path : Join(_explorerDir, "tiles.png");
        _panel = PanelPrompt;
    }

    private void StartImport()
    {
        if (Apps.OpenDialog("png"))
        {
            _dialog = 1;
            Status("Choose a PNG to import...");
        }
    }

    private void StartExport()
    {
        string name = _path.Length > 0 ? FileName(_path) : "tiles.png";
        if (Apps.SaveDialog(name, "png"))
        {
            _dialog = 2;
            Status("Choose where to export...");
        }
    }

    private void PollDialog()
    {
        if (_dialog == 0)
        {
            return;
        }

        int state = Apps.DialogState();
        if (state == 0)
        {
            return;
        }

        int which = _dialog;
        _dialog = 0;
        if (state != 1)
        {
            Status("Cancelled");
            return;
        }

        if (which == 1)
        {
            byte[]? imported = Images.Import(_theme.Rgb8());
            if (imported is null)
            {
                Status("Import failed: " + Images.Error());
                return;
            }

            _clip = imported;
            _clipWidth = Images.ImportWidth();
            _clipHeight = Images.ImportHeight();
            _pasting = true;
            Status("Imported " + Apps.DialogName() + " " + _clipWidth.ToString() + "x" + _clipHeight.ToString() + ": click to place");
        }
        else
        {
            string error = Images.Export(_image.Pixels, _theme.Rgb8());
            Status(error.Length > 0 ? "Export failed: " + error : "Exported " + Apps.DialogName());
        }
    }

    private void LoadPalette(string path)
    {
        string? text = Workspace.ReadText(path);
        if (text is null || !_theme.Parse(text))
        {
            Status("Not a palette: " + FileName(path));
            return;
        }

        _theme.Path = path;
        BuildIcons();
        _backgroundDirty = true;
        Status("Palette " + FileName(path));
    }

    private void SavePalette(string path)
    {
        string error = Workspace.WriteText(path, _theme.Format());
        if (error.Length > 0)
        {
            Status("Palette save failed: " + error);
            return;
        }

        _theme.Path = path;
        _theme.Modified = false;
        Status("Saved palette " + FileName(path));
    }

    // ---- panels -------------------------------------------------------------------------------------------

    private void PanelInput(int mx, int my)
    {
        if (_panel == PanelExplorer)
        {
            ExplorerInput(mx, my);
        }
        else if (_panel == PanelPrompt)
        {
            PromptInput();
        }
        else if (_panel == PanelConfirm)
        {
            string typed = Strings.ToLower(Input.GetText());
            if (typed == "y" || Input.WasKeyPressed(Key.Enter))
            {
                _panel = PanelNone;
                if (Save())
                {
                    OpenImage(_pendingPath);
                }
            }
            else if (typed == "n")
            {
                _panel = PanelNone;
                OpenImage(_pendingPath);
            }
            else if (Input.WasKeyPressed(Key.Escape))
            {
                _panel = PanelNone;
            }
        }
        else if (_panel == PanelPaletteMenu)
        {
            MenuInput(mx, my);
        }
        else if (_panel == PanelColor)
        {
            ColorInput(mx, my);
        }
    }

    private void MenuInput(int mx, int my)
    {
        if (Input.WasKeyTyped(Key.Up) && _menuIndex > 0) _menuIndex--;
        if (Input.WasKeyTyped(Key.Down) && _menuIndex < PaletteMenu.Length - 1) _menuIndex++;
        if (Input.WasKeyPressed(Key.Escape))
        {
            _panel = PanelNone;
            return;
        }

        int menuTop = PaletteY + 18;
        if (Input.WasMouseButtonPressed(MouseButton.Left))
        {
            int row = (menuTop - 1 - my) / LineHeight;
            if (mx >= 2 && mx < 88 && row >= 0 && row < PaletteMenu.Length)
            {
                _menuIndex = row;
                MenuChoose();
            }
            else
            {
                _panel = PanelNone;
            }

            return;
        }

        if (Input.WasKeyPressed(Key.Enter))
        {
            MenuChoose();
        }
    }

    private void MenuChoose()
    {
        _panel = PanelNone;
        if (_menuIndex == 0)
        {
            _colorIndex = _primary;
            _colorChannel = 0;
            _panel = PanelColor;
        }
        else if (_menuIndex == 1)
        {
            OpenExplorer(".pal");
        }
        else if (_menuIndex == 2)
        {
            if (_theme.Path.Length > 0)
            {
                SavePalette(_theme.Path);
            }
            else
            {
                PromptPaletteName();
            }
        }
        else
        {
            PromptPaletteName();
        }
    }

    private void PromptPaletteName()
    {
        _promptPurpose = 2;
        string project = ProjectOf(_path);
        _promptText = _theme.Path.Length > 0 ? _theme.Path : project.Length > 0 ? project + "/palettes/default.pal" : Join(_explorerDir, "palette.pal");
        _panel = PanelPrompt;
    }

    // Colour editor: R, G and B 0..7 for one palette entry, changed live.
    private void ColorInput(int mx, int my)
    {
        int r = _theme.R[_colorIndex], g = _theme.G[_colorIndex], b = _theme.B[_colorIndex];
        if (Input.WasKeyTyped(Key.Up) && _colorChannel > 0) _colorChannel--;
        if (Input.WasKeyTyped(Key.Down) && _colorChannel < 2) _colorChannel++;
        int delta = (Input.WasKeyTyped(Key.Right) ? 1 : 0) - (Input.WasKeyTyped(Key.Left) ? 1 : 0);

        if (Input.WasMouseButtonPressed(MouseButton.Left))
        {
            // Rows of the dialog: "R - n +" etc. at x 60.. ; "-" at 84..91, "+" at 100..107.
            int row = (150 - my) / 10;
            if (row >= 0 && row < 3)
            {
                _colorChannel = row;
                if (mx >= 84 && mx < 92) delta = -1;
                if (mx >= 100 && mx < 108) delta = 1;
            }
            else if (my < 100 || my > 170 || mx < 52 || mx > 204)
            {
                _panel = PanelNone;
            }
        }

        if (delta != 0)
        {
            if (_colorChannel == 0) r += delta;
            if (_colorChannel == 1) g += delta;
            if (_colorChannel == 2) b += delta;
            _theme.SetColor(_colorIndex, r, g, b);
            BuildIcons();
            _backgroundDirty = true;
        }

        if (Input.WasKeyPressed(Key.Escape) || Input.WasKeyPressed(Key.Enter))
        {
            _panel = PanelNone;
        }
    }

    private void PromptInput()
    {
        string typed = Input.GetText();
        if (typed.Length > 0)
        {
            _promptText += typed;
        }

        if (Input.WasKeyTyped(Key.Backspace) && _promptText.Length > 0)
        {
            _promptText = Strings.Sub(_promptText, 0, _promptText.Length - 1);
        }

        if (Input.WasKeyPressed(Key.Escape))
        {
            _panel = PanelNone;
            return;
        }

        if (!Input.WasKeyPressed(Key.Enter))
        {
            return;
        }

        string path = Strings.Trim(_promptText);
        if (path.Length == 0 || path[0] != '/')
        {
            path = Join(_explorerDir, path);
        }

        _panel = PanelNone;
        if (_promptPurpose == 1)
        {
            string previous = _path;
            _path = path;
            if (!Save())
            {
                _path = previous;
            }
        }
        else
        {
            SavePalette(path);
        }
    }

    private void OpenExplorer(string filter)
    {
        _explorerFilter = filter;
        _panel = PanelExplorer;
        LoadEntries();
    }

    private void LoadEntries()
    {
        _entries.Clear();
        if (_explorerDir != "/")
        {
            _entries.Add("../");
        }

        string? listing = Workspace.List(_explorerDir);
        if (listing is null)
        {
            _explorerDir = "/";
            listing = Workspace.List("/");
        }

        if (listing is not null && listing.Length > 0)
        {
            List<string> all = Strings.Split(listing, '\n');
            for (int i = 0; i < all.Count; i++)
            {
                string entry = all[i];
                string lower = Strings.ToLower(entry);
                if ((Strings.EndsWith(entry, "/") && lower != "bin/" && lower != "obj/") || Strings.EndsWith(lower, _explorerFilter))
                {
                    _entries.Add(entry);
                }
            }
        }

        _explorerIndex = 0;
        _explorerScroll = 0;
    }

    private void ExplorerInput(int mx, int my)
    {
        if (Input.WasKeyTyped(Key.Up) && _explorerIndex > 0) _explorerIndex--;
        if (Input.WasKeyTyped(Key.Down) && _explorerIndex < _entries.Count - 1) _explorerIndex++;
        if (Input.WasKeyPressed(Key.Enter)) ExplorerActivate(_explorerIndex);
        if (Input.WasKeyTyped(Key.Backspace) && _explorerDir != "/")
        {
            _explorerDir = Parent(_explorerDir);
            LoadEntries();
        }

        if (Input.WasKeyPressed(Key.Escape))
        {
            _panel = PanelNone;
            return;
        }

        int rows = ExplorerRows();
        _explorerScroll = Clamp(_explorerScroll - Input.MouseWheel, 0, Max(0, _entries.Count - rows));
        if (_explorerIndex < _explorerScroll) _explorerScroll = _explorerIndex;
        if (_explorerIndex >= _explorerScroll + rows) _explorerScroll = _explorerIndex - rows + 1;

        if (Input.WasMouseButtonPressed(MouseButton.Left))
        {
            int row = (ExplorerTop() - 1 - my) / LineHeight;
            int index = _explorerScroll + row;
            if (row >= 0 && row < rows && index < _entries.Count && mx >= 20 && mx < 236)
            {
                if (index == _lastClickIndex && Machine.TickCount - _lastClickTick < 25)
                {
                    ExplorerActivate(index);
                    _lastClickIndex = -1;
                    return;
                }

                _explorerIndex = index;
                _lastClickIndex = index;
                _lastClickTick = Machine.TickCount;
            }
        }
    }

    private void ExplorerActivate(int index)
    {
        if (index < 0 || index >= _entries.Count)
        {
            return;
        }

        string entry = _entries[index];
        if (entry == "../")
        {
            _explorerDir = Parent(_explorerDir);
            LoadEntries();
        }
        else if (Strings.EndsWith(entry, "/"))
        {
            _explorerDir = Join(_explorerDir, Strings.Sub(entry, 0, entry.Length - 1));
            LoadEntries();
        }
        else
        {
            _panel = PanelNone;
            RequestOpen(Join(_explorerDir, entry));
        }
    }

    private const int ExplorerTopY = 226;

    private int ExplorerTop() => ExplorerTopY;

    private int ExplorerRows() => (ExplorerTopY - 30) / LineHeight;

    // ---- render -------------------------------------------------------------------------------------------

    public void Render()
    {
        if (_backgroundDirty)
        {
            DrawBackground();
            _backgroundDirty = false;
        }

        Graphics.Clear(Pixels);
        Graphics.Clear(Ui);
        DrawPixelsAreas();
        DrawOverlays();
        DrawToolbar();
        DrawPalette();
        DrawTools();
        DrawSettings();
        DrawAreaRow();
        DrawZoomSlider();
        DrawAnimationPlaceholder();
        DrawScrollbars();
        DrawTooltip();
        if (_panel == PanelExplorer) DrawExplorer();
        if (_panel == PanelPrompt) DrawPrompt();
        if (_panel == PanelConfirm) DrawConfirm();
        if (_panel == PanelPaletteMenu) DrawMenu();
        if (_panel == PanelColor) DrawColorEditor();
    }

    // Panels and checkerboards (under transparent pixels); redrawn only when layout or palette change.
    private void DrawBackground()
    {
        Graphics.Clear(Back);
        Graphics.FillRect(Back, 0, 0, Screen, Screen, _theme.Panel);
        Checker(ViewX, ViewY, ViewWidth, ViewHeight, 4);
        int w = Min(MainWidth, _selTileWidth * 8 * _zoom), h = Min(MainHeight, _selTileHeight * 8 * _zoom);
        Graphics.FillRect(Back, MainX, MainY, MainWidth, MainHeight, _theme.Dark);
        Checker(MainX, MainY, w, h, Max(2, _zoom / 2));
    }

    private void Checker(int x, int y, int width, int height, int size)
    {
        Graphics.FillRect(Back, x, y, width, height, _theme.Dark);
        for (int cy = 0; cy < height; cy += size)
        {
            for (int cx = ((cy / size) % 2) * size; cx < width; cx += size * 2)
            {
                Graphics.FillRect(Back, x + cx, y + cy, Min(size, width - cx), Min(size, height - cy), _theme.Check);
            }
        }
    }

    private void DrawPixelsAreas()
    {
        byte[] pixels = _image.Pixels;
        Graphics.DrawPixels(Pixels, ViewX, ViewY, pixels, Image.Size, _viewScrollX, _viewScrollY, ViewWidth, ViewHeight, 1, true);

        int ox = _selTileX * 8, oy = _selTileY * 8;
        int w = Min(MainWidth / _zoom, (_selTileWidth * 8) - _mainScrollX);
        int h = Min(MainHeight / _zoom, (_selTileHeight * 8) - _mainScrollY);
        if (w > 0 && h > 0)
        {
            Graphics.DrawPixels(Pixels, MainX, MainY, pixels, Image.Size, ox + _mainScrollX, oy + _mainScrollY, w, h, _zoom, true);
        }
    }

    private void DrawOverlays()
    {
        // Tile grid on the tilemap.
        if (_grid)
        {
            for (int gx = 8 - (_viewScrollX % 8); gx < ViewWidth; gx += 8)
            {
                Graphics.FillRect(Ui, ViewX + gx, ViewY, 1, ViewHeight, _theme.Mid);
            }

            for (int gy = 8 - (_viewScrollY % 8); gy < ViewHeight; gy += 8)
            {
                Graphics.FillRect(Ui, ViewX, ViewY + gy, ViewWidth, 1, _theme.Mid);
            }
        }

        // The selected region on the tilemap.
        Outline(Ui, ViewX + (_selTileX * 8) - _viewScrollX - 1, ViewY + (_selTileY * 8) - _viewScrollY - 1,
            (_selTileWidth * 8) + 2, (_selTileHeight * 8) + 2, ViewX, ViewY, ViewWidth, ViewHeight, (_blink / 15) % 2 == 0 ? _theme.Accent : _theme.Light);

        // Pixel selection in the main screen.
        if (_hasSelection)
        {
            int sx = MainX + ((_selX - (_selTileX * 8) - _mainScrollX) * _zoom);
            int sy = MainY + ((_selY - (_selTileY * 8) - _mainScrollY) * _zoom);
            Outline(Ui, sx - 1, sy - 1, (_selWidth * _zoom) + 2, (_selHeight * _zoom) + 2, MainX, MainY, MainWidth, MainHeight, (_blink / 10) % 2 == 0 ? _theme.Accent : _theme.Dark);
        }

        // Floating paste following the mouse, in either area.
        int mx = Input.MouseX, my = Input.MouseY;
        if (_pasting && _panel == PanelNone)
        {
            if (In(mx, my, ViewX, ViewY, ViewWidth, ViewHeight))
            {
                PreviewPaste(mx, my, 1, ViewX, ViewY, ViewWidth, ViewHeight);
            }
            else if (In(mx, my, MainX, MainY, MainWidth, MainHeight))
            {
                // Snap to the zoomed pixel grid.
                int cx = MainX + (FloorDiv(mx - MainX, _zoom) * _zoom), cy = MainY + (FloorDiv(my - MainY, _zoom) * _zoom);
                PreviewPaste(cx, cy, _zoom, MainX, MainY, MainWidth, MainHeight);
            }
        }
        else if (_panel == PanelNone && (_tool == ToolBrush || _tool == ToolEraser) && In(mx, my, MainX, MainY, MainWidth, MainHeight))
        {
            // Brush outline under the cursor.
            int half = (_brushSize - 1) / 2;
            int cx = MainX + ((FloorDiv(mx - MainX, _zoom) - half) * _zoom), cy = MainY + ((FloorDiv(my - MainY, _zoom) - half) * _zoom);
            if (_roundBrush && _brushSize > 2)
            {
                BrushShapeOutline(cx, cy, _zoom, MainX, MainY, MainWidth, MainHeight, _theme.Light);
            }
            else
            {
                Outline(Ui, cx, cy, _brushSize * _zoom, _brushSize * _zoom, MainX, MainY, MainWidth, MainHeight, _theme.Light);
            }
        }
    }

    // The outline of the round brush: an edge wherever a brush cell borders a cell outside the circle.
    private void BrushShapeOutline(int x, int y, int scale, int clipX, int clipY, int clipWidth, int clipHeight, int color)
    {
        int size = _brushSize;
        for (int dy = 0; dy < size; dy++)
        {
            for (int dx = 0; dx < size; dx++)
            {
                if (!Image.InCircle(dx, dy, size))
                {
                    continue;
                }

                int px = x + (dx * scale), py = y + (dy * scale);
                if (dx == 0 || !Image.InCircle(dx - 1, dy, size)) VLine(Ui, px, py, scale, clipX, clipY, clipWidth, clipHeight, color);
                if (dx == size - 1 || !Image.InCircle(dx + 1, dy, size)) VLine(Ui, px + scale - 1, py, scale, clipX, clipY, clipWidth, clipHeight, color);
                if (dy == 0 || !Image.InCircle(dx, dy - 1, size)) HLine(Ui, px, py, scale, clipX, clipY, clipWidth, clipHeight, color);
                if (dy == size - 1 || !Image.InCircle(dx, dy + 1, size)) HLine(Ui, px, py + scale - 1, scale, clipX, clipY, clipWidth, clipHeight, color);
            }
        }
    }

    private void PreviewPaste(int x, int y, int scale, int clipX, int clipY, int clipWidth, int clipHeight)
    {
        int w = Min(_clipWidth, (clipX + clipWidth - x) / scale), h = Min(_clipHeight, (clipY + clipHeight - y) / scale);
        if (w > 0 && h > 0)
        {
            Graphics.DrawPixels(Ui, x, y, _clip, _clipWidth, 0, 0, w, h, scale, _pasteTransparent);
        }

        Outline(Ui, x - 1, y - 1, (_clipWidth * scale) + 2, (_clipHeight * scale) + 2, clipX, clipY, clipWidth, clipHeight, _theme.Accent);
    }

    private void DrawToolbar()
    {
        Graphics.FillRect(Ui, 0, ToolbarY, Screen, 11, _theme.Mid);
        int mx = Input.MouseX, my = Input.MouseY;
        for (int i = 0; i < ToolbarLabels.Length; i++)
        {
            bool hover = my >= ToolbarY && mx >= _toolbarX[i] && mx < _toolbarX[i] + _toolbarWidth[i];
            Graphics.FillRect(Ui, _toolbarX[i], ToolbarY + 1, _toolbarWidth[i], 9, hover ? _theme.Accent : _theme.Panel);
            Text.Draw(Ui, ToolbarLabels[i], _toolbarX[i] + 3, ToolbarY + 2, hover ? _theme.Dark : _theme.Light, Font);
        }

        int x = _toolbarX[4] + _toolbarWidth[4] + 6;
        string text = _statusTicks > 0 ? _status : (_image.Modified ? "*" : "") + (_path.Length > 0 ? FileName(_path) : "untitled");
        int room = (Screen - x - 2) / CharWidth;
        if (text.Length > room)
        {
            text = Strings.Sub(text, 0, Max(0, room - 2)) + "..";
        }

        Text.Draw(Ui, text, x, ToolbarY + 2, _statusTicks > 0 ? _theme.Accent : _theme.Light, Font);
    }

    private void DrawPalette()
    {
        int y = PaletteY;
        Graphics.FillRect(Ui, 0, y + 25, LeftWidth, 1, _theme.Mid);
        Text.Draw(Ui, "PALETTE", 2, y + 18, _theme.Light, Font);

        // Primary / secondary colours.
        Graphics.FillRect(Ui, 33, y + 18, 7, 6, _theme.Light);
        Graphics.FillRect(Ui, 34, y + 19, 5, 4, _primary);
        Graphics.FillRect(Ui, 42, y + 18, 7, 6, _theme.Mid);
        Graphics.FillRect(Ui, 43, y + 19, 5, 4, _secondary);
        Text.Draw(Ui, "MENU", LeftWidth - 19, y + 18, _theme.Accent, Font);

        for (int i = 0; i < Theme.Colors; i++)
        {
            int col = i % 16, row = i / 16;
            int sx = 2 + (col * 5), sy = y + 10 - (row * 8);
            if (i == 0)
            {
                Graphics.FillRect(Ui, sx, sy, 4, 7, _theme.Dark);
                Graphics.FillRect(Ui, sx + 1, sy + 1, 2, 5, _theme.Check);     // 0 = transparent
            }
            else
            {
                Graphics.FillRect(Ui, sx, sy, 4, 7, i);
            }

            if (i == _primary)
            {
                Graphics.FillRect(Ui, sx, sy + 7, 4, 1, _theme.Light);
            }

            if (i == _secondary)
            {
                Graphics.FillRect(Ui, sx, sy - 1, 4, 1, _theme.Mid);
            }
        }
    }

    private void DrawTools()
    {
        Graphics.FillRect(Ui, 0, ToolsY + 23, LeftWidth, 1, _theme.Mid);
        for (int i = 0; i < IconCount; i++)
        {
            int col = i % 8, row = i / 8;
            int x = 2 + (col * 11), y = ToolsY + 12 - (row * 11);
            bool active = i == _tool && !_pasting || (i == ActionPaste && _pasting);
            Graphics.FillRect(Ui, x, y, 10, 10, active ? _theme.Accent : _theme.Mid);
            Graphics.DrawPixels(Ui, x + 1, y + 1, _icons[i], 7, 0, 0, 7, 7, 1, true);
            if (active)
            {
                // Dark icon on the accent background.
                Graphics.DrawPixels(Ui, x + 1, y + 1, Recolored(i, _theme.Dark), 7, 0, 0, 7, 7, 1, true);
            }
        }
    }

    private void DrawSettings()
    {
        Graphics.FillRect(Ui, 0, SettingsY + 26, LeftWidth, 1, _theme.Mid);
        string name = _pasting ? "PASTE" : ToolNames[_tool];
        Text.Draw(Ui, name, 2, SettingsTitleY + 1, _theme.Accent, Font);
        Button(68, SettingsTitleY, 21, "GRID", _grid);

        bool brush = (_tool == ToolBrush || _tool == ToolEraser) && !_pasting;
        int y = SettingsOptionY;
        if (brush)
        {
            // Shape: square | circle.
            Button(2, y, 9, "", !_roundBrush);
            Graphics.FillRect(Ui, 4, y + 2, 5, 4, !_roundBrush ? _theme.Dark : _theme.Light);
            Button(12, y, 9, "", _roundBrush);
            int ink = _roundBrush ? _theme.Dark : _theme.Light;
            Graphics.FillRect(Ui, 15, y + 2, 3, 4, ink);
            Graphics.FillRect(Ui, 14, y + 3, 5, 2, ink);
            Text.Draw(Ui, _roundBrush ? "CIRCLE" : "SQUARE", 24, y + 1, _theme.Light, Font);

            // Size slider 1..8.
            int sy = SettingsSliderY;
            Text.Draw(Ui, "SIZE", 2, sy + 1, _theme.Light, Font);
            Graphics.FillRect(Ui, SliderX, sy + 3, SliderWidth, 1, _theme.Mid);
            int size = _brushSize;
            int thumb = SliderX + (((size - 1) * (SliderWidth - 3)) / 7);
            Graphics.FillRect(Ui, thumb, sy, 3, 7, _theme.Accent);
            Text.Draw(Ui, size.ToString(), SliderX + SliderWidth + 3, sy + 1, _theme.Light, Font);
        }
        else if (_tool == ToolFill && !_pasting)
        {
            Button(2, y, 31, "AREA", !_fillGlobal);
            Button(34, y, 27, "COLOR", _fillGlobal);
        }
        else if (_tool == ToolSelect || _pasting)
        {
            Button(2, y, 43, "0 CLEAR", _pasteTransparent);
            Button(46, y, 27, "SOLID", !_pasteTransparent);
        }
    }

    // A small toggle button: the accent background when on.
    private void Button(int x, int y, int width, string label, bool on)
    {
        Graphics.FillRect(Ui, x, y, width, 8, on ? _theme.Accent : _theme.Mid);
        if (label.Length > 0)
        {
            Text.Draw(Ui, label, x + ((width - (label.Length * CharWidth)) / 2) + 1, y + 1, on ? _theme.Dark : _theme.Light, Font);
        }
    }

    // Region size buttons: AREA 1 2 4 8 16 32 (tiles per side).
    private void DrawAreaRow()
    {
        Text.Draw(Ui, "AREA", 2, AreaY + 1, _theme.Light, Font);
        for (int i = 0; i < AreaSizes.Length; i++)
        {
            int n = AreaSizes[i];
            Button(22 + (i * 11), AreaY, 10, n.ToString(), _selTileWidth == n && _selTileHeight == n);
        }
    }

    // Vertical zoom slider right of the main screen: bottom 1x, top 16x.
    private void DrawZoomSlider()
    {
        Graphics.FillRect(Ui, ZoomX, MainY, ZoomWidth, MainHeight, _theme.Dark);
        Graphics.FillRect(Ui, ZoomX + 2, MainY + 1, 2, MainHeight - 2, _theme.Mid);
        Graphics.FillRect(Ui, ZoomX, SliderFromZoom(_zoom), ZoomWidth, 4, _theme.Accent);
    }

    private void DrawAnimationPlaceholder()
    {
        Graphics.FillRect(Ui, 0, 78, LeftWidth, 1, _theme.Mid);
        Text.Draw(Ui, "ANIMATION", 2, 69, _theme.Mid, Font);
        Text.Draw(Ui, "timeline, frames", 2, 59, _theme.Mid, Font);
        Text.Draw(Ui, "and sprites:", 2, 52, _theme.Mid, Font);
        Text.Draw(Ui, "next stage", 2, 45, _theme.Mid, Font);
        Graphics.FillRect(Ui, LeftWidth - 1, 0, 1, ToolbarY, _theme.Mid);
    }

    // ---- tooltips -----------------------------------------------------------------------------------------

    private const int TooltipDelay = 20;

    private void DrawTooltip()
    {
        if (_hoverTicks < TooltipDelay || _panel != PanelNone)
        {
            return;
        }

        int mx = Input.MouseX, my = Input.MouseY;
        string text = TooltipAt(mx, my);
        if (text.Length == 0)
        {
            return;
        }

        int width = (text.Length * CharWidth) + 5, height = 9;
        int x = Clamp(mx + 6, 0, Screen - width), y = my - height - 4;
        if (y < 0)
        {
            y = my + 8;
        }

        Graphics.FillRect(Ui, x, y, width, height, _theme.Dark);
        Outline(Ui, x, y, width, height, 0, 0, Screen, Screen, _theme.Light);
        Text.Draw(Ui, text, x + 3, y + 2, _theme.Light, Font);
    }

    private static readonly string[] ToolKeys = { "B", "E", "F", "I", "S", "CTRL+C", "CTRL+V", "H", "V", "R" };

    private string TooltipAt(int mx, int my)
    {
        if (In(mx, my, ZoomX, MainY, ZoomWidth, MainHeight))
        {
            int zoom = _zoom;
            return "ZOOM " + zoom.ToString() + "X";
        }

        if (In(mx, my, 0, ToolsY, LeftWidth, 23))
        {
            int col = (mx - 2) / 11, row = (ToolsY + 22 - my) / 11;
            int index = (row * 8) + col;
            if (mx >= 2 && col < 8 && index < IconCount && ((mx - 2) % 11) < 10)
            {
                return ToolNames[index] + " (" + ToolKeys[index] + ")";
            }

            return "";
        }

        if (In(mx, my, 0, AreaY, LeftWidth, 8))
        {
            int button = AreaButtonAt(mx);
            if (button >= 0)
            {
                int n = AreaSizes[button];
                return "REGION " + n.ToString() + "X" + n.ToString() + " TILES";
            }

            return "";
        }

        if (In(mx, my, 0, PaletteY, LeftWidth, 18))
        {
            int col = (mx - 2) / 5, row = (PaletteY + 17 - my) / 8;
            int index = (row * 16) + col;
            if (mx >= 2 && col < 16 && index < Theme.Colors)
            {
                return index == 0 ? "COLOR 0 (TRANSPARENT)" : "COLOR " + index.ToString();
            }

            return "";
        }

        if (In(mx, my, 0, SettingsY, LeftWidth, 27))
        {
            bool brush = (_tool == ToolBrush || _tool == ToolEraser) && !_pasting;
            if (my >= SettingsTitleY && mx >= 68) return "TILE GRID (G)";
            if (brush && my >= SettingsOptionY && my < SettingsOptionY + 8)
            {
                if (mx >= 2 && mx < 11) return "SQUARE BRUSH";
                if (mx >= 12 && mx < 21) return "ROUND BRUSH";
            }

            if (brush && my >= SettingsSliderY && my < SettingsSliderY + 7) return "BRUSH SIZE ([ ])";
        }

        return "";
    }

    private void DrawScrollbars()
    {
        Scrollbar(ViewX, 0, ViewWidth, Bar, _viewScrollX, Image.Size - ViewWidth, ViewWidth, Image.Size, true);
        Scrollbar(ViewX + ViewWidth, ViewY, Bar, ViewHeight, _viewScrollY, Image.Size - ViewHeight, ViewHeight, Image.Size, false);
        if (MainScrollsX())
        {
            Scrollbar(MainX, MainY - Bar, MainWidth, Bar, _mainScrollX, MainRangeX(), MainWidth / _zoom, _selTileWidth * 8, true);
        }

        if (MainScrollsY())
        {
            Scrollbar(MainX + MainWidth, MainY, Bar, MainHeight, _mainScrollY, MainRangeY(), MainHeight / _zoom, _selTileHeight * 8, false);
        }
    }

    private void Scrollbar(int x, int y, int width, int height, int value, int range, int visible, int total, bool horizontal)
    {
        Graphics.FillRect(Ui, x, y, width, height, _theme.Dark);
        int length = horizontal ? width : height;
        int thumb = Max(4, (length * visible) / Max(1, total));
        int position = range <= 0 ? 0 : ((length - thumb) * value) / range;
        if (horizontal)
        {
            Graphics.FillRect(Ui, x + position, y + 1, thumb, height - 2, _theme.Mid);
        }
        else
        {
            Graphics.FillRect(Ui, x + 1, y + position, width - 2, thumb, _theme.Mid);
        }
    }

    private void DrawExplorer()
    {
        Box(16, 20, Screen - 32, ExplorerTopY + 12 - 20);
        Text.Draw(Ui, (_explorerFilter == ".pal" ? "LOAD PALETTE  " : "OPEN  ") + _explorerDir, 20, ExplorerTopY + 3, _theme.Accent, Font);
        int rows = ExplorerRows();
        for (int r = 0; r < rows; r++)
        {
            int index = _explorerScroll + r;
            if (index >= _entries.Count)
            {
                break;
            }

            int y = ExplorerTopY - ((r + 1) * LineHeight);
            if (index == _explorerIndex)
            {
                Graphics.FillRect(Ui, 18, y, Screen - 36, LineHeight, _theme.Mid);
            }

            string entry = _entries[index];
            Text.Draw(Ui, (Strings.EndsWith(entry, "/") ? "[D] " : "    ") + entry, 20, y + 1, Strings.EndsWith(entry, "/") ? _theme.Accent : _theme.Light, Font);
        }

        Text.Draw(Ui, "Enter open  Backspace up  Esc close", 20, 22, _theme.Mid, Font);
    }

    private void DrawPrompt()
    {
        Box(16, 108, Screen - 32, 34);
        Text.Draw(Ui, _promptPurpose == 1 ? "SAVE IMAGE AS (workspace path)" : "SAVE PALETTE AS (workspace path)", 20, 132, _theme.Accent, Font);
        Graphics.FillRect(Ui, 20, 118, Screen - 40, 9, _theme.Dark);
        string shown = _promptText.Length > 52 ? Strings.Sub(_promptText, _promptText.Length - 52) : _promptText;
        Text.Draw(Ui, shown, 22, 119, _theme.Light, Font);
        if ((_blink / 24) % 2 == 0)
        {
            Graphics.FillRect(Ui, 22 + (shown.Length * CharWidth), 119, 1, 6, _theme.Accent);
        }

        Text.Draw(Ui, "Enter save   Esc cancel", 20, 110, _theme.Mid, Font);
    }

    private void DrawConfirm()
    {
        Box(40, 108, Screen - 80, 30);
        Text.Draw(Ui, "Save this file?", 46, 128, _theme.Accent, Font);
        Text.Draw(Ui, _path.Length > 0 ? FileName(_path) : "untitled", 46, 120, _theme.Light, Font);
        Text.Draw(Ui, "Y yes  N no  Esc cancel", 46, 111, _theme.Light, Font);
    }

    private void DrawMenu()
    {
        int top = PaletteY + 18;
        Box(1, top - (PaletteMenu.Length * LineHeight) - 2, 88, (PaletteMenu.Length * LineHeight) + 3);
        for (int i = 0; i < PaletteMenu.Length; i++)
        {
            int y = top - ((i + 1) * LineHeight);
            if (i == _menuIndex)
            {
                Graphics.FillRect(Ui, 2, y, 86, LineHeight, _theme.Mid);
            }

            Text.Draw(Ui, PaletteMenu[i], 4, y + 1, _theme.Light, Font);
        }
    }

    private void DrawColorEditor()
    {
        Box(52, 100, 152, 70);
        Text.Draw(Ui, "COLOR " + _colorIndex.ToString() + "   arrows / click  Esc", 56, 161, _theme.Accent, Font);
        Graphics.FillRect(Ui, 58, 106, 20, 44, _theme.Light);
        Graphics.FillRect(Ui, 59, 107, 18, 42, _colorIndex);
        string[] names = { "R", "G", "B" };
        int[] values = { _theme.R[_colorIndex], _theme.G[_colorIndex], _theme.B[_colorIndex] };
        for (int c = 0; c < 3; c++)
        {
            int y = 142 - (c * 10);
            if (c == _colorChannel)
            {
                Graphics.FillRect(Ui, 82, y - 1, 60, 8, _theme.Mid);
            }

            Text.Draw(Ui, names[c], 84 - 8, y, _theme.Light, Font);
            Text.Draw(Ui, "-", 86, y, _theme.Accent, Font);
            int value = values[c];
            Text.Draw(Ui, value.ToString(), 94, y, _theme.Light, Font);
            Text.Draw(Ui, "+", 102, y, _theme.Accent, Font);

            // A bar showing the value 0..7.
            Graphics.FillRect(Ui, 112, y + 1, 28, 4, _theme.Dark);
            Graphics.FillRect(Ui, 112, y + 1, values[c] * 4, 4, _theme.Light);
        }
    }

    private void Box(int x, int y, int width, int height)
    {
        Graphics.FillRect(Ui, x, y, width, height, _theme.Dark);
        Outline(Ui, x, y, width, height, 0, 0, Screen, Screen, _theme.Accent);
    }

    // A one-pixel rectangle outline clipped to a rectangle.
    private static void Outline(int layer, int x, int y, int width, int height, int clipX, int clipY, int clipWidth, int clipHeight, int color)
    {
        HLine(layer, x, y, width, clipX, clipY, clipWidth, clipHeight, color);
        HLine(layer, x, y + height - 1, width, clipX, clipY, clipWidth, clipHeight, color);
        VLine(layer, x, y, height, clipX, clipY, clipWidth, clipHeight, color);
        VLine(layer, x + width - 1, y, height, clipX, clipY, clipWidth, clipHeight, color);
    }

    private static void HLine(int layer, int x, int y, int width, int cx, int cy, int cw, int ch, int color)
    {
        if (y < cy || y >= cy + ch)
        {
            return;
        }

        int x0 = Max(x, cx), x1 = Min(x + width, cx + cw);
        if (x1 > x0)
        {
            Graphics.FillRect(layer, x0, y, x1 - x0, 1, color);
        }
    }

    private static void VLine(int layer, int x, int y, int height, int cx, int cy, int cw, int ch, int color)
    {
        if (x < cx || x >= cx + cw)
        {
            return;
        }

        int y0 = Max(y, cy), y1 = Min(y + height, cy + ch);
        if (y1 > y0)
        {
            Graphics.FillRect(layer, x, y0, 1, y1 - y0, color);
        }
    }

    // ---- icons --------------------------------------------------------------------------------------------

    private void BuildIcons()
    {
        _icons = new byte[IconCount][];
        for (int i = 0; i < IconCount; i++)
        {
            _icons[i] = Recolored(i, _theme.Light);
        }
    }

    // An icon's 7×7 pixels in one colour (rows from the bottom).
    private static byte[] Recolored(int icon, int color)
    {
        byte[] pixels = new byte[49];
        List<string> rows = Strings.Split(IconArt[icon], ' ');
        for (int r = 0; r < 7 && r < rows.Count; r++)
        {
            string row = rows[r];
            for (int c = 0; c < 7 && c < row.Length; c++)
            {
                if (row[c] == '#')
                {
                    pixels[((6 - r) * 7) + c] = (byte)color;
                }
            }
        }

        return pixels;
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private void Status(string text)
    {
        _status = text;
        _statusTicks = 240;
    }

    private static string ProjectOf(string path)
    {
        if (path.Length == 0)
        {
            return "";
        }

        string dir = Parent(path);
        while (true)
        {
            if (Projects.IsProject(dir))
            {
                return dir;
            }

            if (dir == "/")
            {
                return "";
            }

            dir = Parent(dir);
        }
    }

    private static string DefaultPalette()
    {
        // The ZTX16 default palette (as in new projects), in case the workspace has none.
        return "0 0 1\n0 0 0\n2 2 2\n4 4 4\n6 6 6\n7 7 7\n4 0 0\n7 0 0\n7 4 4\n7 3 0\n4 2 0\n7 7 0\n7 7 4\n0 3 0\n0 6 0\n4 7 2\n"
            + "0 3 3\n0 7 7\n0 0 4\n0 2 7\n3 5 7\n3 0 4\n6 0 6\n7 4 7\n5 4 3\n3 2 1\n1 2 3\n2 4 5\n5 6 7\n6 5 2\n2 1 0\n1 1 1\n";
    }

    private static bool In(int x, int y, int rx, int ry, int rw, int rh) => x >= rx && y >= ry && x < rx + rw && y < ry + rh;

    private static string Parent(string path)
    {
        int slash = Strings.LastIndexOf(path, '/');
        return slash <= 0 ? "/" : Strings.Sub(path, 0, slash);
    }

    private static string Join(string directory, string name) => directory == "/" ? "/" + name : directory + "/" + name;

    private static string FileName(string path) => Strings.Sub(path, Strings.LastIndexOf(path, '/') + 1);

    private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

    private static int Min(int a, int b) => a < b ? a : b;

    private static int Max(int a, int b) => a > b ? a : b;

    private static int Abs(int v) => v < 0 ? -v : v;

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -(((-a) + b - 1) / b);
}
