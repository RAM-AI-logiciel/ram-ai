using System.IO;
using System.Text.Json;
using RamAI.Phase4.Models;

namespace RamAI.Phase4.Services;

public sealed class SessionHistoryService
{
    private static readonly string SharedDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RAM-AI");

    private static readonly string SessionsPath = Path.Combine(SharedDir, "sessions.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public List<SessionRecord> Load()
    {
        try
        {
            if (!File.Exists(SessionsPath)) return new();
            var json = File.ReadAllText(SessionsPath);
            return JsonSerializer.Deserialize<List<SessionRecord>>(json, Opts) ?? new();
        }
        catch { return new(); }
    }

    public void Append(SessionRecord record)
    {
        try
        {
            Directory.CreateDirectory(SharedDir);
            var list = Load();
            list.Add(record);
            File.WriteAllText(SessionsPath, JsonSerializer.Serialize(list, Opts));
        }
        catch { }
    }
}
