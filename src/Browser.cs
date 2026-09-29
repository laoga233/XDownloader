using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace XDownloader {
static class Json {
    public static string Write(object o) { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Serialize(o); }
    public static Dictionary<string, object> Read(string s) { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(s); }
    public static Dictionary<string, object> Map(object o) { return o as Dictionary<string, object> ?? new Dictionary<string, object>(); }
    public static object Get(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) ? v : null; }
    public static string Str(Dictionary<string, object> d, string k) { return Convert.ToString(Get(d, k)); }
    public static IEnumerable<object> Arr(object o) { var items = o as IList; return items == null ? new object[0] : items.Cast<object>(); }
}
class UserError : Exception { public UserError(string m) : base(m) {} }
class StopQueue : UserError { public StopQueue(string m) : base(m) {} }
// Only the dedicated Chrome profile is accessed. No personal browser profile is inspected.
partial class Browser : IDisposable {
    readonly string profile;
    int launchPort;
    ClientWebSocket socket;
    int serial;
    string session;
    internal readonly Queue<Dictionary<string,object>> events = new Queue<Dictionary<string,object>>();
    readonly SemaphoreSlim gate = new SemaphoreSlim(1,1);
    public Browser(string profilePath) { profile = profilePath; }
    string ChromePath() {
        foreach (string root in new [] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }) {
            string path = Path.Combine(root,"Google","Chrome","Application","chrome.exe");
            if (File.Exists(path)) return path;
        }
        throw new UserError("没有找到 Google Chrome，请先安装 Chrome。");
    }
    protected virtual async Task<string> DebugAddress(CancellationToken ct) {
        string file = Path.Combine(profile,"XDownloaderDebugEndpoint");
        if (!File.Exists(file)) throw new UserError("请先点击「打开登录窗口」，并保持该窗口打开。");
        string[] lines = File.ReadAllLines(file);
        int port;
        if (lines.Length < 2 || !Int32.TryParse(lines[0],out port) || port < 1 || port > 65535) throw new UserError("登录窗口尚未就绪，请稍后重试。");
        using (var h = new HttpClient(new HttpClientHandler { UseProxy = false })) {
            h.Timeout = TimeSpan.FromSeconds(2);
            var response = await h.GetAsync("http://127.0.0.1:"+port+"/json/version",ct);
            response.EnsureSuccessStatusCode();
            var d = Json.Read(await response.Content.ReadAsStringAsync());
            Uri u = new Uri(Json.Str(d,"webSocketDebuggerUrl"));
            if (!u.IsLoopback || u.Port != port || u.AbsolutePath != lines[1]) throw new UserError("登录窗口身份不匹配，请关闭专用窗口后重试。");
            return "ws://127.0.0.1:"+port+u.PathAndQuery;
        }
    }
    public static string LoginArguments(string profilePath,int port) {
        if(port<1 || port>65535)throw new UserError("无效的浏览器连接端口。");
        return "--user-data-dir=\""+profilePath+"\" --remote-debugging-address=127.0.0.1 --remote-debugging-port="+port+" --no-first-run --no-default-browser-check --new-window https://x.com/i/flow/login";
    }
    protected virtual void LaunchLoginWindow() {
        Directory.CreateDirectory(profile);
        try {
            using(var started=Process.Start(new ProcessStartInfo(ChromePath(), LoginArguments(profile,launchPort)) { UseShellExecute = false, WindowStyle=ProcessWindowStyle.Normal })) {
                if(started==null)throw new UserError("Windows 未能启动 Chrome，请检查 Chrome 安装。");
            }
        } catch(System.ComponentModel.Win32Exception) { throw new UserError("Windows 无法启动 Chrome，请检查 Chrome 安装或应用运行限制。"); }
    }
    public async Task Open(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        // Reuse a verified v0.2 browser. Do not reuse a live v0.1 port-zero session.
        string existing=null;
        try { existing=await DebugAddress(ct); } catch(OperationCanceledException) { ct.ThrowIfCancellationRequested(); }catch(UserError) {}catch(HttpRequestException) {}catch(IOException) {}
        if(existing!=null) { launchPort=new Uri(existing).Port;LaunchLoginWindow();return; }
        string old=Path.Combine(profile,"DevToolsActivePort");
        if(File.Exists(old)) {
            string[] lines=File.ReadAllLines(old);int oldPort;
            if(lines.Length>=2 && Int32.TryParse(lines[0],out oldPort) && oldPort>0 && oldPort<=65535) {
                string address=null;
                try { address=await Probe(oldPort,ct); }catch(OperationCanceledException) { ct.ThrowIfCancellationRequested(); }catch(UserError) {}catch(HttpRequestException) {}catch(IOException) {}
                if(address!=null && new Uri(address).AbsolutePath==lines[1])throw new UserError("旧版启动的专用 Chrome 仍在运行。请关闭它的全部窗口，再点「打开登录窗口」；无需关闭日常 Chrome，也无需删除登录资料。");
            }
        }
        var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);
        try { listener.Start();launchPort=((IPEndPoint)listener.LocalEndpoint).Port; }finally { listener.Stop(); }
        LaunchLoginWindow();
        var timer=Stopwatch.StartNew();
        while(timer.Elapsed<TimeSpan.FromSeconds(25)) {
            ct.ThrowIfCancellationRequested();
            try {
                string address=await Probe(launchPort,ct);
                File.WriteAllLines(Path.Combine(profile,"XDownloaderDebugEndpoint"),new[]{launchPort.ToString(),new Uri(address).AbsolutePath});
                await DebugAddress(ct);return;
            }
            catch(OperationCanceledException) { ct.ThrowIfCancellationRequested(); /* probe timeout, not user cancellation */ }
            catch(UserError) { }
            catch(HttpRequestException) { }
            catch(IOException) { }
            await Task.Delay(300,ct);
        }
        throw new UserError("已向 Windows 发送打开 Chrome 的指令，但 25 秒内未能连接专用窗口。若窗口已打开，请关闭下载器专用的 Chrome 窗口后重试；若未出现，请检查 Chrome 是否能正常启动。无需删除登录资料。");
    }
    async Task<string> Probe(int port,CancellationToken ct) {
        using(var http=new HttpClient(new HttpClientHandler { UseProxy=false })) {
            http.Timeout=TimeSpan.FromSeconds(2);
            var response=await http.GetAsync("http://127.0.0.1:"+port+"/json/version",ct);response.EnsureSuccessStatusCode();
            var d=Json.Read(await response.Content.ReadAsStringAsync());Uri u;
            if(!Uri.TryCreate(Json.Str(d,"webSocketDebuggerUrl"),UriKind.Absolute,out u) || u.Scheme!="ws" || !u.IsLoopback || u.Port!=port || !u.AbsolutePath.StartsWith("/devtools/browser/",StringComparison.Ordinal))throw new UserError("浏览器连接信息不匹配。");
            return "ws://127.0.0.1:"+port+u.PathAndQuery;
        }
    }
    public async Task Connect(CancellationToken ct) {
        Dispose();
        string address;
        try { address = await DebugAddress(ct); } catch (UserError) { throw; } catch { throw new UserError("无法连接登录窗口，请重新打开专用 Chrome。"); }
        socket = new ClientWebSocket();
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await socket.ConnectAsync(new Uri(address),timeout.Token);
        }
    }
    public virtual async Task<Dictionary<string,object>> Call(string method, object args, CancellationToken ct) {
        await gate.WaitAsync(ct);
        try {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(method == "Network.loadNetworkResource" ? 90 : 15));
                int id = ++serial;
                var command = new Dictionary<string,object> { {"id",id}, {"method",method}, {"params",args} };
                if(session != null && !method.StartsWith("Target.") && !method.StartsWith("Browser.")) command["sessionId"] = session;
                byte[] bytes = Encoding.UTF8.GetBytes(Json.Write(command));
                await socket.SendAsync(new ArraySegment<byte>(bytes),WebSocketMessageType.Text,true,timeout.Token);
                byte[] buffer = new byte[16384];
                while (true) {
                    using (var memory = new MemoryStream()) {
                        WebSocketReceiveResult r;
                        do {
                            r = await socket.ReceiveAsync(new ArraySegment<byte>(buffer),timeout.Token);
                            if (r.MessageType == WebSocketMessageType.Close) throw new UserError("登录窗口已关闭。");
                            memory.Write(buffer,0,r.Count);
                            if (memory.Length > 32*1024*1024) throw new UserError("浏览器响应过大。");
                        } while (!r.EndOfMessage);
                        var d = Json.Read(Encoding.UTF8.GetString(memory.ToArray()));
                        if (Convert.ToInt32(Json.Get(d,"id") ?? 0) != id) {
                            if(Json.Str(d,"sessionId")==session && events.Count<4096 && (Json.Str(d,"method")=="Network.responseReceived" || Json.Str(d,"method")=="Network.loadingFinished" || Json.Str(d,"method")=="Network.loadingFailed")) events.Enqueue(d);
                            continue;
                        }
                        if (Json.Get(d,"error") != null) throw new UserError("Chrome 操作失败："+method+"。请确认专用窗口仍打开，或更新 Chrome。");
                        return Json.Map(Json.Get(d,"result"));
                    }
                }
            }
        } catch(OperationCanceledException) {
            if(ct.IsCancellationRequested)throw;
            throw new UserError("Chrome 操作超时（"+method+"）。本次连接已中断，请查看任务标签页后重试。");
        } finally { gate.Release(); }
    }
    public void Dispose() { if (socket != null) { socket.Dispose(); socket=null; } session=null; events.Clear(); }
}

}
