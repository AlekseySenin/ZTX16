using System.Diagnostics;
using SDL;
using ZTX16.Platform.Graphics;
using ZTX16.Platform.Authoring;
using ZTX16.Platform.Runtime;
using static SDL.SDL3;

namespace ZTX16.Host;

/// <summary>
/// Reference host: owns the SDL3 window, presentation, audio device and event pump,
/// and drives a <see cref="RuntimeSession"/> with the fixed 60 Hz loop.
/// </summary>
/// <remarks>
/// Presentation uses SDL's renderer on its OpenGL driver: the composed 256×256 frame is
/// uploaded to a streaming texture and shown with integer logical-presentation scaling
/// and nearest sampling, which also produces the letterbox borders.
/// Host hotkeys (not forwarded to the session): F11 toggles fullscreen, F10 cycles the
/// window scale.
/// </remarks>
internal sealed unsafe class SdlHost : IDisposable
{
    private const int Width = GraphicsSystem.DisplayWidth;
    private const int Height = GraphicsSystem.DisplayHeight;
    private const int AudioSampleRate = 48000;

    private readonly byte[] _frame = new byte[Width * Height];
    private readonly uint[] _argb = new uint[Width * Height];

    private SDL_Window* _window;
    private SDL_Renderer* _renderer;
    private SDL_Texture* _texture;
    private SdlAudioOutput? _audio;
    private SdlInputTranslator? _input;
    private int _scale;
    private bool _fullscreen;
    private bool _minimized;
    private readonly string _title;

    // The system mouse arrow, drawn by the host over the display (the OS cursor is hidden above it).
    private int _cursorX = -1, _cursorY = -1;       // render coordinates, top-left origin
    private bool _mouseInWindow;
    private bool _osCursorHidden;

    public SdlHost(HostOptions options, string title = "ZTX16")
    {
        _scale = options.Scale;
        _fullscreen = options.Fullscreen;
        _title = title;
    }

    public const int SampleRate = AudioSampleRate;

    private ZTX16.Platform.Authoring.AppEnvironment? _environment;

    /// <summary>
    /// Runs the ZTX16 environment (Console and editors, plus cartridges they start) until the Console exits or
    /// the window closes. F12 returns to the Console.
    /// </summary>
    public int RunEnvironment(ZTX16.Platform.Authoring.AppEnvironment environment, string? edit = null)
    {
        CreateWindowAndRenderer();
        _environment = environment;
        environment.Clipboard = new SdlClipboard();
        environment.CodeAnalysis = new HostCodeAnalysis();
        environment.Dialogs = new SdlFileDialogs(_window);
        environment.AppFailed += (app, message) => Console.Error.WriteLine($"ZTX16: {app.Name} failed: {message}");
        environment.Start();
        // The environment opens in the code editor (it reopens the last file, or the Hello World project);
        // F1 switches to the Console, F2 back to the editor.
        if (environment.Active is { } shell && environment.SystemApps.Contains("code"))
        {
            string editor = edit is not null && (edit.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || edit.EndsWith(".pal", StringComparison.OrdinalIgnoreCase))
                && environment.SystemApps.Contains("graphics") ? "graphics" : "code";
            environment.Launch(shell, editor, edit ?? string.Empty);
            environment.AfterTick();
        }

        ZTX16.Platform.Authoring.AppSession? bound = null;
        var warned = new HashSet<RuntimeSession>();
        var clock = Stopwatch.StartNew();
        var scheduler = new TickScheduler(Stopwatch.Frequency);
        while (environment.Active is { } active)
        {
            if (active != bound)
            {
                // App switch: input, audio and the window title follow the active app.
                if (_input is null)
                {
                    _input = new SdlInputTranslator(active.Session.Input);
                    _audio = new SdlAudioOutput(active.Session.Audio);
                }
                else
                {
                    _input.Retarget(active.Session.Input);
                    _audio!.Engine = active.Session.Audio;
                }

                if (warned.Add(active.Session))
                {
                    string name = active.Name;
                    active.Session.Machine.Warned += message => Console.Error.WriteLine($"ZTX16: {name}: warning: {message}");
                }

                SDL_SetWindowTitle(_window, active.Title);
                bound = active;
            }

            if (!PumpEvents(active.Session))
            {
                break;
            }

            if (environment.Active != active)
            {
                continue;   // F12 switched apps while events were handled
            }

            if (_minimized)
            {
                SDL_Delay(10);
                continue;
            }

            long now = clock.ElapsedTicks;
            if (!scheduler.IsTickDue(now))
            {
                TimeSpan wait = scheduler.ToTimeSpan(scheduler.TimeUntilNextTick(now));
                SDL_DelayPrecise((ulong)(wait.Ticks * 100));
                continue;
            }

            scheduler.TickStarted(now);
            active.Session.Machine.SuspendRequested = false;
            try
            {
                active.Session.RunTick(scheduler.ToTimeSpan(now));
            }
            catch (Exception e) when (e is ZTX16.VM.VmTrapException or ZTX16.VM.LinkException)
            {
                if (!environment.Fail(e.Message))
                {
                    break;
                }

                continue;
            }

            Present(active.Session.Graphics, active.Session.Input);
            if (!environment.AfterTick())
            {
                break;
            }
        }

        environment.Shutdown();
        return 0;
    }

