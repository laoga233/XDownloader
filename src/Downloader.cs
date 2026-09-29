using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XDownloader {
class Downloader {
    readonly History history;readonly Browser browser;readonly Action<string> log;readonly Func<CancellationToken,Task> wait;
    public Downloader(Browser b,Action<string> l,Func<CancellationToken,Task> w,History h=null) { browser=b;log=l;wait=w;history=h; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool MoveFileEx(string a,string b,int flags);
    public static void Commit(string temp,string target) { if(!MoveFileEx(temp,target,1|8))throw new IOException("文件提交失败。",new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error())); }
    public static void AtomicText(string path,string text) { string tmp=path+".tmp";File.WriteAllText(tmp,text,new UTF8Encoding(false));Commit(tmp,path); }
    public static string Stem(Asset a) { return a.Index.ToString("D3")+"_"+Model.Safe(a.Id); }
    public static string Stem(Post p,Asset a) {
        DateTimeOffset date;
        string stamp=DateRange.Parse(p.Date,out date)?date.UtcDateTime.ToString("yyyy-MM-dd_HH-mm-ss'Z'",System.Globalization.CultureInfo.InvariantCulture):"时间未知";
        return stamp+"_"+Model.Safe(p.User)+"_"+Model.Safe(p.Id)+"_"+a.Index.ToString("D3");
    }
    public static bool Verified(string folder,string stem,string id,string receipts=null) {
        try {
            var d=Json.Read(File.ReadAllText(Path.Combine(receipts??folder,stem+".json")));string name=Json.Str(d,"file");
            if(name!=Path.GetFileName(name) || !name.StartsWith(stem+".",StringComparison.Ordinal) || Json.Str(d,"assetId")!=id)return false;
            string path=Path.Combine(folder,name);
            return File.Exists(path) && new FileInfo(path).Length==Convert.ToInt64(Json.Get(d,"bytes")) && Model.Hash(path)==Json.Str(d,"sha256");
        } catch { return false; }
    }
    public async Task<bool> Save(Post post,Asset asset,string root,CancellationToken ct,bool flat=false,bool force=false) {
        if(!Model.Allowed(asset.Url))throw new UserError("不支持的媒体域名。");
        string folder=flat?root:Path.Combine(root,Model.Safe(post.User),post.Id),stem=flat?Stem(post,asset):Stem(asset);Directory.CreateDirectory(folder);
        string receipts=flat?Path.Combine(folder,"_记录"):folder;Directory.CreateDirectory(receipts);
        if(history==null && !force && await Task.Run(()=>Verified(folder,stem,asset.Id,receipts),ct)) { log("已校验，跳过 "+stem);return false; }
        log("正在保存："+stem);
        for(int attempt=0;attempt<3;attempt++) {
            await wait(ct);
            try { await Download(post,asset,folder,stem,ct,receipts);return true; }
            catch(TransientError) { if(attempt==2)throw new UserError("媒体服务器连续失败（5xx），请重试。");log("媒体服务器暂时出错，稍后重试…"); }
            await Task.Delay((attempt+1)*3000,ct);
        }
        return false;
    }
    async Task Download(Post post,Asset asset,string folder,string stem,CancellationToken ct,string receipts) {
        await browser.EnsurePage(ct);
        var tree=await browser.Call("Page.getFrameTree",new {},ct);string frame=Json.Str(Model.At(tree,"frameTree","frame"),"id");
        string handle="",partial=Path.Combine(folder,stem+".part");Exception failure=null;
        try {
            var result=await browser.Call("Network.loadNetworkResource",new { frameId=frame,url=asset.Url,options=new { disableCache=false,includeCredentials=false } },ct);
            var resource=Json.Map(Json.Get(result,"resource"));handle=Json.Str(resource,"stream");
            int status=Convert.ToInt32(Json.Get(resource,"httpStatusCode")??0);
            if(status==429)throw new StopQueue("媒体服务器限速（429），请稍后重试。");
            if(status==401 || status==403)throw new StopQueue("媒体访问被拒绝（"+status+"），请在 Chrome 确认页面状态。");
            if(status>=500)throw new TransientError();
            if(status!=200 || !Object.Equals(Json.Get(resource,"success"),true) || handle=="")throw new UserError("媒体加载失败（HTTP "+status+"），请检查 Chrome 网络后重试。");
            var headers=Json.Map(Json.Get(resource,"headers"));string mime="";long expected=-1;
            foreach(var kv in headers) {
                if(kv.Key.Equals("content-type",StringComparison.OrdinalIgnoreCase))mime=Convert.ToString(kv.Value).ToLowerInvariant();
                if(kv.Key.Equals("content-length",StringComparison.OrdinalIgnoreCase)) { long length;if(Int64.TryParse(Convert.ToString(kv.Value),out length))expected=length; }
            }
            if(!(mime.StartsWith(asset.Kind=="photo"?"image/":"video/mp4") || mime.StartsWith("application/octet-stream")))throw new UserError("媒体响应类型不匹配，未保存网页或错误页。");
            long total=0;bool eof=false;
            using(var output=new FileStream(partial,FileMode.Create,FileAccess.Write,FileShare.None,65536,true)) {
                while(!eof) {
                    await wait(ct);
                    var part=await browser.Call("IO.read",new { handle=handle,size=256*1024 },ct);
                    byte[] bytes=Object.Equals(Json.Get(part,"base64Encoded"),true)?Convert.FromBase64String(Json.Str(part,"data")):Encoding.UTF8.GetBytes(Json.Str(part,"data"));
                    total+=bytes.Length;
                    if(total>8L*1024*1024*1024)throw new UserError("单个文件超过 8 GB 限制。");
                    await output.WriteAsync(bytes,0,bytes.Length,ct);eof=Object.Equals(Json.Get(part,"eof"),true);
                }
                output.Flush(true);
            }
            if(expected>=0 && expected!=total)throw new UserError("媒体长度不完整，请重试。");
            byte[] first=new byte[(int)Math.Min(64,total)];using(var f=File.OpenRead(partial))f.Read(first,0,first.Length);
            string ext=Model.Extension(first,asset.Kind),target=Path.Combine(folder,stem+ext);
            string hash=await Task.Run(()=>Model.Hash(partial),ct);ct.ThrowIfCancellationRequested();
            if(history!=null)history.Persist(post,asset,target,total,hash,"pending");
            Commit(partial,target);
            if(history!=null)history.Persist(post,asset,target,total,hash,"complete");
            else AtomicText(Path.Combine(receipts,stem+".json"),Json.Write(new { postId=post.Id,userId=post.UserId,assetId=asset.Id,file=Path.GetFileName(target),bytes=total,sha256=hash,kind=asset.Kind }));
            log("已保存："+stem+ext+"（"+(total/1024)+" KB）");
        } catch(Exception ex) { failure=ex; }
        try {
            if(handle!="")try { using(var close=new CancellationTokenSource(2000))await browser.Call("IO.close",new { handle=handle },close.Token); }catch { }
        } finally {
            if(File.Exists(partial))File.Delete(partial);
        }
        if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    class TransientError:Exception {}
}
}
