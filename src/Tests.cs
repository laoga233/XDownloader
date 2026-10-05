using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XDownloader {
static class Tests {
    static void Check(bool value,string name) { if(!value)throw new Exception("FAILED: "+name); }
    static void Reject(Action f,string name) { try { f(); }catch(UserError) { return; }throw new Exception("FAILED: "+name); }
    static Dictionary<string,object> Fixture(string user,string id) {
        return Json.Read(Json.Write(new {
            __typename="Tweet",rest_id=id,
            core=new { user_results=new { result=new { __typename="User",core=new { screen_name=user },privacy=new { @protected=false } } } },
            legacy=new { full_text="fixture",created_at="today",extended_entities=new { media=new object[] {
                new { id_str="photo1",type="photo",media_url_https="https://pbs.twimg.com/media/sample.jpg" },
                new { id_str="video1",type="video",video_info=new { variants=new object[] {
                    new { content_type="application/x-mpegURL",url="https://video.twimg.com/a.m3u8",bitrate=999999 },
                    new { content_type="video/mp4",url="https://video.twimg.com/low.mp4",bitrate=100 },
                    new { content_type="video/mp4",url="https://video.twimg.com/high.mp4",bitrate=200 }
                } } }
            } } }
        }));
    }
    static Batch Parse(Dictionary<string,object> tweet) { return Model.Parse(Json.Write(new { data=new { entries=new[]{new { content=new { itemContent=new { tweet_results=new { result=tweet } } } } } } }),Target.Parse("tester"),true); }
    public static async Task Run() {
        string arguments=Browser.LoginArguments(@"C:\Test Profile",19222);
        Check(arguments.Contains("--remote-debugging-port=19222") && !arguments.Contains("--remote-debugging-port=0"),"nonzero debug port");
        Check(arguments.Contains("https://x.com/i/flow/login") && arguments.Contains("--user-data-dir=\"C:\\Test Profile\""),"login route and profile quoting");
        Reject(()=>Browser.LoginArguments("test",0),"reject port zero");
        Check(Target.Parse("@tester").Url=="https://x.com/tester/media","username");
        Check(Target.Parse("https://twitter.com/tester/status/123/photo/1?s=20").Id=="123","tweet URL");
        Check(Target.Parse("https://x.com/tester/media").User=="tester","media URL");
        foreach(var s in new[]{"https://x.com.evil/tester","https://x.com@evil/tester","https://x.com:8080/tester","https://x.com/home","file:///tmp/a","https://x.com/tester/likes"})Reject(()=>Target.Parse(s),"invalid target "+s);
        Check(!Model.Allowed("https://video.twimg.com.evil/a.mp4") && !Model.Allowed("http://pbs.twimg.com/a.jpg"),"CDN allowlist");
        Check(Model.Safe("CON")=="_CON" && !Model.Safe("../x").Contains("/"),"safe paths");
        Check(Model.Original("https://pbs.twimg.com/media/a?format=png&name=small")=="https://pbs.twimg.com/media/a?format=png&name=orig","orig query");
        var tweet=Fixture("tester","123");var batch=Parse(tweet);
        Check(batch.Posts.Count==1 && batch.Posts[0].Media.Count==2,"mixed media");
        Check(batch.Posts[0].Media[1].Url.EndsWith("high.mp4"),"highest MP4");
        Check(batch.Posts[0].Media[0].Url.EndsWith("name=orig"),"original photo");
        Check(Parse(Fixture("another","124")).Posts.Count==0,"author filter");
        tweet["quoted_status_result"]=new { result=Fixture("tester","999") };Check(Parse(tweet).Posts.Count==1,"exclude quoted media");
        var legacy=Model.At(tweet,"legacy");legacy["retweeted_status_id_str"]="999";Check(Parse(tweet).Posts.Count==0,"exclude repost");legacy.Remove("retweeted_status_id_str");
        var privacy=Model.At(tweet,"core","user_results","result","privacy");privacy["protected"]=true;Check(Parse(tweet).Posts.Count==0,"protected tweet");privacy.Remove("protected");Check(Parse(tweet).Rejected==1,"unknown privacy");privacy["protected"]=false;
        tweet["exclusive_tweet_info"]=new {};Check(Parse(tweet).Posts.Count==0,"exclusive");tweet.Remove("exclusive_tweet_info");
        legacy["in_reply_to_status_id_str"]="9";Check(Model.Parse(Json.Write(tweet),Target.Parse("tester"),false).Posts.Count==0,"reply filter");legacy.Remove("in_reply_to_status_id_str");
        Check(Model.Parse(Json.Write(tweet),Target.Parse("https://x.com/tester/status/999"),true).Posts.Count==0,"single tweet filter");
        var media=Json.Arr(Json.Get(Model.At(legacy,"extended_entities"),"media")).Select(Json.Map).ToList();
        Model.At(media[1],"video_info")["variants"]=new[]{new { content_type="application/x-mpegURL",url="https://video.twimg.com/a.m3u8" }};
        Check(Parse(tweet).Posts[0].Warnings.Count==1,"HLS explicitly reported");
        media[0]["source_status_id_str"]="999";Check(Parse(tweet).Posts[0].Media.Count==0,"borrowed media excluded");
        Reject(()=>Model.Parse("{\"errors\":[{\"code\":88}]}",Target.Parse("tester"),true),"rate limit");
        Check(Model.Parse("{\"instructions\":[{\"type\":\"TimelineTerminateTimeline\",\"direction\":\"Bottom\"}]}",Target.Parse("tester"),true).End,"explicit end");
        Check(Browser.Relevant("https://x.com/i/api/graphql/abc/UserMedia",false),"timeline endpoint");
        Check(!Browser.Relevant("https://x.com.evil/i/api/graphql/abc/UserMedia",false),"endpoint allowlist");
        Check(Browser.Relevant("https://api.x.com/graphql/abc/UserMedia/",false),"alternate GraphQL path");
        Check(!Browser.Relevant("https://api.x.com:8443/graphql/abc/UserMedia",false),"reject alternate port");
        Check(!Browser.Relevant("https://x.com/i/api/graphql/abc/HomeTimeline",false),"no unrelated harvesting");
        string unknownOp=Browser.Operation("https://x.com/i/api/graphql/abc/SecretOperation");
        Check(unknownOp.StartsWith("其他GraphQL#") && !unknownOp.Contains("SecretOperation"),"unknown operation redaction");
        Check(unknownOp!=Browser.Operation("https://x.com/i/api/graphql/abc/AnotherOperation"),"distinct unknown diagnostics");
        Check(Browser.NoProgressReason(1,0,false).Contains("仅收到用户资料"),"profile only is not tweet response");
        Check(Browser.NoProgressReason(0,1,false).Contains("未识别帖子结构"),"timeline schema failure distinguished");
        string schema=Model.Schema("{\"data\":{\"user\":{\"result\":{\"screen_name\":\"SECRET_USER\",\"full_text\":\"SECRET_TEXT\",\"SECRET_KEY\":\"SECRET_TOKEN\"}}}}");
        Check(schema.Contains("$.data.user.result.screen_name:string") && !schema.Contains("SECRET"),"diagnostic fields only");
        string nested=Model.Schema("{\"data\":{\"SECRET_WRAPPER\":{\"tweet\":{\"full_text\":\"SECRET_VALUE\"}}}}");
        Check(nested.Contains("<other>.tweet.full_text:string") && !nested.Contains("SECRET"),"unknown wrapper traversed without disclosure");
        Check(Model.Schema(Json.Write(new { instructions=new[]{new { entries=new[]{new { content=Fixture("tester","123") } } } } })).Contains("array(1)"),"diagnostic array structure");
        using(var scanner=new ScanBrowser()) {
            int received=0;var diagnostic=new List<string>();
            var scan=await scanner.Scan(Target.Parse("tester"),0,true,s=>diagnostic.Add(s),p=>{ received++;return Task.FromResult(0); },ct=>Task.FromResult(0),CancellationToken.None);
            Check(scan.End && received==1 && scan.Posts==1,"CDP response capture and explicit end");
            Check(diagnostic.Any(x=>x.Contains("UserByScreenName") && x.Contains("帖子结构=否")),"profile diagnosed separately");
            Check(diagnostic.Any(x=>x.Contains("UserMedia") && x.Contains("匹配帖子=1")),"tweet diagnosed separately");
        }
        using(var scanner=new ScanBrowser()) {
            var scan=await scanner.Scan(Target.Parse("tester"),1,true,s=>{},p=>Task.FromResult(0),ct=>Task.FromResult(0),CancellationToken.None);
            Check(!scan.End && scan.Reason.Contains("上限"),"post cap is not full completion");
        }
        using(var scanner=new ScanBrowser { OperationName="NewMediaOperation" }) {
            int received=0;
            var scan=await scanner.Scan(Target.Parse("tester"),1,true,s=>{},p=>{ received++;return Task.FromResult(0); },ct=>Task.FromResult(0),CancellationToken.None);
            Check(received==1 && scan.Posts==1 && !scan.End,"unknown endpoint reaches download callback with post limit");
        }
        using(var scanner=new ScanBrowser { FirstProfileOnly=true,ScanIdleMilliseconds=0 }) {
            int received=0;
            var scan=await scanner.Scan(Target.Parse("tester"),1,false,s=>{},p=>{ received++;return Task.FromResult(0); },ct=>Task.FromResult(0),CancellationToken.None);
            Check(received==1 && scanner.NavigationCount==2 && scanner.LastUrl=="https://x.com/tester","profile-only media page falls back to posts");
        }
        var photoSelection=new MediaSelection(true,false);var videoSelection=new MediaSelection(false,true);var mixedSelection=new MediaSelection(true,true);
        Check(photoSelection.Page(Target.Parse("tester"),false)=="https://x.com/tester","photos load post timeline not default video media page");
        Check(mixedSelection.Page(Target.Parse("tester"),true)=="https://x.com/tester/with_replies","mixed source includes replies only when selected");
        Check(videoSelection.Page(Target.Parse("tester"),false)=="https://x.com/tester/media","video source retained");
        Check(photoSelection.Page(Target.Parse("https://x.com/tester/status/123"),false)=="https://x.com/tester/status/123","single post source unchanged");
        using(var scanner=new ScanBrowser { MediaSequence=true }) {
            int selected=0;
            var scan=await scanner.Scan(Target.Parse("tester"),10,false,s=>{},p=>{ selected++;Check(p.Media.Count==1 && p.Media[0].Kind=="photo","photo-only callback excludes video");return Task.FromResult(0); },ct=>Task.FromResult(0),CancellationToken.None,null,null,photoSelection);
            Check(scanner.LastUrl=="https://x.com/tester" && selected==10 && scan.Posts==10 && scan.TypeSkipped==12,"twelve video posts do not consume ten-photo-post cap");
        }
        using(var scanner=new ScanBrowser { MediaSequence=true }) {
            int selected=0;
            var scan=await scanner.Scan(Target.Parse("tester"),0,false,s=>{},p=>{ selected++;return Task.FromResult(0); },ct=>Task.FromResult(0),CancellationToken.None,null,null,mixedSelection);
            Check(selected==23 && scan.TypeSkipped==0 && scan.End,"mixed source delivers images and videos together");
        }
        Reject(()=>Model.Extension(Encoding.ASCII.GetBytes("<html>error</html>"),"photo"),"HTML rejection");
        string dir=Path.Combine(Path.GetTempPath(),"XDownloader-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try {
            using(var browser=new FakeBrowser()) {
                var p=Parse(Fixture("tester","123")).Posts[0];var a=p.Media[0];
                var worker=new Downloader(browser,s=>{},ct=>Task.FromResult(0));
                Check(await worker.Save(p,a,dir,CancellationToken.None),"save via browser stream");
                string folder=Path.Combine(dir,"tester","123"),stem=Downloader.Stem(a);
                Check(Downloader.Verified(folder,stem,a.Id),"receipt hash");
                Check(!await worker.Save(p,a,dir,CancellationToken.None),"duplicate skip");
                File.WriteAllText(Path.Combine(folder,stem+".png"),"corrupted");
                Check(await worker.Save(p,a,dir,CancellationToken.None),"corrupt file repaired");
                browser.Status=429;Reject(()=>worker.Save(p,p.Media[1],dir,CancellationToken.None).GetAwaiter().GetResult(),"download 429");
                browser.Status=200;browser.Truncate=true;
                Reject(()=>worker.Save(p,p.Media[1],dir,CancellationToken.None).GetAwaiter().GetResult(),"incomplete length");
                Check(!File.Exists(Path.Combine(folder,Downloader.Stem(p.Media[1])+".json")),"no receipt on failure");
                Check(!Directory.GetFiles(folder,"*.part").Any(),"partial cleanup");
                browser.Truncate=false;browser.Video=true;
                Check(await worker.Save(p,p.Media[1],dir,CancellationToken.None),"MP4 save");
                Check(File.Exists(Path.Combine(folder,Downloader.Stem(p.Media[1])+".mp4")),"MP4 file");
                p.Date="Sun Sep 20 16:24:52 +1200 2026";
                Check(Downloader.Stem(p,p.Media[1]).StartsWith("2026-09-20_04-24-52Z_tester_123_002"),"publication time converted to UTC");
                string flat=Path.Combine(dir,"flat");
                Check(await worker.Save(p,p.Media[1],flat,CancellationToken.None,true),"flat media saved");
                string flatStem=Downloader.Stem(p,p.Media[1]);
                Check(File.Exists(Path.Combine(flat,flatStem+".mp4")),"media in task root");
                Check(File.Exists(Path.Combine(flat,"_记录",flatStem+".json")),"receipt separated from media");
                Check(!await worker.Save(p,p.Media[1],flat,CancellationToken.None,true),"flat duplicate verified");
                p.Date="invalid";Check(Downloader.Stem(p,p.Media[1]).StartsWith("时间未知_"),"no invented publication time");
                using(var cancel=new CancellationTokenSource()) {
                    cancel.Cancel();bool caught=false;
                    try { await worker.Save(p,a,dir,cancel.Token); }catch(OperationCanceledException) { caught=true; }
                    Check(caught,"cancellation");
                }
                Check(browser.Closed>0,"streams closed");
            }
            await FeatureTests(dir);
            await StorageTests(dir);
            await ArchiveTests(dir);
            await NicknameTests(dir);
        } finally { Directory.Delete(dir,true); }
    }
    static async Task NicknameTests(string dir) {
        var tweet=Fixture("tester","555");Model.At(tweet,"core","user_results","result","core")["name"]="显示昵称😀";
        Check(Parse(tweet).Posts[0].DisplayName=="显示昵称😀","display nickname parsed separately from handle");
        Model.At(tweet,"core","user_results","result")["legacy"]=new Dictionary<string,object>{{"name","旧字段昵称"}};
        Model.At(tweet,"core","user_results","result","core").Remove("name");
        Check(Parse(tweet).Posts[0].DisplayName=="旧字段昵称","legacy display nickname supported");
        var ct=CancellationToken.None;string root=Path.Combine(dir,"nick");Directory.CreateDirectory(root);
        var history=new History(root);history.Load(ct);var task=new TaskArchive(root,"tester");
        string oldAccount=Path.Combine(root,"@tester_42"),oldTask=Path.Combine(oldAccount,task.Key);Directory.CreateDirectory(oldAccount);Directory.Move(task.Folder,oldTask);
        var oldRecent=new RecentDestination { key=task.Key,root=root,folder=oldTask,user="tester",userId="42",started=task.Started };
        var recent=new RecentDestinations(Path.Combine(dir,"nickrecent.json"));recent.Remember(oldRecent);
        var post=Parse(Fixture("tester","556")).Posts[0];post.UserId="42";post.Date="Sun Sep 20 10:00:00 +0000 2026";
        history.BeginTask(oldTask,new { taskFolder=oldTask });
        history.PostInfo(post,new { displayName="中文/昵称😀" });
        using(var browser=new FakeBrowser())await new Downloader(browser,s=>{},t=>Task.FromResult(0),history).Save(post,post.Media[0],oldTask,ct,true);
        history.EndTask(new { taskFolder=oldTask,saved=1 },false);
        File.WriteAllText(Path.Combine(oldTask,"_记录","old.诊断.txt"),"keep diagnostic");
        var next=new TaskArchive(root,"tester");next.FinalizeAccount("tester","42","中文/昵称😀",history);
        string account=Path.GetDirectoryName(next.Folder),movedTask=Path.Combine(account,task.Key);
        Check(Path.GetFileName(account)=="中文_昵称😀（@tester）","nickname filename sanitizes slash and preserves Unicode");
        Check(!Directory.Exists(oldAccount) && File.ReadAllText(Path.Combine(movedTask,"_记录","old.诊断.txt"))=="keep diagnostic","v014 account directory moves without losing diagnostics");
        Check(history.Find(post,post.Media[0],ct) && history.FoundPath.StartsWith("中文_昵称😀（@tester）\\"),"migration rebases in-memory and persistent dedup paths");
        string export=Path.Combine(root,"export.json");history.Export(movedTask,export);Check(File.ReadAllText(export).Contains("中文_昵称😀"),"renamed historical task remains exportable");
        string database=Path.Combine(root,"_下载记录.sqlite"),before=Model.Hash(database);
        recent.Load();Check(recent.Items[0].folder==movedTask,"recent task path recovers after nickname migration");
        Check(recent.Items[0].AccountLabel=="中文/昵称😀（@tester）","old recent nickname restored from database without filename sanitization");
        Check(Model.Hash(database)==before,"viewing recent history leaves database unchanged");
        string peerRoot=Path.Combine(dir,"peers");Directory.CreateDirectory(peerRoot);
        var peers=new RecentDestinations(Path.Combine(dir,"peers.json"));
        var missing=new TaskArchive(peerRoot,"tester").Recent();missing.userId="42";peers.Remember(missing);
        var unrelated=new TaskArchive(peerRoot,"tester").Recent();unrelated.userId="99";peers.Remember(unrelated);
        var known=new TaskArchive(peerRoot,"renamed").Recent();known.userId="42";known.displayName="新昵称";peers.Remember(known);peers.Load();
        Check(peers.Items.Single(x=>x.key==missing.key).AccountLabel=="新昵称（@tester）","same stable ID backfills and persists old recent nickname");
        Check(peers.Items.Single(x=>x.key==unrelated.key).AccountLabel=="昵称未取得（@tester）","same handle with different ID cannot borrow nickname");
        Check(!File.Exists(Path.Combine(peerRoot,"_下载记录.sqlite")),"recent nickname lookup never creates a database");
        history=new History(root);history.Load(ct);Check(history.Find(post,post.Media[0],ct),"migration survives restart without redownload");
        var changed=new TaskArchive(root,"renamed");changed.FinalizeAccount("renamed","42","另一个昵称",history);Check(Path.GetDirectoryName(changed.Folder)==account,"stable numeric ID preserves same directory after handle and nickname change");
        for(int state=0;state<2;state++) {
            string recover=Path.Combine(dir,"recover"+state);Directory.CreateDirectory(recover);var h=new History(recover);h.Load(ct);
            string old="@tester_42",renamed="恢复😀（@tester）";string folder=Path.Combine(recover,old,"任务_test");Directory.CreateDirectory(folder);
            string file=Path.Combine(folder,"image.png");File.WriteAllBytes(file,new byte[]{1,2,3,4});h.Persist(post,post.Media[0],file,4,Model.Hash(file),"complete");
            using(var db=new RecordDb(Path.Combine(recover,"_下载记录.sqlite"))) { db.Execute("INSERT INTO account_folders VALUES('42',?)",old);db.Execute("INSERT INTO folder_moves VALUES(?,?,'pending')",old,renamed); }
            if(state==1)Directory.Move(Path.Combine(recover,old),Path.Combine(recover,renamed));
            h=new History(recover);h.Load(ct);
            Check(h.Find(post,post.Media[0],ct) && h.FoundPath.StartsWith(renamed+"\\"),"folder migration recovers crash "+state);
            using(var db=new RecordDb(Path.Combine(recover,"_下载记录.sqlite")))Check(db.Query("SELECT path FROM account_folders WHERE user_id='42'")[0][0]==renamed,"recovery updates stable identity mapping");
        }
    }
    static async Task ArchiveTests(string dir) {
        string root=Path.Combine(dir,"archive");Directory.CreateDirectory(root);var ct=CancellationToken.None;
        var history=new History(root);history.Load(ct);
        var first=new TaskArchive(root,"tester");string staging=first.Folder;
        File.WriteAllText(first.Diagnostic,"diagnostic survives account resolution");
        first.FinalizeAccount("tester","42","测试昵称",history);
        Check(Path.GetFileName(Path.GetDirectoryName(first.Folder))=="测试昵称（@tester）","account directory includes handle and stable ID");
        Check(File.ReadAllText(first.Diagnostic)=="diagnostic survives account resolution" && !Directory.Exists(staging),"only new task relocated with diagnostic preserved");
        Check(!Directory.Exists(Path.GetDirectoryName(staging)),"empty staging parent removed");
        Check(TaskArchive.SaveRoot(first.Folder)==root,"task selection resolves original database root");
        var p=Parse(Fixture("tester","987")).Posts[0];p.UserId="42";p.Date="Sun Sep 20 10:00:00 +0000 2026";var a=p.Media[0];
        using(var browser=new FakeBrowser()) {
            history.BeginTask(first.Folder,new { user=first.User,userId=first.UserId });
            await new Downloader(browser,s=>{},t=>Task.FromResult(0),history).Save(p,a,first.Folder,ct,true);history.EndTask(new { saved=1 },false);
            string original=Path.Combine(first.Folder,Downloader.Stem(p,a)+".png");
            var second=new TaskArchive(root,"renamed");second.FinalizeAccount("renamed","42");
            Check(Path.GetDirectoryName(first.Folder)==Path.GetDirectoryName(second.Folder) && first.Folder!=second.Folder,"renamed account reuses stable directory with unique task");
            p.User="renamed";history=new History(root);history.Load(ct);history.BeginTask(second.Folder,new { });
            Check(history.Find(p,a,ct) && File.Exists(original),"new layout dedup survives restart and renamed handle");history.Result(p,a,"skipped");
            Check(Directory.GetFiles(second.Folder,"*.png").Length==0,"historical media is not copied into new task");
            File.Delete(original);history=new History(root);history.Load(ct);history.BeginTask(second.Folder,new { repair=true });
            Check(!history.Find(p,a,ct) && history.LastRepair,"missing historical account media requires repair");
            await new Downloader(browser,s=>{},t=>Task.FromResult(0),history).Save(p,a,second.Folder,ct,true);
            Check(history.Find(p,a,ct) && history.FoundPath.StartsWith("测试昵称（@tester）\\"),"repair is recorded in current account task");history.EndTask(new { repaired=1 },false);
            var other=new TaskArchive(root,"tester");other.FinalizeAccount("tester","99");
            Check(Path.GetDirectoryName(other.Folder)!=Path.GetDirectoryName(first.Folder),"reused handle with different ID is not merged");
            Reject(()=>second.FinalizeAccount("renamed","99"),"identity change during task stops");
            string otherRoot=Path.Combine(dir,"other");Directory.CreateDirectory(otherRoot);var independent=new History(otherRoot);independent.Load(ct);
            Check(!independent.Find(p,a,ct),"different root uses independent history database");
            string exported=Path.Combine(root,"task-export.json");history.Export(second.Folder,exported);Check(File.ReadAllText(exported).Contains("测试昵称（@tester）"),"nested task export resolves root-relative paths");
        }
        var unknown=new TaskArchive(root,"tester");unknown.FinalizeAccount("tester","../bad");
        Check(unknown.UserId=="" && Path.GetFileName(Path.GetDirectoryName(unknown.Folder))=="@tester_待确认","missing or invalid ID uses explicit fallback");
        Reject(()=>new TaskArchive(root,"../bad"),"unsafe handle rejected");
        Reject(()=>TaskArchive.CheckPath(root,Path.Combine(dir,"outside")),"archive path cannot escape root");
        Reject(()=>new TaskArchive(Path.Combine(root,new string('x',101)),"tester"),"long root rejected before creating directories");
        string settings=Path.Combine(dir,"recent.json");var recent=new RecentDestinations(settings);
        recent.Remember(first.Recent());recent.Remember(first.Recent());Check(recent.Items.Count==1,"same task updates recent entry without duplication");
        for(int i=0;i<6;i++) { var task=new TaskArchive(root,"user"+i);task.FinalizeAccount("user"+i,(100+i).ToString());recent.Remember(task.Recent()); }
        recent=new RecentDestinations(settings);recent.Load();
        Check(recent.Items.Count==5 && recent.Items[0].user=="user5" && recent.Items[4].user=="user1","recent last five survive restart in order");
        Check(recent.Items.All(x=>x.root==root && x.folder!=x.root),"recent keeps root distinct from account task");
        var missing=recent.Items[0];Directory.Delete(missing.folder,true);recent.Load();Check(recent.Items[0].folder==missing.folder,"missing destination stays available for removal, not silently redirected");
        recent.Remove(recent.Items[0]);recent=new RecentDestinations(settings);recent.Load();Check(recent.Items.Count==4,"recent removal persists without deleting files");
        var fromOtherRoot=new TaskArchive(Path.Combine(dir,"other"),"tester");fromOtherRoot.FinalizeAccount("tester","42");recent.Remember(fromOtherRoot.Recent());recent.Load();Check(recent.Items[0].root!=root && recent.Items[1].root==root,"recent roots coexist without merging histories");
        var changed=first.Recent();changed.folder=Path.Combine(dir,"outside");File.WriteAllText(settings,Json.Write(new { items=new[]{changed} }));recent.Load();Check(recent.Items.Count==0 && recent.Warning!=null,"invalid recent containment rejected");
        File.WriteAllText(settings,"broken");recent.Load();Check(recent.Items.Count==0 && recent.Warning!=null,"corrupt recent settings do not prevent app startup");
        using(var scan=new ScanBrowser()) {
            string userId="";int received=0;
            await scan.Scan(Target.Parse("tester"),0,true,s=>{},post=> { received++;return Task.FromResult(0); },t=>Task.FromResult(0),ct,post=>false,null,null,(user,id,nickname)=> { userId=id; });
            Check(userId=="42" && received==0,"profile identity resolves account even when date filter excludes every post");
        }
    }
    static async Task StorageTests(string dir) {
        string root=Path.Combine(dir,"集中记录'中文");Directory.CreateDirectory(root);
        string dbPath=Path.Combine(root,"_下载记录.sqlite");
        var ct=CancellationToken.None;
        var p=Parse(Fixture("tester","789")).Posts[0];p.UserId="42";p.Date="Sun Sep 20 10:00:00 +0000 2026";
        var a=p.Media[0];
        using(var browser=new FakeBrowser()) {
            string legacyFolder=Path.Combine(root,"任务_旧版"),modern=Path.Combine(root,"任务_新版");
            await new Downloader(browser,s=>{},t=>Task.FromResult(0)).Save(p,a,legacyFolder,ct,true);
            string receipt=Path.Combine(legacyFolder,"_记录",Downloader.Stem(p,a)+".json");
            string original=Path.Combine(legacyFolder,Downloader.Stem(p,a)+".png");
            var record=Json.Read(File.ReadAllText(receipt));
            string index=Path.Combine(root,"_历史索引.json");
            Downloader.AtomicText(index,Json.Write(new { entries=new[]{new { postId=p.Id,assetId=a.Id,userId=p.UserId,path=original.Substring(root.Length+1),bytes=new FileInfo(original).Length,sha256=Model.Hash(original) }} }));
            Downloader.AtomicText(Path.Combine(legacyFolder,"_记录","tester_789_帖子信息.json"),Json.Write(new { postId=p.Id,user=p.User,date=p.Date,text="旧帖子正文" }));
            Downloader.AtomicText(Path.Combine(legacyFolder,"_记录","运行记录_旧版.json"),Json.Write(new { saved=1,time="legacy time" }));
            string diagnostic=Path.Combine(legacyFolder,"_记录","运行记录_旧版.诊断.txt");File.WriteAllText(diagnostic,"接口诊断：保留");
            var history=new History(root);history.Load(ct);
            Check(history.Imported==1 && history.Find(p,a,ct),"SQLite imports receipt and index without duplicate media");
            using(var db=new RecordDb(dbPath)) {
                Check(db.Query("SELECT COUNT(*) FROM legacy")[0][0]=="4","all four legacy document types imported");
                Check(db.Query("SELECT metadata FROM posts WHERE id=?",p.Id)[0][0].Contains("旧帖子正文"),"legacy post metadata preserved");
            }
            history.Load(ct);
            using(var db=new RecordDb(dbPath))Check(db.Query("SELECT COUNT(*) FROM legacy")[0][0]=="4","repeat migration is idempotent");
            Check(File.Exists(receipt) && File.Exists(index),"migration retains original JSON");
            Directory.CreateDirectory(modern);history.BeginTask(modern,new { test=true });history.PostInfo(p,new { postId=p.Id,text="中文 ' quote",date=p.Date });
            history.Result(p,a,"skipped");history.EndTask(new { skipped=1 },false);
            using(var db=new RecordDb(dbPath))Check(db.Query("SELECT COUNT(DISTINCT task_id) FROM task_media WHERE post_id=?",p.Id)[0][0]=="2","one media belongs to old and new tasks");
            var p2=Parse(Fixture("tester","790")).Posts[0];p2.Date=p.Date;p2.UserId=p.UserId;
            history.BeginTask(modern,new { images=true });
            var worker=new Downloader(browser,s=>{},t=>Task.FromResult(0),history);
            await worker.Save(p2,p2.Media[0],modern,ct,true);history.Result(p2,p2.Media[0],"saved");history.PostInfo(p2,new { postId=p2.Id });
            Check(Directory.GetFiles(modern,"*.json",SearchOption.AllDirectories).Length==0,"new SQLite downloads produce no JSON sidecars");
            Check(!File.Exists(Path.Combine(root,"_历史索引.json.tmp")),"new index not rewritten");
            history=new History(root);history.Load(ct);
            Check(history.Find(p2,p2.Media[0],ct),"new database download survives restart");
            using(var db=new RecordDb(dbPath))Check(db.Query("SELECT COUNT(*) FROM tasks WHERE state='interrupted'")[0][0]=="1","unfinished task recovered as interrupted");
            string p2file=Path.Combine(modern,Downloader.Stem(p2,p2.Media[0])+".png");
            byte[] bytes=File.ReadAllBytes(p2file);bytes[bytes.Length-1]^=1;File.WriteAllBytes(p2file,bytes);
            // FakeBrowser has identical image bytes; remove the old valid copy so relocation cannot mask corruption.
            File.Delete(original);
            Check(!history.Find(p2,p2.Media[0],ct) && history.LastRepair,"equal-length corruption detected by hash");
            history.BeginTask(modern,new { repair=true });
            worker=new Downloader(browser,s=>{},t=>Task.FromResult(0),history);
            await worker.Save(p2,p2.Media[0],modern,ct,true);Check(history.Find(p2,p2.Media[0],ct),"database media repaired");
            var pending=Parse(Fixture("tester","791")).Posts[0];pending.UserId=p.UserId;
            string pendingPath=Path.Combine(modern,"pending.png");
            history.Persist(pending,pending.Media[0],pendingPath,new FileInfo(p2file).Length,Model.Hash(p2file),"pending");
            // Hide identical bytes to simulate a crash before the final rename.
            string hold=p2file+".hold";File.Move(p2file,hold);
            history=new History(root);history.Load(ct);
            Check(!history.Find(pending,pending.Media[0],ct) && history.LastRepair,"pending row before file rename does not skip");
            File.Copy(hold,pendingPath);
            history=new History(root);history.Load(ct);
            Check(history.Find(pending,pending.Media[0],ct),"pending row after file rename recovers without redownload");
            using(var db=new RecordDb(dbPath)) {
                Check(db.Query("SELECT state FROM media WHERE post_id=?",pending.Id)[0][0]=="complete","recovered pending row committed");
                try { db.Transaction(()=> { db.Execute("INSERT INTO tasks VALUES('rollback','','','running','{}')");throw new InvalidOperationException(); }); }catch(InvalidOperationException) {}
                Check(db.Query("SELECT id FROM tasks WHERE id='rollback'").Count==0,"transaction rollback is atomic");
            }
            string export=Path.Combine(root,"导出.json");history.Export(modern,export);
            string exported=File.ReadAllText(export);var exportedTasks=Json.Arr(Json.Get(Json.Read(exported),"tasks"));
            bool textPreserved=exportedTasks.Any(t=>Json.Arr(Json.Get(Json.Map(t),"posts")).Any(item=>Json.Str(Json.Map(Json.Get(Json.Map(item),"metadata")),"text")=="中文 ' quote"));
            Check(exported.Contains("sha256") && textPreserved && exported.Contains("skipped"),"on-demand export includes posts and media verification");
            string backup=history.ArchiveLegacy();
            Check(File.Exists(backup) && !File.Exists(receipt) && !File.Exists(index),"explicit legacy archive removes only backed-up JSON");
            Check(File.ReadAllText(diagnostic)=="接口诊断：保留" && File.Exists(export),"archive preserves diagnostics and nonlegacy JSON");
            history=new History(root);history.Load(ct);Check(history.Find(pending,pending.Media[0],ct),"dedup survives archive and restart");
            using(var zip=System.IO.Compression.ZipFile.OpenRead(backup))Check(zip.Entries.Count==4,"verified ZIP contains every legacy source");
            // A changed legacy source must block archive rather than silently deleting new data.
            Directory.CreateDirectory(Path.GetDirectoryName(receipt));File.WriteAllText(receipt,"changed");
            Reject(()=>history.ArchiveLegacy(),"changed original not deleted");Check(File.Exists(receipt),"changed source retained");
        }
        string corrupt=Path.Combine(dir,"损坏数据库");Directory.CreateDirectory(corrupt);File.WriteAllText(Path.Combine(corrupt,"_下载记录.sqlite"),"not sqlite");
        Reject(()=>new History(corrupt).Load(ct),"corrupt database fails closed");
        string future=Path.Combine(dir,"未来数据库");Directory.CreateDirectory(future);
        using(var db=new RecordDb(Path.Combine(future,"_下载记录.sqlite")))db.Execute("PRAGMA user_version=999");
        Reject(()=>new History(future).Load(ct),"future database schema fails closed");
    }
    static async Task FeatureTests(string dir) {
        var boundary=new DateBoundary(new DateRange(new DateTime(2026,9,1),null));
        Func<int,int,List<Post>> page=(offset,n)=>Enumerable.Range(offset,n).Select(i=>new Post { Id=i.ToString(),Date=new DateTime(2026,8,31).AddMinutes(-i).ToString("ddd MMM dd HH:mm:ss '+0000' yyyy",System.Globalization.CultureInfo.InvariantCulture) }).ToList();
        Check(!boundary.Observe(page(1,2)) && boundary.Observe(page(3,3)),"two batches totaling five stop date scan");
        boundary=new DateBoundary(new DateRange(new DateTime(2026,9,1),null));
        Check(!boundary.Observe(page(1,1)),"one old pinned post never stops scan");
        Check(!boundary.Observe(page(1,1)),"repeated page does not advance boundary");
        Check(!boundary.Observe(page(2,3)),"two batches below five do not stop");
        Check(!boundary.Observe(new[]{new Post { Id="new",Date="Sun Sep 20 00:00:00 +0000 2026" }}),"in range resets boundary");
        Check(!boundary.Observe(page(12,2)) && boundary.Observe(page(14,3)),"must rebuild sequence after reset");
        boundary=new DateBoundary(new DateRange(new DateTime(2026,9,1),null));
        Check(!boundary.Observe(page(1,20)),"single large batch cannot stop");
        Check(!new DateBoundary(new DateRange(null,new DateTime(2026,9,1))).Observe(page(1,50)),"end only cannot stop old history");
        Check(!new DateBoundary(null).Observe(page(1,50)),"strict scan disables boundary");
        var day=new DateTime(2026,9,20);var range=new DateRange(day,day);
        Check(range.Accept(new Post { Date="Sun Sep 20 00:00:00 +0000 2026" }),"inclusive range start");
        Check(range.Accept(new Post { Date="Sun Sep 20 23:59:59 +0000 2026" }),"inclusive range end");
        Check(!range.Accept(new Post { Date="Mon Sep 21 00:00:00 +0000 2026" }),"exclude next midnight");
        Check(!range.Accept(new Post { Date="Sun Sep 20 01:00:00 +1200 2026" }),"UTC date boundary");
        Check(!range.Accept(new Post { Date="invalid" }) && range.Unknown==1,"unknown date excluded");
        Check(new DateRange(null,day).Accept(new Post { Date="Sat Sep 19 00:00:00 +0000 2026" }),"open start");
        Check(new DateRange(day,null).Accept(new Post { Date="Mon Sep 21 00:00:00 +0000 2026" }),"open end");
        Reject(()=>new DateRange(day.AddDays(1),day),"inverted dates rejected");
        using(var scan=new ScanBrowser { ThreePosts=true }) {
            var dates=new DateRange(day,day);int count=0;
            var result=await scan.Scan(Target.Parse("tester"),1,true,s=>{},p=>{ count++;Check(p.Id=="124","in range post selected after older pinned");return Task.FromResult(0); },ct=>Task.FromResult(0),CancellationToken.None,dates.Accept);
            Check(count==1 && result.Posts==1 && dates.Outside==1,"out of range does not consume cap");
        }
        string root=Path.Combine(dir,"history");Directory.CreateDirectory(root);
        using(var b=new FakeBrowser { Video=true }) {
            var worker=new Downloader(b,s=>{},ct=>Task.FromResult(0));
            var p=Parse(Fixture("tester","123")).Posts[0];p.Date="Sun Sep 20 10:00:00 +0000 2026";p.UserId="42";var asset=p.Media[1];
            string old=Path.Combine(root,"任务_legacy");
            await worker.Save(p,asset,old,CancellationToken.None,true);
            // Simulate a v0.5 receipt with no stable user/post fields.
            string receipt=Path.Combine(old,"_记录",Downloader.Stem(p,asset)+".json");var legacy=Json.Read(File.ReadAllText(receipt));legacy.Remove("postId");legacy.Remove("userId");Downloader.AtomicText(receipt,Json.Write(legacy));
            var history=new History(root);history.Load(CancellationToken.None);
            Check(history.Find(p,asset,CancellationToken.None),"legacy task automatically imported");
            p.User="renamed";Check(history.Find(p,asset,CancellationToken.None),"renamed user does not defeat identity");
            var overlap=new DateRange(day.AddDays(-1),day.AddDays(1));
            Check(overlap.Accept(p) && history.Find(p,asset,CancellationToken.None),"overlapping date ranges skip historical media");
            var changed=new Asset { Id="new-media",Index=2,Kind="video",Url=asset.Url };
            Check(!history.Find(p,changed,CancellationToken.None),"distinct media identity not falsely skipped");
            string original=Path.Combine(old,Json.Str(legacy,"file")),moved=Path.Combine(root,"moved.mp4");File.Move(original,moved);
            history=new History(root);history.Load(CancellationToken.None);Check(history.Find(p,asset,CancellationToken.None),"moved file discovered by hash");
            File.WriteAllText(moved,"corrupt");history=new History(root);history.Load(CancellationToken.None);
            Check(!history.Find(p,asset,CancellationToken.None) && history.LastRepair,"corruption triggers repair");
            string next=Path.Combine(root,"任务_next");await worker.Save(p,asset,next,CancellationToken.None,true);history.Record(p,asset,next);
            history=new History(root);history.Load(CancellationToken.None);Check(history.Find(p,asset,CancellationToken.None),"repair persists across tasks");
            File.Delete(Path.Combine(next,Downloader.Stem(p,asset)+".mp4"));
            Check(!history.Find(p,asset,CancellationToken.None) && history.LastRepair,"missing file triggers repair");
            await worker.Save(p,asset,next,CancellationToken.None,true);history.Record(p,asset,next);
            Check(await worker.Save(p,asset,next,CancellationToken.None,true,true),"force download bypasses local receipt");
            // Pre-flat v0.4 layout migration.
            var p2=Parse(Fixture("tester","125")).Posts[0];await worker.Save(p2,p2.Media[1],root,CancellationToken.None);
            Downloader.AtomicText(Path.Combine(root,"tester","125","帖子信息.json"),Json.Write(new { postId=p2.Id,text="v0.4 metadata" }));
            Downloader.AtomicText(Path.Combine(root,"运行记录_v04.json"),Json.Write(new { time="old root report" }));
            history=new History(root);history.Load(CancellationToken.None);Check(history.Find(p2,p2.Media[1],CancellationToken.None),"old per-post folders imported");
            using(var db=new RecordDb(Path.Combine(root,"_下载记录.sqlite"))) {
                Check(db.Query("SELECT metadata FROM posts WHERE id='125'")[0][0].Contains("v0.4 metadata"),"v0.4 post metadata imported");
                Check(db.Query("SELECT content FROM legacy WHERE path=?","运行记录_v04.json").Count==1,"v0.4 root run report imported");
            }
            string combined=Path.Combine(dir,"combined");Directory.CreateDirectory(combined);
            int newCount=0,skipCount=0;
            for(int run=0;run<3;run++) {
                var joint=new History(combined);joint.Load(CancellationToken.None);
                string output=Path.Combine(combined,"任务_"+run);Directory.CreateDirectory(output);joint.BeginTask(output,new { run=run });
                var centralWorker=new Downloader(b,s=>{},ct=>Task.FromResult(0),joint);
                var dates=new DateRange(new DateTime(2026,9,1),new DateTime(2026,9,run==0?20:30));
                newCount=skipCount=0;
                for(int i=1;i<=30;i++) {
                    var item=Parse(Fixture("tester",(1000+i).ToString())).Posts[0];item.UserId="42";
                    item.Date=new DateTime(2026,9,i).ToString("ddd MMM dd HH:mm:ss '+0000' yyyy",System.Globalization.CultureInfo.InvariantCulture);
                    if(!dates.Accept(item))continue;
                    if(joint.Find(item,item.Media[1],CancellationToken.None)) { skipCount++;joint.Result(item,item.Media[1],"skipped"); }
                    else { await centralWorker.Save(item,item.Media[1],output,CancellationToken.None,true);joint.Result(item,item.Media[1],"saved");newCount++; }
                }
                joint.EndTask(new { saved=newCount,skipped=skipCount },false);
                Check(Directory.GetFiles(output,"*.json",SearchOption.AllDirectories).Length==0,"date and history combination creates no sidecars");
                Check(run==0?newCount==20 && skipCount==0:run==1?newCount==10 && skipCount==20:newCount==0 && skipCount==30,"joint 20 then 30 then repeated date-range acceptance");
            }
        }
    }
    public static async Task BrowserCheck(string profile,string report) {
        using(var browser=new Browser(profile)) {
            try {
                await browser.Open(CancellationToken.None);await browser.Connect(CancellationToken.None);await browser.AttachPage(CancellationToken.None);
                var evaluation=await browser.Call("Runtime.evaluate",new { expression="navigator.webdriver",returnByValue=true },CancellationToken.None);
                Check(Object.Equals(Json.Get(Model.At(evaluation,"result"),"value"),false),"Chrome webdriver flag is false");
                Check(File.Exists(Path.Combine(profile,"XDownloaderDebugEndpoint")),"endpoint saved");
                await browser.Open(CancellationToken.None);
                File.WriteAllText(report,"PASS: nonzero-port Chrome launch, CDP connection, page attachment, navigator.webdriver=false, existing-browser reuse. No X account login tested.");
            }finally {
                // This method is invoked only with a newly created test profile.
                try { awaitClose(browser); }catch {}
            }
        }
    }
    static void awaitClose(Browser browser) {
        // Self-test has no UI synchronization context.
        using(var timeout=new CancellationTokenSource(3000))browser.Call("Browser.close",new {},timeout.Token).GetAwaiter().GetResult();
    }
    class ScanBrowser:Browser {
        public string OperationName="UserMedia",LastUrl;public bool FirstProfileOnly,ThreePosts,MediaSequence;public int NavigationCount;
        public ScanBrowser():base("unused") {}
        public override Task<Dictionary<string,object>> Call(string method,object args,CancellationToken ct) {
            ct.ThrowIfCancellationRequested();object response=new {};
            if(method=="Page.navigate") {
                NavigationCount++;LastUrl=Json.Str(Json.Read(Json.Write(args)),"url");
                events.Enqueue(Json.Read(Json.Write(new { method="Network.responseReceived",@params=new { requestId="profile",response=new { url="https://x.com/i/api/graphql/test/UserByScreenName",status=200 } } })));
                events.Enqueue(Json.Read(Json.Write(new { method="Network.loadingFinished",@params=new { requestId="profile" } })));
                if(FirstProfileOnly && NavigationCount==1)return Task.FromResult(Json.Read("{}"));
                events.Enqueue(Json.Read(Json.Write(new { method="Network.responseReceived",@params=new { requestId="1",response=new { url="https://x.com/i/api/graphql/test/"+OperationName,status=200 } } })));
                events.Enqueue(Json.Read(Json.Write(new { method="Network.loadingFinished",@params=new { requestId="1" } })));
            } else if(method=="Page.getFrameTree")response=new { frameTree=new { frame=new { id="frame",url="https://x.com/tester/media" } } };
            else if(method=="Network.getResponseBody") {
                string id=Json.Str(Json.Read(Json.Write(args)),"requestId");
                if(id=="profile")response=new { body="{\"data\":{\"user\":{\"result\":{\"__typename\":\"User\",\"rest_id\":\"42\",\"core\":{\"screen_name\":\"tester\"},\"privacy\":{\"protected\":false}}}}}",base64Encoded=false };
                else if(MediaSequence) {
                    var sequence=new List<object>();
                    for(int i=0;i<23;i++) {
                        var tweet=Fixture("tester",(3000+i).ToString());var entities=Model.At(tweet,"legacy","extended_entities");
                        entities["media"]=Json.Arr(Json.Get(entities,"media")).Where(x=>Json.Str(Json.Map(x),"type")== (i<12?"video":"photo")).ToArray();
                        sequence.Add(tweet);
                    }
                    response=new { body=Json.Write(new { instructions=new object[]{new { entries=sequence },new { type="TimelineTerminateTimeline",direction="Bottom" }} }),base64Encoded=false };
                }
                else if(ThreePosts) {
                    var old=Fixture("tester","123");Model.At(old,"legacy")["created_at"]="Sat Sep 19 12:00:00 +0000 2026";
                    var match=Fixture("tester","124");Model.At(match,"legacy")["created_at"]="Sun Sep 20 12:00:00 +0000 2026";
                    response=new { body=Json.Write(new { instructions=new object[]{new { entries=new[]{old,match} },new { type="TimelineTerminateTimeline",direction="Bottom" }} }),base64Encoded=false };
                }
                else response=new { body=Json.Write(new { instructions=new object[] { new { type="TimelineAddEntries",entries=new[]{new { content=new { tweet_results=new { result=Fixture("tester","123") } } } } },new { type="TimelineTerminateTimeline",direction="Bottom" } } }),base64Encoded=false };
            }
            else throw new Exception("Unexpected scanner call "+method);
            return Task.FromResult(Json.Read(Json.Write(response)));
        }
    }
    class FakeBrowser:Browser {
        public int Status=200,Closed; public bool Truncate,Video;
        public FakeBrowser():base("unused") {}
        public override Task<Dictionary<string,object>> Call(string method,object args,CancellationToken ct) {
            ct.ThrowIfCancellationRequested();object response;
            byte[] bytes=Video?new byte[]{0,0,0,16,102,116,121,112,105,115,111,109,0,0,0,0}:new byte[]{137,80,78,71,13,10,26,10,1,2,3,4};
            if(method=="Page.getFrameTree")response=new { frameTree=new { frame=new { id="frame",url="https://x.com/tester/media" } } };
            else if(method=="Network.loadNetworkResource")response=new { resource=new { httpStatusCode=Status,success=true,stream="stream",headers=new Dictionary<string,object>{{"content-type",Video?"video/mp4":"application/octet-stream"},{"content-length",bytes.Length+(Truncate?1:0)}} } };
            else if(method=="IO.read")response=new { data=Convert.ToBase64String(bytes),base64Encoded=true,eof=true };
            else if(method=="IO.close") { Closed++;response=new {}; }
            else throw new Exception("Unexpected call "+method);
            return Task.FromResult(Json.Read(Json.Write(response)));
        }
    }
}
}
