using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectSandbox.Core;

/// <summary>
/// 掉落与拾取系统（纯 C# 数据逻辑层）。
/// 与 Inventory/CraftingSystem/HealthSystem 同模式——非 Node，不自驱。
///
/// 职责：
/// 1. RollDrop(lootId, rngSeed) → 按掉落表权重随机 roll_count 次，返回 DropItem 列表；
/// 2. TryPickup(dropItem, inventory) → 调 Inventory.AddItem 公共接口拾取，返回是否完整拾取；
/// 3. 合并：同 item_id 掉落物调用 Merge 合并 count（物理层由 Godot 场景节点处理）。
///
/// 设计要点（经验 #2329740 教训）：
/// - 掉落配置走注入的 List<LootTable>（未来由 IDataLoader 从 loot.json 加载），不手动 Register；
/// - 拾取走 Inventory.AddItem 公共接口，不直接触碰 Slots 数组；
/// - item_id 字符串 → ushort itemId 依赖 Func 注入桥接（ItemRegistry 尚未落地时返回 0 安全降级）；
/// - 权重随机：TotalWeight 累加 + 单次 roll，roll_count 次独立判定（非按概率过滤 entry）。
/// </summary>
public sealed class LootSystem
{
    private readonly List<LootTable> _tables;

    /// <summary>"item_crystal_shell" → ushort itemId（Inventory.ItemStack.ItemId）。null 时返回 0 安全降级。</summary>
    private readonly Func<string, ushort>? _resolveItem;

    public LootSystem(List<LootTable> tables, Func<string, ushort>? resolveItem = null)
    {
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _resolveItem = resolveItem;
    }

    // -------- 事件 --------

    /// <summary>掉落 roll 完成后触发（含空掉落）。</summary>
    public event Action<string, List<DropItem>>? OnDropRolled;

    /// <summary>物品被拾取后触发。</summary>
    public event Action<DropItem, int>? OnItemPickedUp;

    // -------- 掉落 --------

    /// <summary>
    /// 按掉落表 ID roll_count 次权重随机，返回掉落物列表。
    /// rngSeed 为 0 时用 Environment.TickCount64（时间种子）；
    /// 非 0 时每次 roll 用不同子种子（确定性重放可用）。
    /// 返回空列表表示未命中（权重 0 或 roll_count=0）。
    /// </summary>
    public List<DropItem> RollDrop(string lootId, long rngSeed = 0)
    {
        var table = FindTable(lootId);
        if (table == null) return new List<DropItem>();

        var rng = rngSeed == 0
            ? new Random()
            : new Random((int)(rngSeed & 0x7FFFFFFF));

        var result = new List<DropItem>();

        for (int i = 0; i < table.RollCount; i++)
        {
            var entry = RollEntry(table, rng);
            if (entry == null) continue;

            int count = rng.Next(entry.CountMin, entry.CountMax + 1);
            ushort itemId = ResolveItem(entry.ItemId);
            if (itemId == 0) continue; // 解析失败跳过

            result.Add(new DropItem(itemId, count));
        }

        OnDropRolled?.Invoke(lootId, result);
        return result;
    }

    // -------- 拾取 --------

    /// <summary>
    /// 尝试把掉落物放入 Inventory。
    /// 返回值 = 背包剩余放不下的数量；0 表示完整拾取。
    /// 内部调 Inventory.AddItem 的返回值 = 未放入数量。
    /// </summary>
    public int TryPickup(DropItem dropItem, Inventory inventory)
    {
        if (dropItem == null || inventory == null) return dropItem?.Count ?? 0;
        if (dropItem.Count <= 0) return 0;

        int remaining = inventory.AddItem(dropItem.ItemId, dropItem.Count, dropItem.MaxStack);
        int pickedUp = dropItem.Count - remaining;

        if (pickedUp > 0)
        {
            dropItem.Count = remaining;
            OnItemPickedUp?.Invoke(dropItem, pickedUp);
        }

        return remaining;
    }

    // -------- 合并 --------

    /// <summary>
    /// 两个同 itemId 的掉落物合并——count 叠加到主掉落，返回 true 表示成功；
    /// 不同 itemId / count 非法 / 已满时返回 false。
    /// </summary>
    public static bool TryMerge(DropItem target, DropItem source)
    {
        if (target == null || source == null) return false;
        if (target.ItemId != source.ItemId) return false;
        if (target.Count >= target.MaxStack) return false;

        int space = target.MaxStack - target.Count;
        int merge = Math.Min(space, source.Count);
        target.Count += merge;
        source.Count -= merge;
        return merge > 0;
    }

    // -------- 内部工具 --------

    private LootTable? FindTable(string lootId)
    {
        foreach (var t in _tables)
            if (t.Id == lootId) return t;
        return null;
    }

    /// <summary>
    /// 权重随机：TotalWeight 累加 → roll ∈ [0, total) → 按 entry.weight 前缀和命中。
    /// 所有权重为 0 时返回 null（本次 roll 无掉落）。
    /// </summary>
    private static LootEntry? RollEntry(LootTable table, Random rng)
    {
        int total = table.Entries.Sum(e => e.Weight);
        if (total <= 0) return null;

        int roll = rng.Next(total);
        int acc = 0;
        foreach (var entry in table.Entries)
        {
            if (entry.Weight <= 0) continue;
            acc += entry.Weight;
            if (roll < acc) return entry;
        }
        return table.Entries.LastOrDefault(); // 兜底：roll 命中浮点精度外，回退最后一条
    }

    private ushort ResolveItem(string itemIdStr)
    {
        if (_resolveItem != null)
            return _resolveItem(itemIdStr);
        return 0; // null 安全降级：返回空块索引 → RollDrop 跳过
    }

    // -------- 数据结构（对齐 loot.schema.json） --------

    public sealed class LootTable
    {
        public string Id { get; set; } = "";
        public int RollCount { get; set; } = 1;
        public List<LootEntry> Entries { get; set; } = new();
    }

    public sealed class LootEntry
    {
        public string ItemId { get; set; } = "";
        public int Weight { get; set; } = 1;
        public int CountMin { get; set; } = 1;
        public int CountMax { get; set; } = 1;
    }

    /// <summary>掉落物：纯数据，物理位置由 Godot 场景节点持有（约束内不创建 RigidBody2D）。</summary>
    public class DropItem
    {
        public ushort ItemId { get; set; }
        public int Count { get; set; }
        public int MaxStack { get; set; } = 99; // 默认 99，与 Inventory.AddItem 签名对齐

        public DropItem() { }
        public DropItem(ushort itemId, int count) { ItemId = itemId; Count = count; }
        public DropItem(ushort itemId, int count, int maxStack) { ItemId = itemId; Count = count; MaxStack = maxStack; }

        public bool IsEmpty => Count <= 0;
    }
}
