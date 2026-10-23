using EsClient.Api.Es;
using EsClient.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EsClient.Api.Controllers;

[ApiController]
public class ConnectController : ControllerBase
{
    private readonly ConnectionStore _store;

    public ConnectController(ConnectionStore store) => _store = store;

    public record SwitchRequest(string Id);
    public record UpdateRequest(string Id, string Name, string? Url, string? Username, string? Password, string? ApiKey, bool? Insecure, string? AuthMode);

    // 列表/详情不回传明文凭据（apiKey/password），只给是否已配 + 认证方式；
    // username 非机密保留以便展示。避免任何拿到该接口的页面/脚本直接读走密钥。
    private static object ConnItem(ConnectionEntry e) =>
        new
        {
            id = e.Info.Url,
            url = e.Info.Url,
            name = e.Request.Name,
            clusterName = e.Info.ClusterName,
            version = e.Info.Version,
            username = e.Request.Username,
            hasApiKey = !string.IsNullOrEmpty(e.Request.ApiKey),
            hasPassword = !string.IsNullOrEmpty(e.Request.Password),
            authMode = !string.IsNullOrEmpty(e.Request.ApiKey) ? "apikey"
                : !string.IsNullOrEmpty(e.Request.Username) ? "basic" : "none",
            insecure = e.Request.Insecure,
            connected = e.Connected
        };

    [HttpPost("/api/connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "连接名称不能为空" });
        if (string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "ES 地址不能为空" });

        var client = await EsHttpClient.CreateAsync(req, ct);
        _store.Set(client, req);
        return Ok(new
        {
            id = client.Info.Url,
            url = client.Info.Url,
            name = req.Name,
            clusterName = client.Info.ClusterName,
            version = client.Info.Version,
            connections = _store.List().Select(ConnItem)
        });
    }

    [HttpPost("/api/connection/update")]
    public async Task<IActionResult> Update([FromBody] UpdateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "连接名称不能为空" });

        var entry = _store.GetEntry(req.Id);
        if (entry == null)
            return NotFound(new { error = $"连接不存在：{req.Id}" });

        // 仅改名：url 缺省时保留原地址与凭据，不动连接状态
        if (string.IsNullOrWhiteSpace(req.Url))
        {
            _store.UpdateRequest(req.Id, entry.Request with { Name = req.Name.Trim() });
            return Ok(new { ok = true, connected = _store.IsConnected });
        }

        // 凭据合并：前端不再回显明文，密钥框留空表示“保持不变”。
        // 按 authMode 决定沿用/清除，空字段沿用原值（顺带修正旧行为：改基本认证时未重输密码会被清空）。
        string? user = req.Username, pass = req.Password, key = req.ApiKey;
        var old = entry.Request;
        switch (req.AuthMode?.ToLowerInvariant())
        {
            case "none":
                user = pass = key = null;
                break;
            case "basic":
                if (string.IsNullOrEmpty(user)) user = old.Username;
                if (string.IsNullOrEmpty(pass)) pass = old.Password;
                key = null;
                break;
            case "apikey":
                if (string.IsNullOrEmpty(key)) key = old.ApiKey;
                user = pass = null;
                break;
            // authMode 缺省（旧客户端）：按传入值原样使用
        }

        var newReq = new ConnectRequest(req.Url, user, pass, key, req.Insecure ?? false, req.Name);

        if (!string.Equals(req.Url, req.Id, StringComparison.OrdinalIgnoreCase))
        {
            // 地址变更：先建连验证，成功后再删旧条目按新地址重建。顺序不能反 ——
            // 新地址填错时 CreateAsync 会抛异常，而 Remove 已把旧条目连同凭据落盘删除，
            // 用户仅是改个地址打错一个字就会丢掉整个连接，且无从恢复。
            var wasActive = _store.Active != null && string.Equals(_store.Active.Info.Url, req.Id, StringComparison.OrdinalIgnoreCase);
            var client = await EsHttpClient.CreateAsync(newReq, ct);
            _store.Remove(req.Id);
            if (wasActive) _store.Set(client, newReq);
            else
            {
                _store.SaveInactive(newReq, client.Info);
                client.Dispose();
            }
        }
        else if (entry.Connected)
        {
            // 地址不变且在线：用新配置重连验证，成功后替换客户端
            var client = await EsHttpClient.CreateAsync(newReq, ct);
            _store.Set(client, newReq);
        }
        else
        {
            // 地址不变且离线：仅保存配置
            _store.UpdateRequest(req.Id, newReq);
        }
        return Ok(new { ok = true, connected = _store.IsConnected });
    }

    [HttpGet("/api/connection")]
    public IActionResult Connection()
    {
        // Active 只读一次：原写法读两次并在两取锁间靠 Deactivate 竞态会让 entry 变 null，
        // 叠加 ConnItem(entry!) 的 null 抑制直接 NRE
        var active = _store.Active;
        var connections = _store.List();
        var activeEntry = active == null ? null
            : connections.FirstOrDefault(e => e.Connected && e.Info.Url == active.Info.Url);
        return Ok(new
        {
            connected = activeEntry != null,
            active = activeEntry == null ? null : ConnItem(activeEntry),
            connections = connections.Select(ConnItem)
        });
    }

    [HttpPost("/api/connection/switch")]
    public async Task<IActionResult> Switch([FromBody] SwitchRequest req, CancellationToken ct)
    {
        var entry = _store.GetEntry(req.Id);
        if (entry == null)
            return NotFound(new { error = $"连接不存在：{req.Id}" });

        if (!entry.Connected)
        {
            // 断开过的连接：用保存的凭据重连
            var client = await EsHttpClient.CreateAsync(entry.Request, ct);
            _store.Set(client, entry.Request);
        }
        else
        {
            _store.Switch(req.Id);
        }
        return Ok(new { ok = true, active = ConnItem(_store.GetEntry(req.Id)!) });
    }

    [HttpPost("/api/connection/remove")]
    public IActionResult Remove([FromBody] SwitchRequest req)
    {
        _store.Remove(req.Id);
        return Ok(new { ok = true, connected = _store.IsConnected });
    }

    [HttpPost("/api/disconnect")]
    public IActionResult Disconnect()
    {
        _store.Deactivate();
        return Ok(new { ok = true, connected = _store.IsConnected });
    }
}
