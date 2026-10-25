// CdpProbe.cs -- 纯 C# 验证：不经 Playwright，能否从浏览器截到 captcha 请求头
//
// 这是"原生 C# 前端"方案的生死线：
//   captcha 只能由真实浏览器计算（算法分片动态下发，实测不可离线复刻），
//   所以必须用 CDP 连上浏览器、监听网络请求、取出 x-captcha-token + authorization。
//   如果这一步在 C# 里做不到，就必须保留 Node+Playwright，方案要改。
//
// 依赖：.NET Framework 4.8 自带 ClientWebSocket + JavaScriptSerializer（零第三方包）
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

class CdpProbe
{
    static ClientWebSocket _ws;
    static int _msgId = 0;
    static JavaScriptSerializer _json = new JavaScriptSerializer();
    static readonly object _lock = new object();

    // 已捕获的结果
    static string _captcha, _auth, _clientId, _deviceId;
    static TaskCompletionSource<bool> _got = new TaskCompletionSource<bool>();

    static async Task Send(string method, object prms)
    {
        var msg = new Dictionary<string, object> { { "id", Interlocked.Increment(ref _msgId) }, { "method", method } };
        if (prms != null) msg["params"] = prms;
        var s = _json.Serialize(msg);
        var bytes = Encoding.UTF8.GetBytes(s);
        lock (_lock) { _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait(); }
    }

    static async Task ReceiveLoop()
    {
        var buf = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (_ws.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult r;
            try
            {
                do
                {
                    r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                } while (!r.EndOfMessage);
            }
            catch { return; }

            if (sb.Length == 0) continue;
            try { Handle(sb.ToString()); } catch { }
        }
    }

    static void Handle(string raw)
    {
        var o = _json.Deserialize<Dictionary<string, object>>(raw);
        if (o == null || !o.ContainsKey("method")) return;
        var method = o["method"] as string;

        // Network.requestWillBeSentExtraInfo 带完整真实请求头（含浏览器加的 captcha）
        // Network.requestWillBeSent 带 URL/method，两者用 requestId 关联
        if (method == "Network.requestWillBeSent")
        {
            var p = o["params"] as Dictionary<string, object>;
            if (p == null) return;
            var req = p["request"] as Dictionary<string, object>;
            if (req == null) return;
            var url = req.ContainsKey("url") ? req["url"] as string : "";
            var m = req.ContainsKey("method") ? req["method"] as string : "";
            if (url != null && url.StartsWith("https://api-pan.xunlei.com") && (m == "GET" || m == "POST"))
            {
                Console.WriteLine("  [net] " + m + " " + url.Substring(0, Math.Min(60, url.Length)));
                // 从 request.headers 里找（有时已含 captcha）
                var hdrs = req.ContainsKey("headers") ? req["headers"] as Dictionary<string, object> : null;
                if (hdrs != null) TryExtract(hdrs);
            }
        }
        else if (method == "Network.requestWillBeSentExtraInfo")
        {
            var p = o["params"] as Dictionary<string, object>;
            if (p == null) return;
            var hdrs = p.ContainsKey("headers") ? p["headers"] as Dictionary<string, object> : null;
            if (hdrs != null) TryExtract(hdrs);
        }
    }

    static void TryExtract(Dictionary<string, object> hdrs)
    {
        string cap = null, auth = null, cid = null, did = null;
        foreach (var kv in hdrs)
        {
            var k = kv.Key.ToLowerInvariant();
            var v = kv.Value == null ? "" : kv.Value.ToString();
            if (k == "x-captcha-token") cap = v;
            else if (k == "authorization") auth = v;
            else if (k == "x-client-id") cid = v;
            else if (k == "x-device-id") did = v;
        }
        if (!string.IsNullOrEmpty(cap) && !string.IsNullOrEmpty(auth))
        {
            lock (_lock)
            {
                if (_captcha == null)
                {
                    _captcha = cap; _auth = auth; _clientId = cid; _deviceId = did;
                    Console.WriteLine("  *** CAPTURED ***");
                    Console.WriteLine("    x-captcha-token len=" + cap.Length + "  prefix=" + cap.Substring(0, Math.Min(20, cap.Length)));
                    Console.WriteLine("    authorization   len=" + auth.Length + "  prefix=" + auth.Substring(0, Math.Min(20, auth.Length)));
                    Console.WriteLine("    x-client-id     = " + cid);
                    Console.WriteLine("    x-device-id     = " + (did == null ? "(none)" : did.Substring(0, Math.Min(12, did.Length)) + "..."));
                    _got.TrySetResult(true);
                }
            }
        }
    }

