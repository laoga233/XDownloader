using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace XDownloader {
class Target {
    public string User, Id, Url;
    public static Target Parse(string input) {
        input=input.Trim();
        if(Regex.IsMatch(input,@"^@?[A-Za-z0-9_]{1,15}$")) input="https://x.com/"+input.TrimStart('@');
        Uri u;
        if(!Uri.TryCreate(input,UriKind.Absolute,out u) || u.Scheme!="https" || !Model.XHost(u.Host) || !u.IsDefaultPort || u.UserInfo!="") throw new UserError("请输入 @用户名、X 用户主页或 https://x.com/用户名/status/帖子编号。");
        var m=Regex.Match(u.AbsolutePath,@"^/([A-Za-z0-9_]{1,15})(?:/(?:media|with_replies)|/status/([0-9]{1,25})(?:/(?:photo|video)/[1-9])?)?/?$");
        if(!m.Success || new[]{"home","explore","search","settings","notifications","messages","i","login","logout","intent","compose"}.Contains(m.Groups[1].Value.ToLowerInvariant())) throw new UserError("该链接不是支持的用户主页或单条帖子。");
        string user=m.Groups[1].Value, id=m.Groups[2].Value;
        return new Target { User=user, Id=id, Url="https://x.com/"+user+(id==""?"/media":"/status/"+id) };
    }
}
class Asset { public string Id, Url, Kind; public int Index; public bool? PossiblySensitive; }
class MediaSelection {
    public readonly bool Images,Videos,SkipSensitive;
    public MediaSelection(bool images,bool videos,bool skipSensitive=false) { if(!images && !videos)throw new UserError("请至少选择图片或视频。");Images=images;Videos=videos;SkipSensitive=skipSensitive; }
    public bool Accept(Post post) { return post.Media.Any(AcceptsType); }
    public bool AcceptsType(Asset asset) { return asset.Kind=="photo"?Images:Videos; }
    public string Page(Target target,bool replies) {
        // The redesigned /media page can default to videos. The normal post timeline
        // carries original mixed media and keeps one chronological post limit.
        return target.Id=="" && Images?"https://x.com/"+target.User+(replies?"/with_replies":""):target.Url;
    }
}
class Post { public string Id, User, UserId, Text, Date; public bool? PossiblySensitive; public List<Asset> Media=new List<Asset>(); public List<string> Warnings=new List<string>(); }
class Batch { public List<Post> Posts=new List<Post>(); public bool End, Structured; public int Rejected; }
static class Model {
    // Only a fixed vocabulary of field names/types is emitted. Never response values.
    public static string Schema(string text) {
        var known=new HashSet<string>(new[]{"data","user","result","results","timeline","timeline_v2","instructions","entries","content","items","item","itemContent","tweet_results","tweetResult","tweet","tweets","legacy","core","user_results","privacy","protected","rest_id","id_str","screen_name","full_text","extended_entities","media","video_info","variants","possibly_sensitive","possibly_sensitive_editable","errors","code","message","__typename","type","cursorType","value"});
        var output=new List<string>();int budget=2500;
        SchemaWalk(Json.Read(text),"$",0,known,output,ref budget);
        return String.Join("; ",output.Take(65));
    }
    static void SchemaWalk(object obj,string path,int depth,HashSet<string> known,List<string> output,ref int budget) {
        if(depth>22 || budget--<=0 || output.Count>=65)return;
        var d=obj as Dictionary<string,object>;
        if(d!=null) {
            int unknown=0;
            foreach(var kv in d) {
                if(!known.Contains(kv.Key)) {
                    unknown++;
                    if(kv.Value is Dictionary<string,object> || kv.Value is IList)SchemaWalk(kv.Value,path+".<other>",depth+1,known,output,ref budget);
                    continue;
                }
                string next=path+"."+kv.Key;
                if(kv.Value is Dictionary<string,object> || kv.Value is IList)SchemaWalk(kv.Value,next,depth+1,known,output,ref budget);
                else output.Add(next+":"+(kv.Value==null?"null":kv.Value is string?"string":kv.Value is bool?"bool":"number/other"));
                if(output.Count>=65)break;
            }
            if(d.Count==0)output.Add(path+":empty-object");
            if(unknown>0)output.Add(path+":other-fields="+unknown);
        } else if(obj is IList) {
            var list=(IList)obj;output.Add(path+":array("+list.Count+")");
            // Sample the beginning and end, which commonly contain different entry types.
            if(list.Count>0)SchemaWalk(list[0],path+"[]",depth+1,known,output,ref budget);
            if(list.Count>1)SchemaWalk(list[list.Count-1],path+"[]",depth+1,known,output,ref budget);
        }
    }
    public static bool XHost(string host) { return new[]{"x.com","www.x.com","twitter.com","www.twitter.com","mobile.twitter.com"}.Contains(host.ToLowerInvariant()); }
    public static bool Allowed(string url) {
        Uri u; return Uri.TryCreate(url,UriKind.Absolute,out u) && u.Scheme=="https" && u.IsDefaultPort && u.UserInfo=="" && (u.Host=="pbs.twimg.com" || u.Host=="video.twimg.com");
    }
    public static Dictionary<string,object> At(Dictionary<string,object> d,params string[] keys) { foreach(var k in keys)d=Json.Map(Json.Get(d,k));return d; }
    static bool? SensitiveFlag(Dictionary<string,object> d) { object v=Json.Get(d,"possibly_sensitive");return v is bool?(bool?)v:null; }
    static bool? OrSensitive(bool? a,bool? b) { return a==true || b==true?(bool?)true:a.HasValue?a:b; }
    public static string Safe(string s) {
        s=Regex.Replace(s??"",@"[^A-Za-z0-9_\-]","_");
        if(s.Length>60)s=s.Substring(0,60);
        if(s=="")s="unknown";
        if(Regex.IsMatch(s,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",RegexOptions.IgnoreCase))s="_"+s;
        return s;
    }
    public static string Hash(string file) { using(var h=SHA256.Create())using(var f=File.OpenRead(file))return BitConverter.ToString(h.ComputeHash(f)).Replace("-","").ToLowerInvariant(); }
    public static string Original(string url) {
        if(!Allowed(url) || new Uri(url).Host!="pbs.twimg.com")throw new UserError("图片地址不属于支持的 X 媒体域名。");
        var u=new Uri(url); var q=System.Web.HttpUtility.ParseQueryString(u.Query);
        string format=q["format"]??Path.GetExtension(u.AbsolutePath).TrimStart('.');
        if(!new[]{"jpg","jpeg","png","webp","gif"}.Contains(format))throw new UserError("未知原图格式。");
        string path=u.GetLeftPart(UriPartial.Path);
        if(Path.GetExtension(u.AbsolutePath)!="")path=path.Substring(0,path.LastIndexOf('.'));
        return path+"?format="+format+"&name=orig";
    }
    public static string Extension(byte[] b,string kind) {
        if(kind!="photo") {
            if(b.Length>=12 && Encoding.ASCII.GetString(b,4,4)=="ftyp")return ".mp4";
        } else {
            if(b.Length>=8 && b[0]==137 && b[1]==80 && b[2]==78 && b[3]==71 && b[4]==13 && b[5]==10 && b[6]==26 && b[7]==10)return ".png";
            if(b.Length>=3 && b[0]==255 && b[1]==216 && b[2]==255)return ".jpg";
            if(b.Length>=6 && (Encoding.ASCII.GetString(b,0,6)=="GIF89a" || Encoding.ASCII.GetString(b,0,6)=="GIF87a"))return ".gif";
            if(b.Length>=12 && Encoding.ASCII.GetString(b,0,4)=="RIFF" && Encoding.ASCII.GetString(b,8,4)=="WEBP")return ".webp";
        }
        throw new UserError("媒体文件头不匹配，未将响应保存为成品。");
    }
    public static Batch Parse(string text,Target target,bool replies) {
        var root=Json.Read(text); var result=new Batch();
        foreach(var error in Json.Arr(Json.Get(root,"errors"))) {
            var e=Json.Map(error);string code=Json.Str(e,"code");
            if(code=="88")throw new StopQueue("X 返回限速错误（88），请稍后重试。");
            if(code=="32" || code=="89" || code=="326")throw new StopQueue("X 要求重新登录或验证，请查看专用 Chrome 窗口。");
            throw new UserError("X 返回 GraphQL 错误"+(Regex.IsMatch(code,@"^\d+$")?"（"+code+"）":"")+"，本次扫描未确认完整。");
        }
        Walk(root,result,target,replies,0,new HashSet<string>());return result;
    }
    static void Walk(object value,Batch batch,Target target,bool replies,int depth,HashSet<string> seen) {
        if(depth>60)return;
        var d=value as Dictionary<string,object>;
        if(d==null) { foreach(var a in Json.Arr(value))Walk(a,batch,target,replies,depth+1,seen);return; }
        if(d.ContainsKey("instructions") || d.ContainsKey("tweet_results"))batch.Structured=true;
        if(Json.Str(d,"type")=="TimelineTerminateTimeline" && Json.Str(d,"direction")=="Bottom")batch.End=true;
        if(d.ContainsKey("promotedMetadata") || d.ContainsKey("promoted_metadata"))return;
        if(Json.Str(d,"__typename")=="TweetWithVisibilityResults") {
            Walk(Json.Get(d,"tweet"),batch,target,replies,depth+1,seen);return;
        }
        if(Json.Str(d,"__typename")=="Tweet" || (d.ContainsKey("rest_id") && d.ContainsKey("legacy") && d.ContainsKey("core") && At(d,"legacy").ContainsKey("full_text"))) {
            batch.Structured=true;
            Post post=ParsePost(d,target,replies,batch);
            if(post!=null && seen.Add(post.Id))batch.Posts.Add(post);
            return; // Do not descend into quoted or retweeted authors' media.
        }
        // Profile metadata can appear before the timeline.
        if(Json.Str(d,"__typename")=="User") {
            var core=At(d,"core");var legacy=At(d,"legacy");
            string name=Json.Str(core,"screen_name");if(name=="")name=Json.Str(legacy,"screen_name");
            if(name.Equals(target.User,StringComparison.OrdinalIgnoreCase) && (Object.Equals(Json.Get(legacy,"protected"),true) || Object.Equals(Json.Get(At(d,"privacy"),"protected"),true)))throw new StopQueue("该用户为受保护账号；本下载器仅处理公开帖子。");
        }
        foreach(var kv in d) {
            if(kv.Key=="quoted_status_result" || kv.Key=="retweeted_status_result")continue;
            Walk(kv.Value,batch,target,replies,depth+1,seen);
        }
    }
    static Post ParsePost(Dictionary<string,object> d,Target target,bool replies,Batch batch) {
        var legacy=At(d,"legacy");string id=Json.Str(d,"rest_id");
        if(!Regex.IsMatch(id,@"^[0-9]{1,25}$") || (target.Id!="" && id!=target.Id))return null;
        var user=At(d,"core","user_results","result");var ul=At(user,"legacy");var uc=At(user,"core");
        string name=Json.Str(uc,"screen_name");if(name=="")name=Json.Str(ul,"screen_name");
        if(!name.Equals(target.User,StringComparison.OrdinalIgnoreCase))return null;
        object protection=Json.Get(At(user,"privacy"),"protected")??Json.Get(ul,"protected");
        if(!Object.Equals(protection,false)) { batch.Rejected++;return null; }
        if(legacy.ContainsKey("retweeted_status_result") || legacy.ContainsKey("retweeted_status_id_str") || d.ContainsKey("retweeted_status_result"))return null;
        if((!replies && Json.Str(legacy,"in_reply_to_status_id_str")!="") || Json.Get(d,"trusted_friends_info")!=null || Json.Get(d,"exclusive_tweet_info")!=null || Json.Get(legacy,"limited_actions")!=null || Json.Get(d,"limited_actions")!=null) { batch.Rejected++;return null; }
        bool? postSensitive=OrSensitive(SensitiveFlag(d),SensitiveFlag(legacy));
        var p=new Post { Id=id,User=name,UserId=Json.Str(user,"rest_id"),Text=Json.Str(legacy,"full_text"),Date=Json.Str(legacy,"created_at"),PossiblySensitive=postSensitive };
        var seen=new HashSet<string>();int index=0;
        foreach(var item in Json.Arr(Json.Get(At(legacy,"extended_entities"),"media"))) {
            var media=Json.Map(item);index++;
            string type=Json.Str(media,"type"), mid=Json.Str(media,"id_str");if(mid=="")mid=id+"_"+index;
            if(!seen.Add(mid))continue;
            string source=Json.Str(media,"source_status_id_str");
            if(source!="" && source!=id)continue;
            try {
                string url="";
                if(type=="photo")url=Original(Json.Str(media,"media_url_https"));
                else if(type=="video" || type=="animated_gif") {
                    long best=-1;
                    foreach(var v in Json.Arr(Json.Get(At(media,"video_info"),"variants"))) {
                        var variant=Json.Map(v);long rate;long.TryParse(Json.Str(variant,"bitrate"),out rate);
                        string candidate=Json.Str(variant,"url");
                        if(Json.Str(variant,"content_type")=="video/mp4" && Allowed(candidate) && new Uri(candidate).Host=="video.twimg.com" && rate>best) { best=rate;url=candidate; }
                    }
                    if(url=="")throw new UserError("无可用 MP4；暂不支持仅 HLS / 直播媒体。");
                } else throw new UserError("遇到未支持的媒体类型。");
                p.Media.Add(new Asset { Id=mid,Index=index,Url=url,Kind=type,PossiblySensitive=OrSensitive(postSensitive,SensitiveFlag(media)) });
            }catch(UserError e) { p.Warnings.Add("媒体 "+index+"："+e.Message); }
        }
        return p;
    }
}
}
