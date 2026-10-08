using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace XDownloader {
sealed class PreviewSummary {
    public static readonly string Notice=
        "这里的数量只是本次预览的结果，不一定包含全部历史帖子。\r\n"+
        "达到帖数上限，或看到较早的帖子时，扫描可能提前结束。\r\n\r\n"+
        "确认下载后会重新查看页面，再检查一次是否已经下载过，\r\n"+
        "所以需要再等一会儿，实际下载数量也可能有变化。\r\n\r\n"+
        "预览不会下载文件，也不会改动原来的下载记录。\r\n"+
        "旧记录整理和文件夹恢复会在正式下载时进行。\r\n"+
        "排查问题用的日志会保存在保存位置下的“_预览记录”文件夹。\r\n\r\n"+
        "X 没有提供敏感标记，不代表内容一定安全。";
    public int Posts,Photos,Videos,Gifs,Existing,Repair,NewMedia,Unknown,Warnings;
    public int Total { get { return Photos+Videos+Gifs; } }
    public int ToDownload(bool force) { return force?Total:NewMedia+Repair; }
    public void Observe(Post post,MediaSelection selection,History history,CancellationToken ct) {
        ct.ThrowIfCancellationRequested();Posts++;Warnings+=post.Warnings.Count;
        foreach(var media in post.Media) {
            if(!selection.AcceptsType(media) || selection.SkipSensitive && media.PossiblySensitive==true)continue;
            ct.ThrowIfCancellationRequested();
            bool exists=history.Find(post,media,ct);
            if(media.Kind=="photo")Photos++;else if(media.Kind=="animated_gif")Gifs++;else Videos++;
            if(!media.PossiblySensitive.HasValue)Unknown++;
            if(exists)Existing++;else if(history.LastRepair)Repair++;else NewMedia++;
        }
    }
    public string Describe(Target target,string root,DateRange range,ScanResult scan,bool force,string stopped) {
        var text=new StringBuilder();
        text.AppendLine("下载账号：@"+target.User).AppendLine("保存到："+root);
        string dates=!range.Start.HasValue && !range.End.HasValue?"不限日期":(range.Start.HasValue?range.Start.Value.ToString("yyyy-MM-dd"):"不限开始日期")+" 至 "+(range.End.HasValue?range.End.Value.ToString("yyyy-MM-dd"):"不限结束日期");
        text.AppendLine("帖子发布日期（UTC）："+dates);
        string reason=stopped;
        if(reason==null) {
            if(scan.Reason.Contains("上限"))reason="已达到你设置的帖数上限，先看到这里。";
            else if(scan.Reason.StartsWith("日期边界收尾"))reason="已看到比开始日期更早的帖子，预览已停止。";
            else if(scan.End)reason="已查看完这次页面能显示的内容。";
            else reason="页面没有继续提供可用的帖子，暂时无法确认是否已经看完。";
        }
        text.AppendLine().AppendLine(reason);
        text.AppendLine(scan==null?"这次预览没有完成，下面只显示停止前已检查的部分。":"这次查看了 "+scan.Seen+" 篇帖子，其中 "+Posts+" 篇符合你的设置。");
        text.AppendLine().AppendLine("找到的文件");
        text.AppendLine("图片\t"+Photos+" 张");
        text.AppendLine("视频\t"+Videos+" 个");
        text.AppendLine("GIF 动图\t"+Gifs+" 个");
        text.AppendLine("文件合计\t"+Total+" 个");
        text.AppendLine().AppendLine("之前的下载情况");
        text.AppendLine("已下载，文件完好\t"+Existing+" 个");
        text.AppendLine("文件缺失或损坏，可能需重下\t"+Repair+" 个");
        text.AppendLine("没有下载记录\t"+NewMedia+" 个");
        text.AppendLine().AppendLine("这次预计下载\t"+ToDownload(force)+" 个文件");
        text.AppendLine(force?"你已勾选“重新下载”，已有文件也会再次下载。":"已经下载且完好的文件会自动跳过。");
        text.AppendLine().AppendLine("其他情况");
        text.AppendLine("不在所选日期内\t"+range.Outside+" 篇");
        text.AppendLine("无法读取发布日期\t"+range.Unknown+" 篇");
        if(scan!=null) {
            text.AppendLine("没有所选类型的图片或视频\t"+scan.TypeSkipped+" 篇");
            text.AppendLine("按敏感设置跳过\t"+scan.SensitiveSkipped+" 个文件");
        }
        if(scan!=null && scan.Rejected>0)text.AppendLine("无法确认公开或被其他规则排除\t"+scan.Rejected+" 篇");
        text.AppendLine("X 未提供明确敏感标记\t"+Unknown+" 个文件");
        if(Warnings>0)text.AppendLine("媒体读取提示（详见日志）\t"+Warnings+" 项");
        return text.ToString();
    }
}
sealed class PreviewWindow : Form {
    readonly ToolTip noticeTip=new ToolTip { InitialDelay=350,ReshowDelay=100,AutoPopDelay=30000,ShowAlways=true,ToolTipTitle="预览说明" };
    public PreviewWindow(string summary,bool canDownload) {
        Text="下载前预览 · 请核对后再下载";ClientSize=new Size(760,620);MinimumSize=new Size(680,500);StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;MinimizeBox=false;
        Font=new Font("Microsoft YaHei UI",10);BackColor=Color.FromArgb(245,247,251);
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(16),RowCount=2,ColumnCount=1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));Controls.Add(layout);
        var content=new RichTextBox { Text=summary,ReadOnly=true,DetectUrls=false,ScrollBars=RichTextBoxScrollBars.Vertical,Dock=DockStyle.Fill,BackColor=Color.White,BorderStyle=BorderStyle.FixedSingle,AccessibleName="预览统计及扫描范围说明" };layout.Controls.Add(content,0,0);
        content.SelectAll();content.SelectionTabs=new[]{300};
        using(var bold=new Font(Font,FontStyle.Bold)) {
            foreach(Match match in Regex.Matches(content.Text,@"(?m)^.*\t(\d+[^\n]*)")) {
                var value=match.Groups[1];content.Select(value.Index,value.Length);content.SelectionFont=bold;content.SelectionColor=Color.FromArgb(31,99,221);
            }
            foreach(string heading in new[]{"找到的文件","之前的下载情况","其他情况","文件合计"}) {
                int index=content.Text.IndexOf(heading,StringComparison.Ordinal);if(index<0)continue;
                content.Select(index,heading.Length);content.SelectionFont=bold;
            }
        }
        int expected=content.Text.IndexOf("这次预计下载",StringComparison.Ordinal);
        if(expected>=0)using(var emphasis=new Font(Font.FontFamily,12,FontStyle.Bold)) {
            int end=content.Text.IndexOf('\n',expected);content.Select(expected,(end<0?content.Text.Length:end)-expected);content.SelectionFont=emphasis;content.SelectionColor=Color.FromArgb(31,99,221);
        }
        content.Select(0,0);
        var buttons=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false };
        var download=new Button { Text="确认并重新扫描下载",Width=220,Height=36,Enabled=canDownload,DialogResult=DialogResult.OK };
        var close=new Button { Text="仅预览，返回",Width=160,Height=36,DialogResult=DialogResult.Cancel };
        var notice=new Button { Text="[声明]",Width=80,Height=36,FlatStyle=FlatStyle.Flat,ForeColor=Color.FromArgb(31,99,221),Cursor=Cursors.Help,AccessibleName="声明，悬停查看预览说明",AccessibleDescription=PreviewSummary.Notice };
        notice.FlatAppearance.BorderSize=0;noticeTip.SetToolTip(notice,PreviewSummary.Notice);
        notice.Enter+=delegate { noticeTip.Show(PreviewSummary.Notice,notice,0,-310,30000); };
        notice.Leave+=delegate { noticeTip.Hide(notice); };
        buttons.Controls.Add(download);buttons.Controls.Add(close);buttons.Controls.Add(notice);layout.Controls.Add(buttons,0,1);CancelButton=close;AcceptButton=close;
        Shown+=delegate { content.Select(0,0);content.ScrollToCaret();close.Select(); };
    }
    protected override void Dispose(bool disposing) { if(disposing)noticeTip.Dispose();base.Dispose(disposing); }
}
partial class MainWindow {
    void SetTaskControls(bool enabled) {
        start.Enabled=login.Enabled=folder.Enabled=recentButton.Enabled=urls.Enabled=destination.Enabled=images.Enabled=videos.Enabled=replies.Enabled=limit.Enabled=fromDate.Enabled=toDate.Enabled=forceDownload.Enabled=strictScan.Enabled=skipSensitive.Enabled=previewBeforeDownload.Enabled=enabled;
        pause.Enabled=cancel.Enabled=!enabled;
    }
    async Task<bool> PreviewBeforeDownload(Target target,string root,DateRange range,MediaSelection selection) {
        cancellation=new CancellationTokenSource();var ct=cancellation.Token;paused=false;
        runLog.Clear();logs.Clear();SetTaskControls(false);progress.Style=ProgressBarStyle.Marquee;
        var summary=new PreviewSummary();ScanResult scan=null;string stopped=null;
        try {
            string folder=Path.Combine(root,"_预览记录");TaskArchive.CheckPath(root,folder);Directory.CreateDirectory(folder);
            activeDiagnostic=Path.Combine(folder,"预览_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_"+Guid.NewGuid().ToString("N").Substring(0,6)+".诊断.txt");
            Log("v0.15 · 下载前预览，只读检查历史，不保存媒体。诊断日志："+activeDiagnostic);
            status.Text="预览 · 正在校验历史记录…";
            var history=new History(root);await Task.Run(()=>history.LoadPreview(ct),ct);
            await browser.Connect(ct);await browser.AttachPage(ct);
            scan=await browser.Scan(target,(int)limit.Value,replies.Checked,Log,async post=> {
                await Task.Run(()=>summary.Observe(post,selection,history,ct),ct);
                status.Text="预览 · 匹配 "+summary.Posts+" 篇 / 媒体 "+summary.Total+" 个 / 历史完整 "+summary.Existing+" 个";
            },Wait,ct,range.Accept,strictScan.Checked?null:range,selection);
            await Wait(ct);
        }catch(OperationCanceledException) { stopped="预览已取消或连接超时，未开始下载。";scan=null;Log(stopped); }
        catch(Exception ex) { stopped="预览出错，扫描未完成，未开始下载。";scan=null;Error(ex); }
        finally {
            string description=summary.Describe(target,root,range,scan,forceDownload.Checked,stopped);
            Log(description);
            if(scan!=null)Log("预览扫描原始状态："+scan.Reason);
            Log(PreviewSummary.Notice);
            activeDiagnostic=null;
        }
        await browser.ClosePage();browser.Dispose();cancellation.Dispose();cancellation=null;paused=false;pause.Text="暂停";
        progress.Style=ProgressBarStyle.Blocks;progress.Value=0;SetTaskControls(true);
        status.Text=stopped??"预览完成 · 等待确认下载";
        if(closing) { Close();return false; }
        bool proceed;
        using(var dialog=new PreviewWindow(summary.Describe(target,root,range,scan,forceDownload.Checked,stopped),scan!=null && summary.Total>0))proceed=dialog.ShowDialog(this)==DialogResult.OK;
        if(!proceed)status.Text=stopped??"预览结束 · 未开始下载";
        return proceed;
    }
}
}
