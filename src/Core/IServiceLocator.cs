namespace ProjectSandbox.Core;

/// <summary>
/// 核心服务定位接口：系统间获取共享服务的唯一入口。
/// 服务须在启动阶段注册完毕；运行时动态换实现须走显式再注册，避免隐式替换。
/// </summary>
public interface IServiceLocator
{
    /// <summary>注册服务实现；同一类型重复注册抛异常。</summary>
    void Register<TService>(TService instance) where TService : class;

    /// <summary>获取已注册服务；未注册抛异常，调用方不得缓存结果超过一帧。</summary>
    TService Get<TService>() where TService : class;
}