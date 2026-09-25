using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MelonLoader;

namespace SongRequestMod
{
    // 游戏里有个全局命名空间也叫 Process(MusicSelectProcess 等), 不起别名的话 Process 会解析成它
    using Proc = System.Diagnostics.Process;

    /// <summary>
    /// 远程分享: 把本机点歌台映射成公网链接, 贴给别人就能远程点歌。全程免注册、不用改路由器。
    /// 按顺序尝试, 第一个连上的就用:
    ///   1. Cloudflare 临时隧道(cloudflared, 没有就自动从 GitHub 下载; 先 QUIC 再 HTTP2)
    ///   2. localhost.run(用 Windows 自带的 ssh, 不用下载; 免费域名几小时会换一次)
    /// 另外本机有公网 IPv6 时, 额外给一条 IPv6 直连链接(不经过海外服务器, 但要对方也有 IPv6、路由器放行入站)。
    /// 每次开启都换新链接 + 新密钥, 关掉后旧链接立刻失效; 远程访问必须带密钥(?k=), 首次打开后写进 cookie。
    /// </summary>
    internal static class Tunnel
    {
        private const string DownloadUrl =
            "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

        private static readonly object _lock = new object();
        private static Proc _proc;
        private static string _state = "off";     // off / downloading / starting / running / error
        private static string _msg = "";
        private static string _tunnelUrl;         // https://xxx.trycloudflare.com 或 https://xxx.lhr.life
        private static string _tunnelName;        // Cloudflare / localhost.run
        private static string _v6Url;             // http://[240e:..]:8790
        private static string _key;               // 本次分享的访问密钥

        internal static string State { get { lock (_lock) { CheckExited(); return _state; } } }
        /// <summary>分享开着(包括隧道还在连、但 IPv6 链接已经能用)时才有密钥</summary>
        internal static string Key { get { lock (_lock) { return _state == "off" || _state == "error" ? null : _key; } } }

        /// <summary>给网页的状态 JSON(只给本机/局域网看, 里面有带密钥的链接)</summary>
        internal static string Json()
        {
            lock (_lock)
            {
                CheckExited();
                StringBuilder links = new StringBuilder();
                string primary = "";
                if (_key != null && _tunnelUrl != null)
                {
                    primary = _tunnelUrl + "/?k=" + _key;
                    links.Append("{\"name\":\"" + SongTable.Escape(_tunnelName) + "\",\"url\":\"" + SongTable.Escape(primary) + "\"}");
                }
                if (_key != null && _v6Url != null && _state != "off" && _state != "error")
                {
                    string v6 = _v6Url + "/?k=" + _key;
                    if (primary.Length == 0) primary = v6;
                    if (links.Length > 0) links.Append(",");
                    links.Append("{\"name\":\"IPv6 直连\",\"url\":\"" + SongTable.Escape(v6) + "\"}");
                }
                return "{\"ok\":true,\"state\":\"" + _state + "\",\"url\":\"" + SongTable.Escape(primary)
                    + "\",\"links\":[" + links + "],\"msg\":\"" + SongTable.Escape(_msg) + "\"}";
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
                _msg = "正在启动隧道…";
                _tunnelUrl = null;
                _tunnelName = null;
                _key = NewKey();
                _lastLine = "";
                // IPv6 直连不用等, 马上就能给链接
                _v6Url = StartDirect();
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
                _tunnelUrl = null;
                _tunnelName = null;
                _v6Url = null;
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
                int proxyPort = EnsureProxy();
                if (TryCloudflare(proxyPort) != false || !StillStarting())
                {
                    return;
                }
                SetState("starting", "Cloudflare 连不上, 改用 localhost.run 重试…");
                if (TryLocalhostRun(proxyPort) != false || !StillStarting())
                {
                    return;
                }
                lock (_lock)
                {
                    if (_state == "off") return;
                    if (_v6Url != null)
                    {
                        _state = "running";
                        _msg = "隧道都连不上, 只有 IPv6 直连可用(需要对方也有 IPv6)。" + _lastLine;
                        ModLog.Always("[SongRequest] 远程分享(仅 IPv6 直连): " + _v6Url + "/?k=" + _key);
                        return;
                    }
                }
                SetState("error", "Cloudflare 和 localhost.run 都连不上, 请检查网络后重试。" + _lastLine);
            }
            catch (Exception e)
            {
                SetState("error", "启动隧道失败: " + e.Message);
            }
        }

        private static bool StillStarting()
        {
            lock (_lock)
            {
                return _state == "starting" || _state == "downloading";
            }
        }

