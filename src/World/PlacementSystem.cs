using System;
using Godot;

namespace ProjectSandbox.World;

/// <summary>
/// 放置系统：合法位置 + 碰撞检测 + 消耗物品 + 预览。
/// 与 DiggingSystem 同架构模式——纯逻辑层调用 BlockInteraction.Place() 做实际放置，
/// 不重写放置规则；_Draw 画预览（可放=绿半透明 / 不可放=红描边）。
///
/// 约束：本文件为新增，尚未在 Game.cs 接线——需后续任务注入 player/cursor/inventory 状态。
/// </summary>
public sealed partial class PlacementSystem : Node2D
{
    /// <summary>放置范围（格）——以 origin 格为中心，切比雪夫距离 ≤ PlaceRange。</summary>
    [Export] public int PlaceRange { get; set; } = 5;

    private readonly BlockGrid _grid;
    private readonly BlockRegistry _registry;
    private readonly BlockInteraction _interaction;
    private readonly int _tileSize;

    /// <summary>瞄准格；(-1,-1) 哨兵值表无目标。</summary>
    private Vector2I _cursorCell = new(-1, -1);

    /// <summary>范围中心（玩家所在格）；(-1,-1) 哨兵值表未设置。</summary>
    private Vector2I _originCell = new(-1, -1);

    /// <summary>玩家 AABB（世界像素坐标）；空 Rect2 表未设置——此时跳过碰撞检测。</summary>
    private Rect2 _playerAABB;

    /// <summary>当前准备放置的块索引；0 = 空 = 不放置。</summary>
    private ushort _previewBlockIndex;

    /// <summary>放置按键是否按下——外部每帧通过 SetPlacePressed 更新。</summary>
    private bool _placePressed;

    /// <summary>
    /// 消耗物品回调：块索引 → 是否成功扣除库存。
    /// null 时默认返回 true（调试模式不消耗）——项目尚无 InventorySystem。
    /// </summary>
    private Func<ushort, bool>? _tryConsume;

    public PlacementSystem(BlockGrid grid, BlockRegistry registry, BlockInteraction interaction, int tileSize)
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
            QueueRedraw();
        }
    }

    public void SetOrigin(int cellX, int cellY)
    {
        _originCell = new Vector2I(cellX, cellY);
        QueueRedraw();
    }

    public void SetPlayerAABB(Rect2 aabb)
    {
        _playerAABB = aabb;
        QueueRedraw();
    }

    public void SetPreviewBlock(ushort blockIndex)
    {
        if (_previewBlockIndex != blockIndex)
        {
            _previewBlockIndex = blockIndex;
            QueueRedraw();
        }
    }

    public void SetPlacePressed(bool pressed) => _placePressed = pressed;

    public void SetConsumeCallback(Func<ushort, bool>? tryConsume) => _tryConsume = tryConsume;

    // -------- 每帧逻辑 --------

    public override void _Process(double delta)
    {
        if (!_placePressed) return;
        TryPlace();
    }

    /// <summary>
    /// 核心放置判定：cursor 有效 → 范围 → 目标格空 → Solid 块 AABB 不重叠 → 消耗 → Place。
    /// 成功或失败都只处理一次——_Process 每帧调但 Place 返回 true 后不阻塞。
    /// </summary>
    private void TryPlace()
    {
        if (_previewBlockIndex == 0) return; // 未选中任何可放置块
        if (!IsValidCell(_cursorCell)) return;
        if (!InRange(_cursorCell)) return;

        ushort existing = _grid.Get(_cursorCell.X, _cursorCell.Y);
        if (existing != BlockGrid.EmptyIndex) return; // 目标格非空

        // 碰撞检测：仅 Solid 块需要检查——非 Solid（背景装饰）放置后不阻挡
        if (_registry.IsSolid(_previewBlockIndex) && _playerAABB.Size != Vector2.Zero)
        {
            var tileRect = new Rect2(
                _cursorCell.X * _tileSize, _cursorCell.Y * _tileSize,
                _tileSize, _tileSize);
            if (_playerAABB.Intersects(tileRect)) return; // 玩家与目标格重叠 → 拒绝
        }

        // 消耗物品：回调返回 false = 库存不足
        if (!(_tryConsume?.Invoke(_previewBlockIndex) ?? true)) return;

        _interaction.Place(_cursorCell.X, _cursorCell.Y, _previewBlockIndex);
    }

    // -------- 校验 --------

    private bool IsValidCell(Vector2I cell) => cell.X >= 0 && cell.Y >= 0 && _grid.InBounds(cell.X, cell.Y);

    private bool InRange(Vector2I cell)
    {
        if (_originCell.X < 0) return false;
        int dx = Mathf.Abs(cell.X - _originCell.X);
        int dy = Mathf.Abs(cell.Y - _originCell.Y);
        return Mathf.Max(dx, dy) <= PlaceRange; // 切比雪夫距离
    }

    /// <summary>
    /// 预览合法性判定（供 _Draw 使用）：与 TryPlace 逻辑一致但不消耗物品、不实际放置。
    /// 返回 true 表示当前状态允许放置（cursor 有效 + 范围 + 目标空 + 碰撞通过）。
    /// </summary>
    private bool CanPlaceNow()
    {
        if (_previewBlockIndex == 0) return false;
        if (!IsValidCell(_cursorCell)) return false;
        if (!InRange(_cursorCell)) return false;

        ushort existing = _grid.Get(_cursorCell.X, _cursorCell.Y);
        if (existing != BlockGrid.EmptyIndex) return false;

        if (_registry.IsSolid(_previewBlockIndex) && _playerAABB.Size != Vector2.Zero)
        {
            var tileRect = new Rect2(
                _cursorCell.X * _tileSize, _cursorCell.Y * _tileSize,
                _tileSize, _tileSize);
            if (_playerAABB.Intersects(tileRect)) return false;
        }

        return true;
    }

    // -------- 渲染 --------

    public override void _Draw()
    {
        if (!IsValidCell(_cursorCell)) return;
        if (_previewBlockIndex == 0) return;

        var rect = new Rect2(
            _cursorCell.X * _tileSize, _cursorCell.Y * _tileSize,
            _tileSize, _tileSize);

        bool canPlace = CanPlaceNow();
        bool solid = _registry.IsSolid(_previewBlockIndex);

        // 预览颜色：可放=绿半透明 / 不可放=红描边 / 非Solid可放=青半透明（视觉区分）
        if (canPlace)
        {
            Color fillColor = solid ? new Color(0f, 1f, 0f, 0.4f) : new Color(0f, 0.8f, 1f, 0.4f);
            Color borderColor = solid ? Colors.Lime : Colors.Cyan;
            DrawRect(rect, fillColor);
            DrawRect(rect, borderColor, false, 2f);
        }
        else
        {
            DrawRect(rect, Colors.Red, false, 2f);
        }
    }
}
