using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QarttzVPN;

/// <summary>
/// Гибрид: xray (reality-туннель + локальный SOCKS) + sing-box (TUN -> SOCKS).
/// Умеет автоподбор живого шлюза из списка профилей (сквозная проверка вход→выход→интернет)
/// и автопереключение на резервный при сбое (вотчдог).
/// </summary>
public class SingBoxManager
{
    public event Action<string>? Log;
    public event Action<bool>? StateChanged;   // true=подключено, false=отключено
    public event Action<string>? Status;       // текстовый статус для UI

    private Process? _xray;
    private Process? _singbox;
    private readonly string _baseDir;
    private readonly string _logPath;
    private const int SocksPort = 24808;

    // состояние автоподбора/вотчдога
    private readonly object _gate = new();
    private volatile bool _connecting;
    private volatile bool _userStopped;
    private List<VlessProfile> _candidates = new();
    private bool _autoSwitch;
    private VlessProfile? _active;
    private int _probeFails;
    private System.Threading.Timer? _watchdog;

    public bool IsRunning => _singbox is { HasExited: false } || _xray is { HasExited: false };
    public string? ActiveName => _active?.Name;

    public SingBoxManager()
    {
        _baseDir = AppContext.BaseDirectory;
        _logPath = Path.Combine(_baseDir, "qvpn.log");
        try { File.AppendAllText(_logPath, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===="+Environment.NewLine); } catch { }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    private void L(string s)
    {
        try { File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss} {s}{Environment.NewLine}"); } catch { }
        Log?.Invoke(s);
    }

    private string XrayPath          => Path.Combine(_baseDir, "core", "xray.exe");
    private string SingBoxPath       => Path.Combine(_baseDir, "core", "sing-box.exe");
    private string XrayConfigPath    => Path.Combine(_baseDir, "xray-runtime.json");
    private string TemplatePath      => Path.Combine(_baseDir, "config.template.json");
    private string ExcludePath       => Path.Combine(_baseDir, "exclude.txt");
    private string AppsPath          => Path.Combine(_baseDir, "apps.txt");
    private string RuntimeConfigPath => Path.Combine(_baseDir, "runtime-config.json");

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
            if (System.Net.IPNetwork.TryParse(line, out _))
                list.Add(line);
            else
                L($"Пропущена некорректная строка исключений: '{raw.Trim()}'");
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
            if (line.Contains('\\') || line.Contains('/'))
            {
                L($"Пропущено (нужно имя exe, не путь): '{raw.Trim()}'");
                continue;
            }
            list.Add(line);
        }
        return list;
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
        L($"xray-конфиг профиля '{profile.Name}' создан ({profile.Host}:{profile.Port}).");
    }

    private void BuildRuntimeConfig(VlessProfile profile)
    {
        if (!File.Exists(TemplatePath))
            throw new FileNotFoundException("Не найден config.template.json.", TemplatePath);

        var root = JsonNode.Parse(File.ReadAllText(TemplatePath))!.AsObject();

        if (profile.Kind == ProfileKind.Hysteria2)
        {
            // sing-box ведёт hysteria2 сам: меняем socks-outbound "proxy" на hysteria2-outbound
            // и добавляем mixed-inbound на локальный порт для сквозной проверки (Probe).
            var outs = root["outbounds"]!.AsArray();
            for (int i = outs.Count - 1; i >= 0; i--)
                if ((string?)outs[i]?["tag"] == "proxy") outs.RemoveAt(i);
            outs.Insert(0, profile.BuildSingboxOutbound());

            root["inbounds"]!.AsArray().Add(new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "probe-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = SocksPort,
            });
        }

        var rules = root["route"]!.AsObject()["rules"]!.AsArray();

        // 1) обход туннеля для самого сервера (обязательно)
        var srv = new JsonArray();
        foreach (var c in profile.ServerCidrs()) srv.Add(c);
        if (srv.Count > 0)
        {
            rules.Add(new JsonObject { ["ip_cidr"] = srv, ["action"] = "route", ["outbound"] = "direct" });
            L($"Сервер исключён из туннеля: {string.Join(", ", profile.ServerCidrs())}");
        }
        else
        {
            L("ВНИМАНИЕ: не удалось определить IP сервера для исключения.");
        }

        // Фильтры применяются только при включённой маршрутизации.
        var st = Settings.Load();
        if (!st.Routing)
        {
            L("Маршрутизация выключена — весь трафик в туннель (исключения не применяются).");
        }
        else
        {
            if (st.IpFilter)
            {
                var excludes = ReadExcludes();
                if (excludes.Count > 0)
                {
                    var arr = new JsonArray();
                    foreach (var c in excludes) arr.Add(c);
                    rules.Add(new JsonObject { ["ip_cidr"] = arr, ["action"] = "route", ["outbound"] = "direct" });
                    L($"Фильтр по IP включён, исключений: {excludes.Count}");
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
                    L($"Фильтр по приложениям включён: {apps.Count} ({string.Join(", ", apps)})");
                }
            }
        }

        File.WriteAllText(RuntimeConfigPath, root.ToJsonString(Json.Opts));
    }