        // ── 线路 1: Cloudflare ─────────────────────────────────────
        private static bool? TryCloudflare(int proxyPort)
        {
            string exe = FindExe();
            if (exe == null)
            {
                SetState("downloading", "首次使用, 正在下载 cloudflared(约 50MB)…");
                exe = Download();
                if (!StillStarting()) return null;   // 下载途中用户点了关闭
                if (exe == null)
                {
                    _lastLine = " (cloudflared 下载失败, 可手动下载 " + DownloadUrl + " 改名 cloudflared.exe 放到 "
                        + ExeCandidates()[0] + ")";
                    return false;
                }
                SetState("starting", "正在启动隧道…");
            }
            // 先用默认协议(QUIC/UDP); 有的网络挡 UDP, 连不上就换 HTTP2(TCP) 再试一次
            string[] protocols = { "", " --protocol http2" };
            for (int a = 0; a < protocols.Length; a++)
            {
                if (a > 0)
                {
                    SetState("starting", "默认线路连不上, 改用 TCP 线路重试…");
                }
                bool? ok = TryStart(exe, "tunnel --no-autoupdate --url http://127.0.0.1:" + proxyPort + protocols[a],
                    "Cloudflare", CloudflareUrl, true);
                if (ok != false)
                {
                    return ok;   // true: 成功; null: 用户中途关闭
                }
            }
            return false;
        }

        // ── 线路 2: localhost.run(ssh 反向隧道) ────────────────────
        private static bool? TryLocalhostRun(int proxyPort)
        {
            string ssh = FindSsh();
            // -T: 不要终端; UserKnownHostsFile=NUL: 不往用户目录写 known_hosts
            // stdin 要一直开着(RedirectStandardInput), 关掉的话会话会被对方结束
            return TryStart(ssh, "-T -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL"
                + " -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes"
                + " -R 80:127.0.0.1:" + proxyPort + " nokey@localhost.run",
                "localhost.run", LhrUrl, false);
        }

        private static string FindSsh()
        {
            string win = Environment.GetEnvironmentVariable("WINDIR");
            if (string.IsNullOrEmpty(win)) win = @"C:\Windows";
            // 32 位进程访问 System32 会被重定向到 SysWOW64(那里没有 OpenSSH), 所以先试 Sysnative
            string[] cands =
            {
                Path.Combine(win, "Sysnative", "OpenSSH", "ssh.exe"),
                Path.Combine(win, "System32", "OpenSSH", "ssh.exe")
            };
            foreach (string c in cands)
            {
                if (File.Exists(c)) return c;
            }
            return "ssh";
        }

        private static readonly Regex CloudflareUrl = new Regex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);
        private static readonly Regex LhrUrl = new Regex(@"https://[a-z0-9-]+\.lhr\.(life|pro)", RegexOptions.IgnoreCase);

        /// <summary>
        /// 启动一次隧道进程, 等到链接可用(true) / 连不上(false) / 用户中途关闭(null)。
        /// waitRegistered: cloudflared 会先打印链接、之后才真正连上边缘节点, 要等 "Registered tunnel connection"
        /// </summary>
        private static bool? TryStart(string exe, string args, string name, Regex urlRe, bool waitRegistered)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardInput = true;
            Proc p;
            try
            {
                p = Proc.Start(psi);
            }
            catch (Exception e)
            {
                _lastLine = " (" + name + " 启动失败: " + e.Message + ")";
                return false;
            }
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
            StartReader(p, p.StandardError, name, urlRe, waitRegistered);
            StartReader(p, p.StandardOutput, name, urlRe, waitRegistered);

