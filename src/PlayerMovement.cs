using Godot;
using ProjectSandbox.World;

namespace ProjectSandbox;

/// <summary>
/// 玩家移动控制器：水平移动、跳跃、重力、土狼时间、跳跃缓冲。
/// 继承 CharacterBody2D，但碰撞完全由 CollisionSystem（纯 C# AABB 分轴滑动）处理——
/// 不再依赖 MoveAndSlide，与 Godot TileSet 碰撞形状解耦。
///
/// 接线：Game.cs 在 TryInitWorld 里创建 CollisionSystem 后，调 SetCollisionSystem 注入。
/// CollisionSystem 未注入时降级为自由移动（巡逻开阔区域可用）。
///
/// AABB 约定：CharacterBody2D.Position = AABB 中心，halfSize = (12, 20)。
/// CollisionSystem.ResolveCollision 的 position 参数 = 左上角 = Position - halfSize。
/// </summary>
public sealed partial class PlayerMovement : CharacterBody2D
{
    // -------- 可调参数（Inspector 可改，调试友好） --------

    [Export] public float MoveSpeed { get; set; } = 200f;
    [Export] public float Gravity { get; set; } = 900f;
    [Export] public float JumpForce { get; set; } = -400f;
    [Export] public float CoyoteTime { get; set; } = 0.1f;
    [Export] public float JumpBufferTime { get; set; } = 0.1f;

    // -------- 速度：遮蔽基类公开属性为私有 set（审查 P0-状态守卫） --------

    /// <summary>
    /// 当前速度（px/s）。
    /// 遮蔽 CharacterBody2D.Velocity 并收窄为私有 set——外部节点无法直接改 .X/.Y，
    /// 防止绕过重力/土狼时间/跳跃缓冲组成的完整状态机造成状态漂移。
    /// 本项目碰撞由 CollisionSystem 手动解析（不调 MoveAndSlide），
    /// CharacterBody2D 原生 Velocity 的物理层语义未被消费，遮蔽无兼容风险。
    /// </summary>
    public new Vector2 Velocity { get; private set; }

    // -------- AABB 半尺寸（24×40 玩家体，Position = 中心） --------

    private readonly Vector2 _halfSize = new(12f, 20f);

    /// <summary>碰撞 AABB 半尺寸（只读）。供 Game 出生点定位/读档夹紧复用，避免半宽半高魔法数扩散。</summary>
    public Vector2 HalfSize => _halfSize;

    // -------- 依赖注入 --------

    private CollisionSystem? _collision;

    public void SetCollisionSystem(CollisionSystem collision) => _collision = collision;

    // -------- 状态 --------

    private float _coyoteTimer;
    private float _jumpBufferTimer;
    private bool _wasOnGround;

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // 1) 水平输入：左右方向合成一个 float
        float dir = (Input.IsActionPressed("move_right") ? 1f : 0f)
                  - (Input.IsActionPressed("move_left") ? 1f : 0f);
        var vel = Velocity;
        vel.X = dir * MoveSpeed;

        // 2) 重力累加（只在离地时加——土狼窗口内保持着地状态）
        bool isGrounded = _collision != null ? _collision.IsOnGround(Position - _halfSize, _halfSize) : false;
        if (isGrounded)
            vel.Y = 0f;
        else
            vel.Y += Gravity * dt;

        // 3) 土狼时间：着地时刷新计时器，离地后 CoyoteTime 窗口内仍可跳
        if (isGrounded)
            _coyoteTimer = CoyoteTime;
        else
            _coyoteTimer -= dt;

        // 4) 跳跃缓冲：jump 按下时若条件不满足，存 bufferTimer；着地/土狼窗口内仍可触发
        if (Input.IsActionJustPressed("move_up"))
            _jumpBufferTimer = JumpBufferTime;
        else
            _jumpBufferTimer -= dt;

        // 5) 跳跃判定：buffer > 0 且（着地 或 土狼窗口内）则触发
        if (_jumpBufferTimer > 0f && (isGrounded || _coyoteTimer > 0f))
        {
            vel.Y = JumpForce;
            _jumpBufferTimer = 0f;
            _coyoteTimer = 0f;
        }

        Velocity = vel;

        // 6) 位置积分 + 碰撞解析
        var topLeft = Position - _halfSize; // AABB 左上角
        var correctedTopLeft = _collision != null
            ? _collision.ResolveCollision(topLeft, _halfSize, Velocity, dt)
            : topLeft + Velocity * dt;
        Position = correctedTopLeft + _halfSize; // 转回 CharacterBody2D.Position（中心）

        _wasOnGround = isGrounded;
    }
}
