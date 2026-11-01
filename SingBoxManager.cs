using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QarttzVPN;

/// <summary>
/// Гибрид: xray (reality-туннель + локальный SOCKS) + sing-box (TUN -> SOCKS).
/// Конфиг xray генерируется из выбранного профиля (vless-ссылки).
/// IP сервера автоматически исключается из туннеля (иначе петля).
/// </summary>
public class SingBoxManager
{
    public event Action<string>? Log;
    public event Action<bool>? StateChanged;

    private Process? _xray;
    private Process? _singbox;
    private readonly string _baseDir;
    private readonly string _logPath;
    private const int SocksPort = 24808;

    public bool IsRunning => _singbox is { HasExited: false } || _xray is { HasExited: false };

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
    private string RuntimeConfigPath => Path.Combine(_baseDir, "runtime-config.json");

    private List<string> ReadExcludes()
    {
        var list = new List<string>();
        if (!File.Exists(ExcludePath)) return list;
        foreach (var raw in File.ReadAllLines(ExcludePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            if (!line.Contains('/')) line += line.Contains(':') ? "/128" : "/32";
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
        File.WriteAllText(XrayConfigPath, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        L($"xray-конфиг профиля '{profile.Name}' создан ({profile.Host}:{profile.Port}).");
    }

    private void BuildRuntimeConfig(VlessProfile profile)
    {
        if (!File.Exists(TemplatePath))
            throw new FileNotFoundException("Не найден config.template.json.", TemplatePath);

        var root = JsonNode.Parse(File.ReadAllText(TemplatePath))!.AsObject();
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

        // 2) пользовательские исключения
        var excludes = ReadExcludes();
        if (excludes.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var c in excludes) arr.Add(c);
            rules.Add(new JsonObject { ["ip_cidr"] = arr, ["action"] = "route", ["outbound"] = "direct" });
            L($"Пользовательских исключений: {excludes.Count}");
        }

        File.WriteAllText(RuntimeConfigPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
        p.Exited += (_, _) => { L($"{label} завершился (code {TryExit(p)})."); Stop(); };
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

    public void Start(VlessProfile profile)
    {
        if (IsRunning) { L("Start(): уже запущено."); return; }
        try
        {
            if (!File.Exists(XrayPath)) throw new FileNotFoundException("Не найден core\\xray.exe.", XrayPath);
            if (!File.Exists(SingBoxPath)) throw new FileNotFoundException("Не найден core\\sing-box.exe.", SingBoxPath);

            BuildXrayConfig(profile);
            _xray = StartProc(XrayPath, $"run -c \"{XrayConfigPath}\"", _baseDir, "xray");

            L($"Ожидание SOCKS {SocksPort}…");
            if (!WaitForPort(SocksPort, 8000))
                throw new Exception($"xray не открыл порт {SocksPort}. Смотрите qvpn.log.");
            L("SOCKS поднят.");

            BuildRuntimeConfig(profile);
            _singbox = StartProc(SingBoxPath, $"run -c \"{RuntimeConfigPath}\"",
                                 Path.Combine(_baseDir, "core"), "sing-box");

            Task.Run(() =>
            {
                Thread.Sleep(2500);
                if (_xray is { HasExited: false } && _singbox is { HasExited: false })
                {
                    L("Туннель поднят.");
                    StateChanged?.Invoke(true);
                }
            });
        }
        catch (Exception ex)
        {
            L("START ERROR: " + ex.Message);
            Stop();
            throw;
        }
    }

    private void Handle(string label, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        L($"[{label}] {line}");
    }

    private bool _stopping;
    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        try
        {
            foreach (var p in new[] { _singbox, _xray })
            {
                try
                {
                    if (p is { HasExited: false }) { p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
                }
                catch { }
            }
        }
        finally
        {
            _singbox = null;
            _xray = null;
            StateChanged?.Invoke(false);
            _stopping = false;
        }
    }
}
