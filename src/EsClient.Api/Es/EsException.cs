namespace EsClient.Api.Es;

/// <summary>ES 请求异常，携带从 ES 错误体解析出的类型与原因。</summary>
public class EsException : Exception
{
    public string? EsType { get; }
    public int StatusCode { get; }

    public EsException(string message, int statusCode = 502, string? esType = null) : base(message)
    {
        EsType = esType;
        StatusCode = statusCode;
    }

    /// <summary>将 ES 错误类型映射为友好中文文案。</summary>
    public static EsException FromResponse(int statusCode, string? esType, string? reason, string? rootType = null)
    {
        var t = esType ?? rootType;
        var msg = t switch
        {
            "index_not_found_exception" => $"索引不存在：{reason}",
            "security_exception" => "认证或授权失败，请检查用户名/密码/API Key 及索引权限",
            "cluster_block_exception" => $"集群处于只读或锁定状态：{reason}",
            "parsing_exception" => $"查询语法错误：{reason}",
            "query_shard_exception" => $"查询无法执行（检查字段类型与查询语法）：{reason}",
            "search_phase_execution_exception" => $"查询执行失败：{reason}",
            "too_many_clauses" => $"查询条件过多：{reason}",
            "circuit_breaking_exception" => $"集群内存保护已触发，查询被拒绝：{reason}",
            "illegal_argument_exception" => $"参数错误：{reason}",
            "version_conflict_engine_exception" => "版本冲突：文档已被其他请求修改，请刷新后重试",
            "resource_not_found_exception" => "文档不存在（可能已被删除）",
            "document_parsing_exception" => $"文档解析失败（检查字段类型与格式）：{reason}",
            _ => !string.IsNullOrEmpty(reason) ? $"ES 错误：{reason}" : $"ES 请求失败（HTTP {statusCode}）"
        };
        return new EsException(msg, statusCode, t);
    }
}