            // 最多等 25 秒
            for (int i = 0; i < 250; i++)
            {
                Thread.Sleep(100);
                lock (_lock)
                {
                    if (_proc != p) return null;
                    if (_tunnelUrl != null) return true;
                    if (p.HasExited) break;
                }
            }
            lock (_lock)
            {
                if (_proc != p) return null;
                if (_tunnelUrl != null) return true;
                _proc = null;
            }
            Kill(p);
            DeletePidFile();
            return false;
        }

        private static string _pendingUrl;   // cloudflared 已分配、但还没连上边缘节点的链接
        private static string _lastLine = "";

        private static void StartReader(Proc p, StreamReader reader, string name, Regex urlRe, bool waitRegistered)
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
                            _lastLine = " 最后错误: " + line.Trim();
                        }
                        ModLog.Info("[SongRequest] " + name + ": " + line);
                        string found = null;
                        Match m = urlRe.Match(line);
                        // api.trycloudflare.com 是 cloudflared 申请隧道用的接口地址, 不是分配给我们的链接
                        if (m.Success && !m.Value.StartsWith("https://api.", StringComparison.OrdinalIgnoreCase))
                        {
                            found = m.Value.ToLowerInvariant();
                        }
                        bool registered = line.IndexOf("Registered tunnel connection", StringComparison.OrdinalIgnoreCase) >= 0;
                        string share = null;
                        lock (_lock)
                        {
                            if (_proc != p || _key == null)
                            {
                                continue;
                            }
                            if (found != null && waitRegistered)
                            {
                                if (_pendingUrl == null) _pendingUrl = found;
                                found = null;
                            }
                            else if (registered && waitRegistered && _pendingUrl != null && _tunnelUrl == null)
                            {
                                found = _pendingUrl;
                            }
                            // localhost.run 免费域名几小时会换一次, 换了就跟着更新
                            if (found != null && found != _tunnelUrl)
                            {
                                _tunnelUrl = found;
                                _tunnelName = name;
                                _state = "running";
                                _msg = "";
                                share = _tunnelUrl + "/?k=" + _key;
                            }
                        }
                        if (share != null)
                        {
                            ModLog.Always("[SongRequest] 远程分享已开启(" + name + "): " + share);
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

        /// <summary>隧道进程意外退出(调用方持锁): 还有 IPv6 就降级, 否则回到 error</summary>
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
                    _proc = null;
                    _tunnelUrl = null;
                    _tunnelName = null;
                    if (_v6Url != null)
                    {
                        _msg = "隧道已断开, 只剩 IPv6 直连可用; 要恢复隧道请关闭后重新开启分享";
                    }
                    else
                    {
                        _state = "error";
                        _msg = "隧道已断开, 请重新开启分享";
                        _key = null;
                    }
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
                if (state == "error") _key = null;
            }
            if (state == "error") MelonLogger.Warning("[SongRequest] 远程分享: " + msg);
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

        // ── 本机转发口 ─────────────────────────────────────────────
        // 隧道不直接连点歌台, 而是连这个只听 127.0.0.1 的转发口, 由它:
        //   - 把 Host 改回 127.0.0.1(否则 HttpListener 按前缀匹配会直接拒掉 xxx.lhr.life 之类的域名)
        //   - 加上 X-SongRequest-Remote, 让 Web 一定把它当远程请求(要密钥), 不依赖各家隧道自己加的头
        //   - 强制 Connection: close, 一个连接只走一个请求, 这样每个请求的头都能被改写
        // IPv6 直连也走同一套(游戏的 Mono HttpListener 绑不了 IPv6 地址, 只能用 TcpListener 接进来再转)
        internal const string RemoteHeader = "X-SongRequest-Remote";
        private static TcpListener _proxy;
        private static int _proxyPort;
        private static TcpListener _direct;
        private static string _directUrl;

        /// <summary>
        /// IPv6 直连: 本机有公网 IPv6 就在它的点歌台端口上接连接, 转给点歌台(标记 v6, 一律要密钥)。
        /// 只开一次, 之后每次开启分享复用; 没开分享时进来的请求全部 403。
        /// </summary>
        private static string StartDirect()
        {
            if (!Config.IPv6Direct)
            {
                return null;
            }
            if (_direct != null)
            {
                return _directUrl;
            }
            string ip = Web.PublicIPv6();
            if (ip == null)
            {
                return null;
            }
            try
            {
                TcpListener l = new TcpListener(IPAddress.Parse(ip), Config.Port);
                l.Start();
                _direct = l;
                _directUrl = "http://[" + ip + "]:" + Config.Port;
                AcceptLoop(l, "v6");
                ModLog.Info("[SongRequest] IPv6 直连已监听: " + _directUrl);
                return _directUrl;
            }
            catch (Exception e)
            {
                ModLog.Info("[SongRequest] IPv6 直连监听失败: " + e.Message);
                return null;
            }
        }

        private static int EnsureProxy()
        {
            lock (_lock)
            {
                if (_proxy != null) return _proxyPort;
                TcpListener l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                _proxy = l;
                _proxyPort = ((IPEndPoint)l.LocalEndpoint).Port;
            }
            AcceptLoop(_proxy, "1");
            return _proxyPort;
        }

        private static void AcceptLoop(TcpListener l, string mark)
        {
            Thread th = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        TcpClient c = l.AcceptTcpClient();
                        Thread w = new Thread(() => Relay(c, mark));
                        w.IsBackground = true;
                        w.Start();
                    }
                    catch
                    {
                        Thread.Sleep(200);
                    }
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private static void Relay(TcpClient client, string mark)
        {
            TcpClient server = null;
            try
            {
                NetworkStream cs = client.GetStream();
                // 读到请求头结束(\r\n\r\n), 最多 32KB
                byte[] buf = new byte[32768];
                int len = 0, end = -1;
                while (end < 0)
                {
                    if (len == buf.Length) return;
                    int n = cs.Read(buf, len, buf.Length - len);
                    if (n <= 0) return;
                    len += n;
                    for (int i = Math.Max(0, len - n - 3); i + 3 < len; i++)
                    {
                        if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                        {
                            end = i + 4;
                            break;
                        }
                    }
                }
                string[] lines = Latin1.GetString(buf, 0, end - 4).Split(new[] { "\r\n" }, StringSplitOptions.None);
                StringBuilder head = new StringBuilder();
                head.Append(lines[0]).Append("\r\n");
                head.Append("Host: 127.0.0.1:").Append(Config.Port).Append("\r\n");
                head.Append("Connection: close\r\n");
                head.Append(RemoteHeader).Append(": ").Append(mark).Append("\r\n");
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    string name = colon > 0 ? lines[i].Substring(0, colon).Trim().ToLowerInvariant() : "";
                    if (name == "host" || name == "connection" || name == "keep-alive"
                        || name == "proxy-connection" || name == RemoteHeader.ToLowerInvariant())
                    {
                        continue;
                    }
                    head.Append(lines[i]).Append("\r\n");
                }
                head.Append("\r\n");

                server = new TcpClient();
                server.Connect(IPAddress.Loopback, Config.Port);
                NetworkStream ss = server.GetStream();
                byte[] h = Latin1.GetBytes(head.ToString());
                ss.Write(h, 0, h.Length);
                if (len > end) ss.Write(buf, end, len - end);   // 已经读进来的请求体

                TcpClient srv = server;
                Thread up = new Thread(() => Pump(cs, ss, srv));
                up.IsBackground = true;
                up.Start();
                Pump(ss, cs, client);
            }
            catch
            {
            }
            finally
            {
                try { client.Close(); } catch { }
                try { if (server != null) server.Close(); } catch { }
            }
        }

        private static void Pump(Stream from, Stream to, TcpClient closeWhenDone)
        {
            byte[] b = new byte[16384];
            try
            {
                int n;
                while ((n = from.Read(b, 0, b.Length)) > 0)
                {
                    to.Write(b, 0, n);
                    to.Flush();
                }
            }
            catch
            {
            }
            try { closeWhenDone.Close(); } catch { }
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
            foreach (string p in ExeCandidates())
            {
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private static string Download()
        {
            string dst = ExeCandidates()[0];
            string tmp = dst + ".download";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch
            {
            }
            // 交给外部进程下载: 不能在游戏进程里改 ServicePointManager.SecurityProtocol,
            // 那是全进程的设置, 会连带影响游戏自己跟服务器的 HTTPS 通信
            // 1) Windows 10+ 自带的 curl.exe
            RunDownloader(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe"),
                "-L -f -s --connect-timeout 15 --max-time 180 -o \"" + tmp + "\" \"" + DownloadUrl + "\"");
            // 2) 没有 curl 就用 PowerShell
            if (!File.Exists(tmp))
            {
                RunDownloader("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \""
                    + "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;"
                    + "Invoke-WebRequest -UseBasicParsing -Uri '" + DownloadUrl + "' -OutFile '" + tmp + "'\"");
            }
            try
            {
                // 正常的 exe 有几十 MB, 太小说明拿到的是错误页
                if (File.Exists(tmp) && new FileInfo(tmp).Length > 1000000)
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(tmp, dst);
                    ModLog.Always("[SongRequest] cloudflared 已下载: " + dst);
                    return dst;
                }
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SongRequest] 保存 cloudflared 失败: " + e.Message);
            }
            return null;
        }

        private static void RunDownloader(string exe, string args)
        {
            try
            {
                if (Path.IsPathRooted(exe) && !File.Exists(exe)) return;
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Proc p = Proc.Start(psi))
                {
                    if (!p.WaitForExit(200000))
                    {
                        Kill(p);
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.Info("[SongRequest] 下载失败(" + Path.GetFileName(exe) + "): " + e.Message);
            }
        }

        // ── 进程收尾: 游戏崩了没来得及关的隧道, 下次启动时按 pid 文件清掉 ──
        private static string PidFile
        {
            get { return Path.Combine(Web.GameDir, "Mods", "SongRequestMod", "tunnel.pid"); }
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

        private static void KillOrphan()
        {
            try
            {
                if (!File.Exists(PidFile)) return;
                int pid;
                if (int.TryParse(File.ReadAllText(PidFile).Trim(), out pid))
                {
                    Proc old = Proc.GetProcessById(pid);
                    string pn = old.ProcessName;
                    if (pn.IndexOf("cloudflared", StringComparison.OrdinalIgnoreCase) >= 0
                        || pn.Equals("ssh", StringComparison.OrdinalIgnoreCase))
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
