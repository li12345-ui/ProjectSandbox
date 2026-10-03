using Godot;
using ProjectSandbox.Core;
using ProjectSandbox.World;

namespace ProjectSandbox;

/// <summary>
/// 主循环驱动器 Node：在 SceneTree 的 _Process 中驱动 MainLoop（固定步长策略类），
/// 并把每帧性能计数发布为 LoopTick 事件。经 ServiceLocator 静态单例获取 IEventBus——
/// 这是 Godot Node 无法构造注入下的务实让步（详见 ServiceLocator.Instance 注释）。
/// </summary>
public partial class Game : Node
{
    /// <summary>固定步长策略实例（MainLoop 是纯 C# 类，无 Godot 依赖）。</summary>
    /// 完全限定名消歧义：MainLoop 与 Godot.MainLoop 同名。
    public ProjectSandbox.Core.MainLoop Loop { get; } = new();

    private IEventBus? _eventBus;
    private bool _worldReady;

    public override void _Ready()
    {
        GD.Print($"[Game] 就绪：FixedDt={Loop.FixedDt:F4}s MaxAccum={Loop.MaxAccumulator:F2}s");
    }

    public override void _Process(double delta)
    {
        // 首帧惰性初始化：Godot 的 _Ready 子先于父，组合根（Main._Ready）注册服务
        // 发生在本节点 _Ready 之后，故只能在 _Process 里补取（首帧时必已完成注册）
        if (_eventBus == null && ServiceLocator.IsRegistered<IEventBus>())
            _eventBus = ServiceLocator.Instance.Get<IEventBus>();

        TryInitWorld();

        // 1) 推进固定步长（内部含 accumulator + 上限截断 + 帧耗时采样 + FPS 滚动平均）
        Loop.Step(delta);

        // 2) 发布性能计数事件（UI/HUD 订阅显示 FPS/步数）
        _eventBus?.Publish(new LoopTickEvent(
            fps: Loop.Fps,
            frameSteps: Loop.FrameStepsPerformed,
            totalSteps: Loop.TotalStepsPerformed,
            frameDurationNs: Loop.FrameDurationNs,
            isPaused: Loop.IsPaused
        ));
    }

    /// <summary>
    /// 惰性构建世界：注册表 → 生成器 → 网格 → 调试渲染器（挂为本节点子级）。
    /// 与事件总线同样走"每帧试探直到服务就绪"的补偿模式，构建成功后置位短路。
    /// </summary>
    private void TryInitWorld()
    {
        if (_worldReady || !ServiceLocator.IsRegistered<IDataLoader>())
            return;

        var loader = ServiceLocator.Instance.Get<IDataLoader>();
        var registry = new BlockRegistry(loader);
        var generator = new WorldGenerator(loader, registry);
        var grid = generator.Generate();

        var renderer = new WorldRenderer();
        renderer.Initialize(grid, registry);
        AddChild(renderer);

        // 交互三件套共用同一网格：规则（BlockInteraction）改数据，渲染器只负责同步显示
        var interaction = new BlockInteraction(grid, registry);
        AddChild(new WorldCamera(new Rect2(0, 0, grid.Width * WorldRenderer.TileSize, grid.Height * WorldRenderer.TileSize)));
        AddChild(new WorldInputController(interaction, renderer, registry));

        _worldReady = true;
        GD.Print($"[Game] 世界已生成并挂载渲染/相机/输入：{generator.Width}x{generator.Height}");
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
            GetTree().Quit();
    }
}

/// <summary>性能计数事件载体（只读）。UI/HUD 层订阅显示。</summary>
/// 必须是引用类型：IEventBus.Publish&lt;TEvent&gt; 约束 where TEvent : class。
public sealed class LoopTickEvent
{
    public double Fps { get; }
    public int FrameSteps { get; }
    public long TotalSteps { get; }
    public long FrameDurationNs { get; }
    public bool IsPaused { get; }

    public LoopTickEvent(double fps, int frameSteps, long totalSteps, long frameDurationNs, bool isPaused)
    {
        Fps = fps;
        FrameSteps = frameSteps;
        TotalSteps = totalSteps;
        FrameDurationNs = frameDurationNs;
        IsPaused = isPaused;
    }
}