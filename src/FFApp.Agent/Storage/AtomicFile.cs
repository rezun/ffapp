using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FFApp.Agent.Storage;

/// <summary>Writes state files through a temporary file and a rename, so a crash never leaves half a file.</summary>
internal static class AtomicFile
{
    public static void WriteJson<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, typeInfo);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    public static T? ReadJson<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        if (!File.Exists(path))
            return default;
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, typeInfo);
    }
}
