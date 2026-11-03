using System.Globalization;
using System.Threading;
using System.Windows;

namespace Switch2ProManager.Services;

public sealed record LanguageOption(string Code, string Name);

/// <summary>
/// Interface language. The strings live in Resources/Strings.{code}.xaml, merged into the application resources,
/// so {DynamicResource} bindings in XAML update when the language changes. Logs are never translated (docs/UI_SPEC.md, section 8).
/// </summary>
public static class AppText
{
    public static readonly LanguageOption[] Options = { new("en", "English"), new("es", "Spanish") };

    public static event Action? Changed;

    public static void Apply(string code)
    {
        if (!Options.Any(o => o.Code == code)) code = "en";

        var merged = Application.Current.Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("Resources/Strings.", StringComparison.Ordinal) == true);
        if (old is not null) merged.Remove(old);
        merged.Add(new ResourceDictionary { Source = new Uri($"/Switch2ProManager;component/Resources/Strings.{code}.xaml", UriKind.Relative) });

        var culture = CultureInfo.GetCultureInfo(code == "en" ? "en-US" : "es-ES");
        Thread.CurrentThread.CurrentCulture = culture;     // number formats: 4,20 ms in Spanish, 4.20 ms in English
        Thread.CurrentThread.CurrentUICulture = culture;
        Changed?.Invoke();
    }

    /// <summary>The text for a key, or the key itself when it is missing (so gaps are visible on screen).</summary>
    public static string Get(string key) => Application.Current?.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
