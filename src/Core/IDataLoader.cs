namespace ProjectSandbox.Core;

/// <summary>
/// 软内容读取接口：data/ 下 JSON 的唯一读取入口（目录规范第四节：data 只读）。
/// 实现负责 JSON 反序列化与失败抛出；调用方拿到的是反序列化后的只读模型。
/// </summary>
public interface IDataLoader
{
    /// <summary>按 data/ 内相对路径（如 "recipes.json"）读取并绑定到模型；文件缺失或解析失败抛异常。</summary>
    T Load<T>(string relativePath);
}