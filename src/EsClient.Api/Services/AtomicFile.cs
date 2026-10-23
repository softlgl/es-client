namespace EsClient.Api.Services;

/// <summary>
/// 本地 JSON 落盘的统一「临时文件 + 原子替换」写入。连接配置 / AI 配置 / 问答历史三处 store
/// 此前各写一份 <c>File.WriteAllText(tmp) + File.Move(overwrite)</c>，机制重复且失败处理不一致。
/// 集中到此处后写入机制单一来源；失败时一律向上抛出，由各调用方按业务语义决定「记录并继续」
/// 还是「抛给上层报错」——不再有空的 catch 静默吞掉（丢失用户唯一数据副本却毫无线索）。
/// </summary>
internal static class AtomicFile
{
    public static void Write(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    public static async Task WriteAsync(string path, string content, CancellationToken ct = default)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }
}
