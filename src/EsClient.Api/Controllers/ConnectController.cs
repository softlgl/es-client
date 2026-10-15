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
    public record UpdateRequest(string Id, string Name, string? Url, string? Username, string? Password, string? ApiKey, bool? Insecure);

    private static object ConnItem(ConnectionEntry e) =>
        new
        {
            id = e.Info.Url,
            url = e.Info.Url,
            name = e.Request.Name,
            clusterName = e.Info.ClusterName,
            version = e.Info.Version,
            username = e.Request.Username,
            apiKey = e.Request.ApiKey,
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

        var newReq = new ConnectRequest(req.Url, req.Username, req.Password, req.ApiKey, req.Insecure ?? false, req.Name);

        if (!string.Equals(req.Url, req.Id, StringComparison.OrdinalIgnoreCase))
        {
            // 地址变更：删除旧条目按新地址重建；原为活动连接则新连接接管，否则保持断开态
            var wasActive = _store.Active != null && string.Equals(_store.Active.Info.Url, req.Id, StringComparison.OrdinalIgnoreCase);
            _store.Remove(req.Id);
            var client = await EsHttpClient.CreateAsync(newReq, ct);
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
        var entry = _store.List().FirstOrDefault(e => e.Connected && e.Info.Url == _store.Active?.Info.Url);
        return Ok(new
        {
            connected = _store.IsConnected,
            active = _store.Active == null ? null : ConnItem(entry!),
            connections = _store.List().Select(ConnItem)
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
