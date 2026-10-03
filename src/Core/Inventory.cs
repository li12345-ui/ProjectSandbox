using System;

namespace ProjectSandbox.Core;

/// <summary>
/// 物品栏（数据层）：固定槽位数组 + 堆叠逻辑 + Move/Split 操作。
/// 纯 C# 非 Node，与 Godot 解耦——InventoryUI 负责渲染与交互。
///
/// 设计要点：
/// - ItemStack 内嵌结构体：ushort itemId 对齐 BlockRegistry 索引 + int count + int maxStack；
/// - 快捷栏 = Inventory 前 HotbarCount 个槽位的视图，不需要独立数据结构；
/// - MoveStack 统一处理：空槽放置、同 ID 堆叠合并、不同 ID 交换；
/// - 不依赖 BlockRegistry——maxStack 由调用方注入（InventoryUI 从 items.json 解析）。
/// </summary>
public sealed class Inventory
{
    public readonly int SlotsCount;
    public readonly int HotbarCount;
    public readonly ItemStack[] Slots;

    public Inventory(int slotsCount, int hotbarCount)
    {
        if (hotbarCount <= 0 || hotbarCount > slotsCount)
            throw new ArgumentOutOfRangeException(nameof(hotbarCount));
        SlotsCount = slotsCount;
        HotbarCount = hotbarCount;
        Slots = new ItemStack[slotsCount]; // 默认全为 default(ItemStack) = 空
    }

    // -------- 基础 --------

    /// <summary>获取指定槽的堆栈；空槽返回 default。</summary>
    public ref ItemStack GetStack(int slot) => ref Slots[slot];

    /// <summary>指定槽是否空（count == 0）。</summary>
    public bool IsEmpty(int slot) => Slots[slot].Count == 0;

    /// <summary>清空指定槽。</summary>
    public void ClearSlot(int slot) => Slots[slot] = default;

    // -------- 增删 --------

    /// <summary>
    /// 向物品栏添加物品：先找同 ID 未满栈累加，再填空槽，最后返回剩余数量。
    /// 返回 0 = 全部放入；> 0 = 放不下的剩余量。
    /// </summary>
    public int AddItem(ushort itemId, int count, int maxStack)
    {
        int remaining = count;

        // 第一遍：同 ID 未满栈累加
        for (int i = 0; i < SlotsCount && remaining > 0; i++)
        {
            if (Slots[i].ItemId == itemId && Slots[i].Count < maxStack)
            {
                int space = maxStack - Slots[i].Count;
                int add = Math.Min(space, remaining);
                Slots[i].Count += add;
                remaining -= add;
            }
        }

        // 第二遍：空槽放入新栈
        while (remaining > 0)
        {
            int emptySlot = -1;
            for (int i = 0; i < SlotsCount; i++)
                if (Slots[i].Count == 0) { emptySlot = i; break; }
            if (emptySlot < 0) break; // 无空槽

            int add = Math.Min(maxStack, remaining);
            Slots[emptySlot] = new ItemStack(itemId, add, maxStack);
            remaining -= add;
        }

        return remaining;
    }

    /// <summary>
    /// 从指定槽扣除指定数量；count > 当前数量时返回实际扣除量。
    /// 返回 0 = 未扣（空槽/不足）；> 0 = 实际扣除量。
    /// </summary>
    public int RemoveItem(int slot, int count)
    {
        ref var stack = ref Slots[slot];
        if (stack.Count == 0) return 0;
        int actual = Math.Min(stack.Count, count);
        stack.Count -= actual;
        if (stack.Count == 0) stack = default; // 归零清 itemId
        return actual;
    }

    // -------- 操作 --------

    /// <summary>
    /// Move 语义：从 from 槽移动 amount 个到 to 槽。
    /// - to 空槽：整个堆移动；
    /// - to 同 ID 未满栈：amount 受限合并；
    /// - to 不同 ID：交换；
    /// - to 同 ID 满栈：不移动。
    /// </summary>
    public bool MoveStack(int from, int to, int amount)
    {
        if (from < 0 || from >= SlotsCount || to < 0 || to >= SlotsCount) return false;
        if (from == to) return false;
        ref var src = ref Slots[from];
        ref var dst = ref Slots[to];
        if (src.Count == 0 || amount <= 0) return false;
        int actualAmount = Math.Min(amount, src.Count);

        if (dst.Count == 0)
        {
            // 目标空：直接移
            dst = new ItemStack(src.ItemId, actualAmount, src.MaxStack);
            src.Count -= actualAmount;
            if (src.Count == 0) src = default;
            return true;
        }

        if (dst.ItemId == src.ItemId)
        {
            // 同 ID：受限合并
            int space = dst.MaxStack - dst.Count;
            if (space <= 0) return false; // 目标已满
            int move = Math.Min(space, actualAmount);
            dst.Count += move;
            src.Count -= move;
            if (src.Count == 0) src = default;
            return move > 0;
        }

        // 不同 ID：交换整槽
        (src, dst) = (dst, src);
        return true;
    }

    /// <summary>
    /// 从 from 槽拆分出 amount 个到空的 to 槽。to 必须空。
    /// 返回 false 表示 to 不空或 amount 非法。
    /// </summary>
    public bool SplitStack(int from, int to, int amount)
    {
        if (to < 0 || to >= SlotsCount || Slots[to].Count != 0) return false;
        return MoveStack(from, to, amount);
    }

    // -------- 快捷栏 --------

    /// <summary>快捷栏第 index 槽（0-based in hotbar），空槽返回 default。</summary>
    public ref ItemStack GetHotbarItem(int index) => ref Slots[index];

    // -------- 结构体 --------

    public struct ItemStack
    {
        public ushort ItemId;
        public int Count;
        public int MaxStack;

        public ItemStack(ushort itemId, int count, int maxStack)
        {
            ItemId = itemId;
            Count = count;
            MaxStack = maxStack;
        }

        public readonly bool IsEmpty => Count == 0;
    }
}
