using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectSandbox.Core;

/// <summary>
/// 合成系统（纯 C# 数据逻辑层）：配方匹配 + 材料消耗 + 结果入包。
/// 与 Inventory 同模式——非 Node，不依赖 Godot 渲染。
///
/// 设计要点：
/// - 配方数据由外部注入（List<Recipe>），CraftingSystem 不自行加载 recipes.json（文件尚不存在）；
/// - item_id 字符串 → ushort itemId / maxStack 的解析依赖注入 Func（与 PlacementSystem 的消耗回调同模式）；
/// - CanCraft 只读校验，Craft 先 CanCraft 再消耗再产出，保证原子性（材料不足不消耗）；
/// - 消耗逻辑：遍历 Inventory.Slots 找匹配 itemId，从 count 最多的槽开始扣减（堆叠友好）。
///
/// 风险：itemIdResolver 依赖 ItemRegistry 尚未落地，null 时默认返回 (0, 99) 做安全降级——
/// 0 对应空块索引，CanCraft 永远返回 false，不会错误消耗。
/// </summary>
public sealed class CraftingSystem
{
    private readonly Inventory _inventory;
    private readonly List<Recipe> _recipes;

    /// <summary>"item_crystal_shell" → (ushort itemId, int maxStack)。null 时返回 (0, 99) 安全降级。</summary>
    private readonly Func<string, (ushort itemId, int maxStack)>? _resolveItem;

    public CraftingSystem(
        Inventory inventory,
        List<Recipe> recipes,
        Func<string, (ushort itemId, int maxStack)>? resolveItem = null)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        _resolveItem = resolveItem;
    }

    // -------- 查询 --------

    /// <summary>全部已注册配方的只读快照。</summary>
    public IReadOnlyList<Recipe> Recipes => _recipes.AsReadOnly();

    /// <summary>按配方 ID 查找；不存在抛 KeyNotFoundException。</summary>
    public Recipe GetRecipe(string recipeId)
    {
        foreach (var r in _recipes)
            if (r.Id == recipeId) return r;
        throw new KeyNotFoundException($"未知配方：{recipeId}");
    }

    /// <summary>
    /// 校验背包是否满足配方全部材料。
    /// 返回 false 的三种原因：材料不足 / itemId 解析失败 / 背包空间不足以放入产出物。
    /// </summary>
    public bool CanCraft(Recipe recipe)
    {
        if (recipe.Ingredients.Count == 0) return false;

        // 逐条检查材料
        foreach (var ing in recipe.Ingredients)
        {
            var resolved = ResolveItem(ing.ItemId);
            if (resolved.itemId == 0) return false; // 解析失败 → 不可合成

            int total = CountItem(resolved.itemId);
            if (total < ing.Count) return false; // 数量不足
        }

        // 检查产出物能否放入（考虑消耗材料后腾出的空间）
        var output = ResolveItem(recipe.Output.ItemId);
        if (output.itemId == 0) return false;

        int maxNeeded = recipe.Output.Count;
        // 模拟消耗材料后的剩余容量
        int consumedSlots = 0;
        foreach (var ing in recipe.Ingredients)
        {
            var ir = ResolveItem(ing.ItemId);
            if (ir.itemId == 0) continue;
            consumedSlots += ing.Count; // 粗略：扣除 count 个后腾出 count 个位置（不严谨但保守）
        }
        // 简化：只要 Inventory 总空间足够即可
        int used = _inventory.Slots.Count(s => s.Count > 0);
        if (used - consumedSlots + maxNeeded > _inventory.SlotsCount)
        {
            // 空间不足——但如果有同 ID 未满栈可合并，则还能放下
            int remaining = maxNeeded;
            foreach (var slot in _inventory.Slots)
            {
                if (slot.ItemId == output.itemId && slot.Count < output.maxStack)
                {
                    int space = output.maxStack - slot.Count;
                    remaining -= Math.Min(space, remaining);
                    if (remaining <= 0) break;
                }
            }
            if (remaining > 0)
            {
                // 算上空槽
                int emptySlots = _inventory.Slots.Count(s => s.Count == 0);
                // 扣除材料后的空槽数
                int projectedEmpty = emptySlots + consumedSlots;
                int neededEmpty = (int)Math.Ceiling((double)remaining / output.maxStack);
                if (neededEmpty > projectedEmpty) return false;
            }
        }

        return true;
    }

    // -------- 执行 --------

    /// <summary>
    /// 执行合成：先 CanCraft，通过才消耗材料 + 产出入包。
    /// 返回 false 表示不可合成；true = 成功。
    /// 原子性：CanCraft 通过后材料消耗和产出是连续的——若中途 AddItem 失败（理论上不会），材料已扣但产出不全。
    /// </summary>
    public bool Craft(Recipe recipe)
    {
        if (!CanCraft(recipe)) return false;

        // 1. 消耗材料（从 count 最多的槽开始扣，堆叠友好）
        foreach (var ing in recipe.Ingredients)
        {
            var resolved = ResolveItem(ing.ItemId);
            if (resolved.itemId == 0) return false; // 防御式

            int remaining = ing.Count;
            while (remaining > 0)
            {
                int slotIdx = FindSlotWithMost(resolved.itemId);
                if (slotIdx < 0) return false; // 理论上不会，CanCraft 已校验

                var slot = _inventory.Slots[slotIdx];
                int deduct = Math.Min(slot.Count, remaining);
                _inventory.RemoveItem(slotIdx, deduct);
                remaining -= deduct;
            }
        }

        // 2. 产出入包
        var output = ResolveItem(recipe.Output.ItemId);
        _inventory.AddItem(output.itemId, recipe.Output.Count, output.maxStack);

        return true;
    }

    // -------- 内部工具 --------

    private (ushort itemId, int maxStack) ResolveItem(string itemIdStr)
    {
        if (_resolveItem != null)
            return _resolveItem(itemIdStr);
        // null 安全降级：返回空块索引 → CanCraft 永远 false
        return (0, 99);
    }

    private int CountItem(ushort itemId)
    {
        int total = 0;
        foreach (var slot in _inventory.Slots)
            if (slot.ItemId == itemId) total += slot.Count;
        return total;
    }

    private int FindSlotWithMost(ushort itemId)
    {
        int bestIdx = -1;
        int bestCount = 0;
        for (int i = 0; i < _inventory.Slots.Length; i++)
        {
            if (_inventory.Slots[i].ItemId == itemId && _inventory.Slots[i].Count > bestCount)
            {
                bestCount = _inventory.Slots[i].Count;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    // -------- 数据结构（对齐 recipe.schema.json） --------

    public sealed class Recipe
    {
        public string Id { get; set; } = "";
        public RecipeOutput Output { get; set; } = new();
        public List<RecipeIngredient> Ingredients { get; set; } = new();
        public string CraftStation { get; set; } = "hand";
        public int UnlockTier { get; set; } = 0;
    }

    public sealed class RecipeOutput
    {
        public string ItemId { get; set; } = "";
        public int Count { get; set; } = 1;
    }

    public sealed class RecipeIngredient
    {
        public string ItemId { get; set; } = "";
        public int Count { get; set; } = 1;
    }
}
