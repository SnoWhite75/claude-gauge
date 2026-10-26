// ClaudeGauge.cs - Claude usage cluster overlay (C# 5 compatible, builds with Windows' built-in csc.exe)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeGaugeApp
{
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 2;
        public const int WM_HOTKEY = 0x0312;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x20;
        public const int WS_EX_LAYERED = 0x80000;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const uint MOD_ALT = 1;
        public const uint MOD_CONTROL = 2;
        public const uint MOD_NOREPEAT = 0x4000;
    }

    // ------------------------------------------------------------------ settings
    class Settings
    {
        public int X = int.MinValue, Y = int.MinValue;
        public double Scale = 0.75;
        public int OpacityPct = 90;
        public bool TopMost = true, Locked = false;
        public bool ShowFive = true, ShowWeek = true, ShowApi = true;
        public int IntervalSec = 60;
        public string ApiKeyEnc = "";
        public string UpdateUrl = "https://raw.githubusercontent.com/SnoWhite75/claude-gauge/main/";
        public bool AutoUpdate = true;
        public int FailedVersion = 0;
        public string ExecMode = "terminal";
        public bool BgConfirmed = false;

        static string FilePath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeGauge");
            return Path.Combine(dir, "settings.ini");
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                string p = FilePath();
                if (File.Exists(p))
                {
                    foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        string k = line.Substring(0, i).Trim();
                        string v = line.Substring(i + 1).Trim();
                        switch (k)
                        {
                            case "x": int.TryParse(v, out s.X); break;
                            case "y": int.TryParse(v, out s.Y); break;
                            case "scale": double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out s.Scale); break;
                            case "opacity": int.TryParse(v, out s.OpacityPct); break;
                            case "topmost": s.TopMost = v == "1"; break;
                            case "locked": s.Locked = v == "1"; break;
                            case "five": s.ShowFive = v == "1"; break;
                            case "week": s.ShowWeek = v == "1"; break;
                            case "api": s.ShowApi = v == "1"; break;
                            case "interval": int.TryParse(v, out s.IntervalSec); break;
                            case "apikey": s.ApiKeyEnc = v; break;
                            case "updateurl": if (v.Length > 0) s.UpdateUrl = v; break;
                            case "autoupdate": s.AutoUpdate = v == "1"; break;
                            case "failedver": int.TryParse(v, out s.FailedVersion); break;
                            case "exec": s.ExecMode = v == "background" ? "background" : "terminal"; break;
                            case "bgok": s.BgConfirmed = v == "1"; break;
                        }
                    }
                }
            }
            catch { }
            if (s.Scale < 0.35 || s.Scale > 2.0) s.Scale = 0.75;
            if (s.OpacityPct < 20 || s.OpacityPct > 100) s.OpacityPct = 90;
            if (s.IntervalSec < 30) s.IntervalSec = 60;
            return s;
        }

        public void Save()
        {
            try
            {
                string p = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                List<string> l = new List<string>();
                l.Add("x=" + X);
                l.Add("y=" + Y);
                l.Add("scale=" + Scale.ToString("0.###", CultureInfo.InvariantCulture));
                l.Add("opacity=" + OpacityPct);
                l.Add("topmost=" + (TopMost ? "1" : "0"));
                l.Add("locked=" + (Locked ? "1" : "0"));
                l.Add("five=" + (ShowFive ? "1" : "0"));
                l.Add("week=" + (ShowWeek ? "1" : "0"));
                l.Add("api=" + (ShowApi ? "1" : "0"));
                l.Add("interval=" + IntervalSec);
                l.Add("apikey=" + ApiKeyEnc);
                l.Add("updateurl=" + UpdateUrl);
                l.Add("autoupdate=" + (AutoUpdate ? "1" : "0"));
                l.Add("failedver=" + FailedVersion);
                l.Add("exec=" + ExecMode);
                l.Add("bgok=" + (BgConfirmed ? "1" : "0"));
                File.WriteAllLines(p, l.ToArray(), Encoding.UTF8);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ data
    class SubResult
    {
        public bool Ok;
        public string Msg = "";
        public double Five = -1, Week = -1;
        public DateTime FiveReset = DateTime.MinValue, WeekReset = DateTime.MinValue;
        public string Plan = "";
        public bool Backoff;
    }

    class ApiResult
    {
        public bool Ok;
        public string Msg = "";
        public long ReqRem = -1, ReqLim = -1, TokRem = -1, TokLim = -1;
    }

    class Http
    {
        public int Status;
        public string Body = "";
        public WebHeaderCollection Headers;
        public string Error;
    }

    // minimal JSON parser (objects -> Dictionary, arrays -> List, numbers -> double)
    static class MiniJson
    {
        public static object Parse(string s)
        {
            int i = 0;
            return Val(s, ref i);
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object Val(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("eof");
            char c = s[i];
            if (c == '{')
            {
                i++;
                Dictionary<string, object> d = new Dictionary<string, object>();
                Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException("colon");
                    i++;
                    d[k] = Val(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("object");
                }
            }
            if (c == '[')
            {
                i++;
                List<object> l = new List<object>();
                Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Val(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException("array");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st0 = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == st0) throw new FormatException("token");
            return double.Parse(s.Substring(st0, i - st0), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("string");
            i++;
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\')
                {
                    char n = s[i++];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                            break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    static class Fetcher
    {
        public static Http Send(string method, string url, Dictionary<string, string> headers, string body)
        {
            Http res = new Http();
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.UserAgent = "ClaudeGauge/1.0";
                req.Accept = "application/json";
                foreach (KeyValuePair<string, string> kv in headers) req.Headers[kv.Key] = kv.Value;
                if (body != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    req.ContentType = "application/json";
                    req.ContentLength = data.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse()) Fill(res, resp);
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null) { using (resp) { Fill(res, resp); } }
                else res.Error = ex.Message;
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
            }
            return res;
        }

        static void Fill(Http res, HttpWebResponse resp)
        {
            res.Status = (int)resp.StatusCode;
            res.Headers = resp.Headers;
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) res.Body = sr.ReadToEnd();
        }

        // ---- subscription usage (Claude Code OAuth login on this PC) ----
        public static string ConfigDir()
        {
            string d = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrEmpty(d))
                d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return d;
        }

        static double ReadUtil(Dictionary<string, object> root, string key, out DateTime reset)
        {
            reset = DateTime.MinValue;
            object o;
            if (!root.TryGetValue(key, out o) || o == null) return -1;
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) return -1;
            double val = -1;
            object u;
            if (d.TryGetValue("utilization", out u) && u != null)
                val = Convert.ToDouble(u, CultureInfo.InvariantCulture);
            object r;
            if (d.TryGetValue("resets_at", out r) && r != null)
            {
                DateTime t;
                if (DateTime.TryParse(r.ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                    reset = t;
            }
            return val;
        }

        static SubResult Fail(string msg) { SubResult r = new SubResult(); r.Msg = msg; return r; }

        public static SubResult FetchSub()
        {
            string token = null, plan = "";
            try
            {
                string path = Path.Combine(ConfigDir(), ".credentials.json");
                if (!File.Exists(path))
                    return Fail("Claude Code 로그인 정보가 없어요. 이 PC에서 Claude Code로 로그인해 주세요");
                string json;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    json = sr.ReadToEnd();
                Dictionary<string, object> root = MiniJson.Parse(json) as Dictionary<string, object>;
                Dictionary<string, object> oauth = null;
                if (root != null && root.ContainsKey("claudeAiOauth")) oauth = root["claudeAiOauth"] as Dictionary<string, object>;
                if (oauth == null || !oauth.ContainsKey("accessToken") || oauth["accessToken"] == null)
                    return Fail("로그인 정보에 토큰이 없어요. Claude Code에서 다시 로그인해 주세요");
                token = oauth["accessToken"].ToString();
                if (oauth.ContainsKey("subscriptionType") && oauth["subscriptionType"] != null)
                    plan = oauth["subscriptionType"].ToString().ToUpperInvariant();
                if (oauth.ContainsKey("rateLimitTier") && oauth["rateLimitTier"] != null)
                {
                    string tier = oauth["rateLimitTier"].ToString();
                    if (tier.Contains("20x")) plan += " 20x";
                    else if (tier.Contains("5x")) plan += " 5x";
                }
                if (oauth.ContainsKey("expiresAt") && oauth["expiresAt"] != null)
                {
                    long exp = Convert.ToInt64(oauth["expiresAt"], CultureInfo.InvariantCulture);
                    double nowMs = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                    if (exp > 0 && nowMs >= exp)
                    {
                        SubResult e = Fail("로그인 토큰이 만료됐어요. Claude Code를 한 번 실행하면 갱신돼요");
                        e.Plan = plan;
                        return e;
                    }
                }
            }
            catch (Exception ex)
            {
                return Fail("로그인 정보를 읽지 못했어요: " + Short(ex.Message));
            }

            Dictionary<string, string> h = new Dictionary<string, string>();
            h["Authorization"] = "Bearer " + token;
            h["anthropic-beta"] = "oauth-2025-04-20";
            Http r = Send("GET", "https://api.anthropic.com/api/oauth/usage", h, null);
            if (r.Error != null) { SubResult e = Fail("네트워크 오류: " + Short(r.Error)); e.Plan = plan; return e; }
            if (r.Status == 200)
            {
                try
                {
                    Dictionary<string, object> root = MiniJson.Parse(r.Body) as Dictionary<string, object>;
                    SubResult ok = new SubResult();
                    ok.Ok = true;
                    ok.Plan = plan;
                    ok.Five = ReadUtil(root, "five_hour", out ok.FiveReset);
                    ok.Week = ReadUtil(root, "seven_day", out ok.WeekReset);
                    return ok;
                }
                catch (Exception ex)
                {
                    SubResult e = Fail("응답을 해석하지 못했어요: " + Short(ex.Message)); e.Plan = plan; return e;
                }
            }
            SubResult f;
            if (r.Status == 401) f = Fail("로그인이 만료됐어요. Claude Code를 실행해 주세요");
            else if (r.Status == 403) f = Fail("권한이 부족해요. 터미널에서 claude auth login 으로 다시 로그인해 주세요");
            else if (r.Status == 429) { f = Fail("요청이 많아 잠시 쉬는 중이에요"); f.Backoff = true; }
            else f = Fail("서버 응답 오류 (" + r.Status + ")");
            f.Plan = plan;
            return f;
        }

        static string Short(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 60 ? s.Substring(0, 60) + "..." : s;
        }

        // ---- API rate limit (optional, needs an API key) ----
        static long HeaderLong(WebHeaderCollection h, string name)
        {
            if (h == null) return -1;
            string v = h[name];
            long n;
            if (v != null && long.TryParse(v, out n)) return n;
            return -1;
        }

        public static ApiResult FetchApi(string key)
        {
            ApiResult a = new ApiResult();
            Dictionary<string, string> h = new Dictionary<string, string>();
            h["x-api-key"] = key;
            h["anthropic-version"] = "2023-06-01";
            string body = "{\"model\":\"claude-haiku-5-5\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\".\"}]}";
            Http r = Send("POST", "https://api.anthropic.com/v1/messages", h, body);
            if (r.Error != null) { a.Msg = "네트워크 오류: " + Short(r.Error); return a; }
            if (r.Status == 401 || r.Status == 403) { a.Msg = "API 키가 올바르지 않아요"; return a; }
            if (r.Headers == null) { a.Msg = "응답 오류 (" + r.Status + ")"; return a; }
            a.ReqRem = HeaderLong(r.Headers, "anthropic-ratelimit-requests-remaining");
            a.ReqLim = HeaderLong(r.Headers, "anthropic-ratelimit-requests-limit");
            a.TokRem = HeaderLong(r.Headers, "anthropic-ratelimit-tokens-remaining");
            a.TokLim = HeaderLong(r.Headers, "anthropic-ratelimit-tokens-limit");
            if (a.TokLim < 0)
            {
                a.TokRem = HeaderLong(r.Headers, "anthropic-ratelimit-input-tokens-remaining");
                a.TokLim = HeaderLong(r.Headers, "anthropic-ratelimit-input-tokens-limit");
            }
            if (a.ReqLim < 0 && a.TokLim < 0) { a.Msg = "한도 정보를 받지 못했어요 (" + r.Status + ")"; return a; }
            a.Ok = true;
            return a;
        }
    }

    // ------------------------------------------------------------------ app info / paths
    static class AppInfo
    {
        public const int AppVersion = 2;

        public static string DataDir()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeGauge");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        public static string AppDir() { return Path.GetDirectoryName(Application.ExecutablePath); }
    }

    // ------------------------------------------------------------------ self update
    static class Updater
    {
        public static string NormalizeUrl(string u)
        {
            if (u == null) return "";
            u = u.Trim();
            if (u.Length == 0) return "";
            string gh = "https://github.com/";
            if (u.StartsWith(gh, StringComparison.OrdinalIgnoreCase))
            {
                string rest = u.Substring(gh.Length).Trim('/');
                string[] parts = rest.Split('/');
                if (parts.Length >= 2)
                {
                    string repo = parts[1];
                    if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo.Substring(0, repo.Length - 4);
                    return "https://raw.githubusercontent.com/" + parts[0] + "/" + repo + "/main/";
                }
            }
            if (!u.EndsWith("/")) u += "/";
            return u;
        }

        static void Log(string s)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppInfo.DataDir(), "update.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + s + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        static string FindCsc()
        {
            string win = Environment.GetEnvironmentVariable("WINDIR");
            if (string.IsNullOrEmpty(win)) win = @"C:\Windows";
            string a = Path.Combine(win, @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
            if (File.Exists(a)) return a;
            string b = Path.Combine(win, @"Microsoft.NET\Framework\v4.0.30319\csc.exe");
            if (File.Exists(b)) return b;
            return null;
        }

        // 0 = up to date, 1 = installed (restart needed), -1 = temporary failure, -2 = this version failed
        public static int CheckAndApply(string baseUrl, int failedVersion, out int newVersion, out string msg)
        {
            newVersion = 0;
            msg = "";
            string b = NormalizeUrl(baseUrl);
            if (b.Length == 0) { msg = "업데이트 주소가 없어요"; return -1; }
            if (!b.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { msg = "주소는 https:// 로 시작해야 해요"; return -1; }

            Dictionary<string, string> h = new Dictionary<string, string>();
            Http v = Fetcher.Send("GET", b + "version.txt", h, null);
            if (v.Error != null) { msg = "업데이트 서버에 연결하지 못했어요"; return -1; }
            if (v.Status != 200) { msg = "version.txt 를 찾지 못했어요 (" + v.Status + ")"; return -1; }
            string first = v.Body.Trim();
            int nl = first.IndexOfAny(new char[] { '\r', '\n' });
            if (nl > 0) first = first.Substring(0, nl).Trim();
            int ver;
            if (!int.TryParse(first, out ver)) { msg = "version.txt 형식이 올바르지 않아요"; return -1; }
            newVersion = ver;
            if (ver <= AppInfo.AppVersion) return 0;
            if (ver == failedVersion) { msg = "v" + ver + " 설치에 실패한 적이 있어 건너뛰어요"; return 0; }

            Http s = Fetcher.Send("GET", b + "ClaudeGauge.cs", h, null);
            if (s.Error != null || s.Status != 200) { msg = "새 버전 파일을 받지 못했어요"; return -1; }
            string src = s.Body;
            if (src.Length < 20000 || src.Length > 3000000 ||
                src.IndexOf("namespace ClaudeGaugeApp", StringComparison.Ordinal) < 0)
            { msg = "받은 파일이 올바르지 않아요"; return -2; }
            Match m = Regex.Match(src, @"AppVersion\s*=\s*(\d+)\s*;");
            if (!m.Success || m.Groups[1].Value != ver.ToString())
            { msg = "받은 파일의 버전이 version.txt 와 달라요"; return -2; }

            string csc = FindCsc();
            if (csc == null) { msg = ".NET 컴파일러를 찾지 못했어요"; return -2; }
            string work = Path.Combine(AppInfo.DataDir(), "update");
            try { Directory.CreateDirectory(work); } catch { }
            string srcPath = Path.Combine(work, "ClaudeGauge.cs");
            string outPath = Path.Combine(work, "ClaudeGauge.new.exe");
            try
            {
                if (File.Exists(outPath)) File.Delete(outPath);
                File.WriteAllText(srcPath, src, new UTF8Encoding(true));
            }
            catch (Exception ex1) { msg = "임시 파일을 쓰지 못했어요"; Log(ex1.ToString()); return -1; }

            string ico = Path.Combine(AppInfo.AppDir(), "ClaudeGauge.ico");
            string args = "/nologo /target:winexe /optimize+ /codepage:65001 " +
                          (File.Exists(ico) ? "/win32icon:\"" + ico + "\" " : "") +
                          "/out:\"" + outPath + "\" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Security.dll \"" + srcPath + "\"";
            string output = "";
            int code = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(csc, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(120000))
                    {
                        try { p.Kill(); } catch { }
                        msg = "빌드가 너무 오래 걸려서 멈췄어요";
                        return -1;
                    }
                    code = p.ExitCode;
                }
            }
            catch (Exception ex2) { msg = "빌드를 시작하지 못했어요"; Log(ex2.ToString()); return -1; }
            if (code != 0 || !File.Exists(outPath))
            {
                Log("build failed for v" + ver + ":\r\n" + output);
                msg = "새 버전 빌드에 실패했어요 (update.log 참고)";
                return -2;
            }

            string exe = Application.ExecutablePath;
            string oldPath = Path.Combine(AppInfo.AppDir(), "ClaudeGauge.old.exe");
            try
            {
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(exe, oldPath);
            }
            catch (Exception ex3) { Log(ex3.ToString()); msg = "실행 파일을 교체하지 못했어요"; return -1; }
            try
            {
                File.Move(outPath, exe);
            }
            catch (Exception ex4)
            {
                Log(ex4.ToString());
                try { File.Move(oldPath, exe); } catch { }
                msg = "새 실행 파일을 넣지 못했어요";
                return -1;
            }
            try { File.Copy(srcPath, Path.Combine(AppInfo.AppDir(), "ClaudeGauge.cs"), true); } catch { }
            Log("updated to v" + ver);
            msg = "v" + ver + " 설치 완료";
            return 1;
        }
    }

    // ------------------------------------------------------------------ task list data
    class TaskItem
    {
        public string Text = "";
        public string Status = "pending";
        public string SessionId = "";
        public bool Manual;
        public string Key() { return (Manual ? "m|" : "s|") + SessionId + "|" + Text; }
    }

    class SessionInfo
    {
        public string Id = "", Title = "";
        public DateTime Time = DateTime.MinValue;
        public List<TaskItem> Tasks = new List<TaskItem>();
    }

    class ProjectInfo
    {
        public string Dir = "", Name = "";
        public DateTime Time = DateTime.MinValue;
        public List<SessionInfo> Sessions = new List<SessionInfo>();
        public List<TaskItem> Manual = new List<TaskItem>();
        public int Count()
        {
            int n = Manual.Count;
            foreach (SessionInfo si in Sessions) n += si.Tasks.Count;
            return n;
        }
    }

    class TaskRef
    {
        public TaskItem Item;
        public ProjectInfo Project;
    }

    class ParsedFile
    {
        public string Cwd = "", Title = "", Id = "";
        public DateTime Time = DateTime.MinValue;
        public List<TaskItem> Items = new List<TaskItem>();
    }

    static class TaskStore
    {
        public static List<KeyValuePair<string, string>> Manual = new List<KeyValuePair<string, string>>();
        public static Dictionary<string, bool> Dismissed = new Dictionary<string, bool>();

        static string FilePath() { return Path.Combine(AppInfo.DataDir(), "tasks.txt"); }

        public static string Clean(string s)
        {
            if (s == null) return "";
            return s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        public static void Load()
        {
            Manual.Clear();
            Dismissed.Clear();
            try
            {
                string p = FilePath();
                if (!File.Exists(p)) return;
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    if (f.Length >= 3 && f[0] == "M") Manual.Add(new KeyValuePair<string, string>(f[1], f[2]));
                    else if (f.Length >= 2 && f[0] == "D") Dismissed[f[1]] = true;
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                List<string> l = new List<string>();
                foreach (KeyValuePair<string, string> m in Manual) l.Add("M\t" + Clean(m.Key) + "\t" + Clean(m.Value));
                foreach (KeyValuePair<string, bool> d in Dismissed) l.Add("D\t" + Clean(d.Key));
                File.WriteAllLines(FilePath(), l.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        public static void AddManual(string dir, string text)
        {
            Manual.Add(new KeyValuePair<string, string>(dir, Clean(text)));
            Save();
        }

        public static void RemoveManual(string dir, string text)
        {
            for (int i = Manual.Count - 1; i >= 0; i--)
            {
                if (Manual[i].Key == dir && Manual[i].Value == text) { Manual.RemoveAt(i); break; }
            }
            Save();
        }
    }

    // Reads Claude Code session transcripts (~/.claude/projects/*/*.jsonl) and collects unfinished to-dos.
    static class TaskScanner
    {
        static object gate = new object();
        static Dictionary<string, string> sigs = new Dictionary<string, string>();
        static Dictionary<string, ParsedFile> cache = new Dictionary<string, ParsedFile>();

        static object Get(Dictionary<string, object> d, string k)
        {
            object o;
            if (d != null && d.TryGetValue(k, out o)) return o;
            return null;
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object o = Get(d, k);
            return o == null ? "" : o.ToString();
        }

        static string Trunc(string s, int n) { return s.Length > n ? s.Substring(0, n) + "..." : s; }

        static string ExtractJsonString(string line, string key)
        {
            string pat = "\"" + key + "\":\"";
            int i = line.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return "";
            i += pat.Length;
            StringBuilder sb = new StringBuilder();
            while (i < line.Length)
            {
                char c = line[i++];
                if (c == '"') break;
                if (c == '\\' && i < line.Length)
                {
                    char n = line[i++];
                    if (n == 'n' || n == 't' || n == 'r') sb.Append(' ');
                    else if (n == 'u' && i + 4 <= line.Length)
                    {
                        try { sb.Append((char)Convert.ToInt32(line.Substring(i, 4), 16)); } catch { }
                        i += 4;
                    }
                    else sb.Append(n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static string PickTitle(object content)
        {
            List<string> texts = new List<string>();
            string s = content as string;
            if (s != null) texts.Add(s);
            List<object> l = content as List<object>;
            if (l != null)
            {
                foreach (object o in l)
                {
                    Dictionary<string, object> b = o as Dictionary<string, object>;
                    if (b != null && Str(b, "type") == "text") texts.Add(Str(b, "text"));
                }
            }
            foreach (string t0 in texts)
            {
                string t = TaskStore.Clean(t0);
                if (t.Length == 0) continue;
                if (t.StartsWith("<") || t.StartsWith("Caveat:") || t.StartsWith("[Request")) continue;
                return Trunc(t, 48);
            }
            return "";
        }

        static List<TaskItem> ParseTodos(string line)
        {
            Dictionary<string, object> d = MiniJson.Parse(line) as Dictionary<string, object>;
            Dictionary<string, object> msg = Get(d, "message") as Dictionary<string, object>;
            List<object> content = Get(msg, "content") as List<object>;
            if (content == null) return null;
            foreach (object o in content)
            {
                Dictionary<string, object> b = o as Dictionary<string, object>;
                if (b == null) continue;
                if (Str(b, "type") != "tool_use" || Str(b, "name") != "TodoWrite") continue;
                Dictionary<string, object> input = Get(b, "input") as Dictionary<string, object>;
                List<object> todos = Get(input, "todos") as List<object>;
                if (todos == null) continue;
                List<TaskItem> res = new List<TaskItem>();
                foreach (object t in todos)
                {
                    Dictionary<string, object> td = t as Dictionary<string, object>;
                    if (td == null) continue;
                    string text = TaskStore.Clean(Str(td, "content"));
                    if (text.Length == 0) text = TaskStore.Clean(Str(td, "activeForm"));
                    string status = Str(td, "status");
                    if (text.Length == 0 || status == "completed") continue;
                    TaskItem ti = new TaskItem();
                    ti.Text = text;
                    ti.Status = status.Length > 0 ? status : "pending";
                    res.Add(ti);
                }
                return res;
            }
            return null;
        }

        static ParsedFile Parse(FileInfo fi)
        {
            ParsedFile pf = new ParsedFile();
            pf.Id = Path.GetFileNameWithoutExtension(fi.Name);
            pf.Time = fi.LastWriteTime;
            List<TaskItem> todos = null;
            Dictionary<string, TaskItem> tasks = new Dictionary<string, TaskItem>();
            List<string> order = new List<string>();
            string lastPrompt = "";
            int titleTries = 0;

            using (FileStream fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length < 20) continue;
                    if (pf.Cwd.Length == 0) pf.Cwd = ExtractJsonString(line, "cwd");
                    if (line.StartsWith("{\"type\":\"last-prompt\"", StringComparison.Ordinal))
                    {
                        lastPrompt = ExtractJsonString(line, "lastPrompt");
                        continue;
                    }
                    if (line.IndexOf("\"isSidechain\":true", StringComparison.Ordinal) >= 0) continue;

                    if (pf.Title.Length == 0 && titleTries < 40 && line.StartsWith("{\"type\":\"user\"", StringComparison.Ordinal))
                    {
                        titleTries++;
                        try
                        {
                            Dictionary<string, object> ud = MiniJson.Parse(line) as Dictionary<string, object>;
                            Dictionary<string, object> um = Get(ud, "message") as Dictionary<string, object>;
                            pf.Title = PickTitle(Get(um, "content"));
                        }
                        catch { }
                    }

                    if (line.IndexOf("\"TodoWrite\"", StringComparison.Ordinal) >= 0)
                    {
                        try
                        {
                            List<TaskItem> t = ParseTodos(line);
                            if (t != null) todos = t;
                        }
                        catch { }
                    }

                    bool created = line.IndexOf("\"toolUseResult\":{\"task\":{", StringComparison.Ordinal) >= 0;
                    bool changed = line.IndexOf("\"statusChange\"", StringComparison.Ordinal) >= 0;
                    if (created || changed)
                    {
                        try
                        {
                            Dictionary<string, object> rd = MiniJson.Parse(line) as Dictionary<string, object>;
                            Dictionary<string, object> tur = Get(rd, "toolUseResult") as Dictionary<string, object>;
                            if (tur != null)
                            {
                                Dictionary<string, object> task = Get(tur, "task") as Dictionary<string, object>;
                                if (task != null && Str(task, "id").Length > 0)
                                {
                                    string id = Str(task, "id");
                                    TaskItem ti;
                                    if (!tasks.TryGetValue(id, out ti))
                                    {
                                        ti = new TaskItem();
                                        tasks[id] = ti;
                                        order.Add(id);
                                    }
                                    string subj = TaskStore.Clean(Str(task, "subject"));
                                    if (subj.Length > 0) ti.Text = subj;
                                    string st0 = Str(task, "status");
                                    if (st0.Length > 0) ti.Status = st0;
                                }
                                Dictionary<string, object> sc = Get(tur, "statusChange") as Dictionary<string, object>;
                                string tid = Str(tur, "taskId");
                                if (sc != null && tid.Length > 0)
                                {
                                    TaskItem t2;
                                    if (tasks.TryGetValue(tid, out t2))
                                    {
                                        string to = Str(sc, "to");
                                        if (to.Length > 0) t2.Status = to;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            if (pf.Title.Length == 0) pf.Title = lastPrompt.Length > 0 ? Trunc(TaskStore.Clean(lastPrompt), 48) : "(제목 없음)";
            if (todos != null) foreach (TaskItem t in todos) pf.Items.Add(t);
            foreach (string id in order)
            {
                TaskItem ti2 = tasks[id];
                if (ti2.Text.Length == 0) continue;
                if (ti2.Status == "pending" || ti2.Status == "in_progress") pf.Items.Add(ti2);
            }
            return pf;
        }

        static ParsedFile GetParsed(FileInfo fi)
        {
            string sig = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            string old;
            if (sigs.TryGetValue(fi.FullName, out old) && old == sig) return cache[fi.FullName];
            try
            {
                ParsedFile pf = Parse(fi);
                sigs[fi.FullName] = sig;
                cache[fi.FullName] = pf;
                return pf;
            }
            catch { return null; }
        }

        static ProjectInfo NewProject(string cwd)
        {
            ProjectInfo pi = new ProjectInfo();
            pi.Dir = cwd;
            string t = cwd.TrimEnd('\\', '/');
            int i = Math.Max(t.LastIndexOf('\\'), t.LastIndexOf('/'));
            pi.Name = (i >= 0 && i < t.Length - 1) ? t.Substring(i + 1) : t;
            if (pi.Name.Length == 0) pi.Name = cwd;
            return pi;
        }

        public static List<ProjectInfo> Scan()
        {
            lock (gate)
            {
                List<FileInfo> files = new List<FileInfo>();
                string root = Path.Combine(Fetcher.ConfigDir(), "projects");
                if (Directory.Exists(root))
                {
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        string[] names;
                        try { names = Directory.GetFiles(dir, "*.jsonl"); }
                        catch { continue; }
                        foreach (string name in names)
                        {
                            try
                            {
                                FileInfo info = new FileInfo(name);
                                if (info.Name.StartsWith("agent-", StringComparison.OrdinalIgnoreCase)) continue;
                                if ((DateTime.Now - info.LastWriteTime).TotalDays > 30) continue;
                                if (info.Length > 300L * 1024 * 1024) continue;
                                files.Add(info);
                            }
                            catch { }
                        }
                    }
                }
                files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
                if (files.Count > 150) files.RemoveRange(150, files.Count - 150);

                Dictionary<string, ProjectInfo> map = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (FileInfo fi in files)
                {
                    ParsedFile pf = GetParsed(fi);
                    if (pf == null || pf.Cwd.Length == 0) continue;
                    SessionInfo si = new SessionInfo();
                    si.Id = pf.Id;
                    si.Title = pf.Title;
                    si.Time = pf.Time;
                    foreach (TaskItem t in pf.Items)
                    {
                        TaskItem c = new TaskItem();
                        c.Text = t.Text;
                        c.Status = t.Status;
                        c.SessionId = pf.Id;
                        if (!TaskStore.Dismissed.ContainsKey(c.Key())) si.Tasks.Add(c);
                    }
                    if (si.Tasks.Count == 0) continue;
                    ProjectInfo pi;
                    if (!map.TryGetValue(pf.Cwd, out pi))
                    {
                        pi = NewProject(pf.Cwd);
                        map[pf.Cwd] = pi;
                    }
                    pi.Sessions.Add(si);
                    if (si.Time > pi.Time) pi.Time = si.Time;
                }
                foreach (KeyValuePair<string, string> m in TaskStore.Manual)
                {
                    ProjectInfo pj;
                    if (!map.TryGetValue(m.Key, out pj))
                    {
                        pj = NewProject(m.Key);
                        map[m.Key] = pj;
                    }
                    TaskItem mi = new TaskItem();
                    mi.Text = m.Value;
                    mi.Manual = true;
                    pj.Manual.Add(mi);
                }
                List<ProjectInfo> list = new List<ProjectInfo>(map.Values);
                list.Sort(delegate(ProjectInfo a, ProjectInfo b) { return b.Time.CompareTo(a.Time); });
                foreach (ProjectInfo p in list)
                    p.Sessions.Sort(delegate(SessionInfo a, SessionInfo b) { return b.Time.CompareTo(a.Time); });
                return list;
            }
        }
    }

    // ------------------------------------------------------------------ run Claude Code
    static class Runner
    {
        public static string FindClaude()
        {
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string c1 = Path.Combine(up, @".local\bin\claude.exe");
            if (File.Exists(c1)) return c1;
            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string dir in path.Split(';'))
                {
                    string d = dir.Trim();
                    if (d.Length == 0) continue;
                    try
                    {
                        string e = Path.Combine(d, "claude.exe");
                        if (File.Exists(e)) return e;
                        string c = Path.Combine(d, "claude.cmd");
                        if (File.Exists(c)) return c;
                    }
                    catch { }
                }
            }
            return null;
        }

        static string Q(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in s)
            {
                if (c == '\\') { bs++; }
                else if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; }
                else { sb.Append('\\', bs); bs = 0; sb.Append(c); }
            }
            sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }

        static string Clean(string s) { return s.Replace("\r", " ").Replace("\n", " ").Trim(); }

        static ProcessStartInfo Make(string claude, string args, string workDir)
        {
            ProcessStartInfo psi;
            if (claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                psi = new ProcessStartInfo("cmd.exe", "/s /c \"" + Q(claude) + " " + args + "\"");
            else
                psi = new ProcessStartInfo(claude, args);
            psi.WorkingDirectory = workDir;
            psi.UseShellExecute = false;
            return psi;
        }

        // opens a console window running Claude Code in the project folder
        public static string RunTerminal(string claude, string workDir, string sessionId, string prompt)
        {
            string args = (sessionId.Length > 0 ? "-r " + Q(sessionId) + " " : "") + Q(Clean(prompt));
            try
            {
                Process.Start(Make(claude, args, workDir));
                return null;
            }
            catch (Exception ex) { return "실행하지 못했어요: " + ex.Message; }
        }

        // runs Claude Code without a window; file edits are auto-approved, results go to a log file
        public static string RunBackground(string claude, string workDir, string sessionId, string prompt, string label, GaugeForm owner)
        {
            string runs = Path.Combine(AppInfo.DataDir(), "runs");
            try { Directory.CreateDirectory(runs); } catch { }
            string log = Path.Combine(runs, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            string args = (sessionId.Length > 0 ? "-r " + Q(sessionId) + " " : "") +
                          "--permission-mode acceptEdits -p " + Q(Clean(prompt));
            try
            {
                ProcessStartInfo psi = Make(claude, args, workDir);
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                StreamWriter w = new StreamWriter(log, false, new UTF8Encoding(true));
                w.WriteLine("작업: " + label);
                w.WriteLine("폴더: " + workDir);
                w.WriteLine("----");
                w.Flush();
                object gate = new object();
                Process p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;
                DataReceivedEventHandler onData = delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (gate) { try { w.WriteLine(e.Data); w.Flush(); } catch { } }
                };
                p.OutputDataReceived += onData;
                p.ErrorDataReceived += onData;
                p.Exited += delegate
                {
                    try { p.WaitForExit(); } catch { }
                    int code = -1;
                    try { code = p.ExitCode; } catch { }
                    lock (gate)
                    {
                        try { w.WriteLine("----"); w.WriteLine("종료 코드: " + code); w.Close(); } catch { }
                    }
                    owner.NotifyRunDone(label, code, log);
                };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                return null;
            }
            catch (Exception ex) { return "실행하지 못했어요: " + ex.Message; }
        }
    }

    // ------------------------------------------------------------------ task list window
    class TaskForm : Form
    {
        GaugeForm owner;
        Settings st;
        TreeView tree;
        Button btnRun, btnAdd, btnDel, btnRefresh;
        ComboBox cmbMode;
        Label lblStatus;
        List<ProjectInfo> data = new List<ProjectInfo>();

        static Color Rgb(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        Button MakeBtn(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.Left = x; b.Top = y; b.Width = w; b.Height = 30;
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Rgb(30, 42, 54);
            b.ForeColor = Rgb(224, 234, 242);
            b.FlatAppearance.BorderColor = Rgb(58, 76, 94);
            return b;
        }

        public TaskForm(GaugeForm owner, Settings st)
        {
            this.owner = owner;
            this.st = st;
            Text = "Claude 할 일 목록";
            Font = new Font("Malgun Gothic", 9.5f);
            BackColor = Rgb(17, 24, 32);
            ForeColor = Rgb(220, 230, 238);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(580, 660);
            MinimumSize = new Size(480, 420);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 44;
            Label title = new Label();
            title.Text = "프로젝트별 남은 할 일";
            title.Font = new Font("Malgun Gothic", 12f, FontStyle.Bold);
            title.ForeColor = Rgb(224, 122, 84);
            title.AutoSize = true;
            title.Left = 14; title.Top = 10;
            top.Controls.Add(title);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 100;
            btnRun = MakeBtn("실행", 12, 10, 100);
            btnAdd = MakeBtn("할 일 추가", 120, 10, 110);
            btnDel = MakeBtn("삭제", 238, 10, 90);
            btnRefresh = MakeBtn("새로고침", 336, 10, 100);
            cmbMode = new ComboBox();
            cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMode.Left = 12; cmbMode.Top = 48; cmbMode.Width = 424;
            cmbMode.Items.Add("터미널 창에서 실행 (권한은 내가 직접 승인)");
            cmbMode.Items.Add("백그라운드 자동 실행 (파일 수정만 자동 허용)");
            cmbMode.SelectedIndex = st.ExecMode == "background" ? 1 : 0;
            lblStatus = new Label();
            lblStatus.Left = 12; lblStatus.Top = 78;
            lblStatus.AutoSize = true;
            lblStatus.ForeColor = Rgb(130, 148, 162);
            lblStatus.Text = "";
            bottom.Controls.Add(btnRun);
            bottom.Controls.Add(btnAdd);
            bottom.Controls.Add(btnDel);
            bottom.Controls.Add(btnRefresh);
            bottom.Controls.Add(cmbMode);
            bottom.Controls.Add(lblStatus);

            tree = new TreeView();
            tree.Dock = DockStyle.Fill;
            tree.BorderStyle = BorderStyle.None;
            tree.BackColor = Rgb(10, 15, 20);
            tree.ForeColor = Rgb(220, 230, 238);
            tree.Font = new Font("Malgun Gothic", 10f);
            tree.ItemHeight = 26;
            tree.ShowLines = false;
            tree.FullRowSelect = true;
            tree.HideSelection = false;
            tree.ShowNodeToolTips = true;

            Controls.Add(tree);
            Controls.Add(top);
            Controls.Add(bottom);

            btnRun.Click += delegate { RunSelected(); };
            btnAdd.Click += delegate { AddTask(); };
            btnDel.Click += delegate { DeleteSelected(); };
            btnRefresh.Click += delegate { Reload(); };
            tree.AfterSelect += delegate { UpdateButtons(); };
            tree.NodeMouseDoubleClick += delegate(object sender, TreeNodeMouseClickEventArgs e)
            {
                if (e.Node != null && e.Node.Tag is TaskRef) RunSelected();
            };
            cmbMode.SelectedIndexChanged += delegate { ChangeMode(); };
            UpdateButtons();
        }

        TaskRef Selected()
        {
            return tree.SelectedNode == null ? null : tree.SelectedNode.Tag as TaskRef;
        }

        void UpdateButtons()
        {
            bool on = Selected() != null;
            btnRun.Enabled = on;
            btnDel.Enabled = on;
        }

        static string Ago(DateTime t)
        {
            TimeSpan d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "방금";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "분 전";
            if (d.TotalHours < 24) return (int)d.TotalHours + "시간 전";
            return (int)d.TotalDays + "일 전";
        }

        public void Reload()
        {
            lblStatus.Text = "세션 기록을 읽는 중...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<ProjectInfo> res = null;
                string err = null;
                try { res = TaskScanner.Scan(); }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (err != null) { lblStatus.Text = "읽지 못했어요: " + err; return; }
                        data = res;
                        Rebuild();
                    }));
                }
                catch { }
            });
        }

        void Rebuild()
        {
            tree.BeginUpdate();
            tree.Nodes.Clear();
            int projects = 0, total = 0;
            foreach (ProjectInfo p in data)
            {
                int n = p.Count();
                if (n == 0) continue;
                projects++;
                total += n;
                TreeNode pn = new TreeNode(p.Name + "   · " + n + "개");
                pn.Tag = p;
                pn.ToolTipText = p.Dir;
                pn.ForeColor = Rgb(224, 122, 84);
                foreach (SessionInfo s in p.Sessions)
                {
                    TreeNode sn = new TreeNode(s.Title + "   (" + Ago(s.Time) + ")");
                    sn.Tag = s;
                    sn.ToolTipText = s.Title;
                    sn.ForeColor = Rgb(150, 172, 190);
                    foreach (TaskItem t in s.Tasks) sn.Nodes.Add(MakeTaskNode(t, p));
                    pn.Nodes.Add(sn);
                }
                if (p.Manual.Count > 0)
                {
                    TreeNode mn = new TreeNode("내가 추가한 할 일 (" + p.Manual.Count + ")");
                    mn.ForeColor = Rgb(150, 172, 190);
                    foreach (TaskItem t in p.Manual) mn.Nodes.Add(MakeTaskNode(t, p));
                    pn.Nodes.Add(mn);
                }
                tree.Nodes.Add(pn);
            }
            tree.ExpandAll();
            if (tree.Nodes.Count > 0) tree.Nodes[0].EnsureVisible();
            tree.EndUpdate();
            UpdateButtons();
            if (total == 0)
                lblStatus.Text = "남은 할 일이 없어요. '할 일 추가'로 직접 넣을 수 있어요.";
            else
                lblStatus.Text = "프로젝트 " + projects + "개 · 남은 할 일 " + total + "개 · " + DateTime.Now.ToString("HH:mm:ss") + " 갱신";
        }

        TreeNode MakeTaskNode(TaskItem t, ProjectInfo p)
        {
            TaskRef tr = new TaskRef();
            tr.Item = t;
            tr.Project = p;
            string prefix = t.Status == "in_progress" ? "[진행중]  " : "[  ]  ";
            TreeNode n = new TreeNode(prefix + t.Text);
            n.Tag = tr;
            n.ToolTipText = t.Text;
            n.ForeColor = t.Status == "in_progress" ? Rgb(255, 190, 70) : Rgb(226, 236, 244);
            return n;
        }

        void ChangeMode()
        {
            string mode = cmbMode.SelectedIndex == 1 ? "background" : "terminal";
            if (mode == "background" && !st.BgConfirmed)
            {
                DialogResult r = MessageBox.Show(this,
                    "백그라운드 자동 실행은 창 없이 Claude가 알아서 작업해요.\n\n" +
                    "• 파일 수정/생성은 물어보지 않고 허용돼요.\n" +
                    "• 명령 실행처럼 다른 권한이 필요한 작업은 막혀서 끝까지 못 할 수 있어요.\n" +
                    "• 내용은 로그 파일로 남고, 끝나면 알림이 떠요.\n\n" +
                    "이 방식을 쓸까요?",
                    "백그라운드 실행 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { cmbMode.SelectedIndex = 0; return; }
                st.BgConfirmed = true;
            }
            st.ExecMode = mode;
            st.Save();
        }

        void RunSelected()
        {
            TaskRef tr = Selected();
            if (tr == null) return;
            string claude = Runner.FindClaude();
            if (claude == null)
            {
                MessageBox.Show(this, "claude 프로그램을 찾지 못했어요.\nClaude Code가 설치되어 있는지 확인해 주세요.\n(보통 C:\\Users\\내이름\\.local\\bin\\claude.exe)",
                    "실행할 수 없어요", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string dir = tr.Project.Dir;
            if (!Directory.Exists(dir))
            {
                MessageBox.Show(this, "프로젝트 폴더를 찾을 수 없어요:\n" + dir, "실행할 수 없어요", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string sid = tr.Item.Manual ? "" : tr.Item.SessionId;
            string prompt = tr.Item.Manual ? tr.Item.Text : "이 세션에서 남아 있는 할 일을 진행해줘: " + tr.Item.Text;
            string err;
            if (st.ExecMode == "background")
                err = Runner.RunBackground(claude, dir, sid, prompt, tr.Item.Text, owner);
            else
                err = Runner.RunTerminal(claude, dir, sid, prompt);
            if (err != null) MessageBox.Show(this, err, "실행할 수 없어요", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else lblStatus.Text = (st.ExecMode == "background" ? "백그라운드로 시작했어요: " : "터미널 창에서 시작했어요: ") + tr.Item.Text;
        }

        void AddTask()
        {
            List<string> dirs = new List<string>();
            foreach (ProjectInfo p in data) dirs.Add(p.Dir);
            string def = dirs.Count > 0 ? dirs[0] : "";
            TreeNode n = tree.SelectedNode;
            while (n != null && n.Parent != null) n = n.Parent;
            if (n != null && n.Tag is ProjectInfo) def = ((ProjectInfo)n.Tag).Dir;
            string dir, text;
            if (!AskAdd(dirs, def, out dir, out text)) return;
            if (!Directory.Exists(dir))
            {
                MessageBox.Show(this, "그 폴더를 찾을 수 없어요:\n" + dir, "할 일 추가", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            TaskStore.AddManual(dir, text);
            Reload();
        }

        bool AskAdd(List<string> dirs, string defaultDir, out string dir, out string text)
        {
            string rd = "", rt = "";
            bool ok = false;
            using (Form f = new Form())
            {
                f.Text = "할 일 추가";
                f.Font = new Font("Malgun Gothic", 9.5f);
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                f.ClientSize = new Size(500, 270);
                Label l1 = new Label();
                l1.Text = "프로젝트 폴더 (Claude를 실행할 위치)";
                l1.Left = 14; l1.Top = 12; l1.AutoSize = true;
                ComboBox cb = new ComboBox();
                cb.Left = 14; cb.Top = 34; cb.Width = 388;
                cb.DropDownStyle = ComboBoxStyle.DropDown;
                foreach (string d in dirs) cb.Items.Add(d);
                cb.Text = defaultDir;
                Button br = new Button();
                br.Text = "폴더 선택..."; br.Left = 408; br.Top = 32; br.Width = 82;
                br.Click += delegate
                {
                    using (FolderBrowserDialog fb = new FolderBrowserDialog())
                    {
                        fb.Description = "프로젝트 폴더를 고르세요";
                        if (fb.ShowDialog() == DialogResult.OK) cb.Text = fb.SelectedPath;
                    }
                };
                Label l2 = new Label();
                l2.Text = "할 일 내용";
                l2.Left = 14; l2.Top = 72; l2.AutoSize = true;
                TextBox tb = new TextBox();
                tb.Left = 14; tb.Top = 94; tb.Width = 476; tb.Height = 120;
                tb.Multiline = true; tb.AcceptsReturn = true; tb.ScrollBars = ScrollBars.Vertical;
                Button okb = new Button();
                okb.Text = "추가"; okb.Left = 314; okb.Top = 228; okb.Width = 84; okb.DialogResult = DialogResult.OK;
                Button cancel = new Button();
                cancel.Text = "취소"; cancel.Left = 406; cancel.Top = 228; cancel.Width = 84; cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(l1); f.Controls.Add(cb); f.Controls.Add(br);
                f.Controls.Add(l2); f.Controls.Add(tb); f.Controls.Add(okb); f.Controls.Add(cancel);
                f.CancelButton = cancel;
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    rd = cb.Text.Trim();
                    rt = tb.Text.Trim();
                    ok = rd.Length > 0 && rt.Length > 0;
                    if (!ok) MessageBox.Show(this, "폴더와 할 일 내용을 모두 입력해 주세요.", "할 일 추가");
                }
            }
            dir = rd;
            text = TaskStore.Clean(rt);
            return ok;
        }

        void DeleteSelected()
        {
            TaskRef tr = Selected();
            if (tr == null) return;
            if (tr.Item.Manual)
            {
                if (MessageBox.Show(this, "이 할 일을 목록에서 지울까요?\n\n" + tr.Item.Text, "삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                TaskStore.RemoveManual(tr.Project.Dir, tr.Item.Text);
            }
            else
            {
                if (MessageBox.Show(this, "이 할 일을 목록에서 숨길까요?\n(Claude 세션 기록은 그대로 두고, 이 목록에서만 안 보이게 해요.)\n\n" + tr.Item.Text,
                    "삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                TaskStore.Dismissed[tr.Item.Key()] = true;
                TaskStore.Save();
            }
            Reload();
        }
    }

    // ------------------------------------------------------------------ main window
    class GaugeForm : Form
    {
        const float GW = 230f, CW = 150f, DH = 240f, PAD = 10f;

        Settings st;
        float dpiFactor = 1f;
        DateTime startTime = DateTime.UtcNow;

        // data
        bool subHave; bool subStale; string subMsg = ""; string plan = "";
        double fiveUsed = -1, weekUsed = -1;
        DateTime fiveReset = DateTime.MinValue, weekReset = DateTime.MinValue;
        DateTime subUpdated = DateTime.MinValue;
        bool apiHave; string apiMsg = "";
        long reqRem = -1, reqLim = -1, tokRem = -1, tokLim = -1;

        // animation
        double curFive = 0, curWeek = 0;

        // scheduling
        bool subBusy, apiBusy;
        DateTime nextSub = DateTime.MinValue, nextApi = DateTime.MinValue, lastSubStart = DateTime.MinValue;
        bool clickThrough;
        bool hotkeysOk = true;
        bool hasKey;
        TaskForm tasksForm;
        bool updBusy, oldCleaned;
        DateTime nextUpd = DateTime.UtcNow.AddSeconds(30);
        string lastLog;
        ToolStripMenuItem miAutoUpd;

        // ui
        NotifyIcon notify;
        ContextMenuStrip menu;
        System.Windows.Forms.Timer animTimer, secTimer;
        Font fBig, fMid, fSm, fXs, fTick, fBrand, fClock;
        StringFormat sf, sfWrap;
        CultureInfo ko = CultureInfo.GetCultureInfo("ko-KR");

        ToolStripMenuItem miTop, miLock, miClick, miAuto, miFive, miWeek, miApi, miHide;
        List<KeyValuePair<ToolStripMenuItem, double>> sizeItems = new List<KeyValuePair<ToolStripMenuItem, double>>();
        List<KeyValuePair<ToolStripMenuItem, double>> opacItems = new List<KeyValuePair<ToolStripMenuItem, double>>();
        List<KeyValuePair<ToolStripMenuItem, double>> intItems = new List<KeyValuePair<ToolStripMenuItem, double>>();

        public GaugeForm()
        {
            st = Settings.Load();
            using (Graphics g0 = Graphics.FromHwnd(IntPtr.Zero)) dpiFactor = g0.DpiX / 96f;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Text = "Claude 잔여량";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            fBig = new Font("Segoe UI Semibold", 27f, FontStyle.Regular, GraphicsUnit.Pixel);
            fMid = new Font("Malgun Gothic", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
            fSm = new Font("Malgun Gothic", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
            fXs = new Font("Malgun Gothic", 9.5f, FontStyle.Regular, GraphicsUnit.Pixel);
            fTick = new Font("Malgun Gothic", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
            fBrand = new Font("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
            fClock = new Font("Segoe UI Semibold", 30f, FontStyle.Regular, GraphicsUnit.Pixel);
            sf = new StringFormat(StringFormat.GenericTypographic);
            sf.LineAlignment = StringAlignment.Center;
            sfWrap = new StringFormat();
            sfWrap.Alignment = StringAlignment.Center;
            sfWrap.LineAlignment = StringAlignment.Near;
            sfWrap.Trimming = StringTrimming.EllipsisWord;

            TopMost = st.TopMost;
            Opacity = Math.Min(0.99, st.OpacityPct / 100.0);
            ApplyLayout();

            Screen primary = Screen.PrimaryScreen;
            bool onScreen = false;
            if (st.X != int.MinValue && st.Y != int.MinValue)
            {
                Rectangle r = new Rectangle(st.X, st.Y, Width, Height);
                foreach (Screen sc in Screen.AllScreens) if (sc.WorkingArea.IntersectsWith(r)) onScreen = true;
            }
            if (onScreen) Location = new Point(st.X, st.Y);
            else PlaceCorner(1, primary);

            BuildMenu();
            ContextMenuStrip = menu;

            notify = new NotifyIcon();
            try { notify.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { notify.Icon = SystemIcons.Application; }
            notify.Text = "Claude 잔여량";
            notify.ContextMenuStrip = menu;
            notify.DoubleClick += delegate { ToggleVisible(); };
            notify.Visible = true;
            notify.BalloonTipClicked += delegate { OpenLastLog(); };
            TaskStore.Load();
            if (Program.JustUpdated)
            {
                try { notify.ShowBalloonTip(4000, "Claude 잔여량", "v" + AppInfo.AppVersion + " 로 업데이트됐어요", ToolTipIcon.Info); } catch { }
            }

            animTimer = new System.Windows.Forms.Timer();
            animTimer.Interval = 33;
            animTimer.Tick += OnAnim;
            animTimer.Start();
            secTimer = new System.Windows.Forms.Timer();
            secTimer.Interval = 1000;
            secTimer.Tick += OnSec;
            secTimer.Start();
        }

        // ---------- window behaviour ----------
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            uint m = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
            hotkeysOk = Native.RegisterHotKey(Handle, 1, m, (uint)Keys.G);
            Native.RegisterHotKey(Handle, 2, m, (uint)Keys.C);
            Native.RegisterHotKey(Handle, 3, m, (uint)Keys.R);
            Native.RegisterHotKey(Handle, 4, m, (uint)Keys.T);
            ApplyExStyle();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Native.UnregisterHotKey(Handle, 1);
            Native.UnregisterHotKey(Handle, 2);
            Native.UnregisterHotKey(Handle, 3);
            Native.UnregisterHotKey(Handle, 4);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == 1) ToggleVisible();
                else if (id == 2) SetClickThrough(!clickThrough);
                else if (id == 3) RefreshNow();
                else if (id == 4) ShowTasks();
                return;
            }
            base.WndProc(ref m);
        }

        void ApplyExStyle()
        {
            if (!IsHandleCreated) return;
            int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            if (clickThrough) ex |= (Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED);
            else ex &= ~Native.WS_EX_TRANSPARENT;
            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !st.Locked)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
                st.X = Left; st.Y = Top; st.Save();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
                SetScale(Math.Max(0.35, Math.Min(2.0, st.Scale + (e.Delta > 0 ? 0.05 : -0.05))));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            st.X = Left; st.Y = Top; st.Save();
            if (notify != null) { notify.Visible = false; notify.Dispose(); }
            base.OnFormClosing(e);
        }

        void ToggleVisible()
        {
            if (Visible) Hide(); else Show();
        }

        void SetClickThrough(bool on)
        {
            clickThrough = on;
            ApplyExStyle();
            Invalidate();
            try
            {
                if (on) notify.ShowBalloonTip(3000, "Claude 잔여량",
                    "클릭 통과 모드가 켜졌어요. 끄려면 Ctrl+Alt+C 또는 트레이 아이콘 우클릭 메뉴를 쓰세요.", ToolTipIcon.Info);
            }
            catch { }
        }

        float ActualScale() { return (float)(st.Scale * dpiFactor); }

        float DesignWidth()
        {
            float w = PAD * 2 + CW;
            if (st.ShowFive) w += GW;
            if (st.ShowWeek) w += GW;
            return w;
        }

        void ApplyLayout()
        {
            float s = ActualScale();
            int w = (int)Math.Round(DesignWidth() * s);
            int h = (int)Math.Round(DH * s);
            Size = new Size(w, h);
            using (GraphicsPath gp = RoundPath(new RectangleF(0, 0, w, h), 20f * s))
            {
                Region old = Region;
                Region = new Region(gp);
                if (old != null) old.Dispose();
            }
            Invalidate();
        }

        void PlaceCorner(int c, Screen sc)
        {
            Rectangle wa = sc.WorkingArea;
            int m = 14;
            int x = wa.Left + m, y = wa.Top + m;
            if (c == 1 || c == 3) x = wa.Right - Width - m;
            if (c == 2 || c == 3) y = wa.Bottom - Height - m;
            if (c == 4) { x = wa.Left + (wa.Width - Width) / 2; y = wa.Top + (wa.Height - Height) / 2; }
            Location = new Point(x, y);
            st.X = x; st.Y = y;
        }

        void SetScale(double v)
        {
            st.Scale = v;
            ApplyLayout();
            st.Save();
        }

        void SetOpacity(int pct)
        {
            st.OpacityPct = pct;
            Opacity = Math.Min(0.99, pct / 100.0);
            ApplyExStyle();
            st.Save();
        }

        // ---------- menu ----------
        ToolStripMenuItem MI(string text, EventHandler h)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text);
            if (h != null) i.Click += h;
            return i;
        }

        void BuildMenu()
        {
            menu = new ContextMenuStrip();
            menu.Items.Add(MI("할 일 목록...  (Ctrl+Alt+T)", delegate { ShowTasks(); }));
            menu.Items.Add(MI("지금 새로고침  (Ctrl+Alt+R)", delegate { RefreshNow(); }));
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem show = new ToolStripMenuItem("표시 항목");
            miFive = MI("5시간 한도 게이지", delegate { st.ShowFive = !st.ShowFive; ApplyLayout(); st.Save(); });
            miWeek = MI("주간 한도 게이지", delegate { st.ShowWeek = !st.ShowWeek; ApplyLayout(); st.Save(); });
            miApi = MI("API rate limit 패널", delegate { st.ShowApi = !st.ShowApi; nextApi = DateTime.MinValue; Invalidate(); st.Save(); });
            show.DropDownItems.Add(miFive);
            show.DropDownItems.Add(miWeek);
            show.DropDownItems.Add(miApi);
            menu.Items.Add(show);

            ToolStripMenuItem size = new ToolStripMenuItem("크기");
            double[] sv = { 0.5, 0.75, 1.0, 1.3 };
            string[] sn = { "작게", "보통", "크게", "아주 크게" };
            for (int i = 0; i < sv.Length; i++)
            {
                double val = sv[i];
                ToolStripMenuItem it = MI(sn[i], delegate { SetScale(val); });
                size.DropDownItems.Add(it);
                sizeItems.Add(new KeyValuePair<ToolStripMenuItem, double>(it, val));
            }
            ToolStripMenuItem hint = MI("Ctrl + 마우스 휠로 미세 조절", null);
            hint.Enabled = false;
            size.DropDownItems.Add(new ToolStripSeparator());
            size.DropDownItems.Add(hint);
            menu.Items.Add(size);

            ToolStripMenuItem op = new ToolStripMenuItem("투명도");
            int[] ov = { 100, 90, 80, 70, 60, 50, 40, 30 };
            for (int i = 0; i < ov.Length; i++)
            {
                int val = ov[i];
                ToolStripMenuItem it = MI(val + "%", delegate { SetOpacity(val); });
                op.DropDownItems.Add(it);
                opacItems.Add(new KeyValuePair<ToolStripMenuItem, double>(it, val));
            }
            menu.Items.Add(op);

            ToolStripMenuItem pos = new ToolStripMenuItem("위치");
            string[] pn = { "왼쪽 위", "오른쪽 위", "왼쪽 아래", "오른쪽 아래", "화면 중앙" };
            for (int i = 0; i < pn.Length; i++)
            {
                int c = i;
                pos.DropDownItems.Add(MI(pn[i], delegate
                {
                    Screen sc = IsHandleCreated ? Screen.FromControl(this) : Screen.PrimaryScreen;
                    PlaceCorner(c, sc);
                    st.Save();
                }));
            }
            pos.DropDownItems.Add(new ToolStripSeparator());
            miLock = MI("위치 잠금 (드래그 방지)", delegate { st.Locked = !st.Locked; st.Save(); Invalidate(); });
            pos.DropDownItems.Add(miLock);
            menu.Items.Add(pos);

            miTop = MI("항상 위에 표시", delegate { st.TopMost = !st.TopMost; TopMost = st.TopMost; st.Save(); });
            menu.Items.Add(miTop);
            miClick = MI("클릭 통과 모드  (Ctrl+Alt+C)", delegate { SetClickThrough(!clickThrough); });
            menu.Items.Add(miClick);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem iv = new ToolStripMenuItem("갱신 주기");
            int[] iu = { 30, 60, 120, 300 };
            string[] iname = { "30초", "1분", "2분", "5분" };
            for (int i = 0; i < iu.Length; i++)
            {
                int val = iu[i];
                ToolStripMenuItem it = MI(iname[i], delegate { st.IntervalSec = val; nextSub = DateTime.MinValue; st.Save(); });
                iv.DropDownItems.Add(it);
                intItems.Add(new KeyValuePair<ToolStripMenuItem, double>(it, val));
            }
            menu.Items.Add(iv);

            ToolStripMenuItem upd = new ToolStripMenuItem("업데이트");
            miAutoUpd = MI("자동 업데이트 (6시간마다 확인)", delegate { st.AutoUpdate = !st.AutoUpdate; st.Save(); });
            upd.DropDownItems.Add(miAutoUpd);
            upd.DropDownItems.Add(MI("지금 업데이트 확인", delegate { StartUpdateCheck(true); }));
            upd.DropDownItems.Add(MI("업데이트 주소 설정...", delegate { EditUpdateUrl(); }));
            upd.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem verItem = MI("현재 버전: v" + AppInfo.AppVersion, null);
            verItem.Enabled = false;
            upd.DropDownItems.Add(verItem);
            menu.Items.Add(upd);

            menu.Items.Add(MI("API 키 설정...", delegate { EditApiKey(); }));
            miAuto = MI("Windows 시작 시 자동 실행", delegate { SetAutoStart(!GetAutoStart()); });
            menu.Items.Add(miAuto);
            menu.Items.Add(new ToolStripSeparator());
            miHide = MI("숨기기  (Ctrl+Alt+G)", delegate { ToggleVisible(); });
            menu.Items.Add(miHide);
            menu.Items.Add(MI("종료", delegate { Close(); }));

            menu.Opening += delegate
            {
                miFive.Checked = st.ShowFive;
                miWeek.Checked = st.ShowWeek;
                miApi.Checked = st.ShowApi;
                miLock.Checked = st.Locked;
                miTop.Checked = st.TopMost;
                miClick.Checked = clickThrough;
                miAuto.Checked = GetAutoStart();
                miAutoUpd.Checked = st.AutoUpdate;
                miHide.Text = Visible ? "숨기기  (Ctrl+Alt+G)" : "보이기  (Ctrl+Alt+G)";
                foreach (KeyValuePair<ToolStripMenuItem, double> kv in sizeItems) kv.Key.Checked = Math.Abs(kv.Value - st.Scale) < 0.02;
                foreach (KeyValuePair<ToolStripMenuItem, double> kv in opacItems) kv.Key.Checked = (int)kv.Value == st.OpacityPct;
                foreach (KeyValuePair<ToolStripMenuItem, double> kv in intItems) kv.Key.Checked = (int)kv.Value == st.IntervalSec;
            };
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        bool GetAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue("ClaudeGauge") != null;
            }
            catch { return false; }
        }

        void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("ClaudeGauge", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("ClaudeGauge", false);
                }
            }
            catch { }
        }

        // ---------- tasks / notifications / updates ----------
        void ShowTasks()
        {
            if (tasksForm == null || tasksForm.IsDisposed) tasksForm = new TaskForm(this, st);
            if (!tasksForm.Visible) tasksForm.Show();
            if (tasksForm.WindowState == FormWindowState.Minimized) tasksForm.WindowState = FormWindowState.Normal;
            tasksForm.Activate();
            tasksForm.Reload();
        }

        public void Notify(string title, string text, string logPath)
        {
            lastLog = logPath;
            try
            {
                notify.ShowBalloonTip(5000, title, text.Length > 120 ? text.Substring(0, 120) + "..." : text, ToolTipIcon.Info);
            }
            catch { }
        }

        public void NotifyRunDone(string label, int code, string log)
        {
            Post(delegate
            {
                Notify(code == 0 ? "Claude 작업 완료" : "Claude 작업 종료 (코드 " + code + ")", label + "  (눌러서 로그 보기)", log);
            });
        }

        void OpenLastLog()
        {
            if (lastLog == null) return;
            try
            {
                if (File.Exists(lastLog)) Process.Start("notepad.exe", "\"" + lastLog + "\"");
            }
            catch { }
        }

        void StartUpdateCheck(bool manual)
        {
            if (updBusy) return;
            if (st.UpdateUrl.Length == 0)
            {
                if (manual) Notify("업데이트", "업데이트 주소가 아직 없어요. 우클릭 > 업데이트 > 업데이트 주소 설정", null);
                return;
            }
            updBusy = true;
            nextUpd = DateTime.UtcNow.AddHours(6);
            string url = st.UpdateUrl;
            int failed = st.FailedVersion;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int nv;
                string msg;
                int res = Updater.CheckAndApply(url, failed, out nv, out msg);
                Post(delegate { OnUpdateResult(res, nv, msg, manual); });
            });
        }

        void OnUpdateResult(int res, int nv, string msg, bool manual)
        {
            updBusy = false;
            if (res == 1) { RestartApp(); return; }
            if (res == -2)
            {
                st.FailedVersion = nv;
                st.Save();
                Notify("업데이트 실패", msg, null);
                return;
            }
            if (manual) Notify("업데이트", msg.Length > 0 ? msg : "최신 버전이에요 (v" + AppInfo.AppVersion + ")", null);
        }

        void RestartApp()
        {
            try
            {
                string exe = Application.ExecutablePath;
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & start \"\" \"" + exe + "\" --updated");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch { return; }
            Close();
        }

        static string PromptText(string title, string msg, string current)
        {
            string res = null;
            using (Form f = new Form())
            {
                f.Text = title;
                f.Font = new Font("Malgun Gothic", 9f);
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.TopMost = true;
                f.ClientSize = new Size(500, 270);
                Label lb = new Label();
                lb.Left = 14; lb.Top = 12; lb.Width = 472; lb.Height = 150;
                lb.Text = msg;
                TextBox tb = new TextBox();
                tb.Left = 14; tb.Top = 172; tb.Width = 472;
                tb.Text = current;
                Button ok = new Button();
                ok.Text = "저장"; ok.Left = 304; ok.Top = 222; ok.Width = 84; ok.DialogResult = DialogResult.OK;
                Button cancel = new Button();
                cancel.Text = "취소"; cancel.Left = 402; cancel.Top = 222; cancel.Width = 84; cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(lb); f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog() == DialogResult.OK) res = tb.Text.Trim();
            }
            return res;
        }

        void EditUpdateUrl()
        {
            string res = PromptText("업데이트 주소 설정",
                "새 버전을 받아올 GitHub 저장소 주소를 입력하세요.\r\n" +
                "예: https://github.com/내아이디/claude-gauge\r\n\r\n" +
                "• 이 주소의 프로그램 코드를 내려받아 이 PC에서 실행하게 돼요.\r\n" +
                "• 반드시 본인이 직접 관리하는 저장소 주소만 입력하세요.\r\n" +
                "• 비워 두고 저장하면 기본 주소로 돌아가요. 끄려면 업데이트 메뉴에서 자동 업데이트 체크를 해제하세요.", st.UpdateUrl);
            if (res == null) return;
            string norm = Updater.NormalizeUrl(res);
            if (norm.Length > 0 && !norm.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("주소는 https:// 로 시작해야 해요.", "업데이트 주소");
                return;
            }
            if (norm.Length > 0) st.UpdateUrl = norm;
            st.FailedVersion = 0;
            st.Save();
            if (norm.Length > 0) StartUpdateCheck(true);
        }

        // ---------- API key ----------
        string GetApiKey()
        {
            string k = "";
            if (!string.IsNullOrEmpty(st.ApiKeyEnc))
            {
                try
                {
                    byte[] raw = ProtectedData.Unprotect(Convert.FromBase64String(st.ApiKeyEnc), null, DataProtectionScope.CurrentUser);
                    k = Encoding.UTF8.GetString(raw);
                }
                catch { k = ""; }
            }
            if (string.IsNullOrEmpty(k))
            {
                string e = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                if (!string.IsNullOrEmpty(e)) k = e.Trim();
            }
            return k;
        }

        void EditApiKey()
        {
            string res = null;
            using (Form f = new Form())
            {
                f.Text = "Anthropic API 키 설정";
                f.Font = new Font("Malgun Gothic", 9f);
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.MaximizeBox = false; f.MinimizeBox = false;
                f.TopMost = true;
                f.ClientSize = new Size(450, 200);
                Label lb = new Label();
                lb.Left = 14; lb.Top = 12; lb.Width = 422; lb.Height = 90;
                lb.Text = "rate limit 잔여량(요청/토큰)을 보려면 Anthropic API 키(sk-ant-...)를 입력하세요.\r\n" +
                          "• 이 PC의 Windows 계정으로만 풀 수 있게 암호화해서 저장해요.\r\n" +
                          "• 1분마다 아주 작은 요청(약 1토큰)을 보내 응답 헤더를 읽어요.\r\n" +
                          "• 비워 두고 저장하면 키가 삭제돼요.";
                TextBox tb = new TextBox();
                tb.Left = 14; tb.Top = 112; tb.Width = 422; tb.UseSystemPasswordChar = true;
                Button ok = new Button();
                ok.Text = "저장"; ok.Left = 256; ok.Top = 156; ok.Width = 84; ok.DialogResult = DialogResult.OK;
                Button cancel = new Button();
                cancel.Text = "취소"; cancel.Left = 352; cancel.Top = 156; cancel.Width = 84; cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(lb); f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog() == DialogResult.OK) res = tb.Text.Trim();
            }
            if (res == null) return;
            if (res.Length == 0) st.ApiKeyEnc = "";
            else
            {
                try
                {
                    byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(res), null, DataProtectionScope.CurrentUser);
                    st.ApiKeyEnc = Convert.ToBase64String(enc);
                }
                catch { st.ApiKeyEnc = ""; }
            }
            st.ShowApi = true;
            apiHave = false; apiMsg = "";
            nextApi = DateTime.MinValue;
            st.Save();
            Invalidate();
        }

        // ---------- polling ----------
        void RefreshNow()
        {
            nextSub = DateTime.MinValue;
            nextApi = DateTime.MinValue;
            OnSec(null, EventArgs.Empty);
        }

        void OnSec(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            if (!subBusy)
            {
                bool resetPassed = (fiveReset != DateTime.MinValue && now > fiveReset.AddSeconds(3)) ||
                                   (weekReset != DateTime.MinValue && now > weekReset.AddSeconds(3));
                if (now >= nextSub || (resetPassed && (now - lastSubStart).TotalSeconds > 45)) StartSub();
            }
            string k = GetApiKey();
            hasKey = !string.IsNullOrEmpty(k);
            if (st.ShowApi && !apiBusy && now >= nextApi)
            {
                if (hasKey) StartApi(k);
                else nextApi = now.AddSeconds(5);
            }
            if (st.AutoUpdate && st.UpdateUrl.Length > 0 && !updBusy && now >= nextUpd) StartUpdateCheck(false);
            if (!oldCleaned && (now - startTime).TotalSeconds > 10)
            {
                oldCleaned = true;
                try
                {
                    string op = Path.Combine(AppInfo.AppDir(), "ClaudeGauge.old.exe");
                    if (File.Exists(op)) File.Delete(op);
                }
                catch { }
            }
            Invalidate();
        }

        void StartSub()
        {
            subBusy = true;
            lastSubStart = DateTime.UtcNow;
            nextSub = DateTime.UtcNow.AddSeconds(st.IntervalSec);
            ThreadPool.QueueUserWorkItem(delegate
            {
                SubResult r = Fetcher.FetchSub();
                Post(delegate { ApplySub(r); });
            });
        }

        void StartApi(string key)
        {
            apiBusy = true;
            nextApi = DateTime.UtcNow.AddSeconds(Math.Max(60, st.IntervalSec));
            ThreadPool.QueueUserWorkItem(delegate
            {
                ApiResult r = Fetcher.FetchApi(key);
                Post(delegate { ApplyApi(r); });
            });
        }

        void Post(Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
            }
            catch { }
        }

        void ApplySub(SubResult r)
        {
            subBusy = false;
            if (!string.IsNullOrEmpty(r.Plan)) plan = r.Plan;
            if (r.Ok)
            {
                subHave = true; subStale = false; subMsg = "";
                fiveUsed = r.Five; weekUsed = r.Week;
                fiveReset = r.FiveReset; weekReset = r.WeekReset;
                subUpdated = DateTime.Now;
            }
            else
            {
                subStale = subHave;
                subMsg = r.Msg;
                if (r.Backoff) nextSub = DateTime.UtcNow.AddSeconds(300);
            }
            Invalidate();
        }

        void ApplyApi(ApiResult r)
        {
            apiBusy = false;
            if (r.Ok)
            {
                apiHave = true; apiMsg = "";
                reqRem = r.ReqRem; reqLim = r.ReqLim; tokRem = r.TokRem; tokLim = r.TokLim;
            }
            else apiMsg = r.Msg;
            Invalidate();
        }

        // ---------- animation ----------
        static bool Approach(ref double cur, double target)
        {
            double d = target - cur;
            if (Math.Abs(d) < 0.05)
            {
                if (cur != target) { cur = target; return true; }
                return false;
            }
            cur += d * 0.14;
            return true;
        }

        double UsedFive() { return fiveUsed < 0 ? -1 : Math.Max(0, Math.Min(100, fiveUsed)); }
        double UsedWeek() { return weekUsed < 0 ? -1 : Math.Max(0, Math.Min(100, weekUsed)); }

        void OnAnim(object sender, EventArgs e)
        {
            bool sweeping = (DateTime.UtcNow - startTime).TotalMilliseconds < 1000;
            double tf = sweeping ? 100 : Math.Max(0, UsedFive());
            double tw = sweeping ? 100 : Math.Max(0, UsedWeek());
            bool ch = false;
            if (Approach(ref curFive, tf)) ch = true;
            if (Approach(ref curWeek, tw)) ch = true;
            bool hot = !subStale && (curFive >= 90 || curWeek >= 90);
            if ((ch || hot) && Visible) Invalidate();
        }

        // ---------- drawing ----------
        static Color Rgb(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        static GraphicsPath RoundPath(RectangleF r, float rad)
        {
            float d = rad * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Color Lerp(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // usage 0..100 -> cyan (calm) -> amber (careful) -> red (almost at the limit)
        static Color ZoneColor(double v)
        {
            Color cyan = Rgb(56, 214, 255), amber = Rgb(255, 176, 32), red = Rgb(255, 72, 66);
            if (v <= 55) return cyan;
            if (v <= 78) return Lerp(cyan, amber, (v - 55) / 23.0);
            if (v <= 92) return Lerp(amber, red, (v - 78) / 14.0);
            return red;
        }

        void Txt(Graphics g, string s, Font f, Color c, float x, float y, StringAlignment al)
        {
            float w = 420f, h = 60f;
            float rx = al == StringAlignment.Center ? x - w / 2 : (al == StringAlignment.Near ? x : x - w);
            sf.Alignment = al;
            using (SolidBrush br = new SolidBrush(c))
                g.DrawString(s, f, br, new RectangleF(rx, y - h / 2, w, h), sf);
        }

        void WrapTxt(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            using (SolidBrush br = new SolidBrush(c)) g.DrawString(s, f, br, r, sfWrap);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = ActualScale();
            g.ScaleTransform(s, s);
            float W = DesignWidth();

            RectangleF bg = new RectangleF(0, 0, W, DH);
            using (GraphicsPath gp = RoundPath(bg, 20f))
            {
                using (LinearGradientBrush b = new LinearGradientBrush(bg, Rgb(17, 24, 32), Rgb(6, 9, 13), 90f))
                    g.FillPath(b, gp);
            }
            RectangleF inset = new RectangleF(1f, 1f, W - 2f, DH - 2f);
            using (GraphicsPath gp2 = RoundPath(inset, 19f))
            using (Pen p = new Pen(Rgb(44, 58, 72), 1.6f))
                g.DrawPath(p, gp2);

            float x = PAD;
            if (st.ShowFive)
            {
                DrawGauge(g, x + GW / 2, 120f, 108f, curFive, UsedFive(), "5시간 사용량", Countdown(fiveReset), subStale);
                x += GW;
            }
            DrawCenter(g, x);
            x += CW;
            if (st.ShowWeek)
            {
                DrawGauge(g, x + GW / 2, 120f, 108f, curWeek, UsedWeek(), "주간 사용량", Countdown(weekReset), subStale);
            }
        }

        string Countdown(DateTime resetUtc)
        {
            if (resetUtc == DateTime.MinValue) return subHave ? "사용 기록 없음" : "";
            TimeSpan ts = resetUtc - DateTime.UtcNow;
            if (ts.TotalSeconds <= 0) return "리셋됨 (갱신 대기)";
            if (ts.TotalHours >= 24) return "리셋 " + (int)ts.TotalDays + "일 " + ts.Hours + "시간 후";
            if (ts.TotalHours >= 1) return "리셋 " + (int)ts.TotalHours + "시간 " + ts.Minutes + "분 후";
            return "리셋 " + Math.Max(1, (int)Math.Ceiling(ts.TotalMinutes)) + "분 후";
        }

        void DrawGauge(Graphics g, float cx, float cy, float R, double cur, double actual, string title, string sub, bool stale)
        {
            double v = Math.Max(0, Math.Min(100, cur));
            bool known = actual >= 0;

            RectangleF outer = new RectangleF(cx - R, cy - R, 2 * R, 2 * R);
            using (LinearGradientBrush b = new LinearGradientBrush(outer, Rgb(56, 70, 84), Rgb(10, 14, 18), 65f))
                g.FillEllipse(b, outer);
            float R2 = R - 4f;
            RectangleF face = new RectangleF(cx - R2, cy - R2, 2 * R2, 2 * R2);
            using (LinearGradientBrush b = new LinearGradientBrush(face, Rgb(16, 23, 30), Rgb(6, 9, 12), 90f))
                g.FillEllipse(b, face);

            float ra = R - 18f;
            RectangleF arc = new RectangleF(cx - ra, cy - ra, 2 * ra, 2 * ra);
            using (Pen p = new Pen(Rgb(26, 37, 48), 9f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, arc, 135f, 270f);
            }
            using (Pen p = new Pen(Rgb(92, 30, 32), 9f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, arc, 135f + 243f, 27f);
            }

            Color zc = stale ? Rgb(112, 128, 142) : ZoneColor(v);
            float sweep = (float)(2.7 * v);
            if (sweep > 1f)
            {
                int ga = 55;
                if (known && !stale && v >= 90) ga = 55 + (int)(70 * (0.5 + 0.5 * Math.Sin(Environment.TickCount / 200.0)));
                using (Pen glow = new Pen(Color.FromArgb(ga, zc), 15f))
                {
                    glow.StartCap = LineCap.Round; glow.EndCap = LineCap.Round;
                    g.DrawArc(glow, arc, 135f, sweep);
                }
                using (Pen p = new Pen(zc, 7f))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    g.DrawArc(p, arc, 135f, sweep);
                }
            }

            // ticks
            for (int i = 0; i <= 20; i++)
            {
                double pct = i * 5;
                bool major = (i % 5 == 0);
                double ang = (135 + 2.7 * pct) * Math.PI / 180.0;
                float r1 = ra - 10f, r2 = ra - (major ? 21f : 16f);
                bool lit = pct <= v;
                Color tc;
                if (pct >= 90) tc = lit ? Rgb(255, 110, 100) : Rgb(104, 48, 48);
                else tc = lit ? Rgb(222, 234, 242) : Rgb(66, 82, 95);
                using (Pen p = new Pen(tc, major ? 2.2f : 1.2f))
                    g.DrawLine(p, cx + (float)Math.Cos(ang) * r1, cy + (float)Math.Sin(ang) * r1,
                                  cx + (float)Math.Cos(ang) * r2, cy + (float)Math.Sin(ang) * r2);
            }
            // labels E - 1/2 - F
            string[] lab = { "E", "½", "F" };
            double[] lp = { 0, 50, 100 };
            for (int i = 0; i < 3; i++)
            {
                double ang = (135 + 2.7 * lp[i]) * Math.PI / 180.0;
                float lr = ra - 35f;
                Txt(g, lab[i], fTick, i == 2 ? Rgb(214, 100, 90) : Rgb(150, 168, 182),
                    cx + (float)Math.Cos(ang) * lr, cy + (float)Math.Sin(ang) * lr, StringAlignment.Center);
            }

            // needle
            double na = (135 + 2.7 * v) * Math.PI / 180.0;
            float ux = (float)Math.Cos(na), uy = (float)Math.Sin(na);
            float nx = -uy, ny = ux;
            float L = ra - 6f;
            PointF[] pts = new PointF[] {
                new PointF(cx + ux * L, cy + uy * L),
                new PointF(cx + nx * 3.8f, cy + ny * 3.8f),
                new PointF(cx - ux * 17f, cy - uy * 17f),
                new PointF(cx - nx * 3.8f, cy - ny * 3.8f)
            };
            Color nc = stale ? Rgb(150, 120, 100) : Rgb(255, 98, 52);
            using (Pen glow = new Pen(Color.FromArgb(70, nc), 6f))
            {
                glow.LineJoin = LineJoin.Round;
                g.DrawPolygon(glow, pts);
            }
            using (SolidBrush b = new SolidBrush(nc)) g.FillPolygon(b, pts);

            // hub
            RectangleF hub = new RectangleF(cx - 10, cy - 10, 20, 20);
            using (LinearGradientBrush b = new LinearGradientBrush(hub, Rgb(70, 84, 98), Rgb(14, 18, 24), 60f))
                g.FillEllipse(b, hub);
            using (Pen p = new Pen(Rgb(88, 104, 118), 1.2f)) g.DrawEllipse(p, hub);
            using (SolidBrush b = new SolidBrush(nc)) g.FillEllipse(b, cx - 3.5f, cy - 3.5f, 7f, 7f);

            // readout
            string num = known ? ((int)Math.Round(actual)).ToString() + "%" : "--";
            Color numC;
            if (!known || stale) numC = Rgb(128, 142, 154);
            else if (actual <= 55) numC = Rgb(236, 243, 248);
            else numC = ZoneColor(actual);
            Txt(g, num, fBig, numC, cx, cy + 0.42f * R, StringAlignment.Center);
            if (!string.IsNullOrEmpty(sub)) Txt(g, sub, fXs, Rgb(120, 138, 152), cx, cy + 0.65f * R, StringAlignment.Center);
            Txt(g, title, fMid, Rgb(176, 192, 204), cx, cy + 0.85f * R, StringAlignment.Center);
        }

        static string Abbrev(long n)
        {
            if (n >= 1000000) return (n / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000) return (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            return n.ToString();
        }

        void DrawBar(Graphics g, float x, float y, float w, float h, double frac, bool known)
        {
            int segs = 14;
            float gap = 2f;
            float sw = (w - gap * (segs - 1)) / segs;
            int lit = (int)Math.Round(frac * segs);
            Color c;
            if (frac <= 0.15) c = Rgb(255, 72, 66);
            else if (frac <= 0.35) c = Rgb(255, 176, 32);
            else c = Rgb(70, 220, 140);
            for (int i = 0; i < segs; i++)
            {
                RectangleF r = new RectangleF(x + i * (sw + gap), y, sw, h);
                using (SolidBrush b = new SolidBrush(known && i < lit ? c : Rgb(27, 38, 49)))
                    g.FillRectangle(b, r);
            }
        }

        void DrawMeter(Graphics g, float x, float y, float w, string label, long rem, long lim)
        {
            bool known = lim > 0 && rem >= 0;
            Txt(g, label, fXs, Rgb(140, 158, 172), x, y, StringAlignment.Near);
            string val = known ? Abbrev(rem) + " / " + Abbrev(lim) : "--";
            Txt(g, val, fXs, Rgb(214, 226, 236), x + w, y, StringAlignment.Far);
            double frac = known ? Math.Min(1.0, (double)rem / lim) : 0;
            DrawBar(g, x, y + 10f, w, 8f, frac, known);
        }

        void DrawCenter(Graphics g, float x0)
        {
            float cx = x0 + CW / 2f;
            Txt(g, "CLAUDE", fBrand, Rgb(224, 122, 84), cx, 26f, StringAlignment.Center);
            if (!string.IsNullOrEmpty(plan))
                Txt(g, plan + " PLAN", fXs, Rgb(130, 148, 162), cx, 46f, StringAlignment.Center);
            using (Pen p = new Pen(Rgb(32, 44, 56), 1f)) g.DrawLine(p, x0 + 16f, 60f, x0 + CW - 16f, 60f);

            if (st.ShowApi)
            {
                Txt(g, "API RATE LIMIT", fXs, Rgb(120, 138, 152), cx, 74f, StringAlignment.Center);
                if (apiHave)
                {
                    DrawMeter(g, x0 + 14f, 92f, CW - 28f, "요청 잔여", reqRem, reqLim);
                    DrawMeter(g, x0 + 14f, 126f, CW - 28f, "토큰 잔여", tokRem, tokLim);
                }
                else
                {
                    string t;
                    if (!string.IsNullOrEmpty(apiMsg)) t = apiMsg;
                    else if (hasKey) t = "불러오는 중...";
                    else t = "API 키 없음\n우클릭 > API 키 설정";
                    WrapTxt(g, t, fXs, Rgb(128, 142, 154), new RectangleF(x0 + 12f, 90f, CW - 24f, 60f));
                }
            }
            else
            {
                Txt(g, DateTime.Now.ToString("HH:mm"), fClock, Rgb(214, 226, 236), cx, 100f, StringAlignment.Center);
                Txt(g, DateTime.Now.ToString("M월 d일 (ddd)", ko), fSm, Rgb(130, 148, 162), cx, 132f, StringAlignment.Center);
            }

            string msg = subMsg;
            if (string.IsNullOrEmpty(msg) && apiHave && !string.IsNullOrEmpty(apiMsg)) msg = apiMsg;
            if (!string.IsNullOrEmpty(msg))
                WrapTxt(g, msg, fXs, Rgb(255, 176, 32), new RectangleF(x0 + 10f, 156f, CW - 20f, 44f));

            string upd = subHave ? "갱신 " + subUpdated.ToString("HH:mm:ss") : "연결 대기 중";
            Txt(g, upd, fXs, Rgb(96, 112, 126), cx, 208f, StringAlignment.Center);
            string tags = "";
            if (st.Locked) tags += "[잠금] ";
            if (clickThrough) tags += "[클릭통과]";
            if (tags.Length > 0) Txt(g, tags, fXs, Rgb(120, 138, 152), cx, 224f, StringAlignment.Center);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (animTimer != null) animTimer.Dispose();
                if (secTimer != null) secTimer.Dispose();
                if (fBig != null) fBig.Dispose();
                if (fMid != null) fMid.Dispose();
                if (fSm != null) fSm.Dispose();
                if (fXs != null) fXs.Dispose();
                if (fTick != null) fTick.Dispose();
                if (fBrand != null) fBrand.Dispose();
                if (fClock != null) fClock.Dispose();
                if (sf != null) sf.Dispose();
                if (sfWrap != null) sfWrap.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    static class Program
    {
        public static bool JustUpdated;

        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch { }
            foreach (string a in args) if (a == "--updated") JustUpdated = true;
            bool created;
            Mutex mutex = new Mutex(true, "Local\\ClaudeGaugeOverlay", out created);
            if (!created && JustUpdated)
            {
                for (int i = 0; i < 20 && !created; i++)
                {
                    mutex.Close();
                    Thread.Sleep(500);
                    mutex = new Mutex(true, "Local\\ClaudeGaugeOverlay", out created);
                }
            }
            if (!created)
            {
                MessageBox.Show("이미 실행 중이에요.\n화면 오른쪽 아래 트레이(^) 아이콘을 확인해 보세요.", "Claude 잔여량");
                return;
            }
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new GaugeForm());
            GC.KeepAlive(mutex);
        }
    }
}
