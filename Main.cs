using System.Collections.Generic;
using System.Text.Json.Serialization;
using Godot;
using ProjectSandbox.Core;

namespace ProjectSandbox;

/// <summary>
/// 应用入口 + 组合根：启动阶段注册全局事件总线、服务定位、数据读取三服务，并做一次管线冒烟。
/// 后续系统经 IServiceLocator 获取服务，禁止自行 new 实现实例。
/// </summary>
public partial class Main : Node2D
{
    private readonly ServiceLocator _services = ServiceLocator.Instance;

    public override void _Ready()
    {
        GD.Print("[ProjectSandbox] 启动");

        RegisterServices();
        SmokeTestServices();
    }

    /// <summary>组合根：注册唯一一份核心服务实现。</summary>
    private void RegisterServices()
    {
        _services.Register<IEventBus>(new MemoryEventBus());
        _services.Register<IServiceLocator>(_services);
        _services.Register<IDataLoader>(new JsonDataLoader());
    }

    /// <summary>管线冒烟：数据读取 + 事件发布订阅各走一次，验证三服务连通。</summary>
    private void SmokeTestServices()
    {
        var catalog = _services.Get<IDataLoader>().Load<TestItemCatalog>("items.json");
        GD.Print($"[ProjectSandbox] 冒烟：读到 {catalog.Items.Count} 条正式物品（首条 {catalog.Items[0].DisplayName}）");

        var bus = _services.Get<IEventBus>();
        var received = 0;
        using (bus.Subscribe<SmokeEvent>(_ => received++))
        {
            bus.Publish(new SmokeEvent());
        }
        GD.Print($"[ProjectSandbox] 冒烟：事件回环接收 {received} 次");
    }

    // M0 冒烟数据模型（子集）：仅映射冒烟所需字段，M1 引入正式物品模型后删除
    private sealed class TestItemCatalog
    {
        [JsonPropertyName("items")]
        public List<TestItemEntry> Items { get; set; } = new();
    }

    private sealed class TestItemEntry
    {
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";
    }

    private sealed class SmokeEvent { }
}