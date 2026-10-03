using System;
using Godot;
using ProjectSandbox.Core;

namespace ProjectSandbox;

/// <summary>
/// 物品栏 UI（Godot Control）：格子 + 拖拽 + 堆叠预览 + 快捷栏。
/// 与 Inventory（纯数据层）配套——UI 只负责渲染和交互，不修改数据结构。
///
/// 拖拽实现（经验 #1292206 教训内聚版）：
/// - 拖拽覆盖层 TextureRect 作为 InventoryUI 私有子节点，MouseFilter=Ignore 穿透，ZIndex=999 置顶；
/// - 槽位通过 _GuiInput 捕获 MouseButtonDown → 发 DragStart(this, fromSlot, amount)；
/// - InventoryUI 订阅槽位信号，内部 AddChild overlay，_Process 更新 overlay.Position 跟随鼠标；
/// - 拖拽结束时 overlay RemoveChild 销毁，Inventory.MoveStack 执行数据写入。
///
/// 关键约束：items.json 解析用 IDataLoader 注入（构造时传入），不依赖 BlockRegistry。
/// </summary>
public sealed partial class InventoryUI : Control
{
    [Export] public int SlotsCount { get; set; } = 20;
    [Export] public int HotbarCount { get; set; } = 5;
    [Export] public int SlotSize { get; set; } = 32;
    [Export] public int SlotGap { get; set; } = 4;
    [Export] public int SlotsPerRow { get; set; } = 5;

    private Inventory _inventory;
    private IDataLoader _loader;

    // UI 节点
    private GridContainer _grid = null!;
    private GridContainer _hotbarGrid = null!;
    private PanelContainer _hotbarPanel = null!;
    private TextureRect _dragOverlay = null!;

    // 槽位控件数组：索引 = Inventory.Slots 索引
    private SlotControl[] _slots = Array.Empty<SlotControl>();

    // 拖拽状态
    private int _dragFromSlot = -1;
    private int _dragAmount;

    /// <summary>块索引 → maxStack 映射（从 items.json 解析）。</summary>
    private readonly System.Collections.Generic.Dictionary<ushort, int> _maxStackByItem = new();

