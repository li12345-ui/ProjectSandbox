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