using System;
using System.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YAPA.Shared.Contracts;

namespace YAPA.Avalonia.Specifics;

public class AvaloniaJson : IJson
{
    public string Serialize(object obj)
        => JsonConvert.SerializeObject(obj, Formatting.Indented);

    public T Deserialize<T>(string obj)
        => JsonConvert.DeserializeObject<T>(obj)!;

    public T ConvertToType<T>(object? value)
    {
        if (value == null)
            return default!;
        if (value.GetType() == typeof(T))
            return (T)value;
        if (typeof(T).IsEnum)
            return (T)Enum.ToObject(typeof(T), value);
        if (typeof(T).IsValueType || value is string)
            return (T)Convert.ChangeType(value, typeof(T));
        if (value is JArray ja)
            return ja.ToObject<T>()!;
        return ((JObject)value).ToObject<T>()!;
    }

    public bool AreEqual(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.GetType().IsValueType || a is string) return a.Equals(b);

        if (a.GetType().GetInterface(nameof(IEnumerable)) != null)
        {
            var la = (IEnumerable)a;
            IEnumerable lb = b is JObject jo
                ? (IEnumerable)jo.ToObject(a.GetType())!
                : (IEnumerable)b;
            return Count(la) == Count(lb) && AllContained(la, lb);
        }

        return false;
    }

    private static bool AllContained(IEnumerable a, IEnumerable b)
    {
        foreach (var bItem in b)
        {
            var found = false;
            foreach (var aItem in a)
                found |= bItem.ToString()!.Equals(aItem);
            if (!found) return false;
        }
        return true;
    }

    private static int Count(IEnumerable source)
    {
        int c = 0;
        var e = source.GetEnumerator();
        while (e.MoveNext()) c++;
        return c;
    }
}
