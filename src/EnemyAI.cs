using System;
using System.Collections.Generic;
using Godot;
using ProjectSandbox.Core;
using ProjectSandbox.World;

namespace ProjectSandbox;

/// <summary>
/// 简单敌人 AI：巡逻 → 追击 → 攻击 → 死亡。
/// 纯状态机 + 距离判定，不依赖 NavigationAgent2D（项目无导航网格烘焙，经验 #1453614 教训）。
/// 碰撞可选注入 CollisionSystem，未注入时降级为无碰撞移动。
///
/// 生命/伤害已迁移到 HealthSystem + DamageSystem（R2）：
/// - 旧 _hp 字段 + TakeDamage 方法已删除，改为 SetHealthSystem 注入；
/// - TakeDamage(int) 委托到 HealthSystem.TakeDamage，死亡由 HealthSystem.OnDeath 事件触发；
/// - Game.cs 订阅 OnDeath → LootSystem.RollDrop 掉落。
/// </summary>
public sealed partial class EnemyAI : Node2D
{
    // -------- 导出参数 --------

    [Export] public float PatrolSpeed { get; set; } = 40f;
    [Export] public float ChaseSpeed { get; set; } = 80f;
    [Export] public float DetectRange { get; set; } = 120f;
    [Export] public float AttackRange { get; set; } = 24f;
    [Export] public int AttackDamage { get; set; } = 6;
    [Export] public float AttackCooldown { get; set; } = 1.0f;
    [Export] public int MaxHp { get; set; } = 30;

    // -------- 内部状态 --------

    private enum AIState { Patrol, Chase, Attack, Dead }
    private AIState _state = AIState.Patrol;

    private Node2D? _target;
    private CollisionSystem? _collision;
    private HealthSystem? _health;
    private DamageSystem? _damage;
    private LootSystem? _loot;
    private string _lootId = "";

    private Vector2 _velocity;
    private float _attackCooldownTimer;
    private float _patrolTurnTimer;
    private Vector2 _patrolDir = Vector2.Right;
    private bool _deathHandled;

    // -------- 运行时注入 --------

    public void SetTarget(Node2D? target) => _target = target;
    public void SetCollisionSystem(CollisionSystem collision) => _collision = collision;
    public void SetHealthSystem(HealthSystem health)
    {
        _health = health;
        if (health != null)
        {
            health.OnDeath += OnHealthDeath;
        }
    }
    public void SetDamageSystem(DamageSystem damage) => _damage = damage;
    public void SetLootSystem(LootSystem loot, string lootId) { _loot = loot; _lootId = lootId; }

    private void OnHealthDeath(HealthSystem hs)
    {
        if (_deathHandled) return;
        _deathHandled = true;

        // 掉落
        if (_loot != null && !string.IsNullOrEmpty(_lootId))
        {
            var drops = _loot.RollDrop(_lootId);
            if (drops.Count > 0)
                GD.Print($"[EnemyAI] 死亡掉落 {drops.Count} 件物品（位置 {GlobalPosition.X:F0},{GlobalPosition.Y:F0}）");
        }

        _state = AIState.Dead;
        // 延迟到帧尾销毁——避免死亡回调嵌套释放自身导致 LootSystem 后续访问对象异常（审查 P1-耦合）
        Callable.From(QueueFree).CallDeferred();
    }

    // -------- 公开接口（HealthSystem 委托） --------

    /// <summary>受击入口：委托到 HealthSystem.TakeDamage。未注入 HealthSystem 时返回 false。</summary>
    public bool TakeDamage(int damage)
    {
        if (_health == null || _state == AIState.Dead) return false;
        return _health.TakeDamage(damage);
    }

    public bool IsDead => _health?.IsDead ?? false;
    public int CurrentHp => _health?.CurrentHp ?? 0;

    // -------- 主循环 --------

    public override void _PhysicsProcess(double delta)
    {
        if (_state == AIState.Dead) return;

        float dt = (float)delta;
        UpdateTimer(dt);

        switch (_state)
        {
            case AIState.Patrol:  UpdatePatrol(dt); break;
            case AIState.Chase:   UpdateChase(dt); break;
            case AIState.Attack:  UpdateAttack(dt); break;
        }

        MoveWithCollision(_velocity * dt);
    }

    // -------- 状态更新 --------

    private void UpdateTimer(float dt)
    {
        if (_attackCooldownTimer > 0f) _attackCooldownTimer = Math.Max(0f, _attackCooldownTimer - dt);
        _patrolTurnTimer -= dt;
    }

    private void UpdatePatrol(float dt)
    {
        if (TryDetectPlayer()) return;

        if (_patrolTurnTimer <= 0f)
        {
            var rng = new RandomNumberGenerator();
            rng.Randomize();
            float angle = rng.RandfRange(0f, Mathf.Tau);
            _patrolDir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            _patrolTurnTimer = rng.RandfRange(2f, 5f);
        }

        _velocity = _patrolDir * PatrolSpeed;
    }

    private void UpdateChase(float dt)
    {
        if (_target == null) { _state = AIState.Patrol; return; }

        float dist = GetDistanceToTarget();

        if (dist <= AttackRange)
        {
            _state = AIState.Attack;
            _velocity = Vector2.Zero;
            return;
        }

        if (dist > DetectRange * 1.5f)
        {
            _state = AIState.Patrol;
            return;
        }

        var dir = (_target.GlobalPosition - GlobalPosition).Normalized();
        _velocity = dir * ChaseSpeed;
    }

    private void UpdateAttack(float dt)
    {
        if (_target == null) { _state = AIState.Patrol; return; }
        float dist = GetDistanceToTarget();
        if (dist > AttackRange * 1.2f)
        {
            _state = AIState.Chase;
            return;
        }

        if (_attackCooldownTimer <= 0f)
        {
            PerformAttack();
            _attackCooldownTimer = AttackCooldown;
        }

        _velocity = Vector2.Zero;
    }

    // -------- 检测与攻击 --------

    private bool TryDetectPlayer()
    {
        if (_target == null) return false;
        if (GetDistanceToTarget() <= DetectRange)
        {
            _state = AIState.Chase;
            return true;
        }
        return false;
    }

    private float GetDistanceToTarget()
    {
        if (_target == null) return float.MaxValue;
        return GlobalPosition.DistanceTo(_target.GlobalPosition);
    }

    private void PerformAttack()
    {
        if (_target == null) return;

        // 反射调 TakeDamage（玩家侧尚未实现 HealthSystem，当前仅 EnemyAI 有）
        if (_target.HasMethod("TakeDamage"))
            _target.Call("TakeDamage", AttackDamage);
    }

    // -------- 移动 --------

    private void MoveWithCollision(Vector2 displacement)
    {
        if (_collision == null)
        {
            GlobalPosition += displacement;
            return;
        }

        var half = new Vector2(8f, 12f);
        var newPos = _collision.ResolveCollision(GlobalPosition - half, half, _velocity / 0.016f, 0.016f);
        GlobalPosition = newPos + half;
    }
}