    public InventoryUI(IDataLoader loader)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _inventory = new Inventory(SlotsCount, HotbarCount);
    }

    public override void _Ready()
    {
        ParseItemsJson();
        BuildUI();
    }

    // -------- 数据 --------

    private void ParseItemsJson()
    {
        // 解析 items.json：取每个 item 的 item_id → 查 BlockRegistry 索引 → 记 maxStack
        // 这里用硬编码映射（项目尚无 ItemRegistry），InventoryUI 自行维护
        // item_id 字符串 → blockIndex 映射后续由 BlockRegistry 统一提供
        var catalog = _loader.Load<ItemCatalog>("items.json");
        foreach (var item in catalog.Items)
        {
            // 暂不做 item_id → blockIndex 解析——等 ItemRegistry 落地后统一接线
            // 先用占位：假设 item 可放置物块的 blockIndex 已知
            // 实际 Inventory 用 itemId(ushort) 对齐 BlockRegistry 索引 1..N
        }
    }

    private sealed class ItemCatalog
    {
        public System.Collections.Generic.List<ItemDef> Items { get; set; } = new();
    }

    private sealed class ItemDef
    {
        public string Id { get; set; } = "";
        public int StackSize { get; set; } = 99;
        public string Place { get; set; } = "";
    }

    // -------- UI 构建 --------

    private void BuildUI()
    {
        var mainVbox = new VBoxContainer();
        mainVbox.AddThemeConstantOverride("separation", 8);
        AddChild(mainVbox);

        // 快捷栏（顶部水平一排）
        _hotbarPanel = new PanelContainer();
        var hotbarMargin = new MarginContainer { Theme = new Theme() };
        hotbarMargin.AddThemeConstantOverride("margin_left", 4);
        hotbarMargin.AddThemeConstantOverride("margin_right", 4);
        hotbarMargin.AddThemeConstantOverride("margin_top", 4);
        hotbarMargin.AddThemeConstantOverride("margin_bottom", 4);
        _hotbarGrid = new GridContainer { Columns = HotbarCount };
        _hotbarGrid.AddThemeConstantOverride("h_separation", SlotGap);
        hotbarMargin.AddChild(_hotbarGrid);
        _hotbarPanel.AddChild(hotbarMargin);
        mainVbox.AddChild(_hotbarPanel);

        // 背包格子（GridContainer）
        _grid = new GridContainer { Columns = SlotsPerRow };
        _grid.AddThemeConstantOverride("h_separation", SlotGap);
        _grid.AddThemeConstantOverride("v_separation", SlotGap);
        mainVbox.AddChild(_grid);

        // 构建槽位控件
        _slots = new SlotControl[SlotsCount];
        for (int i = 0; i < SlotsCount; i++)
        {
            var slot = new SlotControl(i, this);
            slot.CustomMinimumSize = new Vector2(SlotSize, SlotSize);
            _slots[i] = slot;

            if (i < HotbarCount)
                _hotbarGrid.AddChild(slot);
            else
                _grid.AddChild(slot);
        }

        // 拖拽 overlay（内聚在 InventoryUI 内，经验 #1292206：不新建 DragOverlay 文件）
        _dragOverlay = new TextureRect
        {
            MouseFilter = Control.MouseFilterEnum.Ignore, // 穿透，让鼠标事件落到下层槽位
            ZIndex = 999,
            TextureFilter = TextureRect.TextureFilterEnum.Nearest,
            Visible = false,
        };
        AddChild(_dragOverlay);
    }

    // -------- 拖拽交互 --------

    internal void HandleDragStart(int slotIndex)
    {
        if (_inventory.IsEmpty(slotIndex)) return;
        _dragFromSlot = slotIndex;
        _dragAmount = _inventory.GetStack(slotIndex).Count;

        // overlay 显示一个占位 ColorRect（暂无图标资源，用色块+文字）
        _dragOverlay.Visible = true;
        // overlay 位置在 _Process 每帧更新
    }

    internal void HandleDragEndAt(Vector2 screenPos, int? targetSlot)
    {
        if (_dragFromSlot < 0) return;

        if (targetSlot.HasValue && targetSlot.Value >= 0 && targetSlot.Value < SlotsCount && targetSlot.Value != _dragFromSlot)
        {
            _inventory.MoveStack(_dragFromSlot, targetSlot.Value, _dragAmount);
        }

        _dragOverlay.Visible = false;
        _dragFromSlot = -1;
        RefreshAll();
    }

    public override void _Process(double delta)
    {
        // overlay 跟随鼠标（screenPos → 本地相对 InventoryUI 的位置）
        if (_dragOverlay.Visible)
        {
            var mouseScreenPos = GetViewport().GetMousePosition();
            var localPos = mouseScreenPos - GetGlobalRect().Position;
            _dragOverlay.Position = localPos - new Vector2(SlotSize / 2f, SlotSize / 2f);
        }

        // 更新槽位显示
        RefreshAll();
    }

    // -------- 刷新 --------

    private void RefreshAll()
    {
        for (int i = 0; i < SlotsCount; i++)
            _slots[i].Refresh(_inventory.GetStack(i));
    }

    // -------- 快捷栏查询（供外部调用） --------

    public ref Inventory.ItemStack GetHotbarSlot(int index) => ref _inventory.GetHotbarItem(index);

    /// <summary>对外：从指定槽扣除物品（供 PlacementSystem 消耗回调注入）。</summary>
    public bool TryConsume(ushort itemId, int count)
    {
        for (int i = 0; i < SlotsCount; i++)
        {
            if (_inventory.Slots[i].ItemId == itemId && _inventory.Slots[i].Count >= count)
            {
                _inventory.RemoveItem(i, count);
                RefreshAll();
                return true;
            }
        }
        return false;
    }

    // -------- 存档桥接 --------

    /// <summary>导出所有非空槽位为 SaveInventorySlot 列表。</summary>
    public System.Collections.Generic.List<SaveSystem.SaveInventorySlot> SaveInventorySlot()
    {
        var list = new System.Collections.Generic.List<SaveSystem.SaveInventorySlot>();
        for (int i = 0; i < SlotsCount; i++)
        {
            var stack = _inventory.Slots[i];
            if (stack.IsEmpty) continue;
            list.Add(new SaveSystem.SaveInventorySlot
            {
                Slot = i,
                ItemId = ResolveItemIdStr(stack.ItemId),
                Count = stack.Count,
            });
        }
        return list;
    }

    /// <summary>从 SaveInventorySlot 列表恢复背包（先清空再填充）。</summary>
    public void LoadFromSaveData(System.Collections.Generic.List<SaveSystem.SaveInventorySlot> slots)
    {
        // 清空所有槽
        for (int i = 0; i < SlotsCount; i++) _inventory.ClearSlot(i);

        // 填充（当前 item_id 字符串 → ushort itemId 映射未落地，暂存 0 不报错）
        foreach (var s in slots)
        {
            ushort itemId = ResolveItemIdNum(s.ItemId);
            if (itemId == 0) continue; // 未解析的跳过，不崩
            _inventory.AddItem(itemId, s.Count, 99);
        }
        RefreshAll();
    }

    private static string ResolveItemIdStr(ushort itemId) => $"item_placeholder_{itemId}"; // 等 ItemRegistry 落地替换
    private static ushort ResolveItemIdNum(string itemId) => 0; // 等 ItemRegistry 落地替换
}

