using System.Collections;
using System.Globalization;
using System.Resources;
using System.Windows;
using NightDim.Core;

namespace NightDim.Localization;

internal static class UiText
{
    private static readonly ResourceManager Strings =
        new("NightDim.Localization.Strings", typeof(UiText).Assembly);
    private static ResourceDictionary? _dictionary;

    internal static string Language { get; private set; } = "ru";
    internal static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("ru-RU");

    internal static void Apply(Application app, string language)
    {
        Language = language == "en" ? "en" : "ru";
        Culture = CultureInfo.GetCultureInfo(Language == "en" ? "en-US" : "ru-RU");
        CultureInfo.CurrentUICulture = Culture;

        var dictionary = new ResourceDictionary();
        foreach (DictionaryEntry entry in Strings.GetResourceSet(CultureInfo.InvariantCulture, true, false)!)
        {
            var key = (string)entry.Key;
            dictionary.Add(key, Get(key));
        }
        var dictionaries = app.Resources.MergedDictionaries;
        var oldIndex = _dictionary is null ? -1 : dictionaries.IndexOf(_dictionary);
        if (oldIndex < 0)
            dictionaries.Add(dictionary);
        else
            dictionaries[oldIndex] = dictionary;
        _dictionary = dictionary;
    }

    internal static string Get(string key) => Strings.GetString(key, Culture) ?? key;

    internal static string Format(string key, object value) => string.Format(Culture, Get(key), value);

    internal static string Describe(DisplayEffectResult result)
    {
        var status = Get("Effect_" + result.Status);
        return result.AdditionalDiagnostic is { } additional
            ? status + " " + Get("Effect_" + additional)
            : status;
    }
}

internal readonly record struct UiMessage(string Key, string? Detail = null)
{
    internal string Text => Detail is null ? UiText.Get(Key) : UiText.Format(Key, Detail);
}
