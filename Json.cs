using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace QarttzVPN;

/// <summary>
/// Общие опции сериализации. В single-file сборке рефлексивный резолвер
/// по умолчанию выключен — задаём его явно, иначе ToJsonString(options) падает.
/// </summary>
static class Json
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
}
