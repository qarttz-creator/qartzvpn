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
    // Кольцевой буфер лога в памяти. В клиентской сборке на диск ничего не пишем — наружу лог уходит
    // только зашифрованным (LogExport); буфер и есть источник для экспорта.
    private readonly System.Collections.Generic.Queue<string> _logBuf = new();
    private readonly object _logGate = new();
    private const int LogBufMax = 5000;
    private const int SocksPort = 24808;
    private const int CensusPort = 24809;   // scratch-порт для фонового скана маршрутов

    private readonly object _gate = new();
    private volatile bool _connecting;
    private volatile bool _userStopped;
    private List<VlessProfile> _candidates = new();
    private string _mainServerKey = "";
    private bool _autoSwitch;
    private VlessProfile? _active;
    private int _probeFails;
    private System.Threading.Timer? _watchdog;
    private System.Threading.Timer? _retry;

    // census: фоновый скан всех маршрутов
    private System.Threading.Timer? _census;
    private Process? _censusProc;
    private readonly object _censusGate = new();
    private volatile bool _censusRunning;

    // телеметрия доступности маршрутов (для /hb → панель)
    private readonly object _healthGate = new();
    // Ключ — УНИКАЛЬНЫЙ идентификатор маршрута (kind|host:port; /sub дедупит по host:port, так что он
    // уникален), значение — отображаемое имя + живость. Ключуем НЕ по имени: у AWG имена часто
    // совпадают (все "AmneziaWG"), и ключ по имени схлопывал бы все амнезии в одну запись.
    private readonly Dictionary<string, (string name, bool up)> _routeHealth = new();
    // Пинг маршрутов (мс, 0 — не прошёл) по тому же ключу — из census и пробы активного; для RouteSteer.
    private readonly Dictionary<string, int> _routeDelay = new();

    public bool IsRunning => _main is { HasExited: false } || _provider is { HasExited: false };
    public string? ActiveName => _active?.Name;

    // протокол активного маршрута для подписи в UI (vless/hysteria2/awg)
    public string? ActiveProto => _active == null ? null : _active.Kind switch
    {
        ProfileKind.Vless => "vless",
        ProfileKind.Hysteria2 => "hysteria2",
        ProfileKind.Awg => "awg",
        _ => null,
    };

    /// <summary>Снимок health всех маршрутов: отображаемое имя → поднялся ли при последней проверке.</summary>
    public List<(string name, bool up)> RouteHealth()
    {
        lock (_healthGate) return _routeHealth.Values.ToList();
    }

    // Уникальный ключ маршрута: host:port инбаунда уникален (разные транспорты = разные порты),
    // плюс Kind для надёжности. Именно по нему ключуем health, чтобы ничего не схлопывалось.
    private static string RouteKey(VlessProfile p) => $"{p.Kind}|{p.Host}:{p.Port}";

    // Отображаемое имя маршрута для /hb → панель: имя шлюза + протокол (vless/hysteria2/awg),
    // чтобы разные транспорты одного шлюза различались в админке.
    private static string RouteLabel(VlessProfile p)
    {
        var proto = p.Kind switch
        {
            ProfileKind.Vless => "vless",
            ProfileKind.Hysteria2 => "hysteria2",
            ProfileKind.Awg => "awg",
            _ => "",
        };
        return proto.Length > 0 ? $"{p.Name} · {proto}" : p.Name;
    }

    private void SetHealth(VlessProfile p, bool up)
    {
        lock (_healthGate) _routeHealth[RouteKey(p)] = (RouteLabel(p), up);
    }

    public SingBoxManager()
    {
        _baseDir = AppContext.BaseDirectory;
        _logPath = Path.Combine(_baseDir, "qvpn.log");
        if (Build.DevTools) { try { File.AppendAllText(_logPath, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====" + Environment.NewLine); } catch { } }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    private void L(string s)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {s}";
        lock (_logGate) { _logBuf.Enqueue(line); while (_logBuf.Count > LogBufMax) _logBuf.Dequeue(); }
        if (Build.DevTools) { try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { } }
        Log?.Invoke(s);
    }

    /// <summary>Пишет строку в лог-буфер менеджера (попадёт в зашифрованный экспорт).</summary>
    public void LogMessage(string s) => L(s);

    /// <summary>Снимок лога из памяти — источник для зашифрованного экспорта (LogExport).</summary>
    public string LogSnapshot()
    {
        lock (_logGate) return string.Join(Environment.NewLine, _logBuf);
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
    private string CensusXrayPath  => Path.Combine(_baseDir, "census-xray.json");
    private string CensusHyPath    => Path.Combine(_baseDir, "census-hy.json");
    private string CensusAwgPath   => Path.Combine(_baseDir, "census-awg.json");

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

    /// <summary>The TUN sing-box: by default ALL traffic (including DNS) goes into the tunnel;
    /// the only unconditional bypass is the gateway server IPs. Any manual exclusions enabled by the
    /// user (RU domains, IP/subnet list, domain list, app list) bypass BOTH routing and DNS (using
    /// the local/router DNS resolver).</summary>
    private void BuildMainConfig(List<VlessProfile> cands)
    {
        if (!File.Exists(TemplatePath))
            throw new FileNotFoundException("Не найден config.template.json.", TemplatePath);

        var root = JsonNode.Parse(File.ReadAllText(TemplatePath))!.AsObject();
        var routeObj = root["route"]!.AsObject();
        var rules = routeObj["rules"]!.AsArray();

        // 1) Единственное безусловное исключение — IP самих VPN-шлюзов из конфигурации
        // (и в route.rules -> direct, и в tun.route_exclude_address на уровне таблицы маршрутов ОС).
        var srv = new JsonArray();
        var srvSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in cands)
            foreach (var c in p.ServerCidrs())
                if (srvSet.Add(c)) srv.Add(c);
        if (srv.Count > 0)
        {
            rules.Add(new JsonObject { ["ip_cidr"] = srv, ["action"] = "route", ["outbound"] = "direct" });
            L($"Серверы шлюзов исключены из туннеля: {srv.Count} адрес(ов).");
        }
        _mainServerKey = ServerCidrsKey(cands);

        var tunExclude = new JsonArray();
        var tunExSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddTunEx(string cidr) { if (tunExSet.Add(cidr)) tunExclude.Add(cidr); }
        foreach (var c in srvSet) AddTunEx(c);

        var dns = root["dns"]!.AsObject();
        dns["servers"]!.AsArray().Add(new JsonObject { ["type"] = "local", ["tag"] = "dns-local" });
        if (dns["rules"] is not JsonArray dnsRules) { dnsRules = new JsonArray(); dns["rules"] = dnsRules; }
        routeObj["default_domain_resolver"] = "dns-local";

        var st = Settings.Load();

        // 2) Ручные исключения — всё, что включено пользователем, идёт напрямую ПОЛНОСТЬЮ (трафик + локальный DNS).

        // RU-домены (.ru/.рф/.su) напрямую + их DNS напрямую через роутер/ОС
        if (st.RuDirect)
        {
            var ruRoute = new JsonArray { ".ru", ".su", ".xn--p1ai" };
            var ruDns = new JsonArray { ".ru", ".su", ".xn--p1ai" };
            rules.Add(new JsonObject { ["domain_suffix"] = ruRoute, ["action"] = "route", ["outbound"] = "direct" });
            dnsRules.Add(new JsonObject { ["domain_suffix"] = ruDns, ["server"] = "dns-local" });
            L("RU-домены (.ru/.рф/.su) идут напрямую (включая DNS).");
        }

        if (st.Routing)
        {
            if (st.IpFilter)
            {
                var ex = ReadExcludes();
                if (ex.Count > 0)
                {
                    var arr = new JsonArray();
                    foreach (var c in ex)
                    {
                        arr.Add(c);
                        if (!OverlapsTun(c)) AddTunEx(c);
                    }
                    rules.Add(new JsonObject { ["ip_cidr"] = arr, ["action"] = "route", ["outbound"] = "direct" });
                    L($"Исключения по IP: {ex.Count}");
                }
            }
            if (st.AppsFilter)
            {
                var apps = ReadApps();
                if (apps.Count > 0)
                {
                    var arrRoute = new JsonArray();
                    var arrDns = new JsonArray();
                    foreach (var a in apps) { arrRoute.Add(a); arrDns.Add(a); }
                    rules.Add(new JsonObject { ["process_name"] = arrRoute, ["action"] = "route", ["outbound"] = "direct" });
                    dnsRules.Add(new JsonObject { ["process_name"] = arrDns, ["server"] = "dns-local" });
                    L($"Исключения по приложениям: {apps.Count} (включая DNS)");
                }
            }
            if (st.DomainFilter)
            {
                var userDomains = ReadDomains();
                if (userDomains.Count > 0)
                {
                    var userExact = new List<string>(userDomains);
                    var userSuffix = new List<string>();
                    foreach (var d in userDomains) if (d.Contains('.')) userSuffix.Add("." + d);

                    var dr = new JsonObject { ["domain"] = JArr(userExact), ["action"] = "route", ["outbound"] = "direct" };
                    var dd = new JsonObject { ["domain"] = JArr(userExact), ["server"] = "dns-local" };
                    if (userSuffix.Count > 0)
                    {
                        dr["domain_suffix"] = JArr(userSuffix);
                        dd["domain_suffix"] = JArr(userSuffix);
                    }
                    rules.Add(dr);
                    dnsRules.Add(dd);
                    L($"Исключения по доменам: {userExact.Count} (включая DNS)");
                }
            }
        }

        if (tunExclude.Count > 0 && root["inbounds"] is JsonArray inbs && inbs.Count > 0 && inbs[0] is JsonObject tunIn)
            tunIn["route_exclude_address"] = tunExclude;

        File.WriteAllText(MainConfigPath, root.ToJsonString(Json.Opts));
    }

    private static string ServerCidrsKey(List<VlessProfile> cands)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in cands)
            foreach (var c in p.ServerCidrs())
                set.Add(c);
        return string.Join(",", set);
    }

    private static bool OverlapsTun(string cidr)
    {
        try
        {
            if (System.Net.IPNetwork.TryParse(cidr, out var net))
                return net.Contains(IPAddress.Parse("172.19.0.1")) || net.Contains(IPAddress.Parse("172.19.0.2"));
        }
        catch { }
        return false;
    }

    private void BuildXrayConfig(VlessProfile profile) => BuildXrayConfig(profile, SocksPort, XrayConfigPath);

    private void BuildXrayConfig(VlessProfile profile, int port, string outPath)
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
                    ["port"] = port,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["udp"] = true }
                }
            },
            ["outbounds"] = new JsonArray { profile.BuildXrayOutbound() }
        };
        File.WriteAllText(outPath, cfg.ToJsonString(Json.Opts));
        L($"xray-конфиг '{profile.Name}' ({profile.Host}:{profile.Port}).");
    }

    /// <summary>Hysteria2 provider: a second sing-box with a local socks on 24808 (no TUN).</summary>
    private void BuildHyProviderConfig(VlessProfile profile) => BuildHyProviderConfig(profile, SocksPort, HyConfigPath);

    private void BuildHyProviderConfig(VlessProfile profile, int port, string outPath)
    {
        var outs = new JsonArray { profile.BuildSingboxOutbound(), new JsonObject { ["type"] = "direct", ["tag"] = "direct" } };
        var cfg = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject { ["type"] = "mixed", ["tag"] = "in", ["listen"] = "127.0.0.1", ["listen_port"] = port }
            },
            ["outbounds"] = outs,
            ["route"] = new JsonObject { ["final"] = "proxy" }
        };
        File.WriteAllText(outPath, cfg.ToJsonString(Json.Opts));
        L($"hysteria-провайдер '{profile.Name}' ({profile.Host}:{profile.Port}).");
    }

    /// <summary>AmneziaWG provider: a second sing-box with a local socks on 24808 (no TUN) whose
    /// `awg` endpoint tunnels everything to the gateway. Needs the AWG-capable sing-box.exe built
    /// from the amnezia-box fork with -tags with_awg (the CI build does exactly this).</summary>
    private void BuildAwgProviderConfig(VlessProfile profile) => BuildAwgProviderConfig(profile, SocksPort, AwgConfigPath);

    // Скан-вариант для census (сигнатура под Action<VlessProfile,int,string>): БЕЗ auto_detect_interface.
    private void BuildAwgScanConfig(VlessProfile profile, int port, string outPath)
        => BuildAwgProviderConfig(profile, port, outPath, scan: true);

    private void BuildAwgProviderConfig(VlessProfile profile, int port, string outPath, bool scan = false)
    {
        var ep = profile.AwgEndpoint?.DeepClone().AsObject()
            ?? throw new InvalidOperationException("нет awg-endpoint в профиле");
        var tag = (string?)ep["tag"] ?? "awg";
        var route = new JsonObject { ["final"] = tag };
        // auto_detect_interface заставляет ядро мониторить/привязываться к дефолтному интерфейсу Windows.
        // Для СКАНА он не нужен (IP шлюза и так идёт напрямую через bypass главного TUN) и ВРЕДЕН: лезет в
        // маршруты/интерфейс параллельно с настройкой главного TUN (strict_route) → тот падает на 15-й сек.
        // Поэтому в скане опускаем; у живого провайдера оставляем (критичный транспорт, не трогаем).
        if (!scan) route["auto_detect_interface"] = true;
        var cfg = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject { ["type"] = "mixed", ["tag"] = "in", ["listen"] = "127.0.0.1", ["listen_port"] = port }
            },
            ["endpoints"] = new JsonArray { ep },
            ["outbounds"] = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = "direct" } },
            ["route"] = route,
        };
        File.WriteAllText(outPath, cfg.ToJsonString(Json.Opts));
        L($"awg-{(scan ? "скан" : "провайдер")} '{profile.Name}' ({profile.Host}:{profile.Port}).");
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

    private bool Probe(int timeoutMs) => Probe(timeoutMs, SocksPort);
    private bool Probe(int timeoutMs, int port) => Probe(timeoutMs, port, "http://www.gstatic.com/generate_204", anyResponse: false);

    // url — что дёргаем; anyResponse=true: достаточно ЛЮБОГО HTTP-ответа (туннель донёс запрос и получил
    // ответ). Это для censu-проба по IP, где 204 нет и, главное, не нужен резолв домена — для AWG это
    // критично: он везёт только IP-пакеты, и домен через не-поднятый awg резолвить нечем.
    private bool Probe(int timeoutMs, int port, string url, bool anyResponse)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(Math.Min(timeoutMs, 8000)),
                AllowAutoRedirect = false,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var r = http.GetAsync(url).GetAwaiter().GetResult();
            return anyResponse || (int)r.StatusCode == 204 || r.IsSuccessStatusCode;
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

    // AWG везёт только IP-пакеты и awg-provider.json не содержит DNS-резолвера — домен www.gstatic.com
    // падает с "cannot marshal DNS message". Для AWG проверяем по IP (как в census).
    private bool ProbeRouteOk(VlessProfile p, int perTryMs, int tries)
    {
        if (p.Kind != ProfileKind.Awg) return ProbeOk(perTryMs, tries);
        for (int i = 0; i < tries; i++)
        {
            if (_userStopped) return false;
            if (Probe(perTryMs, SocksPort, "http://1.1.1.1", anyResponse: true)) return true;
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
        _seatedByScan = false;
        BeginConnect(new List<VlessProfile>(profiles), autoSwitch: true, startIndex: 0);
    }

    /// <summary>Применяет обновлённую подписку: если изменился набор IP шлюзов, пересобирает и
    /// перезапускает _main, чтобы новые адреса попали в route_exclude_address и direct-исключения.</summary>
    public void ApplyUpdatedProfiles(List<VlessProfile> profiles)
    {
        if (profiles.Count == 0) return;
        if (_main is { HasExited: false } && ServerCidrsKey(profiles) != _mainServerKey)
        {
            L("Список IP шлюзов изменился — пересобираю main (TUN) под новую подписку.");
            StopMain();
        }
        StartAuto(profiles);
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
            if (!up) { SetHealth(p, false); StopProvider(); continue; }

            if (!ProbeRouteOk(p, 8000, 2)) { SetHealth(p, false); L($"'{p.Name}': сквозная проверка не прошла."); StopProvider(); continue; }
            if (_userStopped) { Teardown(); return; }

            SetHealth(p, true);
            _active = p;
            _probeFails = 0;
            L($"Подключено через '{p.Name}'.");
            StateChanged?.Invoke(true);
            Status?.Invoke($"Подключено: {p.Name}");
            StartWatchdog();
            StartCensus();
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
        // Живой шлюз не бросаем из-за одного сбоя пробы: кратковременная потеря/нагрузка (в т.ч. фоновый
        // census) или троттлинг одного хоста — не повод рвать рабочее соединение. Подтверждаем двумя
        // запросами к двум разным хостам и терпим до 3 тиков подряд (~75с), прежде чем переподключаться.
        if (WatchdogProbe()) { _probeFails = 0; return; }
        _probeFails++;
        L($"Health-check не прошёл ({_probeFails}/3).");
        if (_probeFails >= 3) { _probeFails = 0; HealthLost("нет ответа через туннель"); }
    }

    // Терпимая проба живого туннеля: любой из двух независимых хостов ответил ⇒ туннель жив.
    // Два хоста, чтобы точечный троттлинг/блокировка одного не читались как «связь потеряна».
    private bool WatchdogProbe()
        => _active?.Kind == ProfileKind.Awg
            ? Probe(4000, SocksPort, "http://1.1.1.1", anyResponse: true)
              || Probe(4000, SocksPort, "http://1.0.0.1", anyResponse: true)
            : Probe(4000, SocksPort, "http://www.gstatic.com/generate_204", anyResponse: false)
              || Probe(4000, SocksPort, "http://cp.cloudflare.com/generate_204", anyResponse: false);

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
        StopCensusProc(); // прибить скан на лету, чтобы не мешал пересборке
        StopProvider(); // TUN (_main) остаётся — kill switch держит, утечки нет
        L($"Связь потеряна: {reason}. Пересобираю (TUN остаётся поднят).");
        var cands = _candidates;
        int idx = (_active != null) ? cands.IndexOf(_active) : -1;
        _active = null;
        StateChanged?.Invoke(false);
        Status?.Invoke("Переподключение…");
        // Сначала пробуем ТОТ ЖЕ шлюз (idx), а не следующий: если он просто моргнул, остаёмся на нём.
        // ConnectLoop сам переберёт остальные по кругу, только если текущий не поднимется — но начиная
        // с лучшего по правилу посадки (пинг + нагрузка), а не со следующего по списку.
        var lost = idx >= 0 ? cands[idx] : null;
        BeginConnect(SeatOrder(cands, lost), _autoSwitch, 0);
    }

    // ---------- RouteSteer: выбор маршрута по пингу и нагрузке шлюза ----------
    // Правило (то же, что на Android):
    //  1. Посадка (первый скан после подключения или текущий умер): живые по возрастанию пинга, первый
    //     с нагрузкой ≤ 50%; все > 50% — наименее загруженный.
    //  2. Сел — сидит: нагрузка текущего сама по себе не повод уходить.
    //  3. Уходим только если сглаженный пинг текущего вырос на ≥ 100 мс и в ≥ 1,5 раза относительно
    //     посадки — на маршрут с лучшим пингом и нагрузкой ≤ 50%; такого нет — остаёмся.
    private const int LoadLimit = 50, WorseAbsMs = 100;
    private const double WorseRatio = 1.5;
    private bool _seatedByScan;          // первый скан после подключения уже пересадил по правилу 1
    private string? _seatKey;            // маршрут, для которого копим базу пинга
    private int _seatBaseline;
    private readonly Queue<int> _seatRecent = new();

    private void SetDelay(VlessProfile p, int ms) { lock (_healthGate) _routeDelay[RouteKey(p)] = ms; }

    private int DelayOf(VlessProfile p) { lock (_healthGate) return _routeDelay.TryGetValue(RouteKey(p), out var d) ? d : 0; }

    private static int? LoadOf(VlessProfile p) => Heartbeat.LastLoad.TryGetValue(p.Name, out var l) ? l : null;

    private static bool LoadOk(VlessProfile p) => LoadOf(p) is not int l || l <= LoadLimit;

    // время ответа через socks-порт (мс; 0 — не прошёл). Запрос по IP, без резолва — годится и для AWG.
    private int TimedProbe(int port)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = Probe(5000, port, "http://1.1.1.1", anyResponse: true);
        return ok ? Math.Max(1, (int)sw.ElapsedMilliseconds) : 0;
    }

    /// <summary>Правило посадки: лучший маршрут или null, если пингов ещё нет.</summary>
    private VlessProfile? PickSeat(List<VlessProfile> cands)
    {
        var alive = cands.Select(p => (p, d: DelayOf(p))).Where(x => x.d > 0).OrderBy(x => x.d).ToList();
        if (alive.Count == 0) return null;
        var ok = alive.FirstOrDefault(x => LoadOk(x.p));
        if (ok.p != null) return ok.p;
        return alive.OrderBy(x => LoadOf(x.p) ?? 0).First().p;
    }

    /// <summary>Порядок перебора при переподключении: сначала прежний (вдруг моргнул), затем живые
    /// не перегруженные по пингу, затем перегруженные по нагрузке, затем без замеров — в исходном порядке.</summary>
    private List<VlessProfile> SeatOrder(List<VlessProfile> cands, VlessProfile? first)
    {
        var rest = cands.Where(p => !ReferenceEquals(p, first)).ToList();
        var measured = rest.Where(p => DelayOf(p) > 0).ToList();
        var ordered = measured.Where(LoadOk).OrderBy(DelayOf)
            .Concat(measured.Where(p => !LoadOk(p)).OrderBy(p => LoadOf(p) ?? 0))
            .Concat(rest.Where(p => DelayOf(p) <= 0));
        var outList = new List<VlessProfile>();
        if (first != null) outList.Add(first);
        outList.AddRange(ordered);
        return outList;
    }

    private void Steer()
    {
        if (_userStopped || _connecting || !_autoSwitch) return;
        var cur = _active;
        var cands = _candidates;
        if (cur == null || cands == null || cands.Count < 2) return;

        // 1) первый скан после подключения: садимся по правилу (на старте пингов ещё не было)
        if (!_seatedByScan)
        {
            _seatedByScan = true;
            var best = PickSeat(cands);
            if (best != null && !ReferenceEquals(best, cur))
            {
                L($"steer: посадка на '{best.Name}' ({DelayOf(best)} мс, нагрузка {LoadOf(best)?.ToString() ?? "?"}%)");
                SwitchTo(best);
                return;
            }
        }

        // 2) копим сглаженный пинг текущего (медиана последних 3 сканов); база — минимум первых замеров
        var key = RouteKey(cur);
        if (_seatKey != key) { _seatKey = key; _seatBaseline = 0; _seatRecent.Clear(); }
        int d = DelayOf(cur);
        if (d <= 0) return;
        _seatRecent.Enqueue(d);
        while (_seatRecent.Count > 3) _seatRecent.Dequeue();
        var sorted = _seatRecent.OrderBy(x => x).ToList();
        int sm = sorted[sorted.Count / 2];
        if (_seatBaseline == 0 || (_seatRecent.Count < 3 && sm < _seatBaseline)) _seatBaseline = sm;
        if (_seatRecent.Count < 3) return;

        // 3) уходим только при заметном ухудшении и только на более быстрый и не перегруженный
        if (sm - _seatBaseline >= WorseAbsMs && sm >= _seatBaseline * WorseRatio)
        {
            var target = cands.Where(p => !ReferenceEquals(p, cur) && DelayOf(p) > 0 && DelayOf(p) < sm && LoadOk(p))
                              .OrderBy(DelayOf).FirstOrDefault();
            if (target != null)
            {
                L($"steer: пинг '{cur.Name}' {_seatBaseline}→{sm} мс — ухожу на '{target.Name}' ({DelayOf(target)} мс)");
                SwitchTo(target);
            }
        }
    }

    private void SwitchTo(VlessProfile target)
    {
        var cands = _candidates;
        int idx = cands.IndexOf(target);
        if (idx < 0 || _connecting || _userStopped) return;
        _connecting = true;
        StopWatchdog();
        StopProvider(); // TUN остаётся — kill switch держит
        _active = null;
        StateChanged?.Invoke(false);
        Status?.Invoke("Переключение маршрута…");
        BeginConnect(cands, _autoSwitch, idx);
    }

    // ---------- census: фоновый скан ВСЕХ маршрутов ----------
    // Основной перебор (ConnectLoop) останавливается на первом живом шлюзе, поэтому про остальные
    // маршруты здоровье неизвестно. Censu-скан периодически поднимает КАЖДЫЙ неактивный маршрут на
    // отдельном scratch-порту (24809; боевой 24808/TUN/активный провайдер не трогаем), гоняет сквозную
    // проверку и пишет результат в _routeHealth. Это и есть «полный скан», как urltest на Android:
    // в /hb → панель уходит реальная картина живых/мёртвых по всем шлюзам, а не только активного.

    private void StartCensus()
    {
        StopCensus();
        // Скан включён во всех сборках. Раньше он ронял главный TUN, но причиной был
        // auto_detect_interface в скан-конфиге AWG (конфликт с strict_route главного TUN → падение main
        // на 15-й секунде). Устранено в BuildAwgScanConfig (скан AWG без auto_detect_interface);
        // проверено на Windows — TUN держит, health неактивных маршрутов возвращается в /hb → панель.
        _census = new System.Threading.Timer(_ => CensusTick(), null, 12000, 75000);
    }

    private void StopCensus()
    {
        try { _census?.Dispose(); } catch { }
        _census = null;
        StopCensusProc();
    }

    private void StopCensusProc()
    {
        var p = _censusProc;
        _censusProc = null;
        try { if (p is { HasExited: false }) { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } } catch { }
    }

    private void CensusTick()
    {
        if (_userStopped || _connecting) return;
        if (!(_main is { HasExited: false })) return;     // смысл только при поднятом TUN
        if (_probeFails > 0) return;                      // туннель сейчас шаткий — не грузим его сканом
        lock (_censusGate) { if (_censusRunning) return; _censusRunning = true; }
        try { CensusScan(); }
        catch (Exception ex) { L("census: " + ex.Message); }
        finally { lock (_censusGate) _censusRunning = false; }
    }

    private void CensusScan()
    {
        var cands = _candidates;
        if (cands == null || cands.Count == 0) return;
        var active = _active;

        var keys = new HashSet<string>();
        foreach (var p in cands)
        {
            if (_userStopped || _connecting) return;
            if (!(_main is { HasExited: false })) return;
            keys.Add(RouteKey(p));

            // активный маршрут держит watchdog — он заведомо жив, не трогаем его порт; пинг меряем через
            // рабочий socks (тёплое соединение, как и у остальных — второй запрос после хендшейка)
            if (active != null && ReferenceEquals(p, active))
            {
                SetHealth(p, true);
                SetDelay(p, TimedProbe(SocksPort));
                continue;
            }

            var ok = CensusProbeRoute(p, out var ms);
            SetHealth(p, ok);
            SetDelay(p, ok ? ms : 0);
        }

        // убрать из health маршруты, которых больше нет в списке (сменилась подписка)
        lock (_healthGate)
        {
            foreach (var k in _routeHealth.Keys.Where(k => !keys.Contains(k)).ToList())
                _routeHealth.Remove(k);
            foreach (var k in _routeDelay.Keys.Where(k => !keys.Contains(k)).ToList())
                _routeDelay.Remove(k);
        }

        try { Steer(); } catch (Exception ex) { L("steer: " + ex.Message); }
    }

    private bool CensusProbeRoute(VlessProfile p, out int delayMs)
    {
        delayMs = 0;
        StopCensusProc();
        bool started;
        try
        {
            started = p.Kind switch
            {
                ProfileKind.Hysteria2 => StartCensusProvider(p, BuildHyProviderConfig, SingBoxPath, CensusHyPath, Path.Combine(_baseDir, "core"), checkTcp: false),
                ProfileKind.Awg       => StartCensusProvider(p, BuildAwgScanConfig, SingBoxPath, CensusAwgPath, Path.Combine(_baseDir, "core"), checkTcp: false),
                _                     => StartCensusProvider(p, BuildXrayConfig, XrayPath, CensusXrayPath, _baseDir, checkTcp: true),
            };
        }
        catch (Exception ex) { L($"census '{p.Name}': {ex.Message}"); StopCensusProc(); return false; }

        if (!started) { StopCensusProc(); return false; }

        bool ok = false;
        // проба по IP + «любой ответ»: без резолва домена (иначе AWG, который везёт только IP, валит
        // проверку). 3 попытки по 8с — AWG-хендшейку нужно время на первый коннект.
        for (int i = 0; i < 3 && !ok; i++) { if (_userStopped) break; ok = Probe(8000, CensusPort, "http://1.1.1.1", anyResponse: true); }
        // пинг — второй запрос по уже поднятому маршруту (первый включал хендшейк)
        if (ok) delayMs = TimedProbe(CensusPort);
        StopCensusProc();
        L($"census '{p.Name}': {(ok ? $"жив, {delayMs} мс" : "мёртв")}");
        return ok;
    }

    // строит scratch-конфиг на CensusPort, поднимает ядро на нём и ждёт порт
    private bool StartCensusProvider(VlessProfile p, Action<VlessProfile, int, string> build,
                                     string core, string cfgPath, string workDir, bool checkTcp)
    {
        if (checkTcp && !PortAlive(p.Host, p.Port, 2500)) return false;  // TCP-маршрут: быстрый отсев
        build(p, CensusPort, cfgPath);
        _censusProc = StartCensusProc(core, $"run -c \"{cfgPath}\"", workDir);
        return WaitForPort(CensusPort, 8000);
    }

    // тихий запуск ядра для скана: без EnableRaisingEvents/Exited (иначе Kill дёрнул бы HealthLost
    // и спровоцировал боевое переподключение). Вывод дренируем в никуда, чтобы не висло на буфере.
    private Process StartCensusProc(string file, string args, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file, Arguments = args, WorkingDirectory = workDir,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        var pr = new Process { StartInfo = psi };
        pr.OutputDataReceived += (_, _) => { };
        pr.ErrorDataReceived += (_, _) => { };
        pr.Start();
        pr.BeginOutputReadLine();
        pr.BeginErrorReadLine();
        return pr;
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

    private void Teardown() { StopCensusProc(); StopProvider(); StopMain(); }

    public void Stop()
    {
        _userStopped = true;
        StopWatchdog();
        StopRetry();
        StopCensus();
        StopProvider();
        StopMain();
        _active = null;
        lock (_healthGate) _routeHealth.Clear();
        StateChanged?.Invoke(false);
        Status?.Invoke("Отключено");
    }
}