    /// <summary>Runs <paramref name="program"/> until it exits or the window closes. Returns the process exit code.</summary>
    public int Run(IRuntimeProgram program, ResourceStore? resources = null, ZTX16.Platform.Storage.StorageSystem? storage = null)
    {
        CreateWindowAndRenderer();

        var session = new RuntimeSession(program, AudioSampleRate, resources, storage);
        session.Machine.Warned += message => Console.Error.WriteLine($"ZTX16: warning: {message}");
        _input = new SdlInputTranslator(session.Input);
        _audio = new SdlAudioOutput(session.Audio);

        var clock = Stopwatch.StartNew();
        var scheduler = new TickScheduler(Stopwatch.Frequency);

        // An unhandled exception leaves this method without Deinitialize; the host's
        // Dispose then performs emergency cleanup, as the lifecycle contract requires.
        session.Initialize();
        while (PumpEvents(session) && !session.ShouldExit)
        {
            // The MVP runtime does not honor cartridge suspend requests.
            session.Machine.SuspendRequested = false;

            if (_minimized)
            {
                // Runtime-controlled suspend: no ticks while minimized. The scheduler's
                // no-catch-up rule makes the session continue from the next tick.
                SDL_Delay(10);
                continue;
            }

            long now = clock.ElapsedTicks;
            if (scheduler.IsTickDue(now))
            {
                scheduler.TickStarted(now);
                session.RunTick(scheduler.ToTimeSpan(now));
                Present(session.Graphics, session.Input);
            }
            else
            {
                TimeSpan wait = scheduler.ToTimeSpan(scheduler.TimeUntilNextTick(now));
                SDL_DelayPrecise((ulong)(wait.Ticks * 100));
            }
        }

        session.Deinitialize();
        return 0;
    }

    public void Dispose()
    {
        _audio?.Dispose();
        _input?.Dispose();
        if (_texture != null)
        {
            SDL_DestroyTexture(_texture);
        }

        if (_renderer != null)
        {
            SDL_DestroyRenderer(_renderer);
        }

        if (_window != null)
        {
            SDL_DestroyWindow(_window);
        }

        SDL_Quit();
    }

