using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using MelonLoader;

namespace SongRequestMod
{
    // 游戏里有个全局命名空间也叫 Process(MusicSelectProcess 等), 不起别名的话 Process 会解析成它
    using Proc = System.Diagnostics.Process;

    /// <summary>
    /// 远程分享: 用 Cloudflare 免费临时隧道(cloudflared "quick tunnel")把本机点歌台映射成一个公网 https 链接,
    /// 贴给别人就能远程点歌。不需要注册账号、不需要改路由器 / 防火墙。
    ///   - cloudflared.exe 放在 Mods\SongRequestMod\ 下; 没有就自动下载(官方 GitHub Release)
    ///   - 每次开启都会换一个新链接 + 新密钥, 关掉后旧链接立刻失效
    ///   - 远程访问必须带密钥(?k=), 首次打开后写进 cookie, 之后页面内请求自动带上
    /// </summary>
    internal static class Tunnel
    {
        private const string DownloadUrl =
            "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

        private static readonly object _lock = new object();
        private static Proc _proc;
        private static string _state = "off";     // off / downloading / starting / running / error
        private static string _msg = "";
        private static string _baseUrl;           // https://xxx.trycloudflare.com
        private static string _key;               // 本次分享的访问密钥

        internal static string State { get { lock (_lock) { CheckExited(); return _state; } } }
        internal static string Key { get { lock (_lock) { return _state == "running" ? _key : null; } } }

        /// <summary>给网页的状态 JSON(只给本机/局域网看, 里面有带密钥的链接)</summary>
        internal static string Json()
        {
            lock (_lock)
            {
                CheckExited();
                string share = _state == "running" && _baseUrl != null ? _baseUrl + "/?k=" + _key : "";
                return "{\"ok\":true,\"state\":\"" + _state + "\",\"url\":\"" + SongTable.Escape(share)
                    + "\",\"msg\":\"" + SongTable.Escape(_msg) + "\"}";
            }
        }

        /// <summary>开启分享(后台线程做下载/启动, 立即返回)</summary>
        internal static void Start()
        {
            lock (_lock)
            {
                CheckExited();
                if (_state == "downloading" || _state == "starting" || _state == "running")
                {
                    return;
                }
                _state = "starting";
                _msg = "正在啟動通道…";
                _baseUrl = null;
                _key = NewKey();
                _lastLine = "";
            }
            Thread th = new Thread(Run);
            th.IsBackground = true;
            th.Start();
        }

        internal static void Stop()
        {
            Proc p;
            lock (_lock)
            {
                p = _proc;
                _proc = null;
                _state = "off";
                _msg = "";
                _baseUrl = null;
                _key = null;
            }
            Kill(p);
            DeletePidFile();
        }

        private static void Run()
        {
            try
            {
                KillOrphan();
                string exe = FindExe();
                if (exe == null)
                {
                    SetState("downloading", "首次使用, 正在下載 cloudflared(約 60MB)…");
                    exe = Download();
                    if (exe == null)
                    {
                        SetState("error", "下載 cloudflared 失敗。請手動下載 " + DownloadUrl
                            + " 並改名為 cloudflared.exe 放到 " + ExeCandidates()[0]);
                        return;
                    }
                    lock (_lock)
                    {
                        if (_state != "downloading") return;   // 下载途中用户点了关闭
                    }
                    SetState("starting", "正在啟動通道…");
                }

                // 先用默认协议(QUIC/UDP); 有的网络挡 UDP, 连不上就换 HTTP2(TCP) 再试一次
                string[] protocols = { "", " --protocol http2" };
                for (int a = 0; a < protocols.Length; a++)
                {
                    if (a > 0)
                    {
                        SetState("starting", "預設線路連不上, 改用 TCP 線路重試…");
                    }
                    bool? ok = TryStart(exe, protocols[a]);
                    if (ok != false)
                    {
                        return;   // true: 成功; null: 用户中途关闭
                    }
                }
                SetState("error", "連不上 Cloudflare, 請檢查網路(需要能連到外網 7844 埠)後重試。" + _lastLine);
            }
            catch (Exception e)
            {
                SetState("error", "啟動通道失敗: " + e.Message);
            }
        }

