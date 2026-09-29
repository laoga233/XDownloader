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

[assembly: System.Reflection.AssemblyTitle("X/推特图片视频下载器")]
[assembly: System.Reflection.AssemblyProduct("X/推特图片视频下载器")]
[assembly: System.Reflection.AssemblyCompany("By Codex&上邪上")]
[assembly: System.Reflection.AssemblyVersion("0.12.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.12.0.0")]

namespace XDownloader {
class MainWindow : Form {
    TextBox urls=new TextBox(), destination=new TextBox(), logs=new TextBox();
    Button login=new Button(), start=new Button(), pause=new Button(), cancel=new Button(), folder=new Button();
    Label status=new Label(); ProgressBar progress=new ProgressBar();
    NumericUpDown limit=new NumericUpDown(); CheckBox images=new CheckBox(), videos=new CheckBox(), replies=new CheckBox();
    CancellationTokenSource cancellation; bool paused, closing, loginBusy;
    readonly Browser browser;
    readonly string data;
    readonly StringBuilder runLog=new StringBuilder();
    string lastTaskFolder,activeDiagnostic;
    DateTimePicker fromDate=new DateTimePicker(),toDate=new DateTimePicker(); CheckBox forceDownload=new CheckBox(),strictScan=new CheckBox();
    public MainWindow(bool test) {
        data=test ? Path.Combine(Path.GetTempPath(),"XDownloader-UI") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XDownloader");
        browser=new Browser(Path.Combine(data,"ChromeProfile"));
        Text="X/推特图片视频下载器 · v0.12 · By Codex&上邪上"; Font=new Font("Microsoft YaHei UI",10); BackColor=Color.FromArgb(245,247,251); ForeColor=Color.FromArgb(30,42,58);
        Icon=System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize=new Size(960,840); MinimumSize=new Size(940,810); StartPosition=FormStartPosition.CenterScreen; AutoScaleMode=AutoScaleMode.Dpi;
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(26), ColumnCount=1, RowCount=12 };
        float[] heights={48,35,46,28,105,28,42,48,30,46,46,100};
        for(int i=0;i<heights.Length;i++) layout.RowStyles.Add(new RowStyle(i==11?SizeType.Percent:SizeType.Absolute,heights[i]));
        Controls.Add(layout);
        var heading=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false };
        heading.Controls.Add(new Label { Text="X/推特图片视频下载器",Font=new Font(Font.FontFamily,21,FontStyle.Bold),AutoSize=true,UseMnemonic=false });
        heading.Controls.Add(new Label { Text="v0.12  ·  By Codex&上邪上",Font=new Font(Font.FontFamily,11),AutoSize=true,UseMnemonic=false,Margin=new Padding(12,14,0,0),ForeColor=Color.DimGray });
        var headingRow=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=1 };
        headingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));headingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,94));
        headingRow.Controls.Add(heading,0,0);
        var changelog=new Button();Style(changelog,"更新日志",86);changelog.AccessibleName="查看最近六个版本的更新日志";
        changelog.Click+=delegate {
            using(var dialog=new Form { Text="更新日志 · v0.12 至 v0.7",StartPosition=FormStartPosition.CenterParent,ClientSize=new Size(700,570),MinimizeBox=false,MaximizeBox=false,ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.FixedDialog,BackColor=BackColor,ForeColor=ForeColor }) {
                var content=new TextBox { Multiline=true,ReadOnly=true,WordWrap=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BorderStyle=BorderStyle.None,BackColor=Color.White,ForeColor=Color.FromArgb(35,45,60),Font=new Font("Microsoft YaHei UI",11),Text=Changelog.Content,Padding=new Padding(8),AccessibleName="v0.12 至 v0.7 更新内容" };
                dialog.Controls.Add(content);dialog.ShowDialog(this);
            }
        };
        var changelogSlot=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Padding=new Padding(0,3,0,0) };
        changelogSlot.Controls.Add(changelog);headingRow.Controls.Add(changelogSlot,1,0);layout.Controls.Add(headingRow,0,0);
        layout.Controls.Add(new Label { Text="保存指定用户公开帖子的原图、视频和 GIF 动画（MP4）。",AutoSize=true, Dock=DockStyle.Fill, ForeColor=Color.DimGray },0,1);
        var loginRow=new FlowLayoutPanel { Dock=DockStyle.Fill };
        Style(login,"1  打开登录窗口",180); loginRow.Controls.Add(login);
        loginRow.Controls.Add(new Label { Text="在专用 Chrome 中登录，并保持窗口打开",AutoSize=true,Margin=new Padding(12,10,0,0) }); layout.Controls.Add(loginRow,0,2);
        layout.Controls.Add(new Label { Text="2  @用户名 / 用户主页 / 单条帖子链接（仅限输入一位用户）",Dock=DockStyle.Fill },0,3);
        urls.Multiline=true; urls.ScrollBars=ScrollBars.Vertical; urls.Dock=DockStyle.Fill; urls.BorderStyle=BorderStyle.FixedSingle;
        urls.AccessibleName="用户或帖子链接"; layout.Controls.Add(urls,0,4);
        layout.Controls.Add(new Label { Text="3  保存位置",Dock=DockStyle.Fill, Padding=new Padding(0,5,0,0) },0,5);
        var pathRow=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2 }; pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));
        destination.Dock=DockStyle.Fill; destination.AccessibleName="保存位置"; destination.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),"X Downloads");
        if(!test && File.Exists(Path.Combine(data,"settings.json"))) try { var s=Json.Read(File.ReadAllText(Path.Combine(data,"settings.json"))); if(Path.IsPathRooted(Json.Str(s,"folder"))) destination.Text=Json.Str(s,"folder"); } catch {}
        Style(folder,"浏览…",90); pathRow.Controls.Add(destination,0,0);pathRow.Controls.Add(folder,1,0);layout.Controls.Add(pathRow,0,6);
        var actions=new FlowLayoutPanel { Dock=DockStyle.Fill };
        Style(start,"开始 / 重试",160);start.BackColor=Color.FromArgb(31,99,221);start.ForeColor=Color.White;
        Style(pause,"暂停",90);Style(cancel,"取消",90);pause.Enabled=cancel.Enabled=false;
        actions.Controls.AddRange(new Control[]{start,pause,cancel});
        var open=new Button();Style(open,"打开保存目录",150);actions.Controls.Add(open);layout.Controls.Add(actions,0,7);
        var export=new Button();Style(export,"导出任务记录",140);actions.Controls.Add(export);
        export.Click+=delegate {
            if(cancellation!=null) { Log("请在任务停止后导出记录。");return; }
            try {
                string selected;
                using(var choose=new FolderBrowserDialog { Description="选择要导出的任务文件夹",SelectedPath=lastTaskFolder??destination.Text }) { if(choose.ShowDialog(this)!=DialogResult.OK)return;selected=choose.SelectedPath; }
                using(var save=new SaveFileDialog { Title="按需导出任务与帖子记录",Filter="JSON 文件|*.json",FileName=Path.GetFileName(selected)+"_导出.json" }) {
                    if(save.ShowDialog(this)!=DialogResult.OK)return;
                    new History(destination.Text).Export(selected,save.FileName);Log("任务记录已导出。");
                }
            }catch(Exception ex) { Error(ex); }
        };
        var archive=new Button();Style(archive,"归档旧 JSON",140);actions.Controls.Add(archive);
        archive.Click+=async delegate {
            if(cancellation!=null || loginBusy) { Log("请在任务停止后归档旧记录。");return; }
            string selectedRoot=destination.Text;
            if(!File.Exists(Path.Combine(selectedRoot,"_下载记录.sqlite"))) { Log("请先运行一次任务，完成旧记录导入。");return; }
            if(MessageBox.Show(this,"将当前保存根目录中已迁移的旧 JSON 打包为 ZIP，并校验备份后清理对应原 JSON。媒体、数据库和诊断日志均保留。是否继续？","归档旧记录",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            loginBusy=true;start.Enabled=login.Enabled=folder.Enabled=archive.Enabled=export.Enabled=false;
            try { string path=await Task.Run(()=>new History(selectedRoot).ArchiveLegacy());Log("旧 JSON 已归档："+path); }
            catch(Exception ex) { Error(ex); }
            finally { loginBusy=false;start.Enabled=login.Enabled=folder.Enabled=archive.Enabled=export.Enabled=true; }
        };
        var stateRow=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2 };stateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,68));stateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,32));
        status.Text="准备就绪 · 仅公开帖子 · 自动跳过重复文件";status.Dock=DockStyle.Fill;status.AutoEllipsis=true;progress.Dock=DockStyle.Fill;stateRow.Controls.Add(status,0,0);stateRow.Controls.Add(progress,1,0);layout.Controls.Add(stateRow,0,8);
        logs.Multiline=true;logs.ReadOnly=true;logs.ScrollBars=ScrollBars.Vertical;logs.Dock=DockStyle.Fill;logs.BackColor=Color.White;logs.BorderStyle=BorderStyle.FixedSingle;layout.Controls.Add(logs,0,11);
        var options=new FlowLayoutPanel { Dock=DockStyle.Fill }; images.Text="图片";images.Checked=true;images.Width=75;videos.Text="视频 / GIF";videos.Checked=true;videos.Width=120;replies.Text="包含回复媒体";replies.Checked=true;replies.Width=145;limit.Minimum=0;limit.Maximum=1000000;limit.Value=0;limit.Width=95;options.Controls.AddRange(new Control[]{images,videos,replies,new Label { Text="匹配帖数（0 不限）",AutoSize=true,Margin=new Padding(8,5,0,0) },limit});strictScan.Text="完整扫描（不提前停止）";strictScan.Width=215;options.Controls.Add(strictScan);layout.Controls.Add(options,0,9);
        var dates=new FlowLayoutPanel { Dock=DockStyle.Fill };
        foreach(var picker in new[]{fromDate,toDate}) { picker.Format=DateTimePickerFormat.Custom;picker.CustomFormat="yyyy-MM-dd";picker.ShowCheckBox=true;picker.Width=160;picker.Checked=false; }
        dates.Controls.Add(new Label { Text="发布日期（UTC）",AutoSize=true,Margin=new Padding(0,6,8,0) });dates.Controls.Add(fromDate);dates.Controls.Add(new Label { Text="至",AutoSize=true,Margin=new Padding(4,6,4,0) });dates.Controls.Add(toDate);
        forceDownload.Text="重新下载（忽略历史）";forceDownload.Width=220;dates.Controls.Add(forceDownload);layout.Controls.Add(dates,0,10);
        fromDate.AccessibleName="开始日期 UTC，不勾选为不限";toDate.AccessibleName="结束日期 UTC，不勾选为不限";
        if(!test && File.Exists(Path.Combine(data,"settings.json")))try { var settings=Json.Read(File.ReadAllText(Path.Combine(data,"settings.json")));DateTime day;if(DateTime.TryParseExact(Json.Str(settings,"from"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out day)) { fromDate.Value=day;fromDate.Checked=true; }if(DateTime.TryParseExact(Json.Str(settings,"to"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out day)) { toDate.Value=day;toDate.Checked=true; } }catch {}
        Log("默认跨任务校验去重；勾选日期可限定范围（UTC，包含结束日）。日期和媒体类型均匹配的帖子计入上限。");
        login.Click+=async delegate { loginBusy=true;login.Enabled=start.Enabled=false; try { await browser.Open(CancellationToken.None);Log("已连接 Chrome（尚未验证 X 登录）。请在浏览器完成登录，再点击「开始 / 重试」。"); } catch(Exception ex) { Error(ex); } finally { loginBusy=false;if(!IsDisposed) login.Enabled=start.Enabled=true; } };
        folder.Click+=delegate { using(var dialog=new FolderBrowserDialog { Description="选择媒体保存目录",SelectedPath=destination.Text }) if(dialog.ShowDialog(this)==DialogResult.OK) destination.Text=dialog.SelectedPath; };
        open.Click+=delegate { try { if(Directory.Exists(lastTaskFolder??destination.Text)) Process.Start(new ProcessStartInfo(lastTaskFolder??destination.Text) { UseShellExecute=true });else Log("保存目录尚未创建。"); } catch { Log("无法打开保存目录。"); } };
        start.Click+=async delegate { await Run(); };
        pause.Click+=delegate { paused=!paused;pause.Text=paused?"继续":"暂停";status.Text=paused?"已暂停 · 点击继续恢复":"下载中…"; };
        cancel.Click+=delegate { if(cancellation!=null) cancellation.Cancel(); };
        FormClosing+=delegate(object sender,FormClosingEventArgs e) { if(loginBusy) { e.Cancel=true;return; } if(cancellation!=null) { e.Cancel=true; closing=true;cancellation.Cancel(); } else browser.Dispose(); };
    }
    void Style(Button b,string text,int width) { b.Text=text;b.Width=width;b.Height=35;b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=Color.FromArgb(210,217,228);b.BackColor=Color.White;b.Cursor=Cursors.Hand; }
    void Log(string text) {
        string line=DateTime.Now.ToString("HH:mm:ss")+"  "+text+Environment.NewLine;
        if(!text.StartsWith("接口诊断：") && !text.StartsWith("结构诊断：") && !text.StartsWith("解析诊断：") && !text.StartsWith("帖子 ") && !text.StartsWith("诊断日志已"))logs.AppendText(line);
        if(cancellation!=null) {
            runLog.Append(line);
            // Persist during the run too; a process crash must not discard all diagnostics.
            if(activeDiagnostic!=null)try { File.AppendAllText(activeDiagnostic,line,new UTF8Encoding(false)); }catch(IOException) {}catch(UnauthorizedAccessException) {}
        }
    }
    void Error(Exception ex) {
        if(ex is UserError) Log(ex.Message);
        else if(ex is OperationCanceledException) Log("操作已取消或连接超时；已完成文件会保留。");
        else if(ex is HttpRequestException) Log("网络连接失败。请检查网络或系统代理，再重试。");
        else if(ex is UnauthorizedAccessException) Log("无法写入保存目录，请选择可写入的文件夹。");
        else if(ex is IOException) Log("文件操作失败，请检查磁盘空间、路径长度或文件占用。");
        else Log("操作失败（"+ex.GetType().Name+"）。请重试；日志不会输出登录凭证。");
    }
    async Task Wait(CancellationToken ct) { while(paused) await Task.Delay(200,ct); ct.ThrowIfCancellationRequested(); }
    async Task Run() {
        var targets=new List<Target>();string root;DateRange range;MediaSelection mediaSelection;
        try {
            if(urls.Lines.Count(x=>!String.IsNullOrWhiteSpace(x))>1)throw new UserError("仅限输入一位用户：请只保留一个用户名、用户主页或单条帖子链接。");
            foreach(string line in urls.Lines.Where(x=>!String.IsNullOrWhiteSpace(x))) {
                var t=Target.Parse(line);if(!targets.Any(x=>x.Url.Equals(t.Url,StringComparison.OrdinalIgnoreCase)))targets.Add(t);
            }
            range=new DateRange(fromDate.Checked?(DateTime?)fromDate.Value:null,toDate.Checked?(DateTime?)toDate.Value:null);
            if(targets.Count==0)throw new UserError("请先填写用户名或链接。");
            if(!images.Checked && !videos.Checked)throw new UserError("请至少选择图片或视频。");
            mediaSelection=new MediaSelection(images.Checked,videos.Checked);
            if(!Path.IsPathRooted(destination.Text.Trim()))throw new UserError("请选择完整保存路径。");
            root=Path.GetFullPath(destination.Text.Trim());if(root.Length>120)throw new UserError("保存路径过长，请选择较短目录。");
            Directory.CreateDirectory(root);Directory.CreateDirectory(data);
            Downloader.AtomicText(Path.Combine(data,"settings.json"),Json.Write(new { folder=root,from=fromDate.Checked?fromDate.Value.ToString("yyyy-MM-dd"):"",to=toDate.Checked?toDate.Value.ToString("yyyy-MM-dd"):"" }));
        }catch(Exception ex) { Error(ex);return; }
        cancellation=new CancellationTokenSource();var ct=cancellation.Token;paused=false;
        runLog.Clear();logs.Clear();
        start.Enabled=login.Enabled=folder.Enabled=urls.Enabled=destination.Enabled=images.Enabled=videos.Enabled=replies.Enabled=limit.Enabled=fromDate.Enabled=toDate.Enabled=forceDownload.Enabled=strictScan.Enabled=false;
        pause.Enabled=cancel.Enabled=true;progress.Style=ProgressBarStyle.Marquee;
        int saved=0,repaired=0,skipped=0,failed=0,uncertain=0,posts=0;bool interrupted=false,keepPage=false;
        var reports=new List<object>(); string reportFile=Path.Combine(root,"运行记录_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+".诊断.txt");History history=null;
        try {
            history=new History(root);
            root=Path.Combine(root,"任务_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_"+Guid.NewGuid().ToString("N").Substring(0,6));
            Directory.CreateDirectory(Path.Combine(root,"_记录"));lastTaskFolder=root;
            reportFile=Path.Combine(root,"_记录","运行记录_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+".诊断.txt");
            activeDiagnostic=reportFile;
            Log("v0.12 内部测试 · 下载记录集中存储，保留诊断日志。");
            Log("正在检查历史任务记录…");await Task.Run(()=>history.Load(ct),ct);Log("已加载历史记录 "+history.Imported+" 条；旧 JSON 保留原件。");
            history.BeginTask(root,new { targets=targets.Select(t=>t.Url).ToArray(),from=range.Start,to=range.End,images=images.Checked,videos=videos.Checked,limit=limit.Value,force=forceDownload.Checked,strictScan=strictScan.Checked });
            Log("保存目录："+root);
            await browser.Connect(ct);await browser.AttachPage(ct);
            var worker=new Downloader(browser,Log,Wait,history);
            foreach(var target in targets) {
                await Wait(ct);status.Text="扫描 @"+target.User+"…";
                try {
                    var scan=await browser.Scan(target,(int)limit.Value,replies.Checked,Log,async post=> {
                        posts++;Log("帖子 "+post.Id+"：识别 "+post.Media.Count+" 个媒体");
                        bool complete=true;
                        foreach(var warning in post.Warnings) { failed++;complete=false;Log(warning); }
                        string dir=Path.Combine(root,"_记录");Directory.CreateDirectory(dir);

                        history.PostInfo(post,new { postId=post.Id,user=post.User,userId=post.UserId,text=post.Text,date=post.Date,url="https://x.com/"+post.User+"/status/"+post.Id,media=post.Media,mediaCount=post.Media.Count,completed=false });
                        int selected=0;
                        foreach(var media in post.Media) {
                            if(media.Kind=="photo"?!images.Checked:!videos.Checked)continue;
                            selected++;await Wait(ct);status.Text="@"+post.User+" / "+post.Id+" / 媒体 "+media.Index;
                                                        try {
                                bool exists=await Task.Run(()=>history.Find(post,media,ct),ct);
                                if(exists && !forceDownload.Checked) { skipped++;history.Result(post,media,"skipped");Log("历史已校验，跳过："+Downloader.Stem(post,media)); }
                                else {
                                    bool repair=history.LastRepair && !forceDownload.Checked;
                                    bool written=await worker.Save(post,media,root,ct,true,forceDownload.Checked);
                                    if(written) { if(repair) { repaired++;Log("已修复历史缺失或损坏媒体。"); }else saved++; }else skipped++;
                                    history.Result(post,media,written?(repair?"repaired":"saved"):"skipped");
                                }
                            }
                            catch(StopQueue) { throw; }catch(OperationCanceledException) { throw; }
                            catch(Exception ex) { failed++;complete=false;history.Result(post,media,"failed");Log("媒体 "+media.Index+" 未完成：");Error(ex); }
                            status.Text="保存进度 · 新增 "+saved+" 个 / 修复 "+repaired+" 个 / 已存在 "+skipped+" 个 / 失败 "+failed+" 项";
                            await Task.Delay(1000,ct);
                        }
                        if(selected==0 && post.Media.Count>0)Log("本帖没有所选类型的可保存媒体。");
                        history.PostInfo(post,new { postId=post.Id,user=post.User,userId=post.UserId,text=post.Text,date=post.Date,url="https://x.com/"+post.User+"/status/"+post.Id,media=post.Media,mediaCount=post.Media.Count,selectedCount=selected,completed=complete,images=images.Checked,videos=videos.Checked,warnings=post.Warnings });
                    },Wait,ct,range.Accept,strictScan.Checked?null:range,mediaSelection);
                    if((!scan.End && !scan.Reason.Contains("上限") && !scan.Reason.StartsWith("日期边界收尾")) || scan.Rejected>0)uncertain++;
                    if(!scan.End && scan.Posts==0)keepPage=true;
                    Log("@"+target.User+"："+scan.Reason+"；符合日期和类型 "+scan.Posts+" 篇，类型不符/无媒体 "+scan.TypeSkipped+" 篇（不占上限），未确认公开或被过滤 "+scan.Rejected+" 篇。");
                    reports.Add(new { target=target.Url,source=mediaSelection.Page(target,replies.Checked),images=mediaSelection.Images,videos=mediaSelection.Videos,visibleEnd=scan.End,reason=scan.Reason,posts=scan.Posts,typeSkipped=scan.TypeSkipped,rejected=scan.Rejected });
                } catch(StopQueue) { throw; }catch(OperationCanceledException) { throw; }
                catch(Exception ex) { failed++;uncertain++;keepPage=true;Error(ex);reports.Add(new { target=target.Url,visibleEnd=false,reason="任务异常，未确认完整",errorType=ex.GetType().Name }); }
            }
            status.Text=posts==0?(range.Outside>0?"日期范围内没有匹配帖子 · 详情见运行记录":"未读取到帖子 · 请查看诊断日志"):failed>0 || uncertain>0 || range.Unknown>0?"结束 · 有失败或扫描范围未确认，查看日志":"结束 · 新增 "+saved+" 个 / 修复 "+repaired+" 个 / 已存在 "+skipped+" 个";
        }catch(Exception ex) { interrupted=true;status.Text="已停止 · 已完成文件保留";if(ex is OperationCanceledException && ct.IsCancellationRequested)Log("已按你的操作停止；已完成文件保留。");else { keepPage=true;Error(ex); } }
        try { if(keepPage)Log("已保留 Chrome 任务标签页，便于检查页面是否显示媒体、登录或验证提示。");else await browser.ClosePage(); } finally {
            try {
                if(history!=null)history.EndTask(new { version="0.12",limit=(int)limit.Value,replies=replies.Checked,images=images.Checked,videos=videos.Checked,targets=targets.Select(t=>t.Url).ToArray(),time=DateTimeOffset.Now.ToString("o"),interrupted=interrupted,posts=posts,saved=saved,repaired=repaired,dateOutside=range.Outside,dateUnknown=range.Unknown,from=range.Start.HasValue?range.Start.Value.ToString("yyyy-MM-dd"):null,to=range.End.HasValue?range.End.Value.ToString("yyyy-MM-dd"):null,timeZone="UTC",force=forceDownload.Checked,strictScan=strictScan.Checked,skipped=skipped,failed=failed,unconfirmed=uncertain,reports=reports },interrupted);
            }catch(Exception ex) { Error(ex);Log("任务汇总未写入数据库，请保留诊断日志。"); }
            try {
                Log("日期过滤：范围外 "+range.Outside+" 篇，时间无法解析 "+range.Unknown+" 篇。");
                Log("本次新增 "+saved+" 个，修复 "+repaired+" 个，已存在 "+skipped+" 个，失败/不支持 "+failed+" 项。运行记录已保存。");
                string diagnostic=reportFile;
                Log("诊断日志已保存到保存目录，下载明细集中在根目录 _下载记录.sqlite。");
                if(diagnostic!=null)Downloader.AtomicText(diagnostic,runLog.ToString());
            }catch(Exception ex) { Error(ex); }
            activeDiagnostic=null;browser.Dispose();cancellation.Dispose();cancellation=null;paused=false;pause.Text="暂停";
            progress.Style=ProgressBarStyle.Blocks;progress.Value=0;
            start.Enabled=login.Enabled=folder.Enabled=urls.Enabled=destination.Enabled=images.Enabled=videos.Enabled=replies.Enabled=limit.Enabled=fromDate.Enabled=toDate.Enabled=forceDownload.Enabled=strictScan.Enabled=true;
            pause.Enabled=cancel.Enabled=false;if(closing)Close();
        }
    }
}
static class Program {
    [STAThread] static int Main(string[] args) {
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        if(args.Length>2 && args[0]=="--browser-check") {
            try { Tests.BrowserCheck(args[1],args[2]).GetAwaiter().GetResult();return 0; }
            catch(Exception ex) { File.WriteAllText(args[2],ex.ToString());return 1; }
        }
        if(args.Length>0 && args[0]=="--self-test") {
            try { Tests.Run().GetAwaiter().GetResult();return 0; }
            catch(Exception ex) { File.WriteAllText(args.Length>1?args[1]:"test-failure.txt",ex.ToString());return 1; }
        }
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length>1 && args[0]=="--ui-check") {
            using(var form=new MainWindow(true)) { form.Show();Application.DoEvents();using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(args[1]); }form.Close(); }return 0;
        }
        bool created;using(var mutex=new Mutex(true,"Local\\XDownloader-0.1",out created)) {
            if(!created) { MessageBox.Show("下载器已经运行，请切换到已有窗口。","X 下载器");return 0; }
            Application.Run(new MainWindow(false));
        }return 0;
    }
}
}