/// <summary>
/// 单个物品槽控件：Panel 背景 + Label 数量 + 拖拽处理。
/// 输入事件 → 发 DragStart/DragEnd 给 InventoryUI。
/// </summary>
public sealed partial class SlotControl : Control
{
    private readonly int _slotIndex;
    private readonly InventoryUI _owner;
    private readonly Panel _bg = new();
    private readonly Label _countLabel = new();
    private readonly ColorRect _iconRect = new();

    public SlotControl(int slotIndex, InventoryUI owner)
    {
        _slotIndex = slotIndex;
        _owner = owner;

        _bg.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.15f, 0.15f, 0.15f, 0.8f),
            BorderColor = new Color(0.4f, 0.4f, 0.4f),
            BorderWidthBottom = 1, BorderWidthTop = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
        });
        AddChild(_bg);

        _iconRect.Color = Colors.Magenta; // 占位色块，等像素美术替换
        _iconRect.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _iconRect.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        AddChild(_iconRect);

        _countLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _countLabel.VerticalAlignment = VerticalAlignment.Bottom;
        _countLabel.AddThemeFontSizeOverride("font_size", 11);
        _countLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _countLabel.SizeFlagsVertical = SizeFlags.ExpandFill;
        AddChild(_countLabel);

        MouseFilter = MouseFilterEnum.Stop;
    }

    internal void Refresh(Inventory.ItemStack stack)
    {
        if (stack.IsEmpty)
        {
            _iconRect.Visible = false;
            _countLabel.Text = "";
        }
        else
        {
            _iconRect.Visible = true;
            _iconRect.CustomMinimumSize = new Vector2(20, 20);
            _countLabel.Text = stack.Count > 1 ? stack.Count.ToString() : "";
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            _owner.HandleDragStart(_slotIndex);
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            // 拖拽结束：先检查是否落在其他槽位上，用 GetGlobalRect + 命中判断
            var screenPos = GetViewport().GetMousePosition();
            int? target = null;
            var parent = GetParent();
            if (parent != null)
            {
                var children = parent.GetChildren();
                foreach (var child in children)
                {
                    if (child is SlotControl sc && sc != this)
                    {
                        if (sc.GetGlobalRect().HasPoint(screenPos))
                        {
                            target = sc._slotIndex;
                            break;
                        }
                    }
                }
            }
            _owner.HandleDragEndAt(screenPos, target);
            GetViewport().SetInputAsHandled();
        }
    }
}