        /// <summary>启动一次 cloudflared 并等到隧道真正连上(true); 连不上(false); 用户中途关闭(null)</summary>
        private static bool? TryStart(string exe, string extraArgs)
        {
            int port = Config.Port;
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            // --http-host-header: 把 Host 改回 127.0.0.1, 否则 HttpListener 按前缀匹配会直接拒掉
            psi.Arguments = "tunnel --no-autoupdate --url http://127.0.0.1:" + port
                + " --http-host-header 127.0.0.1:" + port + extraArgs;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            Proc p = Proc.Start(psi);
            lock (_lock)
            {
                if (_state != "starting")
                {
                    Kill(p);   // 启动途中用户点了关闭
                    return null;
                }
                _proc = p;
                _pendingUrl = null;
            }
            WritePidFile(p);
            StartReader(p, p.StandardError);
            StartReader(p, p.StandardOutput);

            // 链接一般几秒就打出来, 但要等 "Registered tunnel connection" 才真的能访问; 最多等 25 秒
            for (int i = 0; i < 250; i++)
            {
                Thread.Sleep(100);
                lock (_lock)
                {
                    if (_proc != p) return null;
                    if (_baseUrl != null) return true;
                    if (p.HasExited) break;
                }
            }
            lock (_lock)
            {
                if (_proc != p) return null;
                if (_baseUrl != null) return true;
                _proc = null;
            }
            Kill(p);
            DeletePidFile();
            return false;
        }

        private static string _pendingUrl;   // cloudflared 已分配、但还没连上边缘节点的链接

        private static readonly Regex UrlRe = new Regex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);
        private static string _lastLine = "";

