using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace XDownloader {
sealed class PreviewMedia {
    public string postId,assetId,user,date,kind,thumbnail,large,video,state;
    public static string ImageSize(string url,string size) {
        if(String.IsNullOrEmpty(url) || !Model.Allowed(url) || new Uri(url).Host!="pbs.twimg.com")return "";
        if(size!="small" && size!="large")throw new ArgumentException("Unsupported preview size");
        try { return Model.Original(url).Replace("&name=orig","&name="+size); }catch(UserError) { return ""; }
    }
    public static PreviewMedia Create(Post post,Asset asset,string state) {
        string image=String.IsNullOrEmpty(asset.PreviewImageUrl) && asset.Kind=="photo"?asset.Url:asset.PreviewImageUrl;
        DateTimeOffset date;
        return new PreviewMedia { postId=post.Id,assetId=asset.Id,user=post.User,
            date=DateRange.Parse(post.Date,out date)?date.UtcDateTime.ToString("yyyy-MM-dd HH:mm")+" UTC":"发布日期未知",
            kind=asset.Kind,thumbnail=ImageSize(image,"small"),large=ImageSize(image,"large"),
            video=asset.Kind!="photo" && Model.Allowed(asset.Url) && new Uri(asset.Url).Host=="video.twimg.com"?asset.Url:"",state=state };
    }
}
// The gallery is an isolated, local document. It never reads Chrome login data or download history.
sealed class MediaGallery : UserControl {
    internal const int PageSize=24;
    static bool loaderConfigured;
    readonly IList<PreviewMedia> media;
    readonly Action<string> diagnostic;
    readonly string cacheRoot;
    readonly Label message=new Label { Dock=DockStyle.Fill,Padding=new Padding(20),Text="正在准备媒体预览…" };
    internal WebView2 View;
    internal string SessionFolder;
    FileStream lease;
    bool starting,closed,ready,active;
    string documentUri;
    readonly HashSet<string> reported=new HashSet<string>();
    public MediaGallery(IList<PreviewMedia> items,Action<string> log,string temporaryRoot=null) {
        media=items;diagnostic=log??(s=>{});
        cacheRoot=temporaryRoot??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XDownloader","PreviewCache");
        BackColor=Color.White;Controls.Add(message);
    }
    internal static string Document() {
        using(var stream=typeof(MediaGallery).Assembly.GetManifestResourceStream("XDownloader.Gallery.html"))
        using(var reader=new StreamReader(stream))return reader.ReadToEnd();
    }
    void Report(string text) { if(reported.Add(text))diagnostic(text); }
    public async void ActivateGallery() {
        active=true;
        if(ready) { View.CoreWebView2.PostWebMessageAsJson(Json.Write(new { kind="resume" }));return; }
        if(starting || closed)return;starting=true;
        try {
            CleanupOld(cacheRoot);
            SessionFolder=Path.Combine(cacheRoot,"session_"+Guid.NewGuid().ToString("N"));
            TaskArchive.CheckPath(cacheRoot,SessionFolder);Directory.CreateDirectory(SessionFolder);
            lease=new FileStream(Path.Combine(SessionFolder,".lease"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            if(!loaderConfigured) {
                CoreWebView2Environment.SetLoaderDllFolderPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"vendor","WebView2",IntPtr.Size==8?"x64":"x86"));
                loaderConfigured=true;
            }
            View=new WebView2 { Dock=DockStyle.Fill,AllowExternalDrop=false,CreationProperties=new CoreWebView2CreationProperties { UserDataFolder=SessionFolder,IsInPrivateModeEnabled=true } };
            Controls.Add(View);View.BringToFront();
            await View.EnsureCoreWebView2Async();
            if(closed)return;
            var core=View.CoreWebView2;
            string document=Document();documentUri="data:text/html;charset=utf-8;base64,"+Convert.ToBase64String(Encoding.UTF8.GetBytes(document));
            core.IsMuted=true;
            core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.AreDevToolsEnabled=false;
            core.Settings.AreHostObjectsAllowed=false;core.Settings.AreBrowserAcceleratorKeysEnabled=false;
            core.Settings.IsStatusBarEnabled=false;core.Settings.IsZoomControlEnabled=false;
            core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
            core.PermissionRequested+=delegate(object sender,CoreWebView2PermissionRequestedEventArgs e) { e.State=CoreWebView2PermissionState.Deny;e.Handled=true; };
            core.DownloadStarting+=delegate(object sender,CoreWebView2DownloadStartingEventArgs e) { e.Cancel=true;e.Handled=true; };
            core.NewWindowRequested+=delegate(object sender,CoreWebView2NewWindowRequestedEventArgs e) { e.Handled=true; };
            core.NavigationStarting+=delegate(object sender,CoreWebView2NavigationStartingEventArgs e) { if(e.Uri!="about:blank" && e.Uri!=documentUri)e.Cancel=true; };
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            core.WebResourceRequested+=delegate(object sender,CoreWebView2WebResourceRequestedEventArgs e) {
                if(e.Request.Uri=="about:blank" || e.Request.Uri==documentUri)return;
                bool allowed=Model.Allowed(e.Request.Uri) && (e.ResourceContext==CoreWebView2WebResourceContext.Image || e.ResourceContext==CoreWebView2WebResourceContext.Media);
                if(!allowed)e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(),403,"Blocked","Content-Type: text/plain");
            };
            core.ProcessFailed+=delegate { if(!closed)Fail("媒体预览组件意外退出，请关闭预览后重新打开。数量统计和下载不受影响。"); };
            core.WebMessageReceived+=Receive;
            core.NavigateToString(document);
            Report("媒体预览已启动：独立 InPrivate 环境；每页最多 24 项，悬停 450 毫秒后放大；默认静音，可单独开启当前视频声音，切换媒体后恢复静音。");
        }catch(Exception ex) {
            if(!closed) { Report("媒体预览初始化失败："+ex.GetType().Name);Fail("媒体预览暂时不可用。请确认已完整解压发布包，并已安装 Microsoft Edge WebView2 Runtime。数量统计和正式下载仍可使用。\r\n\r\n如需安装运行时，请从微软官网获取；程序不会自动安装。"); }
        }
    }
    void Fail(string text) { ready=false;message.Text=text;message.BringToFront();if(View!=null) { View.Dispose();View=null; } }
    void Receive(object sender,CoreWebView2WebMessageReceivedEventArgs e) {
        if(closed || e.Source!="about:blank" && e.Source!=documentUri)return;
        try {
            string json=e.WebMessageAsJson;if(json.Length>1024)return;
            var data=Json.Read(json);string kind=Json.Str(data,"kind");
            if(kind=="ready") { ready=true;SendPage(0);if(!active)SuspendGallery(); }
            else if(kind=="page") { int page;if(Int32.TryParse(Json.Str(data,"page"),out page))SendPage(page); }
            else if(kind=="audio" && View!=null) { View.CoreWebView2.IsMuted=!(active && ready && Json.Get(data,"enabled") is bool && (bool)Json.Get(data,"enabled")); }
            else if(kind=="error") {
                string type=Json.Str(data,"type");if(type=="thumbnail" || type=="image" || type=="video")Report("媒体预览加载失败："+type+"。请检查网络或稍后重试；正式下载记录未改变。");
            }
        }catch(Exception ex) { Report("媒体预览消息处理失败："+ex.GetType().Name); }
    }
    void SendPage(int page) {
        int pages=Math.Max(1,(media.Count+PageSize-1)/PageSize);
        if(page<0 || page>=pages || !ready || closed)return;
        View.CoreWebView2.PostWebMessageAsJson(Json.Write(new { kind="page",page=page,pages=pages,total=media.Count,items=media.Skip(page*PageSize).Take(PageSize).ToArray() }));
    }
    public void SuspendGallery() {
        active=false;
        if(ready && !closed && View!=null)try { View.CoreWebView2.IsMuted=true;View.CoreWebView2.PostWebMessageAsJson("{\"kind\":\"suspend\"}"); }catch(Exception) {}
    }
    protected override void Dispose(bool disposing) {
        if(disposing && !closed) {
            SuspendGallery();closed=true;
            if(View!=null) { View.Dispose();View=null; }
            if(lease!=null) { lease.Dispose();lease=null; }
            string folder=SessionFolder;
            if(folder!=null)Task.Run(async ()=> { for(int i=0;i<6;i++) { if(TryCleanup(cacheRoot,folder))return;await Task.Delay(500); } });
        }
        base.Dispose(disposing);
    }
    internal static bool TryCleanup(string root,string folder) {
        try {
            if(!Directory.Exists(folder))return true;
            string full=Path.GetFullPath(folder);
            if(!String.Equals(Path.GetDirectoryName(full),Path.GetFullPath(root).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(full),@"^session_[a-f0-9]{32}$"))return false;
            TaskArchive.CheckPath(root,full);
            var pending=new Stack<string>();pending.Push(full);
            while(pending.Count>0)foreach(string child in Directory.GetFileSystemEntries(pending.Pop())) {
                var attrs=File.GetAttributes(child);if((attrs&FileAttributes.ReparsePoint)!=0)return false;
                if((attrs&FileAttributes.Directory)!=0)pending.Push(child);
            }
            using(new FileStream(Path.Combine(full,".lease"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {}
            Directory.Delete(full,true);return true;
        }catch(IOException) { return false; }catch(UnauthorizedAccessException) { return false; }catch(UserError) { return false; }
    }
    static void CleanupOld(string root) {
        if(!Directory.Exists(root))return;
        TaskArchive.CheckPath(root,root);
        foreach(string folder in Directory.GetDirectories(root,"session_*"))TryCleanup(root,folder);
    }
}
}
