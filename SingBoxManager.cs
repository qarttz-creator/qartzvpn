using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QarttzVPN;

/// <summary>
/// Kill-switch connection engine.
///
/// A single "main" sing-box owns the TUN and routes everything to a socks "proxy" outbound on
/// 127.0.0.1:24808. It is brought up the instant the user connects and stays up the whole session.
/// Behind it, a swappable "provider" supplies that socks port: xray for vless (reality), or a second
/// sing-box for hysteria2. While no provider is up (cycling gateways, or none reachable) traffic to
/// "proxy" simply fails and is dropped — never leaked to direct. Only the gateway server IPs and the
/// user's explicit exclusions go direct. Manual disconnect tears the TUN down, restoring direct.
/// </summary>
public class SingBoxManager
{
    public event Action<string>? Log;
    public event Action<bool>? StateChanged;   // true=подключено, false=отключено/блокировка
    public event Action<string>? Status;

    private Process? _main;       // TUN sing-box (kill switch)
    private Process? _provider;   // xray (vless) or sing-box (hysteria) on 24808
    private readonly string _baseDir;
    private readonly string _logPath;
    private const int SocksPort = 24808;

    private readonly object _gate = new();
    private volatile bool _connecting;
    private volatile bool _userStopped;
    private List<VlessProfile> _candidates = new();
    private bool _autoSwitch;
    private VlessProfile? _active;
    private int _probeFails;
    private System.Threading.Timer? _watchdog;
    private System.Threading.Timer? _retry;

    // телеметрия доступности маршрутов (для /hb → панель)
    private readonly object _healthGate = new();
    private readonly Dictionary<string, bool> _routeHealth = new();

    public bool IsRunning => _main is { HasExited: false } || _provider is { HasExited: false };
    public string? ActiveName => _active?.Name;

