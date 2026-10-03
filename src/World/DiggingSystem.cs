using System;
using Godot;

namespace ProjectSandbox.World;

/// <summary>
/// 挖掘系统：范围限制 + 工具速度 + 冷却 + 目标高亮。
/// 纯逻辑层调用现有 BlockInteraction.Mine() 做实际挖掘，不重写挖掘规则；
/// 渲染层在 _Draw 画目标格高亮（可挖黄/超范围红/不可挖灰）。
///
/// 约束：本文件为新增，尚未在 Game.cs 接线——需后续任务注入 player/cursor 状态。
/// </summary>
public sealed partial class DiggingSystem : Node2D
{
    /// <summary>挖掘范围（格）——以 origin 格为中心，切比雪夫距离 ≤ DigRange。</summary>
    [Export] public int DigRange { get; set; } = 5;

    /// <summary>工具挖掘速度（Hardness 每秒削减量）。值越大挖得越快；hardness=10 speed=10 → 1秒挖穿。</summary>
    [Export] public float ToolSpeed { get; set; } = 10f;

    /// <summary>每次挖掘完成后的冷却秒数——防 spam。</summary>
    [Export] public float Cooldown { get; set; } = 0.15f;

    private readonly BlockGrid _grid;
    private readonly BlockRegistry _registry;
    private readonly BlockInteraction _interaction;
    private readonly int _tileSize;

    private float _cooldownTimer;
    private float _digProgress;

    /// <summary>瞄准格；(-1,-1) 哨兵值表无目标（经验 #705870：避免 Signal Nullable<T>）。</summary>
    private Vector2I _cursorCell = new(-1, -1);

    /// <summary>范围中心（玩家所在格）；(-1,-1) 哨兵值表未设置。</summary>
    private Vector2I _originCell = new(-1, -1);

    /// <summary>本帧挖掘按键是否按下——外部每帧通过 SetMinePressed 更新。</summary>
    private bool _minePressed;

    public DiggingSystem(BlockGrid grid, BlockRegistry registry, BlockInteraction interaction, int tileSize)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        if (tileSize <= 0) throw new ArgumentOutOfRangeException(nameof(tileSize));
        _tileSize = tileSize;
    }

    // -------- 运行时注入 --------

    public void SetCursor(int cellX, int cellY)
    {
        var newCursor = new Vector2I(cellX, cellY);
        if (newCursor != _cursorCell)
        {
            _cursorCell = newCursor;
            _digProgress = 0f; // 跳格重置进度，防穿墙挖穿
            QueueRedraw();
        }
    }

    public void SetOrigin(int cellX, int cellY)
    {
        var newOrigin = new Vector2I(cellX, cellY);
        if (newOrigin != _originCell)
        {
            _originCell = newOrigin;
            _digProgress = 0f; // 玩家移动导致 origin 变化时重置进度——防跨范围挖穿（审查 P1-状态漂移）
            QueueRedraw();
        }
    }

    public void SetMinePressed(bool pressed) => _minePressed = pressed;

    // -------- 每帧逻辑 --------

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // 冷却递减
        if (_cooldownTimer > 0f)
            _cooldownTimer = Mathf.Max(0f, _cooldownTimer - dt);

        TryDig(dt);
    }

    /// <summary>
    /// 核心挖掘判定：范围 → 冷却 → Hardness 进度 → 调 BlockInteraction.Mine。
    /// 切比雪夫距离 max(|dx|,|dy|) ≤ DigRange 为可达范围（方形，沙盒游戏标准）。
    /// </summary>
    private void TryDig(float dt)
    {
        if (!_minePressed) return;
        if (!IsValidCell(_cursorCell)) return;
        if (!InRange(_cursorCell)) return;
        if (_cooldownTimer > 0f) return;

        ushort idx = _grid.Get(_cursorCell.X, _cursorCell.Y);
        if (!_registry.IsMinable(idx)) return; // 空块 / minable=false 跳过

        int hardness = _registry.GetDef(idx).Hardness;
        _digProgress += ToolSpeed * dt;

        if (_digProgress >= hardness)
        {
            _interaction.Mine(_cursorCell.X, _cursorCell.Y);
            _digProgress = 0f;
            _cooldownTimer = Cooldown;
        }
    }

    // -------- 范围校验 --------

    private bool IsValidCell(Vector2I cell) => cell.X >= 0 && cell.Y >= 0 && _grid.InBounds(cell.X, cell.Y);

    private bool InRange(Vector2I cell)
    {
        if (_originCell.X < 0) return false; // 未设置 origin
        int dx = Mathf.Abs(cell.X - _originCell.X);
        int dy = Mathf.Abs(cell.Y - _originCell.Y);
        return Mathf.Max(dx, dy) <= DigRange; // 切比雪夫距离
    }

    // -------- 渲染 --------

    public override void _Draw()
    {
        if (!IsValidCell(_cursorCell)) return;

        ushort idx = _grid.Get(_cursorCell.X, _cursorCell.Y);
        bool inRange = InRange(_cursorCell);
        bool minable = _registry.IsMinable(idx);

        // 三态高亮：范围内可挖=黄描边；范围外=红；范围内不可挖=灰
        Color color = (!inRange) ? Colors.Red : (minable ? Colors.Yellow : Colors.Gray);
        float thickness = inRange && minable ? 2f : 1f;
        var rect = new Rect2(
            _cursorCell.X * _tileSize, _cursorCell.Y * _tileSize,
            _tileSize, _tileSize);

        DrawRect(rect, color, false, thickness);

        // 挖掘进度条（可挖且正在挖时显示）
        if (inRange && minable && _minePressed && _digProgress > 0f)
        {
            int hardness = _registry.GetDef(idx).Hardness;
            float ratio = Mathf.Clamp(_digProgress / hardness, 0f, 1f);
            var barBg = new Rect2(rect.Position.X, rect.Position.Y - 4, rect.Size.X, 3);
            var barFg = new Rect2(rect.Position.X, rect.Position.Y - 4, rect.Size.X * ratio, 3);
            DrawRect(barBg, Colors.Black);
            DrawRect(barFg, Colors.Orange);
        }
    }
}
