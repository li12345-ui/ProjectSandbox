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

    // -------- 接线后的系统实例 --------
    private CollisionSystem? _collision;
    private PlayerMovement? _player;
    private CameraController? _camera;
    private SaveSystem? _save;
    private HealthSystem? _playerHealth;
    private DamageSystem? _damage;
    private InventoryUI? _inventoryUI;
    private LootSystem? _loot;
    private EnemyAI? _enemy;

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

        // HealthSystem Tick：无敌帧递减等
        _playerHealth?.Tick((float)delta);

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
    /// 惰性构建世界：注册表 → 生成器 → 网格 → 渲染 → 玩家 → 相机 → 背包 → 存档。
    /// 与事件总线同样走"每帧试探直到服务就绪"的补偿模式，构建成功后置位短路。
    /// </summary>
    private void TryInitWorld()
    {
        if (_worldReady || !ServiceLocator.IsRegistered<IDataLoader>())
            return;

        var loader = ServiceLocator.Instance.Get<IDataLoader>();

        // 输入服务必须在任何 WorldInputController 之前初始化——
        // 否则 IsActionPressed 查询未注册 action 会触发引擎报错（经验 #301911）
        InputService.Instance.Initialize(loader);

        var registry = new BlockRegistry(loader);
        var generator = new WorldGenerator(loader, registry);
        var grid = generator.Generate();

        // 调试渲染器（TileMapLayer 占位纯色图集）
        var renderer = new WorldRenderer();
        renderer.Initialize(grid, registry);
        AddChild(renderer);

        // 碰撞系统（纯 C# AABB 分轴滑动）
        _collision = new CollisionSystem(grid, registry, WorldRenderer.TileSize);

        // —— 玩家实体 ——
        _player = new PlayerMovement();
        _player.SetCollisionSystem(_collision); // R1：接入瓦片碰撞（切掉 MoveAndSlide）
        // 起始位置：地表第 12 行（空行底）正上方，中心对齐地图宽度
        _player.Position = new Vector2(
            grid.Width * WorldRenderer.TileSize / 2f,
            WorldRenderer.TileSize * 11 - 40 // 站在第 12 行空块底部
        );
        // 玩家碰撞形状（CharacterBody2D 必须有 CollisionShape2D 才能 MoveAndSlide）
        var playerShape = new CollisionShape2D();
        playerShape.Shape = new RectangleShape2D { Size = new Vector2(24, 40) };
        _player.AddChild(playerShape);
        AddChild(_player);

        // —— 相机（替换 WorldCamera）——
        var worldBounds = new Rect2(
            0, 0,
            grid.Width * WorldRenderer.TileSize,
            grid.Height * WorldRenderer.TileSize
        );
        _camera = new CameraController(_player, worldBounds);
        AddChild(_camera);

        // —— 交互三件套（共用同一网格）——
        var interaction = new BlockInteraction(grid, registry);
        AddChild(new WorldInputController(interaction, renderer, registry));

        // —— 背包 UI（CanvasLayer 挂根，不随相机飘）——
        _inventoryUI = new InventoryUI(loader);
        var canvas = new CanvasLayer();
        canvas.Layer = 10; // UI 层，高于 TileMapLayer
        canvas.AddChild(_inventoryUI);
        AddChild(canvas);

        // —— 生命 + 伤害 ——
        _playerHealth = new HealthSystem(100);
        _damage = new DamageSystem();

        // —— 掉落 ——
        _loot = new LootSystem(new List<LootSystem.LootTable>()); // 空表，等 loot.json 加载

        // —— 敌人（在地表第 16 行，玩家右侧 100px）——
        _enemy = new EnemyAI();
        _enemy.Position = _player.Position + new Vector2(100, 0);
        _enemy.SetTarget(_player);
        _enemy.SetCollisionSystem(_collision);
        _enemy.SetHealthSystem(new HealthSystem(_enemy.MaxHp));
        _enemy.SetDamageSystem(_damage);
        _enemy.SetLootSystem(_loot, "enemy_default");
        AddChild(_enemy);

        // —— 存档 ——
        _save = new SaveSystem();

        // 注入完整性断言（审查 P1-耦合防线：任一 setter 漏调此处即炸）
        System.Diagnostics.Debug.Assert(_collision != null, "TryInitWorld：CollisionSystem 未注入");
        System.Diagnostics.Debug.Assert(_player != null, "TryInitWorld：PlayerMovement 未创建");
        System.Diagnostics.Debug.Assert(_camera != null, "TryInitWorld：CameraController 未创建");
        System.Diagnostics.Debug.Assert(_playerHealth != null, "TryInitWorld：玩家 HealthSystem 未创建");
        System.Diagnostics.Debug.Assert(_damage != null, "TryInitWorld：DamageSystem 未创建");
        System.Diagnostics.Debug.Assert(_inventoryUI != null, "TryInitWorld：InventoryUI 未创建");

        _worldReady = true;
        GD.Print($"[Game] 世界已生成并挂载全部系统：{generator.Width}x{generator.Height}");
    }

    // -------- 输入：存档快捷键 --------

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_save == null || !_worldReady) return;

        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (key.Keycode == Key.F5)
            {
                var data = BuildSaveData();
                if (_save.Save(0, data))
                    GD.Print($"[Game] 存档已保存到 slot 0（位置={data.Player.Pos[0]:F0},{data.Player.Pos[1]:F0} HP={data.Player.Hp}/{data.Player.MaxHp}）");
                else
                    GD.PushWarning("[Game] 存档保存失败");
            }
            else if (key.Keycode == Key.F9)
            {
                var data = _save.Load(0);
                if (data != null)
                {
                    GD.Print($"[Game] 已加载存档 slot 0（位置={data.Player.Pos[0]:F0},{data.Player.Pos[1]:F0} HP={data.Player.Hp}/{data.Player.MaxHp}）");
                    ApplySaveData(data);
                }
                else
                {
                    GD.PushWarning("[Game] 存档加载失败（文件不存在或版本不兼容）");
                }
            }
            else if (key.Keycode == Key.F2)
            {
                // 调试：扣 10 血看无敌帧/死亡触发
                if (_playerHealth != null && _damage != null)
                    _damage.ApplyDamage(_playerHealth, 10, defense: 0);
            }
        }
    }

    // -------- 存档桥接 --------

    private SaveSystem.SaveData BuildSaveData()
    {
        var data = new SaveSystem.SaveData();

        // 玩家位置 + HP
        if (_player != null)
        {
            data.Player.Pos = new double[] { _player.Position.X, _player.Position.Y };
            data.Player.Hp = _playerHealth?.CurrentHp ?? 0;
            data.Player.MaxHp = _playerHealth?.MaxHp ?? 100;
            data.Player.HotbarSize = 5;
        }

        // 背包槽位（空槽不落盘）
        if (_inventoryUI != null)
            data.Player.Inventory = _inventoryUI.SaveInventorySlot();

        // 时间戳
        data.Header.Seed = 20261003; // 固定种子（占位：等世界种子系统落地后注入）
        data.Header.CreatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return data;
    }

    private void ApplySaveData(SaveSystem.SaveData data)
    {
        // 恢复玩家位置
        if (_player != null && data.Player.Pos != null && data.Player.Pos.Length >= 2)
        {
            _player.Position = new Vector2((float)data.Player.Pos[0], (float)data.Player.Pos[1]);
        }

        // 恢复 HP（静默：不触发 OnRevived/OnDamaged——存档加载不计入统计事件）
        if (_playerHealth != null)
        {
            if (data.Player.MaxHp > 0) _playerHealth.SetMaxHp(data.Player.MaxHp);
            _playerHealth.SetHpSilently(data.Player.Hp);
        }

        // 恢复背包
        if (_inventoryUI != null && data.Player.Inventory.Count > 0)
            _inventoryUI.LoadFromSaveData(data.Player.Inventory);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            // 退出时自动存档 slot 0
            if (_save != null && _worldReady)
            {
                var data = BuildSaveData();
                _save.Save(0, data);
                GD.Print("[Game] 退出前自动存档 slot 0");
            }
            GetTree().Quit();
        }
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
