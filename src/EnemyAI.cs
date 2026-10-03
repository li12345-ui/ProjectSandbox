using System;
using System.Collections.Generic;
using Godot;
using ProjectSandbox.World;

namespace ProjectSandbox;

/// <summary>
/// 简单敌人 AI：巡逻 → 追击 → 攻击 → 死亡。
/// 纯状态机 + 距离判定，不依赖 NavigationAgent2D（项目无导航网格烘焙，经验 #1453614 教训）。
/// 碰撞可选注入 CollisionSystem，未注入时降级为无碰撞移动。
/// </summary>
public sealed partial class EnemyAI : Node2D
{
    // -------- 导出参数 --------

    [Export] public float PatrolSpeed { get; set; } = 40f;
    [Export] public float ChaseSpeed { get; set; } = 80f;
    [Export] public float DetectRange { get; set; } = 120f;   // 追击触发距离（像素）
    [Export] public float AttackRange { get; set; } = 24f;    // 攻击触发距离（像素）
    [Export] public int AttackDamage { get; set; } = 6;
    [Export] public float AttackCooldown { get; set; } = 1.0f; // 攻击冷却秒数
    [Export] public int MaxHp { get; set; } = 30;

    // -------- 内部状态 --------

    private enum AIState { Patrol, Chase, Attack, Dead }
    private AIState _state = AIState.Patrol;

    private Node2D? _target;
    private CollisionSystem? _collision;
    private Vector2 _velocity;

    private int _hp;
    private float _attackCooldownTimer;
    private float _patrolTurnTimer;
    private Vector2 _patrolDir = Vector2.Right;

    // -------- 构造 --------

    public EnemyAI() { _hp = MaxHp; }

    // -------- 运行时注入 --------

    public void SetTarget(Node2D? target) => _target = target;
    public void SetCollisionSystem(CollisionSystem collision) => _collision = collision;

    // -------- 公开接口 --------

    /// <summary>受击入口；HP ≤ 0 时切 Dead 状态。</summary>
    public void TakeDamage(int damage)
    {
        if (_state == AIState.Dead) return;
        _hp = Math.Max(0, _hp - damage);
        if (_hp <= 0)
        {
            _state = AIState.Dead;
            QueueFree(); // 死亡立即销毁（占位：后续替换为死亡动画+掉落）
        }
    }

    public int CurrentHp => _hp;

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
        // 检测玩家进入 DetectRange → 切 Chase
        if (TryDetectPlayer()) return;

        // 巡逻：定期随机转向
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
        // 失去目标 → 回巡逻
        if (_target == null) { _state = AIState.Patrol; return; }

        float dist = GetDistanceToTarget();

        // 进入 AttackRange → 切 Attack
        if (dist <= AttackRange)
        {
            _state = AIState.Attack;
            _velocity = Vector2.Zero;
            return;
        }

        // 跑出 DetectRange → 回巡逻
        if (dist > DetectRange * 1.5f)
        {
            _state = AIState.Patrol;
            return;
        }

        // 直线追击（简化，无避障）
        var dir = (_target.GlobalPosition - GlobalPosition).Normalized();
        _velocity = dir * ChaseSpeed;
    }

    private void UpdateAttack(float dt)
    {
        // 离开 AttackRange → 切 Chase
        if (_target == null) { _state = AIState.Patrol; return; }
        float dist = GetDistanceToTarget();
        if (dist > AttackRange * 1.2f)
        {
            _state = AIState.Chase;
            return;
        }

        // 攻击冷却完成 → 执行攻击
        if (_attackCooldownTimer <= 0f)
        {
            PerformAttack();
            _attackCooldownTimer = AttackCooldown;
        }

        _velocity = Vector2.Zero; // 攻击时不动
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
        // 约束内最简方案：target 需暴露 TakeDamage(int) 方法
        // 未落地玩家实体前，这里记录攻击意图但不实际造成伤害
        if (_target != null && _target.HasMethod("TakeDamage"))
        {
            _target.Call("TakeDamage", AttackDamage);
        }
    }

    // -------- 移动 --------

    private void MoveWithCollision(Vector2 displacement)
    {
        if (_collision == null)
        {
            // 无碰撞系统：直接位移（巡逻开阔区域可用）
            GlobalPosition += displacement;
            return;
        }

        // 注入了 CollisionSystem：用分轴碰撞解析
        // 构造一个 16×24 的 AABB（与 PlayerMovement 对齐）
        var half = new Vector2(8f, 12f);
        var newPos = _collision.ResolveCollision(GlobalPosition, half, displacement / 0.016f, 0.016f);
        GlobalPosition = newPos;
    }
}
