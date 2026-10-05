using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace XDownloader {
// Windows 10/11 inbox SQLite. Every connection has a bounded lifetime.
sealed class RecordDb : IDisposable {
    IntPtr db;
    const string Native="winsqlite3.dll";
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Native,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_open_v2(byte[] name,out IntPtr db,int flags,IntPtr vfs);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Native,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_close(IntPtr db);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Native,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_busy_timeout(IntPtr db,int ms);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Native,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_prepare_v2(IntPtr db,byte[] sql,int n,out IntPtr stmt,IntPtr tail);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Native,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_bind_text(IntPtr stmt,int index,byte[] value,int n,IntPtr destructor);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Native,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_step(IntPtr stmt);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Native,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_finalize(IntPtr stmt);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Native,CallingConvention=CallingConvention.Cdecl)]static extern IntPtr sqlite3_column_text(IntPtr stmt,int col);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Native,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_column_bytes(IntPtr stmt,int col);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Native,CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_column_count(IntPtr stmt);
    static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s+"\0"); }
    public RecordDb(string path,bool readOnly=false) {
        try {
            int result=sqlite3_open_v2(Utf8(path),out db,(readOnly?1:2|4)|0x10000,IntPtr.Zero);Check(result);
            Check(sqlite3_busy_timeout(db,5000));if(!readOnly)Execute("PRAGMA synchronous=FULL");
        }catch(DllNotFoundException) { Dispose();throw new UserError("集中记录需要 Windows 10/11 系统 SQLite 组件（winsqlite3.dll）。"); }
        catch { Dispose();throw; }
    }
    void Check(int code) { if(code!=0)throw new UserError("下载记录数据库操作失败（SQLite "+code+"）。请保留数据库与诊断日志，检查磁盘空间或文件占用后重试。"); }
    public List<string[]> Query(string sql,params string[] args) {
        IntPtr stmt=IntPtr.Zero;
        try {
            Check(sqlite3_prepare_v2(db,Utf8(sql),-1,out stmt,IntPtr.Zero));
            for(int i=0;i<args.Length;i++) { var bytes=Utf8(args[i]??"");Check(sqlite3_bind_text(stmt,i+1,bytes,bytes.Length-1,new IntPtr(-1))); }
            var rows=new List<string[]>();int result;
            while((result=sqlite3_step(stmt))==100) {
                var row=new string[sqlite3_column_count(stmt)];
                for(int i=0;i<row.Length;i++) { IntPtr p=sqlite3_column_text(stmt,i);int n=sqlite3_column_bytes(stmt,i);var bytes=new byte[n];if(n>0)Marshal.Copy(p,bytes,0,n);row[i]=Encoding.UTF8.GetString(bytes); }
                rows.Add(row);
            }
            if(result!=101)Check(result);return rows;
        }finally { if(stmt!=IntPtr.Zero)sqlite3_finalize(stmt); }
    }
    public void Execute(string sql,params string[] args) { Query(sql,args); }
    public void Transaction(Action action) {
        Execute("BEGIN IMMEDIATE");try { action();Execute("COMMIT"); }catch { try { Execute("ROLLBACK"); }catch {}throw; }
    }
    public void Initialize() {
        var version=Query("PRAGMA user_version")[0][0];if(version!="0" && version!="1" && version!="2")throw new UserError("下载记录数据库版本较新，请使用配套版本程序。");
        if(Query("PRAGMA quick_check")[0][0]!="ok")throw new UserError("下载记录数据库校验失败，已停止。请保留文件，不要删除后重试。");
        Transaction(()=> {
            Execute("CREATE TABLE IF NOT EXISTS media(post_id TEXT NOT NULL,asset_id TEXT NOT NULL,user_id TEXT,path TEXT COLLATE NOCASE NOT NULL,bytes INTEGER NOT NULL,sha256 TEXT NOT NULL,state TEXT NOT NULL,PRIMARY KEY(post_id,asset_id,path))");
            Execute("CREATE TABLE IF NOT EXISTS tasks(id TEXT PRIMARY KEY,path TEXT COLLATE NOCASE NOT NULL,started TEXT NOT NULL,state TEXT NOT NULL,summary TEXT NOT NULL)");
            Execute("CREATE TABLE IF NOT EXISTS posts(id TEXT PRIMARY KEY,user_id TEXT,user_name TEXT,published TEXT,metadata TEXT NOT NULL)");
            Execute("CREATE TABLE IF NOT EXISTS task_media(task_id TEXT NOT NULL,post_id TEXT NOT NULL,asset_id TEXT NOT NULL,result TEXT NOT NULL,path TEXT,PRIMARY KEY(task_id,post_id,asset_id))");
            Execute("CREATE TABLE IF NOT EXISTS task_posts(task_id TEXT NOT NULL,post_id TEXT NOT NULL,metadata TEXT NOT NULL,PRIMARY KEY(task_id,post_id))");
            Execute("CREATE TABLE IF NOT EXISTS legacy(path TEXT COLLATE NOCASE PRIMARY KEY,sha256 TEXT NOT NULL,content TEXT NOT NULL)");
            Execute("CREATE TABLE IF NOT EXISTS account_folders(user_id TEXT PRIMARY KEY,path TEXT COLLATE NOCASE UNIQUE NOT NULL)");
            Execute("CREATE TABLE IF NOT EXISTS folder_moves(old_path TEXT COLLATE NOCASE PRIMARY KEY,new_path TEXT NOT NULL,state TEXT NOT NULL)");
            Execute("PRAGMA user_version=2");
        });
    }
    public void Dispose() { if(db!=IntPtr.Zero) { sqlite3_close(db);db=IntPtr.Zero; } }
}
}
