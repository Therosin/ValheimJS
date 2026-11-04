using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace ValheimJS.Networking;

/// <summary>JSON DTO conversion for native event consumers and binding adapters.</summary>
internal static class NetworkJson
{
    internal static string Write<T>(T value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    internal static T Read<T>(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json), false);
        return (T)new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).ReadObject(stream)!;
    }
}
