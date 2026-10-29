// Salary app launcher + local server.
// Serves index.html on http://localhost:<port>/, stores data in data/salary.json,
// opens Microsoft Edge (or Chrome) in app mode, and exits when the last window closes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("給料計算")]
[assembly: AssemblyProduct("給料計算")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace SalaryApp
{
    static class Program
    {
        const string AppTag = "salary-app";
        const int FirstPort = 47653, LastPort = 47663;
        static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(150);
        static readonly TimeSpan EmptyGrace = TimeSpan.FromSeconds(15);
        static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(90);
        static readonly TimeSpan PageGrace = TimeSpan.FromSeconds(60);   // after serving index.html, wait for its first ping

        static string root, dataDir, dataFile, logFile;
        static readonly Dictionary<string, DateTime> clients = new Dictionary<string, DateTime>();
        static readonly object gate = new object();
        static bool everHadClient = false;
        static DateTime emptySince = DateTime.MinValue, pageServedAt = DateTime.MinValue, lastPingAt = DateTime.MinValue;

        static int Main(string[] args)
        {
            root = AppDomain.CurrentDomain.BaseDirectory;
            dataDir = Path.Combine(root, "data");
            dataFile = Path.Combine(dataDir, "salary.json");
            logFile = Path.Combine(dataDir, "app.log");
            Directory.CreateDirectory(Path.Combine(dataDir, "backup"));
            TrimLog();

            int wantedPort = 0; bool noBrowser = false, keepAlive = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--port" && i + 1 < args.Length) int.TryParse(args[++i], out wantedPort);
                else if (a == "--no-browser") noBrowser = true;
                else if (a == "--keep-alive") keepAlive = true;
            }

            // Another instance already running? Just open one more window.
            int lo = wantedPort > 0 ? wantedPort : FirstPort, hi = wantedPort > 0 ? wantedPort : LastPort;
            for (int p = lo; p <= hi; p++)
            {
                if (IsOurServer(p))
                {
                    Log("existing instance on port " + p);
                    if (!noBrowser) OpenBrowser(p);
                    return 0;
                }
            }

            HttpListener listener = null; int port = 0;
            for (int p = lo; p <= hi && listener == null; p++)
            {
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add("http://localhost:" + p + "/");
                    l.Start();
                    listener = l; port = p;
                }
                catch (Exception ex) { Log("port " + p + " unavailable: " + ex.Message); }
            }
            if (listener == null) { Log("no free port"); return 1; }
            Log("listening on " + port);

            BackupDaily();
            listener.BeginGetContext(OnRequest, listener);
            if (!noBrowser) OpenBrowser(port);

            DateTime started = DateTime.Now;
            while (true)
            {
                Thread.Sleep(1000);
                if (keepAlive) continue;
                bool exit = false;
                lock (gate)
                {
                    var now = DateTime.Now;
                    var stale = new List<string>();
                    foreach (var kv in clients) if (now - kv.Value > ClientTimeout) stale.Add(kv.Key);
                    foreach (var k in stale) { clients.Remove(k); Log("client timed out " + k); }
                    bool pageAwaitingPing = pageServedAt > lastPingAt && now - pageServedAt < PageGrace;
                    if (clients.Count > 0 || pageAwaitingPing) { if (clients.Count > 0) everHadClient = true; emptySince = DateTime.MinValue; }
                    else if (!everHadClient) { if (now - started > StartupGrace) exit = true; }
                    else if (emptySince == DateTime.MinValue) emptySince = now;
                    else if (now - emptySince > EmptyGrace) exit = true;
                }
                if (exit) break;
            }
            Log("no clients, exiting");
            try { listener.Stop(); } catch { }
            return 0;
        }

        static void OnRequest(IAsyncResult ar)
        {
            var listener = (HttpListener)ar.AsyncState;
            HttpListenerContext ctx = null;
            try { ctx = listener.EndGetContext(ar); } catch { return; }
            try { listener.BeginGetContext(OnRequest, listener); } catch { }
            try { Handle(ctx); }
            catch (Exception ex)
            {
                Log("request error: " + ex.Message);
                try { Send(ctx, 500, "text/plain", Encoding.UTF8.GetBytes("error")); } catch { }
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            string path = req.Url.AbsolutePath;
            bool post = req.HttpMethod == "POST";

            if (path == "/" || path == "/index.html") { lock (gate) pageServedAt = DateTime.Now; SendFile(ctx, Path.Combine(root, "index.html"), "text/html; charset=utf-8"); return; }
            if (path == "/icon.png") { SendFile(ctx, Path.Combine(root, "icon.png"), "image/png"); return; }
            if (path == "/icon.ico" || path == "/favicon.ico") { SendFile(ctx, Path.Combine(root, "icon.ico"), "image/x-icon"); return; }

            if (path == "/ping")
            {
                if (post) { string id = ReadBody(req).Trim(); if (id.Length > 0) lock (gate) { if (!clients.ContainsKey(id)) Log("client joined " + id); clients[id] = DateTime.Now; lastPingAt = DateTime.Now; } }
                Send(ctx, 200, "text/plain", Encoding.UTF8.GetBytes(AppTag)); return;
            }
            if (path == "/bye" && post)
            {
                string id = ReadBody(req).Trim();
                lock (gate) { if (clients.Remove(id)) Log("client left " + id); }
                Send(ctx, 200, "text/plain", Encoding.UTF8.GetBytes("ok")); return;
            }
            if (path == "/info")
            {
                var ser = new JavaScriptSerializer();
                var info = new Dictionary<string, object> { { "dataFile", dataFile }, { "port", req.Url.Port } };
                Send(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ser.Serialize(info))); return;
            }
            if (path == "/data")
            {
                if (post)
                {
                    string body = ReadBody(req);
                    var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    ser.DeserializeObject(body); // throws on invalid JSON -> 500, nothing written
                    WriteAtomic(dataFile, body);
                    Send(ctx, 200, "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}")); return;
                }
                if (File.Exists(dataFile)) SendFile(ctx, dataFile, "application/json; charset=utf-8");
                else Send(ctx, 200, "application/json", Encoding.UTF8.GetBytes("{}"));
                return;
            }
            Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("not found"));
        }

        static string ReadBody(HttpListenerRequest req)
        {
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) return sr.ReadToEnd();
        }

        static void SendFile(HttpListenerContext ctx, string file, string type)
        {
            if (!File.Exists(file)) { Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("not found")); return; }
            Send(ctx, 200, type, File.ReadAllBytes(file));
        }

        static void Send(HttpListenerContext ctx, int status, string type, byte[] body)
        {
            var res = ctx.Response;
            res.StatusCode = status;
            res.ContentType = type;
            res.Headers["Cache-Control"] = "no-store";
            res.ContentLength64 = body.Length;
            if (ctx.Request.HttpMethod != "HEAD") res.OutputStream.Write(body, 0, body.Length);
            res.Close();
        }

        static void WriteAtomic(string file, string text)
        {
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }

        static void BackupDaily()
        {
            try
            {
                if (!File.Exists(dataFile)) return;
                string dir = Path.Combine(dataDir, "backup");
                string dst = Path.Combine(dir, "salary_" + DateTime.Now.ToString("yyyy-MM-dd") + ".json");
                if (!File.Exists(dst)) File.Copy(dataFile, dst);
                var files = new List<string>(Directory.GetFiles(dir, "salary_*.json"));
                files.Sort(StringComparer.Ordinal);
                while (files.Count > 14) { File.Delete(files[0]); files.RemoveAt(0); }
            }
            catch (Exception ex) { Log("backup failed: " + ex.Message); }
        }

        static bool IsOurServer(int port)
        {
            try
            {
                var r = (HttpWebRequest)WebRequest.Create("http://localhost:" + port + "/ping");
                r.Timeout = 700; r.Proxy = null;
                using (var res = (HttpWebResponse)r.GetResponse())
                using (var sr = new StreamReader(res.GetResponseStream()))
                    return sr.ReadToEnd().Trim() == AppTag;
            }
            catch { return false; }
        }

        static void OpenBrowser(int port)
        {
            string url = "http://localhost:" + port + "/";
            string[] candidates = {
                Expand(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
                Expand(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"),
                Expand(@"%LocalAppData%\Microsoft\Edge\Application\msedge.exe"),
                Expand(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
                Expand(@"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"),
                Expand(@"%LocalAppData%\Google\Chrome\Application\chrome.exe")
            };
            foreach (string exe in candidates)
            {
                if (!File.Exists(exe)) continue;
                try
                {
                    var psi = new ProcessStartInfo(exe, "--app=" + url + " --window-size=1060,860") { UseShellExecute = false };
                    Process.Start(psi);
                    Log("opened " + exe);
                    return;
                }
                catch (Exception ex) { Log("browser launch failed: " + ex.Message); }
            }
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); Log("opened default browser"); }
            catch (Exception ex) { Log("default browser failed: " + ex.Message); }
        }

        static string Expand(string s) { return Environment.ExpandEnvironmentVariables(s); }

        static void TrimLog()
        {
            try { if (File.Exists(logFile) && new FileInfo(logFile).Length > 512 * 1024) File.Delete(logFile); } catch { }
        }

        static void Log(string msg)
        {
            try { File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8); } catch { }
        }
    }
}
