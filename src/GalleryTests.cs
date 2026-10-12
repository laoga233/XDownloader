using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace XDownloader {
// Optional integration test: real WebView2, synthetic media served by in-process interception.
// No X login, live posts, media requests to X, or personal download database is used.
static class GalleryTests {
    static void Check(bool condition,string name) { if(!condition)throw new Exception("FAILED: "+name); }
    static async Task Until(CoreWebView2 core,string expression,string name) {
        for(int i=0;i<100;i++) { if(await core.ExecuteScriptAsync(expression)=="true")return;await Task.Delay(100); }
        throw new Exception("Timeout: "+name+"; "+await core.ExecuteScriptAsync("JSON.stringify({url:location.href,ready:document.readyState,html:document.documentElement.outerHTML.slice(0,700)})"));
    }
    static async Task Pointer(CoreWebView2 core,string selector,bool click) {
        string expression="(()=>{const r=document.querySelector("+Json.Write(selector)+").getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+Math.min(50,r.height/2)};})()";
        var point=Json.Read(await core.ExecuteScriptAsync(expression));double x=Convert.ToDouble(Json.Get(point,"x")),y=Convert.ToDouble(Json.Get(point,"y"));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",Json.Write(new { type="mouseMoved",x=x,y=y }));
        if(click) {
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",Json.Write(new { type="mousePressed",x=x,y=y,button="left",clickCount=1 }));
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",Json.Write(new { type="mouseReleased",x=x,y=y,button="left",clickCount=1 }));
        }
    }
    static async Task Muted(CoreWebView2 core,bool expected) {
        for(int i=0;i<50 && core.IsMuted!=expected;i++)await Task.Delay(50);
        Check(core.IsMuted==expected,"native audio mute state "+expected);
    }
    public static int Run(string output) {
        Directory.CreateDirectory(output);int exit=1;
        var items=new List<PreviewMedia>();
        for(int i=0;i<55;i++)items.Add(new PreviewMedia { postId=(100+i).ToString(),assetId="fixture"+i,user=i==0?"tester <b>literal</b>":"example",date="2026-10-12 10:00 UTC",kind=i==1 || i==3?"video":i==2?"animated_gif":"photo",thumbnail="https://pbs.twimg.com/media/fixture"+i+".png?name=small",large="https://pbs.twimg.com/media/fixture"+i+".png?name=large",video="https://video.twimg.com/fixture.mp4",state=i%2==0?"已下载，文件完好":"没有下载记录" });
        byte[] photo;
        using(var bitmap=new Bitmap(480,320)) {
            using(var graphics=Graphics.FromImage(bitmap)) {
                graphics.Clear(Color.FromArgb(31,85,137));graphics.FillEllipse(Brushes.LightSkyBlue,220,30,170,170);graphics.FillRectangle(Brushes.SandyBrown,0,245,480,75);
                using(var font=new Font("Arial",25,FontStyle.Bold))graphics.DrawString("PREVIEW TEST",font,Brushes.White,24,180);
            }
            using(var stream=new MemoryStream()) { bitmap.Save(stream,ImageFormat.Png);photo=stream.ToArray(); }
        }
        byte[] clip=null;string clipType="video/mp4";int images=0,videos=0;var log=new List<string>();MediaGallery gallery=null;
        using(var preview=new PreviewWindow("测试统计",true,items,s=>{})) {
            preview.Show();Application.DoEvents();
            var tabs=preview.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();
            Check(tabs.SelectedIndex==0 && tabs.TabCount==2 && tabs.TabPages[1].Controls.Count==0,"statistics opens first without initializing gallery");
            preview.Close();
        }
        using(var host=new Form { Text="媒体预览集成测试",ClientSize=new Size(760,570),StartPosition=FormStartPosition.CenterScreen }) {
            gallery=new MediaGallery(items,text=> {
                log.Add(text);
                if(text.StartsWith("媒体预览已启动")) {
                    var core=gallery.View.CoreWebView2;
                    core.WebMessageReceived+=delegate(object sender,CoreWebView2WebMessageReceivedEventArgs e) { log.Add("message "+e.Source+": "+e.WebMessageAsJson); };
                    core.NavigationStarting+=delegate(object sender,CoreWebView2NavigationStartingEventArgs e) { log.Add("navigation "+e.Uri.Substring(0,Math.Min(e.Uri.Length,100))+" cancel="+e.Cancel); };
                    core.NavigationCompleted+=delegate(object sender,CoreWebView2NavigationCompletedEventArgs e) { log.Add("navigation end "+e.IsSuccess+" "+e.WebErrorStatus); };
                    core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived+=delegate(object sender,CoreWebView2DevToolsProtocolEventReceivedEventArgs e) { log.Add("JS: "+e.ParameterObjectAsJson); };
                    core.CallDevToolsProtocolMethodAsync("Runtime.enable","{}");
                    core.WebResourceRequested+=delegate(object sender,CoreWebView2WebResourceRequestedEventArgs e) {
                        log.Add("resource "+e.ResourceContext+" "+e.Request.Uri.Substring(0,Math.Min(e.Request.Uri.Length,100))+" status="+(e.Response==null?"none":e.Response.StatusCode.ToString()));
                        if(e.Request.Uri.StartsWith("https://pbs.twimg.com/")) { images++;e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(photo),200,"OK","Content-Type: image/png\r\nCache-Control: no-store"); }
                        else if(e.Request.Uri.StartsWith("https://video.twimg.com/")) { videos++;e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(clip??new byte[0]),clip==null?503:200,"OK","Content-Type: "+clipType.Split(';')[0]+"\r\nCache-Control: no-store"); }
                    };
                }
            },Path.Combine(output,"cache")) { Dock=DockStyle.Fill };
            host.Controls.Add(gallery);
            host.Shown+=async delegate {
                try {
                    gallery.ActivateGallery();
                    for(int i=0;i<100 && (gallery.View==null || gallery.View.CoreWebView2==null || !log.Any(x=>x.StartsWith("媒体预览已启动")));i++)await Task.Delay(100);
                    Check(gallery.View!=null && gallery.View.CoreWebView2!=null,"WebView2 initialization: "+String.Join("; ",log));
                    var core=gallery.View.CoreWebView2;
                    await Until(core,"document.querySelectorAll('.card').length===24","24-item pagination");
                    await core.ExecuteScriptAsync("document.addEventListener('error',e=>{if(e.target.tagName==='VIDEO')window.chrome.webview.postMessage({kind:'test-media-error',code:e.target.error&&e.target.error.code,message:e.target.error&&e.target.error.message});},true)");
                    await Pointer(core,"header",false);
                    await Until(core,"[...document.querySelectorAll('.cover img')].some(x=>x.complete&&x.naturalWidth>0)","thumbnail decoding");
                    Check(videos==0,"video never requested before hover");
                    Check(await core.ExecuteScriptAsync("document.querySelector('.details b')===null && document.querySelector('.details').textContent.includes('<b>literal</b>')")=="true","user strings rendered as text");
                    Check(await core.ExecuteScriptAsync("document.querySelectorAll('.cover img[src]').length<24")=="true","only visible thumbnails loaded: "+await core.ExecuteScriptAsync("JSON.stringify({height:innerHeight,grid:document.getElementById('grid').clientHeight,scroll:document.getElementById('grid').scrollHeight,card:document.querySelector('.card').getBoundingClientRect().height})"));
                    using(var file=File.Create(Path.Combine(output,"gallery.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
                    // Synthesize an audio track without microphone access or routing the generator to speakers.
                    string recording=@"(async()=>{window.fixtureReady=false;const c=document.createElement('canvas');c.width=160;c.height=100;const g=c.getContext('2d');let frame=0;const timer=setInterval(()=>{g.fillStyle=frame++%2?'#b05338':'#206ca4';g.fillRect(0,0,160,100);g.fillStyle='white';g.font='18px sans-serif';g.fillText('Video preview',15,55);},60);const stream=c.captureStream(15);const audio=new AudioContext();await audio.resume();const tone=audio.createOscillator(),gain=audio.createGain(),dest=audio.createMediaStreamDestination();tone.frequency.value=440;gain.gain.value=0.015;tone.connect(gain).connect(dest);tone.start();stream.addTrack(dest.stream.getAudioTracks()[0]);const type='video/webm;codecs=vp8,opus';const recorder=new MediaRecorder(stream,{mimeType:type});const chunks=[];recorder.ondataavailable=e=>chunks.push(e.data);recorder.onstop=()=>{clearInterval(timer);tone.stop();stream.getTracks().forEach(t=>t.stop());audio.close();const reader=new FileReader();reader.onload=()=>{window.fixtureData=reader.result.slice(reader.result.indexOf(';base64,')+8);window.fixtureType=type;window.fixtureReady=true;};reader.readAsDataURL(new Blob(chunks,{type}));};recorder.start();setTimeout(()=>recorder.stop(),900);})()";
                    await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",Json.Write(new { expression=recording,userGesture=true,awaitPromise=true }));
                    await Until(core,"window.fixtureReady===true","synthetic video recording");
                    // Failed playback remains local, shows a useful message and can be retried.
                    await core.ExecuteScriptAsync("document.querySelectorAll('.card')[1].dispatchEvent(new MouseEvent('mouseenter'))");
                    await Until(core,"document.getElementById('loading').textContent.includes('暂时无法播放')","failed video feedback");
                    Check(await core.ExecuteScriptAsync("!document.querySelector('video').getAttribute('src')")=="true","failed video released");
                    await core.ExecuteScriptAsync("document.querySelectorAll('.card')[1].dispatchEvent(new MouseEvent('mouseleave'))");
                    clip=Convert.FromBase64String(Json.Str(Json.Read("{\"value\":"+await core.ExecuteScriptAsync("window.fixtureData")+"}"),"value"));
                    clipType=Json.Str(Json.Read("{\"value\":"+await core.ExecuteScriptAsync("window.fixtureType")+"}"),"value");
                    log.Add("Fixture bytes="+clip.Length+" type="+clipType);File.WriteAllBytes(Path.Combine(output,"synthetic-clip.webm"),clip);
                    int beforeFlyover=videos;
                    await core.ExecuteScriptAsync("document.querySelectorAll('.card')[1].dispatchEvent(new MouseEvent('mouseenter'));document.querySelectorAll('.card')[1].dispatchEvent(new MouseEvent('mouseleave'))");
                    await Task.Delay(600);Check(videos==beforeFlyover,"quick flyover never starts video");
                    await Pointer(core,".card:nth-child(2)",false);
                    await Until(core,"!!document.querySelector('video') && document.querySelector('video').currentTime>0","video playback after hover");
                    Check(core.IsMuted && await core.ExecuteScriptAsync("document.querySelector('video').muted && document.querySelector('video').volume===0")=="true","host and video both muted");
                    await Until(core,"document.querySelector('video').webkitAudioDecodedByteCount>0","actual audio track decoded");
                    Check(await core.ExecuteScriptAsync("document.getElementById('caption')===null && !document.getElementById('sound').hidden")=="true","caption removed and per-video sound button visible");
                    using(var file=File.Create(Path.Combine(output,"video-hover.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
                    await Pointer(core,"#sound",true);await Muted(core,false);
                    await Until(core,"!document.querySelector('video').muted && document.querySelector('video').volume===1 && !document.querySelector('video').paused && document.getElementById('sound').getAttribute('aria-pressed')==='true'","pointer moves from card into popup and unmutes playback");
                    using(var file=File.Create(Path.Combine(output,"video-sound-on.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
                    await Pointer(core,"#sound",true);await Muted(core,true);
                    Check(await core.ExecuteScriptAsync("document.querySelector('video').muted && document.getElementById('sound').getAttribute('aria-pressed')==='false'")=="true","sound button mutes again");
                    await Pointer(core,"#sound",true);await Muted(core,false);
                    await Pointer(core,"header",false);
                    await Until(core,"document.querySelectorAll('video').length===0 && document.getElementById('expanded').style.display==='none'","leaving popup stops and removes video");await Muted(core,true);
                    await Pointer(core,".card:nth-child(4)",false);
                    await Until(core,"!!document.querySelector('video') && document.querySelector('video').currentTime>0","second independent video playback");
                    Check(core.IsMuted && await core.ExecuteScriptAsync("document.querySelector('video').muted && document.getElementById('sound').getAttribute('aria-pressed')==='false'")=="true","previous video sound choice never unmutes next video");
                    await Pointer(core,"#sound",true);await Muted(core,false);
                    gallery.SuspendGallery();Check(core.IsMuted,"suspending audible video immediately mutes native host");
                    await Until(core,"document.querySelectorAll('video').length===0","suspending audible video removes player");
                    await Pointer(core,"header",false);gallery.ActivateGallery();
                    await core.ExecuteScriptAsync("document.querySelectorAll('.card')[0].dispatchEvent(new MouseEvent('mouseenter'))");
                    await Until(core,"!!document.querySelector('#expanded img') && document.querySelector('#expanded img').naturalWidth>0","hover image enlargement");
                    Check(await core.ExecuteScriptAsync("document.getElementById('sound').hidden && document.getElementById('caption')===null")=="true","image has neither audio button nor footer caption");
                    using(var file=File.Create(Path.Combine(output,"image-hover.png")))await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
                    await core.ExecuteScriptAsync("document.querySelectorAll('.card')[2].dispatchEvent(new MouseEvent('mouseenter'))");
                    await Until(core,"document.querySelectorAll('video').length===1 && document.querySelector('video').currentTime>0","GIF as muted video");
                    Check(core.IsMuted && await core.ExecuteScriptAsync("document.querySelector('video').muted && document.getElementById('sound').hidden")=="true","GIF remains muted without irrelevant audio control");
                    gallery.SuspendGallery();
                    await Until(core,"document.querySelectorAll('video').length===0 && document.querySelectorAll('.cover img[src]').length===0","tab switch cancels media");
                    gallery.ActivateGallery();await Until(core,"document.querySelectorAll('.cover img[src]').length>0","return to gallery reloads visible covers");
                    await core.ExecuteScriptAsync("document.getElementById('next').click()");
                    await Until(core,"document.getElementById('page').textContent.includes('第 2 / 3') && document.querySelectorAll('.card').length===24","page switch");
                    await core.ExecuteScriptAsync("document.getElementById('next').click()");
                    await Until(core,"document.querySelectorAll('.card').length===7 && document.getElementById('next').disabled","last page");
                    string session=gallery.SessionFolder;gallery.Dispose();
                    for(int i=0;i<100 && Directory.Exists(session);i++)await Task.Delay(100);
                    Check(!Directory.Exists(session),"temporary profile removed after gallery closes");
                    // Opening another preview in the same process must initialize a fresh session.
                    gallery=new MediaGallery(new List<PreviewMedia>(),s=>log.Add(s),Path.Combine(output,"cache")) { Dock=DockStyle.Fill };
                    host.Controls.Add(gallery);gallery.ActivateGallery();
                    for(int i=0;i<100 && (gallery.View==null || gallery.View.CoreWebView2==null);i++)await Task.Delay(100);
                    Check(gallery.View!=null && gallery.View.CoreWebView2!=null,"second gallery initialization");
                    await Until(gallery.View.CoreWebView2,"!!document.getElementById('empty')","second preview opens with empty state");
                    session=gallery.SessionFolder;gallery.Dispose();
                    for(int i=0;i<100 && Directory.Exists(session);i++)await Task.Delay(100);
                    Check(!Directory.Exists(session),"second temporary profile removed");
                    Check(Directory.GetFiles(output,"*.sqlite",SearchOption.AllDirectories).Length==0,"no download database created");
                    File.WriteAllText(Path.Combine(output,"result.txt"),"PASS: statistics opens without gallery, real WebView2 thumbnails, pagination/lazy loading, text escaping, failed playback/retry, delayed hover, decoded "+clipType+", audio track decoded, pointer transfer into popup, independent sound on/off, next video defaults muted, audible playback suspended, GIF muted, caption removed, image enlargement, mouseleave, tab suspension/resume, repeat preview, cache cleanup. Image requests="+images+", video requests="+videos+". All media were synthetic and served locally; no X account or downloads used. Physical speaker output was not measured.");exit=0;
                }catch(Exception ex) {
                    File.WriteAllText(Path.Combine(output,"result.txt"),ex.ToString()+Environment.NewLine+String.Join(Environment.NewLine,log));
                }
                finally { gallery.Dispose();host.Close(); }
            };
            Application.Run(host);
        }
        return exit;
    }
}
}