    static async Task<string> FindPanTab()
    {
        // 从 /json/list 拿目标页的 webSocketDebuggerUrl
        using (var hc = new System.Net.Http.HttpClient())
        {
            var s = await hc.GetStringAsync("http://127.0.0.1:9222/json/list");
            var arr = _json.Deserialize<object[]>(s);
            foreach (var item in arr)
            {
                var d = item as Dictionary<string, object>;
                if (d == null) continue;
                var type = d.ContainsKey("type") ? d["type"] as string : "";
                var url = d.ContainsKey("url") ? d["url"] as string : "";
                if (type == "page" && url != null && url.Contains("pan.xunlei.com"))
                    return d["webSocketDebuggerUrl"] as string;
            }
        }
        return null;
    }

    static int Main(string[] args)
    {
        try { return Run().GetAwaiter().GetResult(); }
        catch (Exception e) { Console.WriteLine("FATAL: " + e.Message); return 1; }
    }

    static async Task<int> Run()
    {
        Console.WriteLine("=== 纯 C# CDP 探针：截 captcha ===");
        var wsUrl = await FindPanTab();
        if (wsUrl == null) { Console.WriteLine("找不到 pan.xunlei.com 标签页"); return 2; }
        Console.WriteLine("目标: " + wsUrl);

        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
        Console.WriteLine("WebSocket 已连接, state=" + _ws.State);

        var pump = Task.Run(new Func<Task>(ReceiveLoop));

        await Send("Network.enable", null);
        await Task.Delay(300);
        await Send("Page.enable", null);
        await Task.Delay(300);
        Console.WriteLine("Network/Page 已启用，现在导航制造一次 API 请求...");

        // 导航到云盘首页 —— 网页版会自动发一次 GET /drive/v1/files（带 captcha）
        await Send("Page.navigate", new Dictionary<string, object> { { "url", "https://pan.xunlei.com/" } });

        // 最多等 25 秒
        var done = await Task.WhenAny(_got.Task, Task.Delay(25000));
        if (done == _got.Task && _got.Task.Result)
        {
            Console.WriteLine("=== 结果: 成功截到 captcha（C# 方案可行）===");
            // 顺手验证一下这个 captcha 能不能直接调 API
            using (var hc = new System.Net.Http.HttpClient())
            {
                var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get,
                    "https://api-pan.xunlei.com/drive/v1/files?limit=5");
                req.Headers.TryAddWithoutValidation("authorization", _auth);
                req.Headers.TryAddWithoutValidation("x-captcha-token", _captcha);
                if (_clientId != null) req.Headers.TryAddWithoutValidation("x-client-id", _clientId);
                if (_deviceId != null) req.Headers.TryAddWithoutValidation("x-device-id", _deviceId);
                req.Headers.TryAddWithoutValidation("origin", "https://pan.xunlei.com");
                req.Headers.TryAddWithoutValidation("referer", "https://pan.xunlei.com/");
                var resp = await hc.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                Console.WriteLine("=== 用截到的头调 API: HTTP " + (int)resp.StatusCode + " ===");
                Console.WriteLine("  " + body.Substring(0, Math.Min(300, body.Length)));
            }
            try { _ws.Abort(); } catch { }
            return 0;
        }
        Console.WriteLine("=== 结果: 25 秒内没截到（C# 方案需要调整）===");
        try { _ws.Abort(); } catch { }
        return 3;
    }
}
