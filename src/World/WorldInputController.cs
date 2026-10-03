using System;
using Godot;

namespace ProjectSandbox.World;

/// <summary>
/// 世界输入控制器：左键挖掘、右键放置、数字键选块。
/// 所有输入经 Godot.InputMap action 查询（InputService 注册），天然合并键鼠+手柄；
/// IsActionJustPressed 做边沿检测，比手动维护 Pressed 状态干净。
/// 规则全部经 BlockInteraction，本类只做「action 触发 → 格坐标 → 规则调用 → 渲染同步」的搬运。
/// </summary>
public sealed partial class WorldInputController : Node2D
{
    private readonly BlockInteraction _interaction;
    private readonly WorldRenderer _renderer;
    private readonly BlockRegistry _registry;
    private readonly Label _hotbar = null!;

    /// <summary>当前放置选中的块索引（1 起；0=未选，放置无效）。</summary>
    public ushort SelectedIndex { get; private set; }

    public WorldInputController(BlockInteraction interaction, WorldRenderer renderer, BlockRegistry registry)
    {
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        // HUD 走 CanvasLayer：不随相机的移动/缩放联动，固定屏幕左上角
        var hud = new CanvasLayer { Name = "DebugHud", Layer = 1 };
        _hotbar = new Label
        {
            Name = "DebugHotbar",
            Position = new Vector2(8, 8),
            Text = HotbarText(),
        };
        hud.AddChild(_hotbar);
        AddChild(hud);
    }

    public override void _Process(double delta)
    {
        // 选块：slot_1 ~ slot_5 对应注册表索引 1~5（块数=5）
        for (int i = 1; i <= _registry.Count; i++)
        {
            if (Input.IsActionJustPressed($"slot_{i}"))
            {
                SelectedIndex = (ushort)i;
                _hotbar.Text = HotbarText();
                break;
            }
        }

        // 挖/放：经 InputMap action 查询，天然合并键鼠+手柄
        var hasMine = Input.IsActionJustPressed("mine");
        var hasPlace = Input.IsActionJustPressed("place");
        if (!hasMine && !hasPlace) return;

        var inGrid = TryWorldToCell(GetGlobalMousePosition(), out var x, out var y);
        if (!inGrid) return;

        if (hasMine && _interaction.Mine(x, y))
            _renderer.UpdateCell(x, y);

        if (hasPlace && SelectedIndex != 0 && _interaction.Place(x, y, SelectedIndex))
            _renderer.UpdateCell(x, y);
    }

    /// <summary>
    /// 世界像素坐标 → 格坐标（纯函数，供自测）。仅校验非负；
    /// 网格上界由 BlockInteraction.InBounds 兜底，本方法不持有网格引用。
    /// </summary>
    public static bool TryWorldToCell(Vector2 worldPos, out int x, out int y)
    {
        x = (int)Mathf.Floor(worldPos.X / WorldRenderer.TileSize);
        y = (int)Mathf.Floor(worldPos.Y / WorldRenderer.TileSize);
        return worldPos.X >= 0f && worldPos.Y >= 0f;
    }

    private string HotbarText()
        => SelectedIndex == 0
            ? "左键挖 右键放 数字键1-5选块 当前：未选"
            : $"左键挖 右键放 数字键1-5选块 当前：{_registry.GetDef(SelectedIndex).DisplayName}";
}
