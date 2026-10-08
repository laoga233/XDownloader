using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace XDownloader {
class DateBoundary {
    readonly DateTime? start;readonly HashSet<string> seen=new HashSet<string>();
    int pages,count;DateTimeOffset? oldest;
    public DateBoundary(DateRange range) { start=range==null?null:range.Start; }
    public bool Observe(IEnumerable<Post> posts) {
        if(!start.HasValue)return false;
        var fresh=posts.Where(p=>seen.Add(p.Id)).ToList();if(fresh.Count==0)return false;
        DateTimeOffset? previous=oldest;bool ordered=true;
        foreach(var p in fresh) {
            DateTimeOffset time;
            if(!DateRange.Parse(p.Date,out time) || time.UtcDateTime.Date>=start.Value || previous.HasValue && time>previous.Value) { ordered=false;break; }
            previous=time;
        }
        if(!ordered) { pages=count=0;oldest=null;return false; }
        pages++;count+=fresh.Count;oldest=previous;
        return pages>=2 && count>=5;
    }
}
class DateRange {
    public DateTime? Start,End;
    public int Outside,Unknown;
    public DateRange(DateTime? start,DateTime? end) {
        Start=start.HasValue?start.Value.Date:(DateTime?)null;End=end.HasValue?end.Value.Date:(DateTime?)null;
        if(Start.HasValue && End.HasValue && Start>End)throw new UserError("开始日期不能晚于结束日期。");
    }
    public static bool Parse(string text,out DateTimeOffset value) {
        string normalized=Regex.Replace(text??"",@"([+-]\d{2})(\d{2})(?=\s|$)","$1:$2");
        return DateTimeOffset.TryParseExact(normalized,new[]{"ddd MMM dd HH:mm:ss zzz yyyy","yyyy-MM-dd'T'HH:mm:ss'Z'","yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz","yyyy-MM-dd'T'HH:mm:sszzz"},CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out value);
    }
    public bool Accept(Post post) {
        if(!Start.HasValue && !End.HasValue)return true;
        DateTimeOffset time;if(!Parse(post.Date,out time)) { Unknown++;return false; }
        // UTC calendar dates implement the inclusive-day range without end+1 overflow.
        DateTime day=time.UtcDateTime.Date;
        if(Start.HasValue && day<Start.Value || End.HasValue && day>End.Value) { Outside++;return false; }
        return true;
    }
}
class HistoryEntry {
    public string postId,assetId,userId,path,sha256;
    public long bytes;
}
class History {
    readonly string root,index,database;
    readonly List<HistoryEntry> entries=new List<HistoryEntry>();
    readonly List<string> files=new List<string>();
    string taskId=""; public string FoundPath;
    bool readOnly;
    public bool LastRepair;
    public int Imported { get { return entries.Count; } }
    public History(string folder) { root=Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;index=Path.Combine(root,"_历史索引.json");database=Path.Combine(root,"_下载记录.sqlite"); }
    RecordDb Open() { return new RecordDb(database); }
    public string RecordedNickname(string folder,string userId) {
        if(!File.Exists(database))return "";
        using(var db=new RecordDb(database,true)) {
            foreach(var row in db.Query("SELECT summary FROM tasks WHERE path=? COLLATE NOCASE ORDER BY started DESC LIMIT 1",Relative(folder))) {
                string name=Json.Str(Json.Read(row[0]),"displayName");if(!String.IsNullOrWhiteSpace(name))return name;
            }
            if(!Regex.IsMatch(userId??"",@"^[0-9]{1,25}$"))return "";
            foreach(var row in db.Query("SELECT metadata FROM posts WHERE user_id=? ORDER BY rowid DESC LIMIT 20",userId)) {
                string name=Json.Str(Json.Read(row[0]),"displayName");if(!String.IsNullOrWhiteSpace(name))return name;
            }
        }
        return "";
    }
    string Relative(string path) { string full=Path.GetFullPath(path);if(full.TrimEnd(Path.DirectorySeparatorChar).Equals(root.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))return "";if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new UserError("历史记录超出保存根目录。");return full.Substring(root.Length); }
    string Resolve(string path) {
        if(String.IsNullOrEmpty(path) || Path.IsPathRooted(path))return null;
        string full=Path.GetFullPath(Path.Combine(root,path));return full.StartsWith(root,StringComparison.OrdinalIgnoreCase)?full:null;
    }
    IEnumerable<string> Walk(string dir,CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)yield break;
        foreach(string file in Directory.GetFiles(dir)) { ct.ThrowIfCancellationRequested();if((File.GetAttributes(file)&FileAttributes.ReparsePoint)==0)yield return file; }
        foreach(string child in Directory.GetDirectories(dir))foreach(string file in Walk(child,ct))yield return file;
    }
    bool Accept(HistoryEntry e) { return Regex.IsMatch(e.postId??"", @"^[0-9]{1,25}$") && !String.IsNullOrEmpty(e.assetId) && e.bytes>0 && Regex.IsMatch(e.sha256??"", @"^[a-fA-F0-9]{64}$") && Resolve(e.path)!=null; }
    void Put(RecordDb db,HistoryEntry e,string state) {
        if(!Accept(e))throw new UserError("历史媒体记录字段不完整，迁移已停止；旧记录保持原样。");
        db.Execute("INSERT OR REPLACE INTO media(post_id,asset_id,user_id,path,bytes,sha256,state) VALUES(?,?,?,?,?,?,?)",e.postId,e.assetId,e.userId,e.path,e.bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),e.sha256,state);
    }
    void Cache(HistoryEntry e) { entries.RemoveAll(x=>x.postId==e.postId && x.assetId==e.assetId && x.path.Equals(e.path,StringComparison.OrdinalIgnoreCase));entries.Add(e); }
    HistoryEntry Entry(Dictionary<string,object> d) { return new HistoryEntry { postId=Json.Str(d,"postId"),assetId=Json.Str(d,"assetId"),userId=Json.Str(d,"userId"),path=Json.Str(d,"path"),sha256=Json.Str(d,"sha256"),bytes=Convert.ToInt64(Json.Get(d,"bytes")??0) }; }
    HistoryEntry Receipt(string file,Dictionary<string,object> d) {
        string name=Json.Str(d,"file");if(name=="" || name!=Path.GetFileName(name))throw new UserError("旧媒体记录文件名无效。");
        string dir=Path.GetDirectoryName(file),post=Json.Str(d,"postId");
        if(Path.GetFileName(dir)=="_记录") {
            dir=Path.GetDirectoryName(dir);
            if(post=="") { var m=Regex.Match(Path.GetFileNameWithoutExtension(name),@"_([0-9]{1,25})_[0-9]{3,}$");if(m.Success)post=m.Groups[1].Value; }
        }else if(post=="")post=Path.GetFileName(dir);
        var e=Entry(d);e.postId=post;e.path=Relative(Path.Combine(dir,name));return e;
    }
    public void Load(CancellationToken ct) {
        readOnly=false;
        entries.Clear();files.Clear();
        using(var db=Open()) {
            db.Initialize();
            foreach(var move in db.Query("SELECT old_path,new_path FROM folder_moves WHERE state='pending'"))FinishFolderMove(db,move[0],move[1]);
            foreach(string file in Walk(root,ct)) {
                files.Add(file);
                if(!file.EndsWith(".json",StringComparison.OrdinalIgnoreCase))continue;
                string name=Path.GetFileName(file),parent=Path.GetFileName(Path.GetDirectoryName(file));
                bool known=file.Equals(index,StringComparison.OrdinalIgnoreCase) || parent=="_记录" || Regex.IsMatch(parent,@"^[0-9]{1,25}$") || name.StartsWith("运行记录_");
                if(!known)continue;
                string rel=Relative(file),hash=Model.Hash(file);
                var old=db.Query("SELECT sha256 FROM legacy WHERE path=?",rel);if(old.Count>0 && old[0][0]==hash)continue;
                string content=File.ReadAllText(file);Dictionary<string,object> d;
                try { d=Json.Read(content); }catch { throw new UserError("旧 JSON 无法解析，未跳过去重依据："+rel+"。请保留该文件用于排查。"); }
                bool receipt=Json.Str(d,"assetId")!="";
                bool isIndex=file.Equals(index,StringComparison.OrdinalIgnoreCase);
                bool isPost=name.EndsWith("_帖子信息.json") || name=="帖子信息.json" || name=="post.json";
                bool isRun=name.StartsWith("运行记录_") || name=="任务.json";
                if(!receipt && !isIndex && !isPost && !isRun)continue;
                db.Transaction(()=> {
                    if(isIndex)foreach(object obj in Json.Arr(Json.Get(d,"entries"))) { ct.ThrowIfCancellationRequested();Put(db,Entry(Json.Map(obj)),"complete"); }
                    string taskPath=Relative(Path.GetDirectoryName(file)).TrimEnd('\\');
                    if(parent=="_记录")taskPath=Relative(Path.GetDirectoryName(Path.GetDirectoryName(file))).TrimEnd('\\');
                    string legacyTask="legacy:"+taskPath;
                    db.Execute("INSERT OR IGNORE INTO tasks VALUES(?,?,?,'legacy',?)",legacyTask,taskPath,"",Json.Write(new { imported=true }));
                    if(receipt) { var e=Receipt(file,d);Put(db,e,"complete");db.Execute("INSERT OR REPLACE INTO task_media VALUES(?,?,?,?,?)",legacyTask,e.postId,e.assetId,"imported",e.path); }
                    if(isPost && Json.Str(d,"postId")!="") {
                        db.Execute("INSERT OR IGNORE INTO posts VALUES(?,?,?,?,?)",Json.Str(d,"postId"),Json.Str(d,"userId"),Json.Str(d,"user"),Json.Str(d,"date"),content);
                        db.Execute("INSERT OR REPLACE INTO task_posts VALUES(?,?,?)",legacyTask,Json.Str(d,"postId"),content);
                    }
                    if(isRun && name!="任务.json")db.Execute("INSERT OR REPLACE INTO tasks VALUES(?,?,?,?,?)","legacy-run:"+rel,taskPath,Json.Str(d,"time"),"legacy",content);
                    db.Execute("INSERT OR REPLACE INTO legacy VALUES(?,?,?)",rel,hash,content);
                    ct.ThrowIfCancellationRequested();
                });
            }
            foreach(var row in db.Query("SELECT post_id,asset_id,user_id,path,bytes,sha256 FROM media")) {
                var e=new HistoryEntry { postId=row[0],assetId=row[1],userId=row[2],path=row[3],bytes=Int64.Parse(row[4]),sha256=row[5] };if(!Accept(e))throw new UserError("数据库中的媒体记录无效，已停止。");Cache(e);
            }
            // A crash may leave a pending row on either side of the atomic file rename.
            // Find always verifies the actual bytes; absent/partial data can never count as complete.
            db.Execute("UPDATE tasks SET state='interrupted' WHERE state='running'");
        }
    }
    public void LoadPreview(CancellationToken ct) {
        readOnly=true;entries.Clear();files.Clear();ct.ThrowIfCancellationRequested();
        if(!Directory.Exists(root))return;
        // No migration, recovery, task-state changes or database creation during preview.
        foreach(string file in Walk(root,ct)) {
            files.Add(file);
            if(!file.EndsWith(".json",StringComparison.OrdinalIgnoreCase))continue;
            string parent=Path.GetFileName(Path.GetDirectoryName(file));
            bool isIndex=file.Equals(index,StringComparison.OrdinalIgnoreCase);
            if(!isIndex && parent!="_记录" && !Regex.IsMatch(parent,@"^[0-9]{1,25}$"))continue;
            Dictionary<string,object> data;
            try { data=Json.Read(File.ReadAllText(file)); }catch { throw new UserError("旧 JSON 无法解析，预览查重已停止；请保留记录用于排查。"); }
            var candidates=new List<HistoryEntry>();
            if(isIndex)candidates.AddRange(Json.Arr(Json.Get(data,"entries")).Select(x=>Entry(Json.Map(x))));
            else if(Json.Str(data,"assetId")!="")candidates.Add(Receipt(file,data));
            foreach(var entry in candidates) { ct.ThrowIfCancellationRequested();if(!Accept(entry))throw new UserError("旧媒体记录无效，预览查重已停止。");Cache(entry); }
        }
        if(!File.Exists(database))return;
        using(var db=new RecordDb(database,true)) {
            string version=db.Query("PRAGMA user_version")[0][0];
            if(version!="1" && version!="2")throw new UserError("下载记录版本不支持只读预览，请使用配套版本程序。");
            foreach(var row in db.Query("SELECT post_id,asset_id,user_id,path,bytes,sha256 FROM media")) {
                ct.ThrowIfCancellationRequested();
                var e=new HistoryEntry { postId=row[0],assetId=row[1],userId=row[2],path=row[3],bytes=Int64.Parse(row[4]),sha256=row[5] };
                if(!Accept(e))throw new UserError("数据库中的媒体记录无效，预览已停止。");Cache(e);
            }
        }
    }
    bool Valid(HistoryEntry e,string path,CancellationToken ct) {
        ct.ThrowIfCancellationRequested();if(path==null || !File.Exists(path))return false;
        for(string p=path;p!=null && p.TrimEnd('\\').Length>=root.TrimEnd('\\').Length;p=Path.GetDirectoryName(p))if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)return false;
        bool valid=new FileInfo(path).Length==e.bytes && Model.Hash(path).Equals(e.sha256,StringComparison.OrdinalIgnoreCase);ct.ThrowIfCancellationRequested();return valid;
    }
    public bool Find(Post p,Asset a,CancellationToken ct) {
        LastRepair=false;FoundPath=null;
        var matching=entries.Where(x=>x.postId==p.Id && x.assetId==a.Id && (String.IsNullOrEmpty(x.userId) || String.IsNullOrEmpty(p.UserId) || x.userId==p.UserId)).ToList();
        foreach(var e in matching)if(Valid(e,Resolve(e.path),ct)) { FoundPath=e.path;if(!readOnly)using(var db=Open())Put(db,e,"complete");return true; }
        foreach(var e in matching)foreach(string candidate in files) {
            if(!new[]{".mp4",".png",".jpg",".gif",".webp"}.Contains(Path.GetExtension(candidate).ToLowerInvariant()))continue;
            if(Valid(e,candidate,ct)) { string previous=e.path;e.path=Relative(candidate);if(!readOnly)using(var db=Open())db.Transaction(()=> { db.Execute("DELETE FROM media WHERE post_id=? AND asset_id=? AND path=?",e.postId,e.assetId,previous);Put(db,e,"complete"); });FoundPath=e.path;return true; }
        }
        LastRepair=matching.Count>0;return false;
    }
    public void Record(Post p,Asset a,string folder) {
        var d=Json.Read(File.ReadAllText(Path.Combine(folder,"_记录",Downloader.Stem(p,a)+".json")));
        Persist(p,a,Path.Combine(folder,Json.Str(d,"file")),Convert.ToInt64(Json.Get(d,"bytes")),Json.Str(d,"sha256"),"complete");
    }
    public void Persist(Post p,Asset a,string path,long bytes,string hash,string state) {
        var e=new HistoryEntry { postId=p.Id,assetId=a.Id,userId=p.UserId,path=Relative(path),bytes=bytes,sha256=hash };
        using(var db=Open())db.Transaction(()=> { Put(db,e,state);if(taskId!="")db.Execute("INSERT OR REPLACE INTO task_media VALUES(?,?,?,?,?)",taskId,p.Id,a.Id,state,e.path); });
        Cache(e);FoundPath=e.path;
    }
    public void BeginTask(string folder,object settings) {
        taskId=Guid.NewGuid().ToString("N");using(var db=Open())db.Execute("INSERT INTO tasks VALUES(?,?,?,'running',?)",taskId,Relative(folder),DateTimeOffset.Now.ToString("o"),Json.Write(settings));
    }
    public string AccountFolder(string user,string id,string nickname) {
        string wanted=Path.Combine(root,TaskArchive.AccountName(root,user,id,nickname));
        using(var db=Open()) {
            db.Initialize();
            var rows=db.Query("SELECT path FROM account_folders WHERE user_id=?",id);
            string current=rows.Count==1?Resolve(rows[0][0]):null;
            if(rows.Count==1 && (current==null || Path.GetDirectoryName(current).TrimEnd('\\')!=root.TrimEnd('\\')))throw new UserError("账号归档路径无效，请保留数据库用于排查。");
            if(current==null) {
                var legacy=Directory.GetDirectories(root).Where(p=>Regex.IsMatch(Path.GetFileName(p),@"^@[A-Za-z0-9_]{1,15}_"+Regex.Escape(id)+"$",RegexOptions.IgnoreCase)).ToArray();
                if(legacy.Length>1)throw new UserError("同一用户 ID 对应多个旧账号目录，请先整理后重试。");
                if(legacy.Length==1)current=legacy[0];
            }
            if(current!=null && (Regex.IsMatch(Path.GetFileName(current),@"^@[A-Za-z0-9_]{1,15}_"+Regex.Escape(id)+"$",RegexOptions.IgnoreCase) || Path.GetFileName(current).StartsWith("未取得昵称（@",StringComparison.Ordinal) && !String.IsNullOrWhiteSpace(nickname)) && !current.Equals(wanted,StringComparison.OrdinalIgnoreCase)) {
                string old=Relative(current),next=Relative(wanted);
                TaskArchive.CheckPath(root,current);TaskArchive.CheckPath(root,wanted);
                if(Directory.Exists(wanted) || File.Exists(wanted))throw new UserError("昵称目录已存在，未合并或覆盖原目录。请检查同名目录。");
                db.Transaction(()=> { db.Execute("INSERT OR REPLACE INTO account_folders VALUES(?,?)",id,old);db.Execute("INSERT OR REPLACE INTO folder_moves VALUES(?,?,'pending')",old,next); });
                FinishFolderMove(db,old,next);current=wanted;
                foreach(var e in entries)if(e.path.StartsWith(old+"\\",StringComparison.OrdinalIgnoreCase))e.path=next+e.path.Substring(old.Length);
                for(int i=0;i<files.Count;i++)if(files[i].StartsWith(Path.Combine(root,old)+"\\",StringComparison.OrdinalIgnoreCase))files[i]=Path.Combine(root,next)+files[i].Substring(Path.Combine(root,old).Length);
            }
            if(current==null) {
                current=wanted;
                if(Directory.Exists(current) || File.Exists(current))throw new UserError("目标昵称目录已存在但没有对应账号记录，未自动混入文件。请检查目录。");
                db.Execute("INSERT INTO account_folders VALUES(?,?)",id,Relative(current));
            }
            TaskArchive.CheckPath(root,current);Directory.CreateDirectory(current);
            db.Execute("INSERT OR REPLACE INTO account_folders VALUES(?,?)",id,Relative(current));return current;
        }
    }
    void FinishFolderMove(RecordDb db,string old,string next) {
        string source=Resolve(old),target=Resolve(next);
        if(source==null || target==null || Path.GetDirectoryName(source).TrimEnd('\\')!=root.TrimEnd('\\') || Path.GetDirectoryName(target).TrimEnd('\\')!=root.TrimEnd('\\'))throw new UserError("待恢复的账号目录迁移路径无效。");
        TaskArchive.CheckPath(root,source);TaskArchive.CheckPath(root,target);
        if(Directory.Exists(source) && !Directory.Exists(target) && !File.Exists(target))Directory.Move(source,target);
        else if(Directory.Exists(source) || !Directory.Exists(target))throw new UserError("账号目录迁移未完成，请保留原目录和数据库用于排查。");
        string before=old+"\\",after=next+"\\";
        db.Transaction(()=> {
            foreach(string table in new[]{"media","tasks","task_media","legacy"}) {
                db.Execute("UPDATE "+table+" SET path=? || substr(path,length(?)+1) WHERE substr(path,1,length(?))=? COLLATE NOCASE",after,before,before,before);
                db.Execute("UPDATE "+table+" SET path=? WHERE path=? COLLATE NOCASE",next,old);
            }
            db.Execute("UPDATE account_folders SET path=? WHERE path=? COLLATE NOCASE",next,old);
            foreach(var task in db.Query("SELECT id,summary FROM tasks WHERE substr(path,1,length(?))=? COLLATE NOCASE",after,after)) {
                var data=Json.Read(task[1]);string folder=Json.Str(data,"taskFolder");
                if(folder.StartsWith(source+"\\",StringComparison.OrdinalIgnoreCase)) { data["taskFolder"]=target+folder.Substring(source.Length);db.Execute("UPDATE tasks SET summary=? WHERE id=?",Json.Write(data),task[0]); }
            }
            db.Execute("UPDATE folder_moves SET state='complete' WHERE old_path=?",old);
        });
    }
    public void PostInfo(Post p,object info) {
        string text=Json.Write(info);using(var db=Open())db.Transaction(()=> { db.Execute("INSERT OR REPLACE INTO posts VALUES(?,?,?,?,?)",p.Id,p.UserId,p.User,p.Date,text);db.Execute("INSERT OR REPLACE INTO task_posts VALUES(?,?,?)",taskId,p.Id,text); });
    }
    public void Result(Post p,Asset a,string result) {
        using(var db=Open())db.Execute("INSERT OR REPLACE INTO task_media VALUES(?,?,?,?,?)",taskId,p.Id,a.Id,result,FoundPath);
    }
    public void EndTask(object summary,bool interrupted) {
        if(taskId=="")return;using(var db=Open())db.Execute("UPDATE tasks SET state=?,summary=? WHERE id=?",interrupted?"interrupted":"finished",Json.Write(summary),taskId);
    }
    public string ArchiveLegacy() {
        using(var db=Open()) {
            db.Initialize();var rows=db.Query("SELECT path,sha256,content FROM legacy");
            var eligible=new List<string[]>();
            foreach(var row in rows) {
                string path=Resolve(row[0]);if(path==null || !File.Exists(path))continue;
                for(string part=path;part!=null && part.Length>=root.TrimEnd('\\').Length;part=Path.GetDirectoryName(part))if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new UserError("旧记录包含链接，未归档。");
                if(!path.EndsWith(".json",StringComparison.OrdinalIgnoreCase) || Model.Hash(path)!=row[1] || File.ReadAllText(path)!=row[2])throw new UserError("旧记录在迁移后发生变化，请先运行一次任务重新导入后再归档。");
                eligible.Add(row);
            }
            if(eligible.Count==0)throw new UserError("没有可归档的已迁移旧 JSON。");
            string archive=Path.Combine(root,"旧JSON备份_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N").Substring(0,6)+".zip");
            using(var stream=new FileStream(archive,FileMode.CreateNew))using(var zip=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Create))
                foreach(var row in eligible)using(var output=zip.CreateEntry(row[0].Replace('\\','/')).Open())using(var input=File.OpenRead(Resolve(row[0])))input.CopyTo(output);
            using(var zip=System.IO.Compression.ZipFile.OpenRead(archive))foreach(var row in eligible) {
                var entry=zip.GetEntry(row[0].Replace('\\','/'));if(entry==null)throw new UserError("旧记录备份验证失败，原文件未清理。");
                using(var input=entry.Open())using(var sha=System.Security.Cryptography.SHA256.Create()) {
                    string hash=BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();if(hash!=row[1])throw new UserError("旧记录备份校验不符，原文件未清理。");
                }
            }
            // Diagnostic text is never selected. Keep a verified ZIP before any deletion.
            foreach(var row in eligible) { string path=Resolve(row[0]);if(File.Exists(path) && Model.Hash(path)==row[1])File.Delete(path); }
            return archive;
        }
    }
    public void Export(string folder,string destination) {
        if(!File.Exists(database))throw new UserError("当前保存根目录中没有下载记录数据库。");
        using(var db=Open()) {
            db.Initialize();string relative=Relative(folder).TrimEnd('\\');
            var tasks=db.Query("SELECT id,path,started,state,summary FROM tasks WHERE path=? COLLATE NOCASE",relative);
            if(tasks.Count==0)throw new UserError("该文件夹没有已入库任务；请先运行或选择保存根目录中的任务文件夹。");
            var exported=new List<object>();
            foreach(var t in tasks)exported.Add(new { id=t[0],folder=t[1],started=t[2],state=t[3],summary=Json.Read(t[4]),posts=db.Query("SELECT post_id,metadata FROM task_posts WHERE task_id=?",t[0]).Select(x=>new { postId=x[0],metadata=Json.Read(x[1]) }).ToArray(),media=db.Query("SELECT t.post_id,t.asset_id,t.result,t.path,COALESCE(m.bytes,0),COALESCE(m.sha256,'') FROM task_media t LEFT JOIN media m ON m.post_id=t.post_id AND m.asset_id=t.asset_id AND m.path=t.path WHERE t.task_id=?",t[0]).Select(x=>new { postId=x[0],assetId=x[1],result=x[2],path=x[3],bytes=x[4],sha256=x[5] }).ToArray() });
            Downloader.AtomicText(destination,Json.Write(new { version=1,tasks=exported }));
        }
    }
}
}
