using Godot;

namespace ProjectSandbox;

/// Project Sandbox — 应用入口
/// 空项目占位：启动时输出一行日志，用于验证引擎与 C# 管线。
public partial class Main : Node2D
{
    public override void _Ready()
    {
        GD.Print("[ProjectSandbox] 启动");
    }
}