using System;
using Godot;

namespace ProjectSandbox.World;

/// <summary>
/// 世界输入控制器（M1 调试版）：左键挖掘、右键放置、数字键选块。
/// 规则全部经 BlockInteraction，本类只做「输入事件 → 格坐标 → 规则调用 → 渲染同步」的搬运；
/// 快捷栏为 CanvasLayer 上的调试 Label 占位，正式 UI 快捷栏另开任务。
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

    public override void _UnhandledInput(InputEvent @event)
    {
        // 数字键 1~9 选块：槽位号即块索引（注册表索引从 1 起），超出块数量忽略
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            var code = (int)key.Keycode;
            var slot = code - (int)Key.Key1 + 1;
            if (code >= (int)Key.Key1 && code <= (int)Key.Key9 && slot <= _registry.Count)
            {
                SelectedIndex = (ushort)slot;
                _hotbar.Text = HotbarText();
            }
            return;
        }

        if (@event is not InputEventMouseButton { Pressed: true } mb) return;

        var inGrid = TryWorldToCell(GetGlobalMousePosition(), out var x, out var y);
        if (!inGrid) return;

        switch (mb.ButtonIndex)
        {
            case MouseButton.Left when _interaction.Mine(x, y):
                _renderer.UpdateCell(x, y); // 挖掘成功 → 同步单格
                break;
            case MouseButton.Right when SelectedIndex != 0 && _interaction.Place(x, y, SelectedIndex):
                _renderer.UpdateCell(x, y); // 放置成功 → 同步单格
                break;
        }
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
