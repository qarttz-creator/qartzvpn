namespace QarttzVPN;

/// <summary>
/// Флаг сборки. DevTools=true — инженерная сборка (открытый лог-файл рядом с exe, dev-мелочи).
/// false — клиентская: лог только в памяти и уходит наружу лишь зашифрованным (LogExport).
/// Задаётся из CI через -p:QartzDev=true (иначе клиент).
/// </summary>
internal static class Build
{
#if QARTZ_DEV
    public const bool DevTools = true;
#else
    public const bool DevTools = false;
#endif
}
