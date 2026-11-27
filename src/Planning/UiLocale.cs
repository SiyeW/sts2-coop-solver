using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CoopSolver;

internal static class UiLocale
{
    private const string Prefix="CoopSolver.Locales.";
    private static readonly Lazy<Dictionary<string,Dictionary<string,string>>> tables=new(Load);

    private static Dictionary<string,Dictionary<string,string>> Load()
    {
        var assembly=typeof(UiLocale).Assembly;
        var result=new Dictionary<string,Dictionary<string,string>>(StringComparer.OrdinalIgnoreCase);
        foreach(string resource in assembly.GetManifestResourceNames())
        {
            if(!resource.StartsWith(Prefix,StringComparison.Ordinal) || !resource.EndsWith(".json",StringComparison.Ordinal))continue;
            using var stream=assembly.GetManifestResourceStream(resource)!;
            result.Add(resource[Prefix.Length..^5],JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!);
        }
        return result;
    }

    internal static bool Includes(string language)=>tables.Value.ContainsKey(language);

    internal static string Text(string language,CultureInfo culture,string key,params object?[] values)
    {
        var files=tables.Value;
        if(files.TryGetValue(language,out var local) && local.TryGetValue(key,out string? value) ||
            files.TryGetValue("eng",out var english) && english.TryGetValue(key,out value))
            return values.Length==0?value:string.Format(culture,value,values);
        return "";
    }
}
