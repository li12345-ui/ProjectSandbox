using Godot;
using ProjectSandbox.Core;

namespace ProjectSandbox;

/// <summary>
/// 主循环驱动器 Node：在 SceneTree 的 _Process 中驱动 MainLoop（固定步长策略类）。
/// 接入 IEventBus 发布 LoopTick 性能事件的工作延后到场景树集成时，Main.cs 将通过
/// 字段注入传入已注册的 locator 实例；当前版本 Game 独立运行，性能计数在
/// MainLoop 内部自洽。
/// </summary>
public partial class Game : Node
{
    /// <summary>固定步长策略实例（MainLoop 是纯 C# 类，无 Godot 依赖）。</summary>
    /// 完全限定名消歧义：MainLoop 与 Godot.MainLoop 同名。
    public ProjectSandbox.Core.MainLoop Loop { get; } = new();

    public override void _Ready()
    {
        GD.Print($"[Game] 就绪：FixedDt={Loop.FixedDt:F4}s MaxAccum={Loop.MaxAccumulator:F2}s");
    }

    public override void _Process(double delta)
    {
        // 推进固定步长（内部含 accumulator + 上限截断 + 帧耗时采样 + FPS 滚动平均）
        Loop.Step(delta);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
            GetTree().Quit();
    }
}

/// <summary>性能计数事件载体（只读）。UI/HUD 层订阅显示。</summary>
public readonly struct LoopTickEvent
{
    public readonly double Fps;
    public readonly int FrameSteps;
    public readonly long TotalSteps;
    public readonly long FrameDurationNs;
    public readonly bool IsPaused;

    public LoopTickEvent(double fps, int frameSteps, long totalSteps, long frameDurationNs, bool isPaused)
    {
        Fps = fps;
        FrameSteps = frameSteps;
        TotalSteps = totalSteps;
        FrameDurationNs = frameDurationNs;
        IsPaused = isPaused;
    }
}
