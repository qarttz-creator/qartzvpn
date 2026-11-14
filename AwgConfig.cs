using System.Text;
using System.Text.Json.Nodes;

namespace QarttzVPN;

/// <summary>
/// AmneziaWG (obfuscated WireGuard) — the Beeline-surviving transport. Parses an AmneziaWG config
/// (the INI Amnezia exports, or its vpn://&lt;base64&gt; / awg:// link) into a sing-box `awg` endpoint
/// object (amnezia-box fork schema). The desktop runs the AWG-capable sing-box.exe (built from the
/// fork), so the endpoint is understood natively.
/// </summary>
static class AwgConfig
{
    public static bool LooksLikeIni(string text) =>
        text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) &&
        text.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase);

    /// <summary>If [link] is a vpn:// / awg:// link (base64 of the INI), return the decoded INI; else null.</summary>
    public static string? FromLink(string link)
    {
        var l = link.Trim();
        foreach (var scheme in new[] { "vpn://", "awg://", "amneziawg://" })
        {
            if (l.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                var b64 = l.Substring(scheme.Length);
                var hash = b64.IndexOf('#');
                if (hash >= 0) b64 = b64.Substring(0, hash);
                return DecodeB64(b64);
            }
        }
        return null;
    }

    private static string? DecodeB64(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch { return null; }
    }

    /// <summary>Parse an AmneziaWG INI into a sing-box `awg` endpoint JSON, or null if it isn't one.</summary>
    public static JsonObject? ParseIni(string text, string tag)
    {
        var iface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inPeer = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            if (line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase)) { inPeer = false; continue; }
            if (line.StartsWith("[Peer]", StringComparison.OrdinalIgnoreCase)) { inPeer = true; continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            (inPeer ? peer : iface)[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }

        if (!iface.TryGetValue("PrivateKey", out var priv) || priv.Length == 0) return null;
        if (!peer.TryGetValue("PublicKey", out var pub) || pub.Length == 0) return null;
        if (!peer.TryGetValue("Endpoint", out var endpoint) || endpoint.Length == 0) return null;
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0) return null;
        var host = endpoint.Substring(0, colon).Trim('[', ']');
        if (!int.TryParse(endpoint.Substring(colon + 1), out var port)) return null;

        var ep = new JsonObject
        {
            ["type"] = "awg",
            ["tag"] = tag,
            ["useIntegratedTun"] = false,
            ["private_key"] = priv,
            ["address"] = CidrList(iface.GetValueOrDefault("Address", "10.8.0.2/32")),
        };
        if (int.TryParse(iface.GetValueOrDefault("MTU"), out var mtu)) ep["mtu"] = mtu;
        foreach (var p in new[] { "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4" })
            if (iface.TryGetValue(p, out var pv) && int.TryParse(pv, out var pi)) ep[p.ToLowerInvariant()] = pi;
        foreach (var s in new[] { "H1", "H2", "H3", "H4", "I1", "I2", "I3", "I4", "I5" })
            if (iface.TryGetValue(s, out var sv) && sv.Length > 0) ep[s.ToLowerInvariant()] = sv;
        if (iface.TryGetValue("HeaderProtectionKey", out var hpk) && hpk.Length > 0) ep["header_protection_key"] = hpk;
        if (iface.TryGetValue("ContentPaddingAddition", out var cpa) && cpa.Length > 0) ep["content_padding_addition"] = cpa;
        void Str(string key, string json) { if (iface.TryGetValue(key, out var v) && v.Length > 0) ep[json] = v; }
        Str("RekeyAfterTime", "rekey_after_time");
        Str("RekeyTimeout", "rekey_timeout");
        Str("RejectAfterTime", "reject_after_time");
        Str("KeepaliveTimeout", "keepalive_timeout");
        Str("MaxHandshakeAttempts", "max_handshake_attempts");
        if (BoolOn(iface.GetValueOrDefault("RandomTrailers"))) ep["random_trailers"] = true;
        if (BoolOn(iface.GetValueOrDefault("DisableCookies"))) ep["disable_cookies"] = true;

        var peerObj = new JsonObject
        {
            ["address"] = host,
            ["port"] = port,
            ["public_key"] = pub,
            ["allowed_ips"] = CidrList(peer.GetValueOrDefault("AllowedIPs", "0.0.0.0/0")),
        };
        if (peer.TryGetValue("PresharedKey", out var psk) && psk.Length > 0) peerObj["preshared_key"] = psk;
        // Если шлюз не прислал PersistentKeepalive — ставим стандартные 25с: без него простаивающая
        // AWG-сессия молчит, и роутер/оператор закрывает её UDP-трансляцию (так же, как на Android).
        peerObj["persistent_keepalive_interval"] =
            int.TryParse(peer.GetValueOrDefault("PersistentKeepalive"), out var pk) && pk > 0 ? pk : 25;
        ep["peers"] = new JsonArray { peerObj };
        return ep;
    }

    private static bool BoolOn(string? v)
    {
        var t = v?.Trim().ToLowerInvariant();
        return t == "on" || t == "true" || t == "1" || t == "yes";
    }

    private static JsonArray CidrList(string v)
    {
        var arr = new JsonArray();
        foreach (var part in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var it = part.Trim();
            if (it.Length == 0) continue;
            if (!it.Contains('/')) it += it.Contains(':') ? "/128" : "/32";
            arr.Add(it);
        }
        if (arr.Count == 0) arr.Add("0.0.0.0/0");
        return arr;
    }
}
