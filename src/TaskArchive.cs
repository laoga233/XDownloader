using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace XDownloader {
// Display names are readable; the database binds account folders to stable X user IDs.
sealed class TaskArchive {
    public readonly string Root,Key,Started;
    public string Folder { get; private set; }
    public string User { get; private set; }
    public string UserId { get; private set; }
    public string DisplayName { get; private set; }
    public string Diagnostic { get { return Path.Combine(Folder,"_记录","运行记录_"+Key.Substring(3)+".诊断.txt"); } }
    public bool Finalized { get; private set; }
    public static string Full(string path) { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar; }
    public static bool Within(string root,string path) { return Full(path).StartsWith(Full(root),StringComparison.OrdinalIgnoreCase); }
    public static string SaveRoot(string selected) {
        if(String.IsNullOrWhiteSpace(selected) || !Path.IsPathRooted(selected))throw new UserError("请选择完整保存路径。");
        string full=Path.GetFullPath(selected.Trim());
        // Selecting an existing account/task directory must not create a second history database.
        for(string current=full;current!=null;current=Path.GetDirectoryName(current))
            if(File.Exists(Path.Combine(current,"_下载记录.sqlite")))return current;
        return full;
    }
    public static void CheckPath(string root,string path) {
        if(!Within(root,path))throw new UserError("任务路径超出保存根目录。");
        for(string current=Path.GetFullPath(path);current!=null && Within(root,current);current=Path.GetDirectoryName(current))
            if((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new UserError("任务路径包含目录链接，请选择普通文件夹。");
    }
    public TaskArchive(string root,string user) {
        Root=Path.GetFullPath(root);User=Target.Parse(user).User;UserId="";DisplayName="";
        if(Root.Length>100)throw new UserError("按用户归档需要较短路径，请将保存根目录缩短至 100 个字符以内。");
        Started=DateTimeOffset.Now.ToString("o");
        Key="任务_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_"+Guid.NewGuid().ToString("N").Substring(0,6);
        Folder=Path.Combine(Root,"@"+User+"_待确认",Key);
        CheckPath(Root,Folder);Directory.CreateDirectory(Path.Combine(Folder,"_记录"));
    }
    public static string AccountName(string root,string user,string id,string nickname) {
        string handle=Target.Parse(user).User;
        string suffix="（@"+handle+"）";
        string name=String.IsNullOrWhiteSpace(nickname)?"未取得昵称":nickname;
        name=Regex.Replace(name,@"[<>:""/\\|?*\x00-\x1F]","_").Trim().TrimEnd('.');
        if(name=="")name="未取得昵称";
        int max=Math.Min(32,138-Path.GetFullPath(root).TrimEnd('\\').Length-suffix.Length);
        if(max<1)throw new UserError("保存路径过长，无法容纳昵称目录，请缩短保存根目录。");
        if(name.Length>max) { name=name.Substring(0,max);if(Char.IsHighSurrogate(name[name.Length-1]))name=name.Substring(0,name.Length-1); }
        return name+suffix;
    }
    public void FinalizeAccount(string user,string id,string nickname="",History history=null) {
        string validId=Regex.IsMatch(id??"",@"^[0-9]{1,25}$")?id:"";
        if(Finalized) {
            if(UserId!="" && validId!="" && UserId!=validId)throw new StopQueue("本次响应的用户 ID 发生变化，已停止以避免混入其他账号。");
            if(String.IsNullOrWhiteSpace(DisplayName) && !String.IsNullOrWhiteSpace(nickname))DisplayName=nickname;
            return;
        }
        if(validId!="") {
            string handle=Target.Parse(user).User;
            string account=(history??new History(Root)).AccountFolder(handle,validId,nickname);
            string next=Path.Combine(account,Key);
            // Only this run's diagnostic directory is moved, before any media/database row is written.
            CheckPath(Root,Folder);CheckPath(Root,next);
            Directory.CreateDirectory(account);
            string oldAccount=Path.GetDirectoryName(Folder);
            Directory.Move(Folder,next);Folder=next;User=handle;UserId=validId;DisplayName=nickname??"";
            // Remove only an empty staging directory; never delete other tasks.
            try { Directory.Delete(oldAccount,false); }catch(IOException) {}catch(UnauthorizedAccessException) {}
        }
        Finalized=true;
    }
    public RecentDestination Recent() { return new RecentDestination { key=Key,root=Root,folder=Folder,user=User,userId=UserId,displayName=DisplayName,started=Started }; }
}
sealed class RecentDestination {
    public string key,root,folder,user,userId,displayName,started;
    public string AccountLabel { get { return (String.IsNullOrWhiteSpace(displayName)?"昵称未取得":displayName)+"（@"+user+"）"; } }
    public override string ToString() {
        DateTimeOffset time;string label=DateTimeOffset.TryParse(started,out time)?time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"):started;
        return label+"  ·  "+AccountLabel+"  ·  "+root;
    }
}
sealed class RecentDestinations {
    readonly string file;
    public List<RecentDestination> Items { get; private set; }
    public string Warning { get; private set; }
    public RecentDestinations(string path) { file=path;Items=new List<RecentDestination>(); }
    public void Load() {
        Items.Clear();Warning=null;if(!File.Exists(file))return;
        try {
            foreach(var item in Json.Arr(Json.Get(Json.Read(File.ReadAllText(file)),"items"))) {
                var d=Json.Map(item);var e=new RecentDestination { key=Json.Str(d,"key"),root=Json.Str(d,"root"),folder=Json.Str(d,"folder"),user=Json.Str(d,"user"),userId=Json.Str(d,"userId"),displayName=Json.Str(d,"displayName"),started=Json.Str(d,"started") };
                DateTimeOffset started;
                if(!Path.IsPathRooted(e.root) || !Path.IsPathRooted(e.folder) || !TaskArchive.Within(e.root,e.folder) || Path.GetFileName(e.folder.TrimEnd('\\'))!=e.key || !Regex.IsMatch(e.key,@"^任务_[0-9_]+[a-f0-9]{6}$") || !Regex.IsMatch(e.user,@"^[A-Za-z0-9_]{1,15}$") || !Regex.IsMatch(e.userId,@"^(?:[0-9]{1,25})?$") || !DateTimeOffset.TryParse(e.started,out started)) { Warning="部分最近目的地记录无效，已忽略。";continue; }
                RepairPath(e);FillNickname(e);if(!Items.Any(x=>Same(x,e)))Items.Add(e);
                if(Items.Count==5)break;
            }
            FillFromPeers(Items);
        }catch { Items.Clear();Warning="最近目的地记录无法读取；当前保存设置仍可使用。"; }
    }
    static bool Same(RecentDestination a,RecentDestination b) { return a.key==b.key && TaskArchive.Full(a.root).Equals(TaskArchive.Full(b.root),StringComparison.OrdinalIgnoreCase); }
    static void FillNickname(RecentDestination entry) {
        if(!String.IsNullOrWhiteSpace(entry.displayName))return;
        try { TaskArchive.CheckPath(entry.root,entry.folder);entry.displayName=new History(entry.root).RecordedNickname(entry.folder,entry.userId); }catch { /* Unavailable history never blocks the recent list. */ }
        if(!String.IsNullOrWhiteSpace(entry.displayName) || !Directory.Exists(entry.folder))return;
        string parent=Path.GetFileName(Path.GetDirectoryName(entry.folder));
        var match=Regex.Match(parent,@"^(?<name>.+)（@(?<user>[A-Za-z0-9_]{1,15})）$");
        if(match.Success && match.Groups["user"].Value.Equals(entry.user,StringComparison.OrdinalIgnoreCase) && match.Groups["name"].Value!="未取得昵称")entry.displayName=match.Groups["name"].Value;
    }
    static void FillFromPeers(IEnumerable<RecentDestination> entries) {
        var list=entries.ToList();
        foreach(var entry in list.Where(x=>String.IsNullOrWhiteSpace(x.displayName) && !String.IsNullOrEmpty(x.userId))) {
            var known=list.FirstOrDefault(x=>x.userId==entry.userId && !String.IsNullOrWhiteSpace(x.displayName));
            if(known!=null)entry.displayName=known.displayName;
        }
    }
    static void RepairPath(RecentDestination entry) {
        if(Directory.Exists(entry.folder) || !Directory.Exists(entry.root))return;
        try {
            TaskArchive.CheckPath(entry.root,entry.root);
            var matches=Directory.GetDirectories(entry.root).Where(p=>(File.GetAttributes(p)&FileAttributes.ReparsePoint)==0).Select(p=>Path.Combine(p,entry.key)).Where(Directory.Exists).ToArray();
            if(matches.Length==1) { TaskArchive.CheckPath(entry.root,matches[0]);entry.folder=matches[0]; }
        }catch(IOException) {}catch(UnauthorizedAccessException) {}catch(UserError) {}
    }
    void Save(List<RecentDestination> next) {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        Downloader.AtomicText(file,Json.Write(new { version=1,items=next.ToArray() }));Items=next;
    }
    public void Remember(RecentDestination entry) { var next=new[]{entry}.Concat(Items.Where(x=>!Same(x,entry))).Take(5).ToList();FillFromPeers(next);foreach(var old in next) { RepairPath(old);FillNickname(old); }FillFromPeers(next);Save(next); }
    public void Remove(RecentDestination entry) { Save(Items.Where(x=>!Same(x,entry)).ToList()); }
}
sealed class RecentDestinationsWindow : Form {
    public RecentDestination Selected { get; private set; }
    public RecentDestinationsWindow(RecentDestinations recent) {
        Text="最近目的地 · 最近 5 次任务";ClientSize=new Size(760,390);MinimumSize=new Size(650,360);StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;MinimizeBox=false;
        Font=new Font("Microsoft YaHei UI",10);BackColor=Color.FromArgb(245,247,251);
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(16),RowCount=4,ColumnCount=1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,32));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,110));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));Controls.Add(layout);
        layout.Controls.Add(new Label { Text="复用保存根目录；下载用户与筛选条件保持当前设置。",Dock=DockStyle.Fill,AutoSize=true },0,0);
        var list=new ListBox { Dock=DockStyle.Fill,HorizontalScrollbar=true,AccessibleName="最近五次任务目的地" };layout.Controls.Add(list,0,1);
        var details=new TextBox { Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.White,AccessibleName="所选任务的完整保存路径" };layout.Controls.Add(details,0,2);
        var actions=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false };layout.Controls.Add(actions,0,3);
        var use=new Button { Text="使用保存根目录",Width=160,Height=34 };var open=new Button { Text="打开任务文件夹",Width=160,Height=34 };var remove=new Button { Text="从列表移除",Width=125,Height=34 };var close=new Button { Text="关闭",Width=90,Height=34,DialogResult=DialogResult.Cancel };actions.Controls.AddRange(new Control[]{use,open,remove,close});CancelButton=close;
        Action refresh=()=> { list.Items.Clear();foreach(var entry in recent.Items)list.Items.Add(entry);if(list.Items.Count>0)list.SelectedIndex=0;else { details.Text="暂无最近任务。完成或停止一次任务后会自动记录。";use.Enabled=open.Enabled=remove.Enabled=false; } };
        list.SelectedIndexChanged+=delegate {
            var entry=list.SelectedItem as RecentDestination;use.Enabled=open.Enabled=remove.Enabled=entry!=null;if(entry==null)return;
            details.Text="账号："+entry.AccountLabel+Environment.NewLine+"保存根目录："+entry.root+Environment.NewLine+"任务文件夹："+entry.folder+Environment.NewLine+(Directory.Exists(entry.folder)?"任务文件夹可用":"任务文件夹不存在或不可访问，可从列表移除。 ");
        };
        use.Click+=delegate { var entry=list.SelectedItem as RecentDestination;if(entry==null)return;if(!Directory.Exists(entry.root)) { MessageBox.Show(this,"保存根目录不存在或不可访问，请重新选择实际位置。","最近目的地");return; }try { TaskArchive.CheckPath(entry.root,entry.root);Selected=entry;DialogResult=DialogResult.OK;Close(); }catch(Exception ex) { MessageBox.Show(this,ex.Message,"最近目的地"); } };
        open.Click+=delegate { var entry=list.SelectedItem as RecentDestination;if(entry==null)return;try { if(!Directory.Exists(entry.folder))throw new UserError("任务文件夹不存在或不可访问。");TaskArchive.CheckPath(entry.root,entry.folder);Process.Start(new ProcessStartInfo(entry.folder) { UseShellExecute=true }); }catch { MessageBox.Show(this,"无法打开任务文件夹，路径可能已移动、删除或不可访问。","最近目的地"); } };
        remove.Click+=delegate { var entry=list.SelectedItem as RecentDestination;if(entry==null)return;try { recent.Remove(entry);refresh(); }catch { MessageBox.Show(this,"无法保存最近目的地列表，请检查本机设置目录权限。","最近目的地"); } };
        refresh();
    }
}
}
