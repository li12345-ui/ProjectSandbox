using Godot;

namespace ProjectSandbox;

/// <summary>
/// 玩家移动控制器：水平移动、跳跃、重力、土狼时间、跳跃缓冲。
/// 继承 CharacterBody2D，_PhysicsProcess 里做输入读取→速度计算→MoveAndSlide 积分。
///
/// 已知限制（风险栏详述）：
/// (1) 复用 "move_up" action 作跳跃输入——InputMap.json 里没有独立 "jump" action，
///     本任务约束只改此文件，故权宜复用；将来可改 InputMap.json 时补独立 "jump" 并切换。
/// (2) 世界 TileMapLayer 尚未配置碰撞形状，MoveAndSlide 会穿透；
///     本类只负责运动逻辑，碰撞接线留待后续 TileSet collision 配置任务。
/// </summary>
public sealed partial class PlayerMovement : CharacterBody2D
{
    // -------- 可调参数（Inspector 可改，调试友好） --------

    [Export] public float MoveSpeed { get; set; } = 200f;
    [Export] public float Gravity { get; set; } = 900f;
    [Export] public float JumpForce { get; set; } = -400f;
    [Export] public float CoyoteTime { get; set; } = 0.1f;
    [Export] public float JumpBufferTime { get; set; } = 0.1f;

    // -------- 状态 --------

    private float _coyoteTimer;
    private float _jumpBufferTimer;

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // 1) 水平输入：左右方向合成一个 float
        float dir = (Input.IsActionPressed("move_right") ? 1f : 0f)
                  - (Input.IsActionPressed("move_left") ? 1f : 0f);
        var vel = Velocity;
        vel.X = dir * MoveSpeed;

        // 2) 重力累加
        vel.Y += Gravity * dt;

        // 3) 土狼时间：着地时刷新计时器，离地后 CoyoteTime 窗口内仍可跳
        if (IsOnFloor())
            _coyoteTimer = CoyoteTime;
        else
            _coyoteTimer -= dt;

        // 4) 跳跃缓冲：jump 按下时若条件不满足，存 bufferTimer；着地/土狼窗口内仍可触发
        if (Input.IsActionJustPressed("move_up"))
            _jumpBufferTimer = JumpBufferTime;
        else
            _jumpBufferTimer -= dt;

        // 5) 跳跃判定：buffer > 0 且（着地 或 土狼窗口内）则触发
        if (_jumpBufferTimer > 0f && _coyoteTimer > 0f)
        {
            vel.Y = JumpForce;
            _jumpBufferTimer = 0f;
            _coyoteTimer = 0f;
        }

        Velocity = vel;

        // 6) 位置积分
        MoveAndSlide();
    }
}
