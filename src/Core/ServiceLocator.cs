using System;
using System.Collections.Generic;

namespace ProjectSandbox.Core;

/// <summary>
/// 核心服务定位的进程内实现：以服务类型为键的注册表。
/// 服务须在启动阶段（组合根）注册完毕；重复注册抛异常，未注册获取抛异常。
/// 不加线程锁：注册与获取均发生在主线程启动流程与游戏循环内。
/// </summary>
public sealed class ServiceLocator : IServiceLocator
{
    /// <summary>进程级唯一服务定位实例。Godot Node 无法构造注入，静态单例是务实让步。</summary>
    public static ServiceLocator Instance { get; } = new();

    /// <summary>查询服务是否已注册（供 Node 在 _Ready 里安全降级，避免未注册抛异常中断启动）。</summary>
    public static bool IsRegistered<TService>() where TService : class
        => Instance._services.ContainsKey(typeof(TService));

    private readonly Dictionary<Type, object> _services = new();

    public void Register<TService>(TService instance) where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);

        var key = typeof(TService);
        if (_services.ContainsKey(key))
        {
            throw new InvalidOperationException($"服务 {key.Name} 已注册，禁止重复注册");
        }

        _services[key] = instance;
    }

    public TService Get<TService>() where TService : class
    {
        if (!_services.TryGetValue(typeof(TService), out var instance))
        {
            throw new InvalidOperationException($"服务 {typeof(TService).Name} 未注册，请检查启动阶段注册顺序");
        }

        return (TService)instance;
    }
}