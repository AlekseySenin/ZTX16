using ZTX16.Input;
using ZTX16.Runtime;

namespace ZTX16.Tests;

public class TickSchedulerTests
{
    private const long Frequency = 60_000; // 1000 clock units per tick

    [Fact]
    public void FirstTickIsImmediatelyDue()
    {
        var scheduler = new TickScheduler(Frequency);
        Assert.True(scheduler.IsTickDue(5));
    }

    [Fact]
    public void NextTickIsOnePeriodLater()
    {
        var scheduler = new TickScheduler(Frequency);
        scheduler.IsTickDue(0);
        scheduler.TickStarted(0);

        Assert.False(scheduler.IsTickDue(999));
        Assert.True(scheduler.IsTickDue(1000));
    }

    [Fact]
    public void SmallJitterKeepsCadence()
    {
        var scheduler = new TickScheduler(Frequency);
        scheduler.IsTickDue(0);
        scheduler.TickStarted(0);
        scheduler.TickStarted(1300); // 300 late

        Assert.True(scheduler.IsTickDue(2000));
    }

    [Fact]
    public void OverrunDoesNotCatchUp()
    {
        var scheduler = new TickScheduler(Frequency);
        scheduler.IsTickDue(0);
        scheduler.TickStarted(0);

        // The tick due at 1000 starts at 5000: four periods were lost.
        scheduler.TickStarted(5000);

        Assert.False(scheduler.IsTickDue(5999));
        Assert.True(scheduler.IsTickDue(6000));
    }
}

public class RuntimeSessionTests
{
    private sealed class RecordingProgram : IRuntimeProgram
    {
        public List<string> Calls { get; } = [];

        public RuntimeSession? Session { get; private set; }

        public void Initialize(RuntimeSession session)
        {
            Session = session;
            Calls.Add("init");
        }

        public void Tick() => Calls.Add($"tick:{Session!.Machine.TickCount}");

        public void Render() => Calls.Add("render");

        public void Deinitialize() => Calls.Add("deinit");
    }

    [Fact]
    public void RunsLifecycleInFixedOrder()
    {
        var program = new RecordingProgram();
        var session = new RuntimeSession(program, 48000);

        session.Initialize();
        session.RunTick(TimeSpan.Zero);
        session.RunTick(TimeSpan.FromSeconds(1.0 / 60));
        session.Deinitialize();
        session.Deinitialize();

        Assert.Equal(["init", "tick:0", "render", "tick:1", "render", "deinit"], program.Calls);
        Assert.Equal(2, session.Machine.TickCount);
    }

    [Fact]
    public void InputIsAppliedBeforeGameLogic()
    {
        var program = new RecordingProgram();
        var session = new RuntimeSession(program, 48000);
        session.Initialize();

        session.Input.Enqueue(InputEvent.KeyChange(Key.Space, true));
        session.RunTick(TimeSpan.Zero);

        Assert.True(session.Input.Keyboard.WasPressed(Key.Space));
    }

    [Fact]
    public void ExitIsARequest()
    {
        var session = new RuntimeSession(new RecordingProgram(), 48000);
        session.Machine.Exit();
        Assert.True(session.ShouldExit);
    }
}

public class InputTests
{
    [Fact]
    public void PressedOnlyForTheTickOfTheTransition()
    {
        var input = new InputSystem();
        input.Enqueue(InputEvent.KeyChange(Key.A, true));
        input.BeginTick();
        Assert.True(input.Keyboard[Key.A] is { IsDown: true, Pressed: true });

        input.BeginTick();
        Assert.True(input.Keyboard[Key.A] is { IsDown: true, Pressed: false });

        input.Enqueue(InputEvent.KeyChange(Key.A, false));
        input.BeginTick();
        Assert.True(input.Keyboard[Key.A] is { IsDown: false, Released: true });
    }

    [Fact]
    public void TapWithinOneTickReportsBothTransitions()
    {
        var input = new InputSystem();
        input.Enqueue(InputEvent.KeyChange(Key.Z, true));
        input.Enqueue(InputEvent.KeyChange(Key.Z, false));
        input.BeginTick();

        Assert.True(input.Keyboard[Key.Z] is { IsDown: false, Pressed: true, Released: true });
    }

    [Fact]
    public void MouseDeltaIsPerTick()
    {
        var input = new InputSystem();
        input.Enqueue(InputEvent.MouseMoved(10, 20));
        input.BeginTick();
        input.Enqueue(InputEvent.MouseMoved(15, 18));
        input.BeginTick();

        Assert.Equal(5, input.Mouse.DeltaX);
        Assert.Equal(-2, input.Mouse.DeltaY);

        input.BeginTick();
        Assert.Equal(0, input.Mouse.DeltaX);
    }

    [Fact]
    public void GamepadDisconnectReleasesButtonsAndZeroesAxes()
    {
        var input = new InputSystem();
        input.Enqueue(InputEvent.GamepadConnection(1, true));
        input.Enqueue(InputEvent.GamepadButtonChange(1, GamepadButton.Action1, true));
        input.Enqueue(InputEvent.GamepadAxisChange(1, GamepadAxis.LeftStickX, 0.5f));
        input.BeginTick();

        input.Enqueue(InputEvent.GamepadConnection(1, false));
        input.BeginTick();

        Gamepad pad = input.Gamepads[1];
        Assert.False(pad.IsConnected);
        Assert.True(pad[GamepadButton.Action1].Released);
        Assert.Equal(0f, pad.GetAxis(GamepadAxis.LeftStickX));
    }

    [Fact]
    public void ReleaseAllPreventsStuckKeys()
    {
        var input = new InputSystem();
        input.Enqueue(InputEvent.KeyChange(Key.LeftShift, true));
        input.BeginTick();
        input.EnqueueReleaseAll();
        input.BeginTick();

        Assert.True(input.Keyboard[Key.LeftShift] is { IsDown: false, Released: true });
    }
}