    /// <summary>Снимок health кандидатов: имя шлюза → поднялся ли при последней проверке.</summary>
    public List<(string name, bool up)> RouteHealth()
    {
        lock (_healthGate) return _routeHealth.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private void SetHealth(string name, bool up)
    {
        lock (_healthGate) _routeHealth[name] = up;
    }

    public SingBoxManager()
    {
        _baseDir = AppContext.BaseDirectory;
        _logPath = Path.Combine(_baseDir, "qvpn.log");
        try { File.AppendAllText(_logPath, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====" + Environment.NewLine); } catch { }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    private void L(string s)
    {
        try { File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss} {s}{Environment.NewLine}"); } catch { }
        Log?.Invoke(s);
    }

    private string XrayPath        => Path.Combine(_baseDir, "core", "xray.exe");
    private string SingBoxPath     => Path.Combine(_baseDir, "core", "sing-box.exe");
    private string TemplatePath    => Path.Combine(_baseDir, "config.template.json");
    private string ExcludePath     => Path.Combine(_baseDir, "exclude.txt");
    private string AppsPath        => Path.Combine(_baseDir, "apps.txt");
    private string DomainsPath     => Path.Combine(_baseDir, "domains.txt");
    private string XrayConfigPath  => Path.Combine(_baseDir, "xray-runtime.json");
    private string MainConfigPath  => Path.Combine(_baseDir, "main-config.json");
    private string HyConfigPath    => Path.Combine(_baseDir, "hy-provider.json");
    private string AwgConfigPath   => Path.Combine(_baseDir, "awg-provider.json");

    private List<string> ReadExcludes()
    {
        var list = new List<string>();
        if (!File.Exists(ExcludePath)) return list;
        foreach (var raw in File.ReadAllLines(ExcludePath))
        {
            var line = raw;
            var h = line.IndexOf('#');
            if (h >= 0) line = line.Substring(0, h);
            line = line.Trim();
            if (line.Length == 0) continue;
            if (!line.Contains('/')) line += line.Contains(':') ? "/128" : "/32";
            if (System.Net.IPNetwork.TryParse(line, out _)) list.Add(line);
            else L($"Пропущена некорректная строка исключений: '{raw.Trim()}'");
        }
        return list;
    }

    private List<string> ReadApps()
    {
        var list = new List<string>();
        if (!File.Exists(AppsPath)) return list;
        foreach (var raw in File.ReadAllLines(AppsPath))
        {
            var line = raw;
            var h = line.IndexOf('#');
            if (h >= 0) line = line.Substring(0, h);
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.Contains('\\') || line.Contains('/')) { L($"Пропущено (нужно имя exe): '{raw.Trim()}'"); continue; }
            list.Add(line);
        }
        return list;
    }

    /// <summary>Reads the domain bypass list → normalized apex domains (lowercased, no scheme/path/
    /// port, no leading *. or .). Each is matched exactly AND as a suffix, so foo.com covers foo.com
    /// and *.foo.com. A single-label name (no dot, e.g. an intranet host) is matched exactly only.</summary>
    private List<string> ReadDomains()
    {
        var list = new List<string>();
        if (!File.Exists(DomainsPath)) return list;
        foreach (var raw in File.ReadAllLines(DomainsPath))
        {
            var line = raw;
            var h = line.IndexOf('#');
            if (h >= 0) line = line.Substring(0, h);
            line = line.Trim().ToLowerInvariant();
            if (line.Length == 0) continue;
            int p;
            if ((p = line.IndexOf("://")) >= 0) line = line.Substring(p + 3);
            if ((p = line.IndexOf('/')) >= 0) line = line.Substring(0, p);
            if ((p = line.IndexOf(':')) >= 0) line = line.Substring(0, p); // strip :port
            line = line.TrimStart('*').TrimStart('.').Trim();
            if (line.Length == 0) continue;
            if (!list.Contains(line)) list.Add(line);
        }
        return list;
    }

    private static JsonArray JArr(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var s in items) a.Add(s);
        return a;
    }

    // ---------- config builders ----------

    /// <summary>The TUN sing-box: everything → socks proxy (24808); gateway IPs, private IPs,
    /// local/intranet domains, RU domains and the user IP/domain/app exclusions go direct. Local and
    /// private-IP bypass are always on (otherwise LAN names can't resolve and LAN hosts are
    /// unreachable); the IP/domain/app filters are opt-in.</summary>
    private void BuildMainConfig(List<VlessProfile> cands)
    {
        if (!File.Exists(TemplatePath))
            throw new FileNotFoundException("Не найден config.template.json.", TemplatePath);

        var root = JsonNode.Parse(File.ReadAllText(TemplatePath))!.AsObject();
        var rules = root["route"]!.AsObject()["rules"]!.AsArray();

        // Gateway server IPs must bypass the tunnel (so the provider can reach them without looping).
        var srv = new JsonArray();
        foreach (var p in cands)
            foreach (var c in p.ServerCidrs())
                srv.Add(c);
        if (srv.Count > 0)
        {
            rules.Add(new JsonObject { ["ip_cidr"] = srv, ["action"] = "route", ["outbound"] = "direct" });
            L($"Серверы шлюзов исключены из туннеля: {srv.Count} адрес(ов).");
        }

        var st = Settings.Load();

        // RU domains → direct (bypass VPN) so RU marketplaces/RKN see the real IP. .рф = xn--p1ai.
        if (st.RuDirect)
        {
            var ru = new JsonArray { ".ru", ".su", "xn--p1ai" };
            rules.Add(new JsonObject { ["domain_suffix"] = ru, ["action"] = "route", ["outbound"] = "direct" });
            L("RU-домены (.ru/.рф/.su) идут напрямую.");
        }

        if (st.Routing)
        {
            if (st.IpFilter)
            {
                var ex = ReadExcludes();
                if (ex.Count > 0)
                {
                    var arr = new JsonArray();
                    foreach (var c in ex) arr.Add(c);
                    rules.Add(new JsonObject { ["ip_cidr"] = arr, ["action"] = "route", ["outbound"] = "direct" });
                    L($"Исключения по IP: {ex.Count}");
                }
            }
            if (st.AppsFilter)
            {
                var apps = ReadApps();
                if (apps.Count > 0)
                {
                    var arr = new JsonArray();
                    foreach (var a in apps) arr.Add(a);
                    rules.Add(new JsonObject { ["process_name"] = arr, ["action"] = "route", ["outbound"] = "direct" });
                    L($"Исключения по приложениям: {apps.Count}");
                }
            }
        }

        // --- Domains (not just IPs). Two problems the IP-only template has: (1) the only DNS is
        // remote DoH over the tunnel, so LAN/intranet names (which that resolver doesn't know) never
        // resolve; (2) nothing sends private IPs direct, so even a resolved 192.168.x goes into the
        // tunnel and dies. Fix: resolve local/intranet, RU and user-listed domains via the OS resolver
        // (type:local — it knows LAN names, incl. a *.ru that only exists on the LAN, mDNS, the hosts
        // file and VPN-pushed suffixes), and route them + private IPs straight out. Local/RU handling
        // is always on; the user domain list is the opt-in "domain filter" (parallel to the IP filter). ---
        string[] localSuffixes = { ".local", ".lan", ".home", ".internal", ".intranet", ".localdomain", ".corp" };
        var userDomains = (st.Routing && st.DomainFilter) ? ReadDomains() : new List<string>();
        var userExact = new List<string>(userDomains);          // apex (and single-label) names
        var userSuffix = new List<string>();                    // ".name" → subdomains
        foreach (var d in userDomains) if (d.Contains('.')) userSuffix.Add("." + d);

        // DNS: send local suffixes and the user bypass list to the OS resolver (the external network's
        // DNS — corporate/router if present, else the ISP's). RU is deliberately NOT sent here: it
        // keeps resolving over the tunnelled DoH (dns.final), so the ISP never sees the query — RU only
        // bypasses at the route level, not the DNS. Resolving via the local DNS leaves a trace, so it's
        // opt-in: the user consents by adding a domain to the list. A domain_suffix like ".foo.ru"
        // matches only *.foo.ru, NOT foo.ru itself, so the user list needs both a `domain` (exact) and
        // a `domain_suffix` matcher — otherwise an apex such as a2nta.goldpolus.ru slips past.
        var dns = root["dns"]!.AsObject();
        dns["servers"]!.AsArray().Add(new JsonObject { ["type"] = "local", ["tag"] = "dns-local" });
        if (dns["rules"] is not JsonArray dnsRules) { dnsRules = new JsonArray(); dns["rules"] = dnsRules; }
        var dnsSuffix = new List<string>(localSuffixes); dnsSuffix.AddRange(userSuffix);
        var localDnsRule = new JsonObject { ["domain_suffix"] = JArr(dnsSuffix), ["server"] = "dns-local" };
        if (userExact.Count > 0) localDnsRule["domain"] = JArr(userExact);
        dnsRules.Insert(0, localDnsRule);

        // Route: local/intranet domains, the user list and private IPs bypass the tunnel. (RU route→
        // direct is added above; local + private are always on, the user list is opt-in.)
        rules.Add(new JsonObject { ["domain_suffix"] = JArr(localSuffixes), ["action"] = "route", ["outbound"] = "direct" });
        if (userExact.Count > 0)
        {
            var dr = new JsonObject { ["domain"] = JArr(userExact), ["action"] = "route", ["outbound"] = "direct" };
            if (userSuffix.Count > 0) dr["domain_suffix"] = JArr(userSuffix);
            rules.Add(dr);
            L($"Исключения по доменам: {userExact.Count}");
        }
        rules.Add(new JsonObject { ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "direct" });

        File.WriteAllText(MainConfigPath, root.ToJsonString(Json.Opts));
    }

    private void BuildXrayConfig(VlessProfile profile)
    {
        var cfg = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "socks",
                    ["listen"] = "127.0.0.1",
                    ["port"] = SocksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["udp"] = true }
                }
            },
            ["outbounds"] = new JsonArray { profile.BuildXrayOutbound() }
        };
        File.WriteAllText(XrayConfigPath, cfg.ToJsonString(Json.Opts));
        L($"xray-конфиг '{profile.Name}' ({profile.Host}:{profile.Port}).");
    }

    /// <summary>Hysteria2 provider: a second sing-box with a local socks on 24808 (no TUN).</summary>
    private void BuildHyProviderConfig(VlessProfile profile)
    {
        var outs = new JsonArray { profile.BuildSingboxOutbound(), new JsonObject { ["type"] = "direct", ["tag"] = "direct" } };
        var cfg = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject { ["type"] = "mixed", ["tag"] = "in", ["listen"] = "127.0.0.1", ["listen_port"] = SocksPort }
            },
            ["outbounds"] = outs,
            ["route"] = new JsonObject { ["final"] = "proxy" }
        };
        File.WriteAllText(HyConfigPath, cfg.ToJsonString(Json.Opts));
        L($"hysteria-провайдер '{profile.Name}' ({profile.Host}:{profile.Port}).");
    }

    /// <summary>AmneziaWG provider: a second sing-box with a local socks on 24808 (no TUN) whose
    /// `awg` endpoint tunnels everything to the gateway. Needs the AWG-capable sing-box.exe built
    /// from the amnezia-box fork with -tags with_awg (the CI build does exactly this).</summary>
    private void BuildAwgProviderConfig(VlessProfile profile)
    {
        var ep = profile.AwgEndpoint?.DeepClone().AsObject()
            ?? throw new InvalidOperationException("нет awg-endpoint в профиле");
        var tag = (string?)ep["tag"] ?? "awg";
        var cfg = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject { ["type"] = "mixed", ["tag"] = "in", ["listen"] = "127.0.0.1", ["listen_port"] = SocksPort }
            },
            ["endpoints"] = new JsonArray { ep },
            ["outbounds"] = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = "direct" } },
            ["route"] = new JsonObject { ["final"] = tag, ["auto_detect_interface"] = true },
        };
        File.WriteAllText(AwgConfigPath, cfg.ToJsonString(Json.Opts));
        L($"awg-провайдер '{profile.Name}' ({profile.Host}:{profile.Port}).");
    }

    // ---------- process helpers ----------

    private Process StartProc(string file, string args, string workDir, string label)
    {
        L($"{label}: запуск {Path.GetFileName(file)} {args}");
        var psi = new ProcessStartInfo
        {
            FileName = file, Arguments = args, WorkingDirectory = workDir,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => Handle(label, e.Data);
        p.ErrorDataReceived += (_, e) => Handle(label, e.Data);
        p.Exited += (_, _) => { L($"{label} завершился (code {TryExit(p)})."); OnProcDied(label); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private static string TryExit(Process p) { try { return p.ExitCode.ToString(); } catch { return "?"; } }

    private bool WaitForPort(int port, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (_userStopped) return false;
            try
            {
                using var c = new TcpClient();
                var ar = c.BeginConnect("127.0.0.1", port, null, null);
                if (ar.AsyncWaitHandle.WaitOne(300) && c.Connected) return true;
            }
            catch { }
            Thread.Sleep(200);
        }
        return false;
    }

    private bool PortAlive(string host, int port, int timeoutMs)
    {
        try
        {
            using var c = new TcpClient();
            var ar = c.BeginConnect(host, port, null, null);
            return ar.AsyncWaitHandle.WaitOne(timeoutMs) && c.Connected;
        }
        catch { return false; }
    }

    private bool Probe(int timeoutMs)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{SocksPort}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(Math.Min(timeoutMs, 8000)),
                AllowAutoRedirect = false,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var r = http.GetAsync("http://www.gstatic.com/generate_204").GetAwaiter().GetResult();
            return (int)r.StatusCode == 204 || r.IsSuccessStatusCode;
        }
        catch (Exception ex) { L("Probe: " + ex.Message); return false; }
    }

    private bool ProbeOk(int perTryMs, int tries)
    {
        for (int i = 0; i < tries; i++)
        {
            if (_userStopped) return false;
            if (Probe(perTryMs)) return true;
        }
        return false;
    }

    // ---------- public entry points ----------

    public void Start(VlessProfile profile)
    {
        _userStopped = false;
        BeginConnect(new List<VlessProfile> { profile }, autoSwitch: false, startIndex: 0);
    }

    public void StartAuto(List<VlessProfile> profiles)
    {
        _userStopped = false;
        BeginConnect(new List<VlessProfile>(profiles), autoSwitch: true, startIndex: 0);
    }

    private void BeginConnect(List<VlessProfile> cands, bool autoSwitch, int startIndex)
    {
        _connecting = true;
        Task.Run(() =>
        {
            lock (_gate)
            {
                try { ConnectLoop(cands, autoSwitch, startIndex); }
                catch (Exception ex) { L("ConnectLoop ERROR: " + ex.Message); }
                finally { _connecting = false; }
            }
        });
    }

    private void ConnectLoop(List<VlessProfile> cands, bool autoSwitch, int startIndex)
    {
        StopWatchdog();
        StopRetry();

        if (!File.Exists(XrayPath) || !File.Exists(SingBoxPath))
        {
            Status?.Invoke("Не найдены ядра (core\\xray.exe / sing-box.exe)");
            StateChanged?.Invoke(false);
            return;
        }
        if (cands.Count == 0) { Status?.Invoke("Нет профилей"); StateChanged?.Invoke(false); return; }

        _candidates = cands;
        _autoSwitch = autoSwitch;

        // 1) Bring the TUN up first — kill switch is active from this point (no provider yet → traffic dropped).
        if (!(_main is { HasExited: false }))
        {
            try { BuildMainConfig(cands); }
            catch (Exception ex) { L("BuildMainConfig: " + ex.Message); Status?.Invoke("Ошибка конфига"); StateChanged?.Invoke(false); return; }
            Status?.Invoke("Защита включена, подключение…");
            _main = StartProc(SingBoxPath, $"run -c \"{MainConfigPath}\"", Path.Combine(_baseDir, "core"), "main");
            Thread.Sleep(1200);
            if (_main is null || _main.HasExited) { L("main (TUN) не поднялся."); Status?.Invoke("Не удалось поднять TUN"); StateChanged?.Invoke(false); return; }
        }

        // 2) Cycle providers behind the TUN.
        for (int i = 0; i < cands.Count; i++)
        {
            if (_userStopped) { Teardown(); return; }
            var p = cands[(startIndex + i) % cands.Count];

            Status?.Invoke(cands.Count > 1 ? $"Проверка: {p.Name}…" : $"Подключение: {p.Name}…");
            L($"Провайдер для '{p.Name}' ({p.Host}:{p.Port}) [{p.Kind}].");

            StopProvider();
            bool up = p.Kind switch
            {
                ProfileKind.Hysteria2 => StartHyProvider(p),
                ProfileKind.Awg => StartAwgProvider(p),
                _ => StartXrayProvider(p),
            };
            if (_userStopped) { Teardown(); return; }
            if (!up) { SetHealth(p.Name, false); StopProvider(); continue; }

            if (!ProbeOk(8000, 2)) { SetHealth(p.Name, false); L($"'{p.Name}': сквозная проверка не прошла."); StopProvider(); continue; }
            if (_userStopped) { Teardown(); return; }

            SetHealth(p.Name, true);
            _active = p;
            _probeFails = 0;
            L($"Подключено через '{p.Name}'.");
            StateChanged?.Invoke(true);
            Status?.Invoke($"Подключено: {p.Name}");
            StartWatchdog();
            return;
        }

        // None worked — keep the TUN up (traffic stays blocked), retry shortly. No direct leak.
        StopProvider();
        _active = null;
        StateChanged?.Invoke(false);
        Status?.Invoke("Нет доступных шлюзов — трафик заблокирован");
        ScheduleRetry(cands, autoSwitch);
    }

    private bool StartXrayProvider(VlessProfile p)
    {
        if (!PortAlive(p.Host, p.Port, 2500)) { L($"'{p.Name}': порт закрыт."); return false; }
        try { BuildXrayConfig(p); } catch (Exception ex) { L("BuildXrayConfig: " + ex.Message); return false; }
        _provider = StartProc(XrayPath, $"run -c \"{XrayConfigPath}\"", _baseDir, "xray");
        return WaitForPort(SocksPort, 8000);
    }

    private bool StartHyProvider(VlessProfile p)
    {
        try { BuildHyProviderConfig(p); } catch (Exception ex) { L("BuildHyProviderConfig: " + ex.Message); return false; }
        _provider = StartProc(SingBoxPath, $"run -c \"{HyConfigPath}\"", Path.Combine(_baseDir, "core"), "hy");
        return WaitForPort(SocksPort, 8000);
    }

    // AWG — UDP, поэтому TCP-порт не проверяем (как и hysteria); handshake проверит сквозной Probe.
    private bool StartAwgProvider(VlessProfile p)
    {
        try { BuildAwgProviderConfig(p); } catch (Exception ex) { L("BuildAwgProviderConfig: " + ex.Message); return false; }
        _provider = StartProc(SingBoxPath, $"run -c \"{AwgConfigPath}\"", Path.Combine(_baseDir, "core"), "awg");
        return WaitForPort(SocksPort, 8000);
    }

    // ---------- watchdog / retry ----------

    private void StartWatchdog()
    {
        StopWatchdog();
        _watchdog = new System.Threading.Timer(_ => WatchdogTick(), null, 25000, 25000);
    }
    private void StopWatchdog() { try { _watchdog?.Dispose(); } catch { } _watchdog = null; }

    private void ScheduleRetry(List<VlessProfile> cands, bool autoSwitch)
    {
        StopRetry();
        _retry = new System.Threading.Timer(_ =>
        {
            if (_userStopped || _connecting) return;
            L("Повторная попытка подбора шлюза…");
            BeginConnect(cands, autoSwitch, 0);
        }, null, 10000, Timeout.Infinite);
    }
    private void StopRetry() { try { _retry?.Dispose(); } catch { } _retry = null; }

    private void WatchdogTick()
    {
        if (_userStopped || _connecting) return;
        if (!(_main is { HasExited: false })) { HealthLost("TUN упал"); return; }
        if (!(_provider is { HasExited: false })) { HealthLost("провайдер упал"); return; }
        if (Probe(6000)) { _probeFails = 0; return; }
        _probeFails++;
        L($"Health-check не прошёл ({_probeFails}/2).");
        if (_probeFails >= 2) { _probeFails = 0; HealthLost("нет ответа через туннель"); }
    }

    private void OnProcDied(string label)
    {
        if (_userStopped || _connecting) return; // намеренный kill / идёт переподключение
        HealthLost($"{label} упал");
    }

    private void HealthLost(string reason)
    {
        if (_userStopped || _connecting) return;
        _connecting = true;
        StopWatchdog();
        StopProvider(); // TUN (_main) остаётся — kill switch держит, утечки нет
        L($"Связь потеряна: {reason}. Пересобираю (TUN остаётся поднят).");
        var cands = _candidates;
        int idx = (_active != null) ? cands.IndexOf(_active) : -1;
        _active = null;
        StateChanged?.Invoke(false);
        Status?.Invoke("Переподключение…");
        BeginConnect(cands, _autoSwitch, idx < 0 ? 0 : (idx + 1) % Math.Max(cands.Count, 1));
    }

    // ---------- teardown ----------

    private void Handle(string label, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        L($"[{label}] {line}");
    }

    private void StopProvider()
    {
        var p = _provider;
        _provider = null;
        try { if (p is { HasExited: false }) { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } } catch { }
    }

    private void StopMain()
    {
        var p = _main;
        _main = null;
        try { if (p is { HasExited: false }) { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } } catch { }
    }

    private void Teardown() { StopProvider(); StopMain(); }

    public void Stop()
    {
        _userStopped = true;
        StopWatchdog();
        StopRetry();
        StopProvider();
        StopMain();
        _active = null;
        lock (_healthGate) _routeHealth.Clear();
        StateChanged?.Invoke(false);
        Status?.Invoke("Отключено");
    }
}