    private Process StartProc(string file, string args, string workDir, string label)
    {
        L($"{label}: запуск {Path.GetFileName(file)} {args}");
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => Handle(label, e.Data);
        p.ErrorDataReceived  += (_, e) => Handle(label, e.Data);
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

    /// <summary>Быстрый TCP-коннект к самому шлюзу — отсеять заведомо мёртвые.</summary>
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

    /// <summary>Сквозная проверка: HTTP 204 через локальный SOCKS (весь путь вход→выход→интернет).</summary>
    private bool Probe(int timeoutMs)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{SocksPort}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(Math.Min(timeoutMs, 5000)),
                AllowAutoRedirect = false,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var r = http.GetAsync("http://www.gstatic.com/generate_204").GetAwaiter().GetResult();
            return (int)r.StatusCode == 204 || r.IsSuccessStatusCode;
        }
        catch (Exception ex) { L("Probe: " + ex.Message); return false; }
    }

    // ---------- публичные точки входа ----------

    /// <summary>Ручной выбор одного профиля (без переключения, но с переподключением при сбое).</summary>
    public void Start(VlessProfile profile)
    {
        _userStopped = false;
        BeginConnect(new List<VlessProfile> { profile }, autoSwitch: false, startIndex: 0);
    }

    /// <summary>Режим «Авто»: подобрать живой шлюз и переключаться при сбоях.</summary>
    public void StartAuto(List<VlessProfile> profiles)
    {
        _userStopped = false;
        BeginConnect(new List<VlessProfile>(profiles), autoSwitch: true, startIndex: 0);
    }

    private void BeginConnect(List<VlessProfile> cands, bool autoSwitch, int startIndex)
    {
        _connecting = true; // синхронно — чтобы вотчдог/Exited не вмешивались
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
        KillProcs();

        if (!File.Exists(XrayPath) || !File.Exists(SingBoxPath))
        {
            Status?.Invoke("Не найдены ядра (core\\xray.exe / sing-box.exe)");
            StateChanged?.Invoke(false);
            return;
        }
        if (cands.Count == 0)
        {
            Status?.Invoke("Нет профилей");
            StateChanged?.Invoke(false);
            return;
        }

        for (int i = 0; i < cands.Count; i++)
        {
            if (_userStopped) { KillProcs(); return; }
            var p = cands[(startIndex + i) % cands.Count];

            Status?.Invoke(cands.Count > 1 ? $"Проверка: {p.Name}…" : $"Подключение: {p.Name}…");
            L($"Пробуем шлюз '{p.Name}' ({p.Host}:{p.Port}) [{p.Kind}].");

            bool ok = p.Kind == ProfileKind.Hysteria2 ? TryConnectHysteria(p) : TryConnectVless(p);
            if (_userStopped) { KillProcs(); return; }
            if (!ok) { KillProcs(); continue; }

            // успех
            _active = p;
            _candidates = cands;
            _autoSwitch = autoSwitch;
            _probeFails = 0;
            L($"Подключено через '{p.Name}'.");
            StateChanged?.Invoke(true);
            Status?.Invoke($"Подключено: {p.Name}");
            StartWatchdog();
            return;
        }

        // ничего не подошло
        KillProcs();
        _active = null;
        StateChanged?.Invoke(false);
        Status?.Invoke(autoSwitch ? "Нет доступных шлюзов" : "Шлюз недоступен");
    }

    /// <summary>vless: xray (reality→SOCKS) + sing-box (TUN→SOCKS). true = поднялось полностью.</summary>
    private bool TryConnectVless(VlessProfile p)
    {
        if (!PortAlive(p.Host, p.Port, 2500)) { L($"'{p.Name}': порт закрыт."); return false; }
        if (_userStopped) return false;

        try { BuildXrayConfig(p); } catch (Exception ex) { L("BuildXrayConfig: " + ex.Message); return false; }
        _xray = StartProc(XrayPath, $"run -c \"{XrayConfigPath}\"", _baseDir, "xray");
        if (!WaitForPort(SocksPort, 8000)) { L($"'{p.Name}': SOCKS не поднялся."); return false; }
        if (_userStopped) return false;

        if (!Probe(6000)) { L($"'{p.Name}': сквозная проверка не прошла."); return false; }
        if (_userStopped) return false;

        try { BuildRuntimeConfig(p); } catch (Exception ex) { L("BuildRuntimeConfig: " + ex.Message); return false; }
        _singbox = StartProc(SingBoxPath, $"run -c \"{RuntimeConfigPath}\"", Path.Combine(_baseDir, "core"), "sing-box");
        Thread.Sleep(1500);
        if (!(_xray is { HasExited: false } && _singbox is { HasExited: false }))
        { L($"'{p.Name}': процесс упал на старте."); return false; }
        return true;
    }

    /// <summary>hysteria2: один sing-box (TUN + hysteria2-outbound + mixed для проверки). true = поднялось.</summary>
    private bool TryConnectHysteria(VlessProfile p)
    {
        // hysteria2 — UDP, TCP-отсев по порту неинформативен, пропускаем его.
        try { BuildRuntimeConfig(p); } catch (Exception ex) { L("BuildRuntimeConfig: " + ex.Message); return false; }
        _singbox = StartProc(SingBoxPath, $"run -c \"{RuntimeConfigPath}\"", Path.Combine(_baseDir, "core"), "sing-box");
        if (!WaitForPort(SocksPort, 8000)) { L($"'{p.Name}': локальный SOCKS (sing-box) не поднялся."); return false; }
        if (_userStopped) return false;

        if (!Probe(6000)) { L($"'{p.Name}': сквозная проверка не прошла."); return false; }
        if (_userStopped) return false;

        if (!(_singbox is { HasExited: false })) { L($"'{p.Name}': sing-box упал на старте."); return false; }
        return true;
    }

    // ---------- вотчдог ----------

    private void StartWatchdog()
    {
        StopWatchdog();
        _watchdog = new System.Threading.Timer(_ => WatchdogTick(), null, 25000, 25000);
    }
    private void StopWatchdog()
    {
        try { _watchdog?.Dispose(); } catch { }
        _watchdog = null;
    }

    private void WatchdogTick()
    {
        if (_userStopped || _connecting) return;
        bool needXray = _active?.Kind != ProfileKind.Hysteria2;   // hysteria2 идёт без xray
        bool procsOk = _singbox is { HasExited: false } && (!needXray || _xray is { HasExited: false });
        if (!procsOk) { HealthLost("процесс не запущен"); return; }
        if (Probe(6000)) { _probeFails = 0; return; }
        _probeFails++;
        L($"Health-check не прошёл ({_probeFails}/2).");
        if (_probeFails >= 2) { _probeFails = 0; HealthLost("нет ответа через туннель"); }
    }

    private void OnProcDied(string label)
    {
        if (_userStopped || _connecting) return; // намеренный kill / идёт переподключение
        HealthLost($"процесс {label} упал");
    }

    private void HealthLost(string reason)
    {
        if (_userStopped || _connecting) return;
        _connecting = true;           // сразу закрываем вход для повторных срабатываний
        StopWatchdog();
        L($"Связь потеряна: {reason}.");
        var cands = _candidates;
        var active = _active;
        int idx = (active != null) ? cands.IndexOf(active) : -1;
        if (_autoSwitch && cands.Count > 1)
        {
            Status?.Invoke("Переключение на резервный…");
            BeginConnect(cands, true, (idx + 1) % cands.Count);
        }
        else
        {
            Status?.Invoke("Переподключение…");
            BeginConnect(cands, _autoSwitch, idx < 0 ? 0 : idx);
        }
    }

    // ---------- останов ----------

    private void Handle(string label, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        L($"[{label}] {line}");
    }

    private void KillProcs()
    {
        foreach (var p in new[] { _singbox, _xray })
        {
            try { if (p is { HasExited: false }) { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } }
            catch { }
        }
        _singbox = null;
        _xray = null;
    }

    public void Stop()
    {
        _userStopped = true;
        StopWatchdog();
        KillProcs();
        _active = null;
        StateChanged?.Invoke(false);
        Status?.Invoke("Отключено");
    }
}
