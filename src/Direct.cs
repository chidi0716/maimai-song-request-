using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SongRequestMod
{
    /// <summary>
    /// 远程分享的"直连"线路: 不经过任何第三方中转, 国内网络也能用, 全程免注册。
    ///   1. IPv6 直连: 本机有公网 IPv6 -> 在 [::]:网页端口 上接连接, 链接是 http://[IPv6]:端口
    ///   2. NAT 打洞: 用 STUN 问出"外面看到的 IPv4:端口", 对同一个本机端口持续保活让映射不过期,
    ///      再在这个端口上接连接(要求网络是 NAT1 / 全锥形); 顺带用 UPnP 让家里的路由器把这个端口转进来
    /// 两条线路都只是 TCP 转发: 接进来的请求改写 Host、打上 X-SongRequest-Remote 标记后转给本机点歌台,
    /// 点歌台看到标记就当远程请求处理(必须带分享密钥), 本身不直接暴露任何接口。
    /// 这里的活全在后台线程做(STUN / UPnP 要连网, 可能要好几秒), 不碰游戏对象。
    /// </summary>
    internal static class Direct
    {
        internal const string RemoteHeader = "X-SongRequest-Remote";

        // TCP STUN 服务器(RFC 5389, 端口默认 3478)。国内能连的放前面; 打洞时会用两台不同的比对映射是否一致
        internal static string[] StunServers =
        {
            "turn.cloud-rtc.com:80",
            "fwa.lifesizecloud.com",
            "global.turn.twilio.com",
            "stun.nextcloud.com",
            "stun.freeswitch.org",
            "stun.voip.blackberry.com",
            "stun.sipnet.com",
            "stun.radiojar.com",
            "stun.sonetel.com",
            "stun.telnyx.com",
            "turn.cloudflare.com"
        };
        /// <summary>保活用的网站(定时发一个 HEAD, 让 NAT 映射不过期)</summary>
        internal static string[] KeepAliveHosts = { "www.baidu.com", "www.qq.com", "www.bing.com" };
        internal static IPEndPoint SsdpTarget = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

        private const int KeepAliveSec = 15;
        private const int RecheckSec = 300;
        private const int UpnpRenewSec = 1800;
        private const int UpnpLeaseSec = 3600;
        private const int V6RefreshSec = 60;
        private const int ConnectTimeoutMs = 3000;
        private const int MaxRelays = 48;

        private static readonly object _lock = new object();
        private static int _gen;              // 每次 Start/Stop 都 +1; 后台线程发现代数变了就收工
        private static bool _pending;         // 正在检查 IPv6 / 打洞

        private static TcpListener _v6;
        private static string _v6Ip;
        private static string _v6Url;
        private static string _v6Note = "";

        private static TcpListener _nat;
        private static Socket _keep;
        private static string _keepHost;
        private static IPAddress _natLocalIp;
        private static int _natLocalPort;
        private static string _natUrl;
        private static string _natNote = "";
        private static UpnpMapping _upnp;

        private static int _relays;

        internal static bool Pending
        {
            get { lock (_lock) { return _pending; } }
        }

        /// <summary>开启(分享开启时调用, 立即返回)</summary>
        internal static void Start()
        {
            int gen;
            lock (_lock)
            {
                gen = ++_gen;
                _pending = true;
                _v6Note = Config.IPv6Direct ? "正在檢查…" : "已在設定中關閉";
                _natNote = Config.NatPunch ? "正在檢查…" : "已在設定中關閉";
            }
            Thread th = new Thread(() => Run(gen));
            th.IsBackground = true;
            th.Start();
        }

        /// <summary>关闭: 停止接连接、断开保活、删掉 UPnP 转发(退出游戏时也会调用, 同步做完)</summary>
        internal static void Stop()
        {
            UpnpMapping m;
            lock (_lock)
            {
                _gen++;
                _pending = false;
                CloseV6();
                m = CloseNat();
                _v6Note = "";
                _natNote = "";
            }
            if (m != null)
            {
                m.Delete();
            }
        }

        /// <summary>给 Tunnel.Json 用: 可用的链接 [名称, 地址(不含密钥), 说明]</summary>
        internal static List<string[]> Links()
        {
            List<string[]> list = new List<string[]>();
            lock (_lock)
            {
                if (_v6Url != null)
                {
                    list.Add(new string[] { "IPv6 直連", _v6Url, "不經過任何伺服器；對方也要有 IPv6（中國手機網路大多有）" });
                }
                if (_natUrl != null)
                {
                    list.Add(new string[] { "IPv4 直連（NAT 打洞）", _natUrl, _natNote });
                }
            }
            return list;
        }

        /// <summary>各线路目前状况(给分享面板显示, 包括用不了的原因)</summary>
        internal static List<string> Notes()
        {
            List<string> list = new List<string>();
            lock (_lock)
            {
                if (_v6Url == null && _v6Note.Length > 0)
                {
                    list.Add("IPv6 直連：" + _v6Note);
                }
                if (_natUrl == null && _natNote.Length > 0)
                {
                    list.Add("IPv4 直連：" + _natNote);
                }
            }
            return list;
        }

        private static bool Alive(int gen)
        {
            lock (_lock)
            {
                return gen == _gen;
            }
        }

        private static void Run(int gen)
        {
            try
            {
                if (Config.IPv6Direct)
                {
                    SetupV6(gen);
                }
                if (Config.NatPunch)
                {
                    SetupNat(gen);
                }
            }
            catch (Exception e)
            {
                ModLog.Info("直連線路啟動異常: " + e.Message);
            }
            finally
            {
                lock (_lock)
                {
                    if (gen == _gen)
                    {
                        _pending = false;
                    }
                }
            }
            Maintain(gen);
        }

        /// <summary>后台维护: 保活、IPv6 地址变化、定期复查映射、续 UPnP</summary>
        private static void Maintain(int gen)
        {
            int tick = 0;
            while (Alive(gen))
            {
                Thread.Sleep(1000);
                tick++;
                if (!Alive(gen))
                {
                    return;
                }
                try
                {
                    if (Config.IPv6Direct && tick % V6RefreshSec == 0)
                    {
                        RefreshV6(gen);
                    }
                    if (_nat == null)
                    {
                        continue;
                    }
                    bool broken = false;
                    if (tick % KeepAliveSec == 0)
                    {
                        broken = !KeepAlive(gen);
                    }
                    if (broken || tick % RecheckSec == 0)
                    {
                        Recheck(gen);
                    }
                    if (tick % UpnpRenewSec == 0)
                    {
                        UpnpMapping m;
                        lock (_lock)
                        {
                            m = _upnp;
                        }
                        if (m != null)
                        {
                            m.Add();
                        }
                    }
                }
                catch (Exception e)
                {
                    ModLog.Info("直連線路維護異常: " + e.Message);
                }
            }
        }

        // ── IPv6 直连 ──────────────────────────────────────────────

        private static void SetupV6(int gen)
        {
            string ip = PublicIPv6();
            if (ip == null)
            {
                SetV6(gen, null, null, "本機沒有公網 IPv6");
                return;
            }
            TcpListener l;
            try
            {
                // 绑 [::] 而不是具体地址: 运营商会不定期换 IPv6 前缀, 换了只要更新链接, 不用重新监听
                l = new TcpListener(IPAddress.IPv6Any, Config.Port);
                try
                {
                    l.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
                }
                catch
                {
                }
                l.Start();
            }
            catch (Exception e)
            {
                SetV6(gen, null, null, "監聽 IPv6 失敗: " + e.Message);
                return;
            }
            lock (_lock)
            {
                if (gen != _gen)
                {
                    try { l.Stop(); } catch { }
                    return;
                }
                _v6 = l;
            }
            Serve(l, "v6", gen);
            SetV6(gen, ip, "http://[" + ip + "]:" + Config.Port, "");
        }

        private static void RefreshV6(int gen)
        {
            string ip = PublicIPv6();
            string old;
            bool listening;
            lock (_lock)
            {
                old = _v6Ip;
                listening = _v6 != null;
            }
            if (ip == old)
            {
                return;
            }
            if (!listening)
            {
                if (ip != null)
                {
                    SetupV6(gen);   // 之前没有 IPv6, 现在有了
                }
                return;
            }
            if (ip == null)
            {
                SetV6(gen, null, null, "本機的公網 IPv6 不見了(網路斷線或換線?)");
            }
            else
            {
                SetV6(gen, ip, "http://[" + ip + "]:" + Config.Port, "");
            }
        }

        private static void SetV6(int gen, string ip, string url, string note)
        {
            bool changed;
            lock (_lock)
            {
                if (gen != _gen)
                {
                    return;
                }
                changed = url != null && url != _v6Url;
                _v6Ip = ip;
                _v6Url = url;
                _v6Note = note;
            }
            // 在锁外取密钥: Tunnel.Json 持 Tunnel 的锁来调 Links(), 这里反过来会死锁
            string key = changed ? Tunnel.Key : null;
            if (key != null)
            {
                ModLog.Always("遠端分享(IPv6 直連): " + url + "/?k=" + key);
            }
        }

        private static void CloseV6()
        {
            if (_v6 != null)
            {
                try { _v6.Stop(); } catch { }
                _v6 = null;
            }
            _v6Ip = null;
            _v6Url = null;
        }

        /// <summary>本机公网 IPv6(2000::/3), 优先固定地址, 不用隐私临时地址(几小时就换)和 Teredo / 6to4 隧道地址</summary>
        internal static string PublicIPv6()
        {
            string temp = null;
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface ni in
                    System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                        || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                        || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ua in
                        ni.GetIPProperties().UnicastAddresses)
                    {
                        IPAddress a = ua.Address;
                        if (a.AddressFamily != AddressFamily.InterNetworkV6)
                        {
                            continue;
                        }
                        byte[] b = a.GetAddressBytes();
                        bool global = (b[0] & 0xE0) == 0x20;
                        bool teredo = b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0;
                        bool sixToFour = b[0] == 0x20 && b[1] == 0x02;
                        if (!global || teredo || sixToFour)
                        {
                            continue;
                        }
                        string s = new IPAddress(b).ToString();   // 去掉 %scope
                        bool isTemp = false;
                        try
                        {
                            isTemp = ua.SuffixOrigin == System.Net.NetworkInformation.SuffixOrigin.Random;
                        }
                        catch
                        {
                        }
                        if (!isTemp)
                        {
                            return s;
                        }
                        if (temp == null)
                        {
                            temp = s;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.Info("取本機 IPv6 失敗: " + e.Message);
            }
            return temp;
        }

        // ── NAT 打洞 ───────────────────────────────────────────────

        private static void SetupNat(int gen)
        {
            // 1. 先随便用一个本机端口问一次 STUN, 拿到本机地址和端口
            IPEndPoint local, outer;
            int used;
            if (!StunFrom(new IPEndPoint(IPAddress.Any, 0), -1, out local, out outer, out used, gen))
            {
                SetNat(gen, null, "連不上 STUN 伺服器(用來查詢公網位址), 可能是網路擋了");
                return;
            }
            IPAddress lip = local.Address;
            int port = local.Port;
            IPEndPoint bind = new IPEndPoint(lip, port);

            // 2. 在同一个端口上接连接
            TcpListener l;
            try
            {
                l = new TcpListener(bind);
                l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                l.Start();
            }
            catch (Exception e)
            {
                SetNat(gen, null, "監聽埠 " + port + " 失敗: " + e.Message);
                return;
            }
            lock (_lock)
            {
                if (gen != _gen)
                {
                    try { l.Stop(); } catch { }
                    return;
                }
                _nat = l;
                _natLocalIp = lip;
                _natLocalPort = port;
            }
            Serve(l, "nat", gen);

            // 3. 让家里的路由器把这个端口转进来(没有 UPnP 也没关系: 光猫直接拨号 / 路由器本身是全锥形时照样能通)
            bool isPrivate = IsPrivate(lip);
            string upnpNote = "";
            if (isPrivate)
            {
                UpnpMapping m = UpnpMapping.Create(lip, port);
                if (m != null && m.Add())
                {
                    lock (_lock)
                    {
                        if (gen != _gen)
                        {
                            m.Delete();
                            return;
                        }
                        _upnp = m;
                    }
                    upnpNote = "已透過 UPnP 讓路由器轉發";
                }
                else
                {
                    upnpNote = "路由器沒有回應 UPnP";
                }
            }

            // 4. 从这个端口再问两台不同的 STUN: 映射一致才是"端点无关映射"(NAT1/NAT2/NAT3 之一), 不一致就是对称型, 打不了洞。
            //    要在建保活连接之前问: Windows 上同一端口已经有一条连接时, 再从这个端口连 STUN 会收不到回应
            IPEndPoint m1, m2;
            int s1, s2;
            if (!StunFrom(bind, -1, out local, out m1, out s1, gen))
            {
                SetNat(gen, null, "STUN 伺服器沒有回應");
                return;
            }
            bool checkedTwo = StunFrom(bind, s1, out local, out m2, out s2, gen);
            if (checkedTwo && !m1.Equals(m2))
            {
                ModLog.Info("NAT 打洞: 兩台 STUN 看到的位址不同 " + m1 + " / " + m2 + ", 是對稱型 NAT");
                UpnpMapping gone;
                lock (_lock)
                {
                    if (gen != _gen)
                    {
                        return;
                    }
                    gone = CloseNat();
                }
                if (gone != null)
                {
                    gone.Delete();
                }
                SetNat(gen, null, "網路是對稱型 NAT, 無法打洞");
                return;
            }
            string note = "要網路是 NAT1(全錐形)才打得開；打不開就用其他連結";
            if (!isPrivate && lip.Equals(m1.Address))
            {
                note = "本機直接有公網 IPv4";
            }
            else if (!checkedTwo)
            {
                note += "(只有一台 STUN 回應, 無法確認 NAT 類型)";
            }
            if (upnpNote.Length > 0)
            {
                note += "；" + upnpNote;
            }
            // 5. 保活: 对同一个本机端口保持一条连到外网的 TCP 连接, NAT 映射才不会过期
            if (!KeepAlive(gen))
            {
                ModLog.Info("NAT 打洞: 保活連線建立失敗, 映射可能很快過期");
            }
            ModLog.Info("NAT 打洞: 本機 " + bind + " -> 公網 " + m1 + (upnpNote.Length > 0 ? " (" + upnpNote + ")" : ""));
            SetNat(gen, "http://" + m1.Address + ":" + m1.Port, note);
        }

        /// <summary>定期(或保活断了之后)复查映射; 端口变了就更新链接</summary>
        private static void Recheck(int gen)
        {
            IPEndPoint bind;
            Socket keep;
            lock (_lock)
            {
                if (gen != _gen || _nat == null)
                {
                    return;
                }
                bind = new IPEndPoint(_natLocalIp, _natLocalPort);
                keep = _keep;
                _keep = null;
            }
            // 先断开保活再问 STUN(Windows 上两条连接共用一个本机端口时 STUN 会收不到回应), 问完马上重连
            Abort(keep);
            IPEndPoint local, outer;
            int used;
            bool ok = StunFrom(bind, -1, out local, out outer, out used, gen);
            KeepAlive(gen);
            if (!ok)
            {
                return;   // STUN 暂时连不上: 链接先留着
            }
            string url = "http://" + outer.Address + ":" + outer.Port;
            string note;
            lock (_lock)
            {
                if (gen != _gen || url == _natUrl)
                {
                    return;
                }
                note = _natNote;
            }
            ModLog.Info("NAT 打洞: 公網位址變了, 新位址 " + outer);
            SetNat(gen, url, note);
        }

        private static void SetNat(int gen, string url, string note)
        {
            bool changed;
            lock (_lock)
            {
                if (gen != _gen)
                {
                    return;
                }
                changed = url != null && url != _natUrl;
                _natUrl = url;
                _natNote = note;
            }
            string key = changed ? Tunnel.Key : null;   // 锁外取(见 SetV6)
            if (key != null)
            {
                ModLog.Always("遠端分享(IPv4 直連): " + url + "/?k=" + key);
            }
        }

        /// <summary>关掉打洞用的监听和保活(调用方持锁); 返回要删除的 UPnP 转发</summary>
        private static UpnpMapping CloseNat()
        {
            if (_nat != null)
            {
                try { _nat.Stop(); } catch { }
                _nat = null;
            }
            Abort(_keep);
            _keep = null;
            _natUrl = null;
            UpnpMapping m = _upnp;
            _upnp = null;
            return m;
        }

        private static bool IsPrivate(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && (b[1] & 0xC0) == 64);
        }

        /// <summary>建一个允许端口复用的 TCP 连接(打洞要让监听和对外连接共用同一个本机端口)</summary>
        private static Socket ConnectFrom(IPEndPoint bind, IPEndPoint to, int gen)
        {
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s.Bind(bind);
                IAsyncResult ar = s.BeginConnect(to, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs, false) || !Alive(gen))
                {
                    throw new TimeoutException("連線逾時");
                }
                s.EndConnect(ar);
                s.ReceiveTimeout = ConnectTimeoutMs;
                s.SendTimeout = ConnectTimeoutMs;
                return s;
            }
            catch
            {
                Abort(s);
                throw;
            }
        }

        /// <summary>
        /// 直接用 RST 关掉(SO_LINGER=0): 正常关闭会在本机留下几分钟的 TIME_WAIT,
        /// 之后再从同一个本机端口连同一台服务器会连不上
        /// </summary>
        private static void Abort(Socket s)
        {
            if (s == null)
            {
                return;
            }
            try { s.LingerState = new LingerOption(true, 0); } catch { }
            try { s.Close(); } catch { }
        }

        private static IPEndPoint Resolve(string hostPort, int defPort)
        {
            string host = hostPort;
            int port = defPort;
            int colon = hostPort.LastIndexOf(':');
            if (colon > 0)
            {
                host = hostPort.Substring(0, colon);
                port = int.Parse(hostPort.Substring(colon + 1));
            }
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
            {
                return new IPEndPoint(ip, port);
            }
            foreach (IPAddress a in Dns.GetHostAddresses(host))
            {
                if (a.AddressFamily == AddressFamily.InterNetwork)
                {
                    return new IPEndPoint(a, port);
                }
            }
            throw new Exception("解析不到 IPv4 位址: " + host);
        }

        /// <summary>
        /// 从 bind 这个本机端口问 STUN(按顺序试, 跳过下标 skip 那台)。
        /// local = 实际用的本机地址端口, outer = 外面看到的地址端口, used = 用的是哪一台
        /// </summary>
        private static bool StunFrom(IPEndPoint bind, int skip, out IPEndPoint local, out IPEndPoint outer, out int used, int gen)
        {
            local = null;
            outer = null;
            used = -1;
            string[] servers = StunServers;
            for (int i = 0; i < servers.Length && Alive(gen); i++)
            {
                if (i == skip)
                {
                    continue;
                }
                Socket s = null;
                try
                {
                    s = ConnectFrom(bind, Resolve(servers[i], 3478), gen);
                    local = (IPEndPoint)s.LocalEndPoint;
                    outer = StunQuery(s);
                    if (outer != null)
                    {
                        used = i;
                        return true;
                    }
                }
                catch (Exception e)
                {
                    ModLog.Info("STUN " + servers[i] + " 失敗: " + e.Message);
                }
                finally
                {
                    if (s != null)
                    {
                        Abort(s);
                    }
                }
            }
            return false;
        }

        private static readonly Random _rnd = new Random();

        /// <summary>RFC 5389 Binding Request(TCP), 读 XOR-MAPPED-ADDRESS / MAPPED-ADDRESS</summary>
        internal static IPEndPoint StunQuery(Socket s)
        {
            byte[] req = new byte[20];
            req[0] = 0x00;
            req[1] = 0x01;   // Binding Request, 长度 0
            req[4] = 0x21;
            req[5] = 0x12;
            req[6] = 0xA4;
            req[7] = 0x42;   // magic cookie
            lock (_rnd)
            {
                for (int i = 8; i < 20; i++)
                {
                    req[i] = (byte)_rnd.Next(256);
                }
            }
            s.Send(req);
            byte[] head = ReadExact(s, 20);
            if ((head[0] << 8 | head[1]) != 0x0101)
            {
                throw new Exception("不是 Binding Success 回應");
            }
            for (int i = 4; i < 20; i++)
            {
                if (head[i] != req[i])
                {
                    throw new Exception("交易 ID 不符");
                }
            }
            int len = head[2] << 8 | head[3];
            if (len > 2048)
            {
                throw new Exception("回應太長");
            }
            byte[] body = ReadExact(s, len);
            IPEndPoint mapped = null;
            int p = 0;
            while (p + 4 <= body.Length)
            {
                int type = body[p] << 8 | body[p + 1];
                int alen = body[p + 2] << 8 | body[p + 3];
                int v = p + 4;
                if (v + alen > body.Length)
                {
                    break;
                }
                if ((type == 0x0020 || type == 0x0001) && alen >= 8 && body[v + 1] == 0x01)
                {
                    int port = body[v + 2] << 8 | body[v + 3];
                    byte[] ip = new byte[] { body[v + 4], body[v + 5], body[v + 6], body[v + 7] };
                    if (type == 0x0020)
                    {
                        port ^= 0x2112;
                        ip[0] ^= 0x21;
                        ip[1] ^= 0x12;
                        ip[2] ^= 0xA4;
                        ip[3] ^= 0x42;
                        return new IPEndPoint(new IPAddress(ip), port);   // XOR 版优先
                    }
                    mapped = new IPEndPoint(new IPAddress(ip), port);
                }
                p = v + ((alen + 3) & ~3);   // 属性按 4 字节对齐
            }
            if (mapped == null)
            {
                throw new Exception("回應裡沒有對外位址");
            }
            return mapped;
        }

        private static byte[] ReadExact(Socket s, int n)
        {
            byte[] b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Receive(b, got, n - got, SocketFlags.None);
                if (r <= 0)
                {
                    throw new Exception("連線被關閉");
                }
                got += r;
            }
            return b;
        }

        /// <summary>发一次保活(连接没了就用同一个本机端口重连)。返回 false = 保活断了且重连不上</summary>
        private static bool KeepAlive(int gen)
        {
            Socket s;
            IPEndPoint bind;
            string host;
            lock (_lock)
            {
                if (gen != _gen || _nat == null)
                {
                    return true;
                }
                s = _keep;
                host = _keepHost;
                bind = new IPEndPoint(_natLocalIp, _natLocalPort);
            }
            if (s != null && SendKeepAlive(s, host))
            {
                return true;
            }
            if (s != null)
            {
                Abort(s);
            }
            foreach (string h in KeepAliveHosts)
            {
                if (!Alive(gen))
                {
                    return true;
                }
                Socket n = null;
                try
                {
                    n = ConnectFrom(bind, Resolve(h, 80), gen);
                    if (!SendKeepAlive(n, h))
                    {
                        throw new Exception("沒有回應");
                    }
                    lock (_lock)
                    {
                        if (gen != _gen || _nat == null)
                        {
                            Abort(n);
                            return true;
                        }
                        _keep = n;
                        _keepHost = h;
                    }
                    return true;
                }
                catch (Exception e)
                {
                    ModLog.Info("保活 " + h + " 失敗: " + e.Message);
                    if (n != null)
                    {
                        Abort(n);
                    }
                }
            }
            lock (_lock)
            {
                if (_keep == s)
                {
                    _keep = null;
                }
            }
            return false;
        }

        private static bool SendKeepAlive(Socket s, string host)
        {
            try
            {
                byte[] req = Encoding.ASCII.GetBytes("HEAD / HTTP/1.1\r\nHost: " + host
                    + "\r\nUser-Agent: SongRequest\r\nAccept: */*\r\nConnection: keep-alive\r\n\r\n");
                s.Send(req);
                // 读掉回应头(不关心内容), 对方关连接 = 保活断了
                byte[] buf = new byte[4096];
                int got = s.Receive(buf);
                if (got <= 0)
                {
                    return false;
                }
                s.ReceiveTimeout = 200;
                try
                {
                    while (s.Available > 0 && s.Receive(buf) > 0)
                    {
                    }
                }
                catch
                {
                }
                s.ReceiveTimeout = ConnectTimeoutMs;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ── 转发: 外面进来的连接 -> 本机点歌台 ─────────────────────

        private static void Serve(TcpListener l, string mark, int gen)
        {
            Thread th = new Thread(() =>
            {
                while (Alive(gen))
                {
                    TcpClient c;
                    try
                    {
                        c = l.AcceptTcpClient();
                    }
                    catch
                    {
                        if (!Alive(gen))
                        {
                            return;
                        }
                        Thread.Sleep(200);
                        continue;
                    }
                    if (Interlocked.Increment(ref _relays) > MaxRelays)
                    {
                        Interlocked.Decrement(ref _relays);
                        try { c.Close(); } catch { }
                        continue;
                    }
                    TcpClient cc = c;
                    Thread w = new Thread(() =>
                    {
                        try
                        {
                            Relay(cc, mark);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _relays);
                        }
                    });
                    w.IsBackground = true;
                    w.Start();
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        /// <summary>
        /// 转一个 HTTP 请求: 读完请求头, 改写 Host(HttpListener 只认 127.0.0.1)、强制 Connection: close
        /// (一个连接只走一个请求, 每个请求的头才都能被改写)、打上远程标记, 然后双向转发到点歌台
        /// </summary>
        internal static void Relay(TcpClient client, string mark)
        {
            TcpClient server = null;
            try
            {
                client.ReceiveTimeout = 10000;   // 10 秒内没发完请求头就断开, 免得空连接占着线程
                NetworkStream cs = client.GetStream();
                byte[] buf = new byte[16384];
                int len = 0, end = -1;
                while (end < 0)
                {
                    if (len == buf.Length)
                    {
                        return;   // 请求头超过 16KB, 不正常
                    }
                    int n = cs.Read(buf, len, buf.Length - len);
                    if (n <= 0)
                    {
                        return;
                    }
                    int from = Math.Max(0, len - 3);
                    len += n;
                    for (int i = from; i + 3 < len; i++)
                    {
                        if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                        {
                            end = i + 4;
                            break;
                        }
                    }
                }
                string[] lines = Latin1.GetString(buf, 0, end - 4).Split(new string[] { "\r\n" }, StringSplitOptions.None);
                string[] rl = lines[0].Split(' ');
                if (rl.Length != 3 || !rl[1].StartsWith("/", StringComparison.Ordinal)
                    || !rl[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                {
                    return;   // 只接受普通的 "GET /path HTTP/1.1"
                }
                string peer = "";
                try
                {
                    peer = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                }
                catch
                {
                }
                StringBuilder head = new StringBuilder(end + 128);
                head.Append(lines[0]).Append("\r\n");
                head.Append("Host: 127.0.0.1:").Append(Config.Port).Append("\r\n");
                head.Append("Connection: close\r\n");
                head.Append(RemoteHeader).Append(": ").Append(mark).Append("\r\n");
                if (peer.Length > 0)
                {
                    head.Append("X-Forwarded-For: ").Append(peer).Append("\r\n");
                }
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }
                    string name = lines[i].Substring(0, colon).Trim().ToLowerInvariant();
                    if (name == "host" || name == "connection" || name == "keep-alive" || name == "proxy-connection"
                        || name == "x-forwarded-for" || name == RemoteHeader.ToLowerInvariant())
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
                if (len > end)
                {
                    ss.Write(buf, end, len - end);   // 已经读进来的请求体
                }
                client.ReceiveTimeout = 0;   // 之后可能是 SSE 长连接, 不能再有读超时

                TcpClient srv = server;
                Thread up = new Thread(() => Pump(cs, ss, null));
                up.IsBackground = true;
                up.Start();
                Pump(ss, cs, srv);
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
                }
            }
            catch
            {
            }
            if (closeWhenDone != null)
            {
                try { closeWhenDone.Close(); } catch { }
            }
        }

        // ── UPnP: 让路由器把打洞用的端口转发进来 ──────────────────

        internal sealed class UpnpMapping
        {
            private string _controlUrl;
            private string _service;
            private string _localIp;
            private int _port;
            private bool _permanent;

            /// <summary>在局域网里找支持 UPnP 的路由器; 找不到返回 null</summary>
            internal static UpnpMapping Create(IPAddress localIp, int port)
            {
                try
                {
                    foreach (string loc in Discover(localIp))
                    {
                        string ctl, svc;
                        if (FindWanService(loc, out ctl, out svc))
                        {
                            UpnpMapping m = new UpnpMapping();
                            m._controlUrl = ctl;
                            m._service = svc;
                            m._localIp = localIp.ToString();
                            m._port = port;
                            return m;
                        }
                    }
                }
                catch (Exception e)
                {
                    ModLog.Info("UPnP 搜尋失敗: " + e.Message);
                }
                return null;
            }

            /// <summary>加(或续)转发: 外部端口 = 内部端口 = 打洞端口</summary>
            internal bool Add()
            {
                string args = "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + _port + "</NewExternalPort>"
                    + "<NewProtocol>TCP</NewProtocol><NewInternalPort>" + _port + "</NewInternalPort>"
                    + "<NewInternalClient>" + _localIp + "</NewInternalClient><NewEnabled>1</NewEnabled>"
                    + "<NewPortMappingDescription>SongRequest</NewPortMappingDescription>"
                    + "<NewLeaseDuration>{0}</NewLeaseDuration>";
                string err;
                if (Soap("AddPortMapping", string.Format(args, _permanent ? 0 : UpnpLeaseSec), out err))
                {
                    return true;
                }
                // 725 = 这台路由器只支持永久转发
                if (!_permanent && err.IndexOf("725", StringComparison.Ordinal) >= 0
                    && Soap("AddPortMapping", string.Format(args, 0), out err))
                {
                    _permanent = true;
                    return true;
                }
                ModLog.Info("UPnP 加轉發失敗: " + err);
                return false;
            }

            internal void Delete()
            {
                string err;
                Soap("DeletePortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + _port
                    + "</NewExternalPort><NewProtocol>TCP</NewProtocol>", out err);
            }

            private bool Soap(string action, string args, out string err)
            {
                err = "";
                try
                {
                    string body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\""
                        + " s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:" + action
                        + " xmlns:u=\"" + _service + "\">" + args + "</u:" + action + "></s:Body></s:Envelope>";
                    int code;
                    string resp = Http("POST", _controlUrl, body, "\"" + _service + "#" + action + "\"", out code);
                    if (code == 200)
                    {
                        return true;
                    }
                    Match m = Regex.Match(resp ?? "", @"<errorCode>\s*(\d+)\s*</errorCode>");
                    err = "HTTP " + code + (m.Success ? " errorCode " + m.Groups[1].Value : "");
                }
                catch (Exception e)
                {
                    err = e.Message;
                }
                return false;
            }

            private static List<string> Discover(IPAddress localIp)
            {
                List<string> found = new List<string>();
                using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Bind(new IPEndPoint(localIp, 0));
                    s.ReceiveTimeout = 500;
                    string[] targets =
                    {
                        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
                        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
                        "urn:schemas-upnp-org:service:WANIPConnection:1",
                        "urn:schemas-upnp-org:service:WANPPPConnection:1"
                    };
                    foreach (string st in targets)
                    {
                        byte[] msg = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\n"
                            + "MAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + st + "\r\n\r\n");
                        s.SendTo(msg, SsdpTarget);
                    }
                    byte[] buf = new byte[2048];
                    int until = Environment.TickCount + 2500;
                    while (unchecked(Environment.TickCount - until) < 0)
                    {
                        int n;
                        try
                        {
                            n = s.Receive(buf);
                        }
                        catch (SocketException)
                        {
                            continue;   // 500ms 没收到, 继续等到 2.5 秒
                        }
                        Match m = Regex.Match(Encoding.ASCII.GetString(buf, 0, n), @"(?im)^location:\s*(\S+)\s*$");
                        if (m.Success && !found.Contains(m.Groups[1].Value))
                        {
                            found.Add(m.Groups[1].Value);
                        }
                    }
                }
                return found;
            }

            /// <summary>读路由器的设备描述, 找 WANIPConnection / WANPPPConnection 服务的控制地址</summary>
            private static bool FindWanService(string location, out string controlUrl, out string service)
            {
                controlUrl = null;
                service = null;
                int code;
                string xml = Http("GET", location, null, null, out code);
                if (code != 200 || xml == null)
                {
                    return false;
                }
                foreach (Match m in Regex.Matches(xml, @"<service>(.*?)</service>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    string block = m.Groups[1].Value;
                    Match t = Regex.Match(block, @"<serviceType>\s*(urn:schemas-upnp-org:service:WAN(IP|PPP)Connection:\d)\s*</serviceType>",
                        RegexOptions.IgnoreCase);
                    Match c = Regex.Match(block, @"<controlURL>\s*(.*?)\s*</controlURL>", RegexOptions.IgnoreCase);
                    if (!t.Success || !c.Success)
                    {
                        continue;
                    }
                    Uri baseUri = new Uri(location);
                    Match ub = Regex.Match(xml, @"<URLBase>\s*(.*?)\s*</URLBase>", RegexOptions.IgnoreCase);
                    if (ub.Success && ub.Groups[1].Value.Length > 0)
                    {
                        try { baseUri = new Uri(ub.Groups[1].Value); } catch { }
                    }
                    controlUrl = new Uri(baseUri, c.Groups[1].Value).ToString();
                    service = t.Groups[1].Value;
                    return true;
                }
                return false;
            }

            /// <summary>极简 HTTP(只给局域网里的路由器用; 不走系统代理)</summary>
            private static string Http(string method, string url, string body, string soapAction, out int code)
            {
                code = 0;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Proxy = null;
                req.Timeout = ConnectTimeoutMs;
                req.ReadWriteTimeout = ConnectTimeoutMs;
                req.KeepAlive = false;
                if (body != null)
                {
                    byte[] b = Encoding.UTF8.GetBytes(body);
                    req.ContentType = "text/xml; charset=\"utf-8\"";
                    req.Headers["SOAPAction"] = soapAction;
                    req.ContentLength = b.Length;
                    using (Stream rs = req.GetRequestStream())
                    {
                        rs.Write(b, 0, b.Length);
                    }
                }
                HttpWebResponse resp;
                try
                {
                    resp = (HttpWebResponse)req.GetResponse();
                }
                catch (WebException we)
                {
                    resp = we.Response as HttpWebResponse;
                    if (resp == null)
                    {
                        throw;
                    }
                }
                using (resp)
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    code = (int)resp.StatusCode;
                    return sr.ReadToEnd();
                }
            }
        }
    }
}