    private void CreateWindowAndRenderer()
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_AUDIO | SDL_InitFlags.SDL_INIT_GAMEPAD))
        {
            throw new InvalidOperationException($"SDL_Init failed: {SDL_GetError()}");
        }

        _window = SDL_CreateWindow(_title, Width * _scale, Height * _scale, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
        if (_window == null)
        {
            throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL_GetError()}");
        }

        _renderer = SDL_CreateRenderer(_window, "opengl");
        if (_renderer == null)
        {
            throw new InvalidOperationException($"OpenGL renderer unavailable: {SDL_GetError()}");
        }

        SDL_SetRenderLogicalPresentation(_renderer, Width, Height, SDL_RendererLogicalPresentation.SDL_LOGICAL_PRESENTATION_INTEGER_SCALE);

        // Text input events (Input.GetText) for typing; key events keep flowing as well.
        SDL_StartTextInput(_window);

        _texture = SDL_CreateTexture(_renderer, SDL_PixelFormat.SDL_PIXELFORMAT_ARGB8888, SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING, Width, Height);
        if (_texture == null)
        {
            throw new InvalidOperationException($"SDL_CreateTexture failed: {SDL_GetError()}");
        }

        SDL_SetTextureScaleMode(_texture, SDL_ScaleMode.SDL_SCALEMODE_NEAREST);

        if (_fullscreen)
        {
            SDL_SetWindowFullscreen(_window, true);
        }
    }

    private void Present(GraphicsSystem graphics, ZTX16.Platform.Input.InputSystem input)
    {
        graphics.Compose(_frame);
        graphics.ConvertToArgb(_frame, _argb);
        bool overDisplay = _mouseInWindow && _cursorX >= 0 && _cursorY >= 0 && _cursorX < Width && _cursorY < Height;
        if (overDisplay != _osCursorHidden)
        {
            // Over the display the host arrow replaces the OS cursor; on the letterbox borders the OS one shows.
            _osCursorHidden = overDisplay;
            if (overDisplay)
            {
                SDL_HideCursor();
            }
            else
            {
                SDL_ShowCursor();
            }
        }

        if (overDisplay && input.CursorVisible)
        {
            MouseCursor.Draw(_argb, Width, Height, _cursorX, _cursorY);
        }

        fixed (uint* pixels = _argb)
        {
            SDL_UpdateTexture(_texture, null, (IntPtr)pixels, Width * sizeof(uint));
        }

        SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 255);
        SDL_RenderClear(_renderer);
        SDL_RenderTexture(_renderer, _texture, null, null);
        SDL_RenderPresent(_renderer);
    }

    /// <summary>Drains host events. Returns false when the host should shut down.</summary>
    private bool PumpEvents(RuntimeSession session)
    {
        SDL_Event e;
        while (SDL_PollEvent(&e))
        {
            switch (e.Type)
            {
                case SDL_EventType.SDL_EVENT_QUIT:
                    return false;

                case SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED:
                    _minimized = true;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_RESTORED:
                    _minimized = false;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                    session.Input.EnqueueReleaseAll();
                    break;

                case SDL_EventType.SDL_EVENT_KEY_DOWN when HandleHostHotkey(e.key.scancode):
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_MOUSE_ENTER:
                    _mouseInWindow = true;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_MOUSE_LEAVE:
                    _mouseInWindow = false;
                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                    // Window pixels → logical 256×256 render coordinates (top-left origin).
                    SDL_ConvertEventToRenderCoordinates(_renderer, &e);
                    if (e.Type == SDL_EventType.SDL_EVENT_MOUSE_MOTION)
                    {
                        _mouseInWindow = true;
                        _cursorX = (int)MathF.Floor(e.motion.x);
                        _cursorY = (int)MathF.Floor(e.motion.y);
                    }

                    _input!.Translate(e);
                    break;

                default:
                    _input!.Translate(e);
                    break;
            }
        }

        return true;
    }

    // Switches to a system app; one that is not installed yet sends the user to the Console with a note.
    private bool SwitchApp(string app, string description)
    {
        if (!_environment!.SwitchTo(app))
        {
            _environment.Notify("The " + description + " is not installed yet.");
        }

        _environment.AfterTick();
        return true;
    }

    private bool HandleHostHotkey(SDL_Scancode scancode)
    {
        switch (scancode)
        {
            case SDL_Scancode.SDL_SCANCODE_F11:
                _fullscreen = !_fullscreen;
                SDL_SetWindowFullscreen(_window, _fullscreen);
                return true;

            // Function keys switch between the environment's apps (also out of a running cartridge):
            // F1 help, F2 Console, F3 code editor, F4 graphics editor, F5 sound editor, F6 font editor.
            case SDL_Scancode.SDL_SCANCODE_F1 when _environment is not null:
                return SwitchApp("help", "help (API reference)");
            case SDL_Scancode.SDL_SCANCODE_F2 when _environment is not null:
            case SDL_Scancode.SDL_SCANCODE_F12 when _environment is not null:
                return SwitchApp(AppEnvironment.ShellApp, "Console");
            case SDL_Scancode.SDL_SCANCODE_F3 when _environment is not null:
                return SwitchApp("code", "code editor");
            case SDL_Scancode.SDL_SCANCODE_F4 when _environment is not null:
                return SwitchApp("graphics", "graphics editor");
            case SDL_Scancode.SDL_SCANCODE_F5 when _environment is not null:
                return SwitchApp("sound", "sound editor");
            case SDL_Scancode.SDL_SCANCODE_F6 when _environment is not null:
                return SwitchApp("font", "font editor");

            case SDL_Scancode.SDL_SCANCODE_F10 when !_fullscreen:
                _scale = _scale >= 4 ? 1 : _scale + 1;
                SDL_SetWindowSize(_window, Width * _scale, Height * _scale);
                return true;

            default:
                return false;
        }
    }
}