        private static void StartReader(Proc p, StreamReader reader)
        {
            Thread th = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf(" ERR ", StringComparison.Ordinal) >= 0)
                        {
                            _lastLine = " 最後錯誤: " + line.Trim();
                        }
                        ModLog.Info("[SongRequest] cloudflared: " + line);
                        Match m = UrlRe.Match(line);
                        // api.trycloudflare.com 是它申请隧道用的接口地址, 不是分配给我们的链接
                        if (m.Success && !m.Value.StartsWith("https://api.", StringComparison.OrdinalIgnoreCase))
                        {
                            lock (_lock)
                            {
                                if (_proc == p && _pendingUrl == null) _pendingUrl = m.Value.ToLowerInvariant();
                            }
                            continue;
                        }
                        if (line.IndexOf("Registered tunnel connection", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                        string share = null;
                        lock (_lock)
                        {
                            if (_proc == p && _pendingUrl != null && _baseUrl == null)
                            {
                                _baseUrl = _pendingUrl;
                                _state = "running";
                                _msg = "";
                                share = _baseUrl + "/?k=" + _key;
                            }
                        }
                        if (share != null)
                        {
                            ModLog.Always("[SongRequest] 遠端分享已開啟: " + share);
                        }
                    }
                }
                catch
                {
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        /// <summary>隧道进程意外退出 -> 状态回到 error(调用方持锁)</summary>
        private static void CheckExited()
        {
            if (_proc == null || _state != "running")
            {
                return;
            }
            try
            {
                if (_proc.HasExited)
                {
                    _state = "error";
                    _msg = "通道已斷開(cloudflared 退出), 請重新開啟分享";
                    _baseUrl = null;
                    _key = null;
                    _proc = null;
                }
            }
            catch
            {
            }
        }

        private static void SetState(string state, string msg)
        {
            lock (_lock)
            {
                if (_state == "off") return;   // 用户已关闭, 不要把状态又改回来
                _state = state;
                _msg = msg;
            }
            if (state == "error") MelonLogger.Warning("[SongRequest] 遠端分享: " + msg);
        }

        /// <summary>访问密钥: 16 字节随机数, URL 安全的 base64</summary>
        private static string NewKey()
        {
            byte[] b = new byte[16];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(b);
            }
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>常量时间比较, 免得密钥被逐字符试出来</summary>
        internal static bool KeyMatches(string given)
        {
            string key = Key;
            if (key == null || given == null || given.Length != key.Length)
            {
                return false;
            }
            int diff = 0;
            for (int i = 0; i < key.Length; i++)
            {
                diff |= key[i] ^ given[i];
            }
            return diff == 0;
        }

        // ── cloudflared.exe 查找 / 下载 ─────────────────────────────
        private static string[] ExeCandidates()
        {
            return new string[]
            {
                Path.Combine(Web.GameDir, "Mods", "SongRequestMod", "cloudflared.exe"),
                Path.Combine(Web.GameDir, "Mods", "cloudflared.exe"),
                Path.Combine(Web.GameDir, "cloudflared.exe")
            };
        }

        private static string FindExe()
        {
            string[] cands = ExeCandidates();
            for (int i = 0; i < cands.Length; i++)
            {
                string p = cands[i];
                if (!File.Exists(p)) continue;
                string why = ExeProblem(p);
                if (why == null) return p;
                // 坏掉的 exe(以前的版本会把下载到一半的文件当成完成) -> 我们自己的下载位置就删掉重下, 别的位置只跳过
                MelonLogger.Warning("[SongRequest] cloudflared.exe 無效(" + why + "): " + p);
                if (i == 0)
                {
                    try { File.Delete(p); } catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// 检查是不是完整的 64 位 Windows 程序: MZ 头 -> PE 头 -> 机器类型 x64 -> 每个区段的数据都在文件范围内
        /// (下载被截断时最后几个区段会落到文件外)。没问题返回 null, 否则返回原因。
        /// </summary>
        private static string ExeProblem(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    long len = fs.Length;
                    if (len < 4 * 1024 * 1024) return "檔案太小 " + len + " bytes";
                    if (br.ReadUInt16() != 0x5A4D) return "不是 Windows 執行檔";
                    fs.Position = 0x3C;
                    int pe = br.ReadInt32();
                    if (pe <= 0 || pe > len - 24) return "檔頭損壞";
                    fs.Position = pe;
                    if (br.ReadUInt32() != 0x00004550) return "檔頭損壞";
                    ushort machine = br.ReadUInt16();
                    if (machine != 0x8664) return "不是 64 位元版本(machine=0x" + machine.ToString("X") + ")";
                    ushort sections = br.ReadUInt16();
                    fs.Position = pe + 20;
                    ushort optSize = br.ReadUInt16();
                    long table = pe + 24 + optSize;
                    for (int i = 0; i < sections; i++)
                    {
                        fs.Position = table + i * 40 + 16;
                        long rawSize = br.ReadUInt32();
                        long rawPtr = br.ReadUInt32();
                        if (rawSize > 0 && rawPtr + rawSize > len) return "檔案不完整(下載被中斷)";
                    }
                }
                return null;
            }
            catch (Exception e)
            {
                return "讀取失敗: " + e.Message;
            }
        }

        /// <summary>
        /// 下载 cloudflared。交给外部进程(不能在游戏进程里改 ServicePointManager.SecurityProtocol, 那是全进程设置)。
        ///   - curl: 不设总时长, 只在"每秒不到 2KB 持续 60 秒"时判定卡住; 断了用 -C - 接着下, 最多 3 次
        ///   - 没有 curl 才用 PowerShell(关掉进度条, 不然 Invoke-WebRequest 会慢好几倍)
        ///   - 退出码不是 0 或文件不完整都不算成功(以前下载到一半被掐断也会被当成完成)
        /// </summary>
        private static string Download()
        {
            string dst = ExeCandidates()[0];
            string tmp = dst + ".download";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
            }
            catch
            {
            }
            string curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
            // 上次已经下完(只是没来得及改名) -> 直接用
            bool ok = File.Exists(tmp) && ExeProblem(tmp) == null;
            if (!ok && File.Exists(curl))
            {
                for (int attempt = 0; attempt < 3 && !ok && StillStarting(); attempt++)
                {
                    int code = RunDownloader(curl, "-L -f -s -C - --connect-timeout 15 --speed-limit 2048 --speed-time 60 -o \""
                        + tmp + "\" \"" + DownloadUrl + "\"", tmp);
                    ok = code == 0 && ExeProblem(tmp) == null;
                    if (!ok) ModLog.Info("[SongRequest] curl 下載第 " + (attempt + 1) + " 次未完成(結束碼 " + code + ")");
                    if (code == 0 && !ok) DeleteQuietly(tmp);   // 下完了但不是正确的程序 -> 删掉重来, 别再接着续
                }
            }
            // 只有没有 curl(老系统)才用 PowerShell; curl 试了 3 次都不行多半是网络问题, 换 PowerShell 也没用还会丢掉已下载的部分
            if (!ok && !File.Exists(curl) && StillStarting())
            {
                DeleteQuietly(tmp);   // PowerShell 不能续传, 从头下
                int code = RunDownloader("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \""
                    + "$ProgressPreference='SilentlyContinue';"
                    + "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;"
                    + "try{Invoke-WebRequest -UseBasicParsing -Uri '" + DownloadUrl + "' -OutFile '" + tmp + "';exit 0}catch{exit 1}\"", tmp);
                ok = code == 0 && ExeProblem(tmp) == null;
                if (code == 0 && !ok) DeleteQuietly(tmp);
            }
            try
            {
                if (ok)
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(tmp, dst);
                    ModLog.Always("[SongRequest] cloudflared 已下載: " + dst);
                    return dst;
                }
                // 没下完的 .download 留着: 下次开启分享时 curl 会接着下
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SongRequest] 儲存 cloudflared 失敗: " + e.Message);
            }
            return null;
        }

        /// <summary>分享还在开启中(没被用户关掉、也没失败)</summary>
        private static bool StillStarting()
        {
            lock (_lock)
            {
                return _state == "starting" || _state == "downloading";
            }
        }

        private static void DeleteQuietly(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>跑下载进程, 每秒把已下载大小报到分享面板; 用户关闭分享就停掉。返回退出码(-1 = 没跑成/被停)</summary>
        private static int RunDownloader(string exe, string args, string tmp)
        {
            try
            {
                if (Path.IsPathRooted(exe) && !File.Exists(exe)) return -1;
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Proc p = Proc.Start(psi))
                {
                    int started = Environment.TickCount;
                    while (!p.WaitForExit(1000))
                    {
                        if (!StillStarting() || unchecked(Environment.TickCount - started) > 30 * 60 * 1000)
                        {
                            Kill(p);
                            return -1;
                        }
                        long got = 0;
                        try { if (File.Exists(tmp)) got = new FileInfo(tmp).Length; } catch { }
                        SetState("downloading", "首次使用, 正在下載 cloudflared(約 60MB)… 已下載 "
                            + (got / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB");
                    }
                    return p.ExitCode;
                }
            }
            catch (Exception e)
            {
                ModLog.Info("[SongRequest] 下載失敗(" + Path.GetFileName(exe) + "): " + e.Message);
                return -1;
            }
        }

        // ── 进程收尾: 游戏崩了没来得及关的隧道, 下次启动时按 pid 文件清掉 ──
        private static string PidFile
        {
            get { return Path.Combine(Web.GameDir, "Mods", "SongRequestMod", "cloudflared.pid"); }
        }

        private static void WritePidFile(Proc p)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PidFile));
                File.WriteAllText(PidFile, p.Id.ToString());
            }
            catch
            {
            }
        }

        private static void DeletePidFile()
        {
            try { if (File.Exists(PidFile)) File.Delete(PidFile); } catch { }
        }

        internal static void KillOrphan()
        {
            try
            {
                if (!File.Exists(PidFile)) return;
                int pid;
                if (int.TryParse(File.ReadAllText(PidFile).Trim(), out pid))
                {
                    Proc old = Proc.GetProcessById(pid);
                    if (old.ProcessName.IndexOf("cloudflared", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Kill(old);
                    }
                }
            }
            catch
            {
            }
            DeletePidFile();
        }

        private static void Kill(Proc p)
        {
            if (p == null) return;
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch
            {
            }
        }
    }
}
