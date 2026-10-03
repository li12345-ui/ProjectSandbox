using System;

namespace ProjectSandbox.Core;

/// <summary>
/// 伤害系统（纯 C# 非 Node）：伤害计算管线 → 调 HealthSystem.TakeDamage 统一入口。
///
/// 设计要点（经验 #1199024 教训）：
/// - 所有伤害入口必须经过同一前置检查链——DamageSystem 只做计算，HealthSystem 做执行；
/// - Armor/Defense 系统接线时只改 DamageSystem，不动 HealthSystem；
/// - 暴击/元素伤害等扩展点用 DamageInfo 结构体，不破坏 ApplyDamage 主签名。
/// </summary>
public sealed class DamageSystem
{
    /// <summary>最低实际伤害（护甲减伤后仍至少造成 1 点）。</summary>
    public int MinDamage { get; set; } = 1;

    // -------- 主入口 --------

    /// <summary>
    /// 对目标 HealthSystem 造成伤害：rawDamage - defense → clamp ≥ MinDamage → 调 HealthSystem.TakeDamage。
    /// 返回值 = HealthSystem.TakeDamage 的结果（true=命中，false=被无敌帧/死亡格挡）。
    /// 注意：defense 为目标防御值，由调用方从 stats 读取——DamageSystem 不直接依赖 Inventory。
    /// </summary>
    public bool ApplyDamage(HealthSystem target, int rawDamage, int defense = 0)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (rawDamage <= 0) return false;

        int actual = Math.Max(MinDamage, rawDamage - Math.Max(0, defense));
        return target.TakeDamage(actual);
    }

    // -------- 扩展入口 --------

    /// <summary>
    /// 带暴击的伤害：critChance 命中时伤害 × critMultiplier。
    /// critRoll 由调用方传入（0~1），不在 DamageSystem 内生成——方便自测固定种子。
    /// </summary>
    public bool ApplyDamageWithCrit(HealthSystem target, int rawDamage, int defense,
                                    float critChance, float critMultiplier, float critRoll)
    {
        float chance = Math.Clamp(critChance, 0f, 1f);
        int final = rawDamage;
        if (critRoll <= chance)
            final = (int)Math.Round(rawDamage * critMultiplier);

        return ApplyDamage(target, final, defense);
    }

    /// <summary>
    /// 带伤害类型的入口——当前仅做基础伤害，元素/穿透等后续扩展点。
    /// 与 ApplyDamage 等价，DamageInfo 留作未来扩展。
    /// </summary>
    public bool ApplyDamage(HealthSystem target, DamageInfo info)
    {
        return ApplyDamage(target, info.RawDamage, info.Defense);
    }

    // -------- 数据结构 --------

    /// <summary>伤害信息载体——当前仅 rawDamage + defense，留作元素/暴击/穿透扩展。</summary>
    public readonly struct DamageInfo
    {
        public readonly int RawDamage;
        public readonly int Defense;

        public DamageInfo(int rawDamage, int defense = 0)
        {
            RawDamage = rawDamage;
            Defense = defense;
        }
    }
}
