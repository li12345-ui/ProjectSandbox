using System;

namespace ProjectSandbox.Core;

/// <summary>
/// 生命系统（纯 C# 非 Node）：血量、无敌帧、死亡、复活。
/// 与 Inventory/CraftingSystem 同模式——不自驱，暴露 Tick(dt) 供外部每帧调。
///
/// 设计要点（经验 #1199024 教训）：
/// - TakeDamage 是唯一受伤入口——无敌帧检查 → 扣 HP → 触发事件，全部前置到这一个函数；
/// - 死亡事件用 C# event Action<HealthSystem>，不依赖 Godot Signal（避免 GD0202 Nullable 陷阱）；
/// - 复活参数化比例：Revive(ratio=1f) 满血 / Revive(0.5f) 半血；
/// - EnemyAI 已有自己的 _hp 字段——后续接线任务把 EnemyAI.TakeDamage 委托到此组件。
/// </summary>
public sealed class HealthSystem
{
    private int _maxHp;
    private int _currentHp;
    private float _iFrameTimer;
    private bool _isDead;

    /// <summary>无敌帧持续秒数。0 = 无无敌帧（每次受伤必扣）。</summary>
    public float IFrameDuration { get; set; } = 0.5f;

    /// <summary>是否无敌帧中。</summary>
    public bool IsInvulnerable => _iFrameTimer > 0f || _isDead;

    public int MaxHp => _maxHp;
    public int CurrentHp => _currentHp;
    public float HpRatio => _maxHp > 0 ? (float)_currentHp / _maxHp : 0f;
    public bool IsDead => _isDead;

    // -------- 事件 --------

    /// <summary>受伤后触发（含无敌帧内被忽略的情况不触发）。</summary>
    public event Action<HealthSystem, int>? OnDamaged;

    /// <summary>死亡时触发（HP 从 >0 降到 0）。</summary>
    public event Action<HealthSystem>? OnDeath;

    /// <summary>复活时触发。</summary>
    public event Action<HealthSystem>? OnRevived;

    // -------- 构造 --------

    public HealthSystem(int maxHp)
    {
        if (maxHp <= 0) throw new ArgumentOutOfRangeException(nameof(maxHp), "MaxHp 必须 > 0");
        _maxHp = maxHp;
        _currentHp = maxHp;
    }

    // -------- 主接口 --------

    /// <summary>
    /// 扣血：无敌帧内 / 已死亡时返回 false（无伤）；
    /// 成功扣血后设置无敌帧，HP≤0 时设 isDead 并触发 OnDeath。
    /// </summary>
    public bool TakeDamage(int amount)
    {
        if (_isDead || _iFrameTimer > 0f) return false;
        if (amount <= 0) return false; // 伤害 ≤ 0 不作数

        int actual = Math.Min(amount, _currentHp); // 防止溢出负数
        _currentHp -= actual;
        _iFrameTimer = IFrameDuration;
        OnDamaged?.Invoke(this, actual);

        if (_currentHp <= 0)
        {
            _currentHp = 0;
            _isDead = true;
            OnDeath?.Invoke(this);
        }

        return true;
    }

    /// <summary>加血：不超过 MaxHp；已死亡时返回 false。</summary>
    public bool Heal(int amount)
    {
        if (_isDead || amount <= 0) return false;
        int before = _currentHp;
        _currentHp = Math.Min(_maxHp, _currentHp + amount);
        return _currentHp > before;
    }

    /// <summary>
    /// 复活：重置 HP 到 maxHp × ratio，清无敌帧，isDead=false。
    /// ratio 默认 1.0 = 满血复活。
    /// </summary>
    public void Revive(float ratio = 1f)
    {
        ratio = Math.Clamp(ratio, 0f, 1f);
        _currentHp = (int)Math.Round(_maxHp * ratio);
        _iFrameTimer = 0f;
        _isDead = false;
        OnRevived?.Invoke(this);
    }

    /// <summary>外部每帧调——递减无敌帧计时器。</summary>
    public void Tick(float dt)
    {
        if (_iFrameTimer > 0f)
            _iFrameTimer = Math.Max(0f, _iFrameTimer - dt);
    }

    /// <summary>
    /// 静默设置血量：存档桥接专用，不触发任何事件（OnDamaged/OnRevived/OnDeath 均静默）。
    /// 与 Revive/TakeDamage/Heal 的核心差异：存档加载不应计入复活次数、受伤次数等统计。
    /// 同步维护 _isDead（HP=0 则 dead）和 _iFrameTimer（清零保证加载后立即可被正常攻击）。
    /// </summary>
    public void SetHpSilently(int hp)
    {
        _currentHp = Math.Clamp(hp, 0, _maxHp);
        _isDead = _currentHp <= 0;
        _iFrameTimer = 0f;
    }

    /// <summary>修改最大血量（升级/装备）；当前血量超出则钳制到新 Max。</summary>
    public void SetMaxHp(int newMax)
    {
        if (newMax <= 0) throw new ArgumentOutOfRangeException(nameof(newMax));
        _maxHp = newMax;
        if (_currentHp > _maxHp) _currentHp = _maxHp;
    }
}
