namespace CamLink.Desktop.Core;

public static class Adb
{
    /// <summary>
    /// <c>adb devices</c> (ya da <c>-l</c>) çıktısı: "List of devices attached" satırından sonraki
    /// "seri durum [ek sütunlar]" satırları. Başlıktan önceki gürültü, '*' satırları ve boş satırlar atlanır.
    /// </summary>
    public static IReadOnlyList<(string Serial, string State)> ParseDevices(string output)
    {
        var devices = new List<(string, string)>();
        var listed = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();   // \r de gider
            if (line.Length == 0 || line[0] == '*') continue;
            if (!listed) { listed = line.StartsWith("List of devices attached", StringComparison.Ordinal); continue; }
            var cols = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length >= 2) devices.Add((cols[0], cols[1]));
        }
        return devices;
    }

    public static string[] ReverseArgs(string serial, int port) => ["-s", serial, "reverse", $"tcp:{port}", $"tcp:{port}"];
}
