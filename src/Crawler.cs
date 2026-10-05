using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XDownloader {
class ScanResult { public string Reason=""; public bool End; public int Posts, Rejected,TypeSkipped,SensitiveSkipped,SensitivityUnknown; }
partial class Browser {
    string ownedTarget;
    internal int ScanIdleMilliseconds=45000;
    public async Task AttachPage(CancellationToken ct) {
        var target=await Call("Target.createTarget",new { url="about:blank" },ct);
        ownedTarget=Json.Str(target,"targetId");
        var attached=await Call("Target.attachToTarget",new { targetId=ownedTarget,flatten=true },ct);
        session=Json.Str(attached,"sessionId");
        await Call("Network.enable",new { maxTotalBufferSize=64*1024*1024,maxResourceBufferSize=16*1024*1024 },ct);
        await Call("Page.enable",new {},ct);
        await Call("Runtime.enable",new {},ct);
    }
    public async Task ClosePage() {
        if(ownedTarget==null)return;
        try { using(var c=new CancellationTokenSource(2000))await Call("Target.closeTarget",new { targetId=ownedTarget },c.Token); } catch { }
        ownedTarget=null;
    }
    public static string Operation(string address) {
        Uri u;
        if(!Uri.TryCreate(address,UriKind.Absolute,out u) || u.Scheme!="https" || !u.IsDefaultPort || u.UserInfo!="" || !(Model.XHost(u.Host) || u.Host=="api.x.com" || u.Host=="api.twitter.com"))return "";
        var match=Regex.Match(u.AbsolutePath,@"^/(?:i/api/)?graphql/[^/]+/([A-Za-z0-9_]+)/?$");
        if(!match.Success)return "";
        string name=match.Groups[1].Value;
        if(new[]{"TweetDetail","TweetResultByRestId","UserMedia","UserTweets","UserTweetsAndReplies","UserByScreenName","UserByRestId"}.Contains(name))return name;
        using(var hash=System.Security.Cryptography.SHA256.Create())return "其他GraphQL#"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(name))).Replace("-","").Substring(0,10);
    }
    public static bool Relevant(string address, bool single) {
        string op=Operation(address);
        return single?op=="TweetDetail" || op=="TweetResultByRestId":new[]{"UserMedia","UserTweets","UserTweetsAndReplies","UserByScreenName","UserByRestId"}.Contains(op);
    }
    public static string NoProgressReason(int profileResponses,int tweetResponses,bool structured) {
        if(tweetResponses==0)return profileResponses>0?"仅收到用户资料，未收到帖子响应；媒体页可能未加载，请查看保留的 Chrome 标签页":"未收到用户资料或帖子响应；请查看 Chrome 登录、验证和网络状态";
        return !structured?"收到帖子接口响应，但未识别帖子结构；请提供本次诊断日志":"连续 45 秒无新帖子；可能到达可见末尾或加载受限，无法确认完整性";
    }
    public async Task EnsurePage(CancellationToken ct) {
        var tree=await Call("Page.getFrameTree",new {},ct);var frame=Model.At(tree,"frameTree","frame");Uri u;
        if(!Uri.TryCreate(Json.Str(frame,"url"),UriKind.Absolute,out u) || u.Scheme!="https" || !Model.XHost(u.Host))throw new StopQueue("任务标签页已离开 X，已停止。");
    }
    public async Task<ScanResult> Scan(Target target,int maxPosts,bool replies,Action<string> log,Func<Post,Task> receive,Func<CancellationToken,Task> wait,CancellationToken ct,Func<Post,bool> include=null,DateRange dateRange=null,MediaSelection selection=null,Action<string,string,string> account=null) {
        events.Clear();
        string source=selection==null?target.Url:selection.Page(target,replies);
        bool postsSource=source!=target.Url;
        var nav=await Call("Page.navigate",new { url=source },ct);
        if(Json.Str(nav,"errorText")!="")throw new UserError("Chrome 无法加载 X 页面，请检查浏览器网络或代理。");
        // Page.navigate can return before the first document commits.
        var navigationTimer=Stopwatch.StartNew();
        while(true) {
            var tree=await Call("Page.getFrameTree",new {},ct);Uri page;
            string address=Json.Str(Model.At(tree,"frameTree","frame"),"url");
            if(Uri.TryCreate(address,UriKind.Absolute,out page) && page.Scheme=="https" && Model.XHost(page.Host))break;
            if(navigationTimer.Elapsed>TimeSpan.FromSeconds(30))throw new UserError("X 页面未在 30 秒内打开，请检查 Chrome 网络或登录页。");
            await Task.Delay(300,ct);
        }
        var pending=new HashSet<string>();var finished=new HashSet<string>();var seen=new HashSet<string>();
        var result=new ScanResult();var quiet=Stopwatch.StartNew();bool structured=false,fallback=false;int profileResponses=0,tweetResponses=0;
        var operations=new Dictionary<string,string>();var diagnosed=new HashSet<string>();
        var boundaries=new Dictionary<string,DateBoundary>();
        log("正在读取 @"+target.User+(target.Id!=""?" 的帖子 "+target.Id:postsSource?" 的用户帖子页（统一读取图片与视频）":" 的媒体页")+"…");
        while(true) {
            var pauseTimer=Stopwatch.StartNew();await wait(ct);
            if(pauseTimer.ElapsedMilliseconds>500)quiet.Restart();
            await EnsurePage(ct);
            var ready=new List<string>();
            while(events.Count>0) {
                var e=events.Dequeue();var p=Json.Map(Json.Get(e,"params"));string rid=Json.Str(p,"requestId"),method=Json.Str(e,"method");
                if(method=="Network.responseReceived") {
                    var r=Json.Map(Json.Get(p,"response"));
                    string op=Operation(Json.Str(r,"url"));
                    if(op!="" && (Relevant(Json.Str(r,"url"),target.Id!="") || op.StartsWith("其他GraphQL#"))) {
                        int code=Convert.ToInt32(Json.Get(r,"status")??0);
                        log("接口诊断："+op+" · HTTP "+code);
                        if(code==429)throw new StopQueue("X 要求限速（HTTP 429），任务已停止，请稍后重试。");
                        if(op.StartsWith("其他GraphQL#") && code!=200)continue;
                        if(code==401 || code==403)throw new StopQueue("X 拒绝访问（HTTP "+code+"），请查看专用 Chrome 的登录或验证提示。");
                        if(code!=200)throw new UserError("X 数据请求失败（HTTP "+code+"），扫描未完成。");
                        pending.Add(rid);
                        operations[rid]=op;
                    }
                } else if(method=="Network.loadingFinished")finished.Add(rid);
                else if(method=="Network.loadingFailed" && pending.Contains(rid))throw new UserError("X 数据传输失败，请重试。");
            }
            ready.AddRange(pending.Where(x=>finished.Contains(x)));
            foreach(string rid in ready) {
                pending.Remove(rid);finished.Remove(rid);
                var body=await Call("Network.getResponseBody",new { requestId=rid },ct);
                string text=Json.Str(body,"body");
                if(Object.Equals(Json.Get(body,"base64Encoded"),true))text=Encoding.UTF8.GetString(Convert.FromBase64String(text));
                string op=operations[rid];operations.Remove(rid);
                if(diagnosed.Add(op)) {
                    string schema;
                    try { schema=Model.Schema(text); }catch { schema="非预期 JSON 结构（未记录响应正文）"; }
                    log("结构诊断："+op+" · "+schema);
                }
                bool unknown=op.StartsWith("其他GraphQL#");
                Batch batch;
                try { batch=Model.Parse(text,target,replies); }
                catch(StopQueue) { throw; }
                catch(UserError) { if(!unknown)throw;log("解析诊断："+op+" · 辅助响应含错误，未作为帖子处理");continue; }
                catch { if(!unknown)throw new UserError("X 网页数据格式发生变化，未将扫描标记为完成。");log("解析诊断："+op+" · 非帖子 JSON，跳过");continue; }
                bool profile=op=="UserByScreenName" || op=="UserByRestId";
                if(profile)profileResponses++;else if(!unknown || batch.Structured)tweetResponses++;
                log("解析诊断："+op+" · 帖子结构="+(batch.Structured?"是":"否")+" · 匹配帖子="+batch.Posts.Count+" · 过滤="+batch.Rejected);
                if(account!=null && batch.UserId!="")account(batch.User,batch.UserId,batch.DisplayName);
                if(profile)continue;
                // Unknown operations may include recommendations. Keep author/privacy/ID filters,
                // and never use their end markers to claim the user's timeline has ended.
                structured|=batch.Structured;result.End|=(!unknown && batch.End);result.Rejected+=batch.Rejected;
                foreach(var post in batch.Posts) {
                    if(!seen.Add(post.Id))continue;
                    quiet.Restart();
                    if(selection!=null && !selection.Accept(post)) {
                        result.TypeSkipped++;
                        foreach(string warning in post.Warnings)log("媒体提示："+warning);
                        if(target.Id!="") { result.End=true;result.Reason="指定帖子没有可处理的所选类型媒体";return result; }
                        continue;
                    }
                    if(include!=null && !include(post))continue;
                    if(selection!=null && selection.SkipSensitive) {
                        var selectedMedia=post.Media.Where(selection.AcceptsType).ToList();
                        int blocked=selectedMedia.Count(a=>a.PossiblySensitive==true);
                        result.SensitiveSkipped+=blocked;
                        result.SensitivityUnknown+=selectedMedia.Count(a=>!a.PossiblySensitive.HasValue);
                        if(blocked==selectedMedia.Count) {
                            if(target.Id!="") { result.End=true;result.Reason="指定帖子媒体均被 X 敏感标记筛除";return result; }
                            continue;
                        }
                    }
                    result.Posts++;
                    await receive(post);quiet.Restart();
                    if(target.Id!="") { result.End=true;result.Reason="指定帖子已处理";return result; }
                    if(maxPosts>0 && result.Posts>=maxPosts) { result.End=false;result.Reason="已达到设定的扫描帖子数上限";return result; }
                }
                DateBoundary boundary;
                if(!boundaries.TryGetValue(op,out boundary)) { boundary=new DateBoundary(dateRange);boundaries[op]=boundary; }
                if(boundary.Observe(batch.Posts)) {
                    result.End=false;
                    result.Reason="日期边界收尾：同一接口连续 2 批、累计至少 5 篇新帖子按时间递减且均早于开始日期，已自动停止（按网页顺序推断，不保证完整历史）";
                    return result;
                }
            }
            if(result.End) { result.Reason="网页返回时间线结束标记（仅代表本次可见范围）";return result; }
            if(quiet.ElapsedMilliseconds>=ScanIdleMilliseconds) {
                if(target.Id=="" && result.Posts==0 && !fallback && !postsSource) {
                    fallback=true;
                    log("媒体页未提供可处理帖子，尝试用户帖子页；仍仅处理目标用户公开内容。");
                    pending.Clear();finished.Clear();operations.Clear();events.Clear();
                    boundaries.Clear();
                    var next=await Call("Page.navigate",new { url="https://x.com/"+target.User+(replies?"/with_replies":"") },ct);
                    if(Json.Str(next,"errorText")!="")throw new UserError("用户帖子页加载失败，请查看保留的 Chrome 标签页。");
                    quiet.Restart();continue;
                }
                result.Reason=NoProgressReason(profileResponses,tweetResponses,structured);
                return result;
            }
            if(target.Id=="")await Call("Runtime.evaluate",new { expression="window.scrollBy(0, Math.max(500, Math.floor(window.innerHeight * 0.8)))",returnByValue=true },ct);
            // Prune unrelated resource IDs; relevant requests are retained until completion.
            if(finished.Count>5000)finished.IntersectWith(pending);
            await Task.Delay(1800,ct);
        }
    }
}
}
