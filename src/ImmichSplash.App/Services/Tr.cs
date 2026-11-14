using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using ImmichSplash.Core;

namespace ImmichSplash.App.Services;

/// <summary>A language the app is translated into.</summary>
public sealed record AppLanguage(string Code, string Name);

/// <summary>
/// The app's languages. Text is looked up by its English wording (Assets/i18n/&lt;code&gt;.json maps English
/// to the translation), so anything untranslated stays English. In XAML, write
/// <c>l:Tr.Text="Sign out"</c> instead of <c>Text="Sign out"</c> (likewise Content, Watermark, Tip, Placeholder).
/// </summary>
public static class Tr
{
    public static IReadOnlyList<AppLanguage> Languages { get; } =
    [
        new("en", "English"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("nl", "Nederlands"),
        new("pl", "Polski"),
        new("pt", "Português"),
    ];

    static Dictionary<string, string> _strings = [];

    /// <summary>The language in use (a code from <see cref="Languages"/>).</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>The system's language if the app has it, else English.</summary>
    public static string SystemLanguage
    {
        get
        {
            var code = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
            return Languages.Any(l => l.Code == code) ? code : "en";
        }
    }

    /// <summary>Switches language: <paramref name="code"/> null means follow the system. Views made afterwards use it.</summary>
    public static void SetLanguage(string? code)
    {
        code = code is not null && Languages.Any(l => l.Code == code) ? code : SystemLanguage;
        Current = code;
        _strings = Load(code);
        Loc.Translate = T;

        // Dates, numbers and month names follow the language too (keeping the system's own
        // regional formats when the language matches).
        var system = CultureInfo.InstalledUICulture;
        var culture = system.TwoLetterISOLanguageName == code ? system : CultureInfo.GetCultureInfo(code);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    static Dictionary<string, string> Load(string code)
    {
        if (code == "en")
            return [];
        try
        {
            var uri = new Uri($"avares://ImmichSplash/Assets/i18n/{code}.json");
            if (!AssetLoader.Exists(uri))
                return [];
            using var stream = AssetLoader.Open(uri);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not load the {code} translation", ex);
            return [];
        }
    }

    public static string T(string english) =>
        _strings.TryGetValue(english, out var translated) && translated.Length > 0 ? translated : english;

    public static string T(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

    // ---- XAML attached properties ----

    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Text", typeof(Tr));
    public static readonly AttachedProperty<string?> ContentProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Content", typeof(Tr));
    public static readonly AttachedProperty<string?> WatermarkProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Watermark", typeof(Tr));
    public static readonly AttachedProperty<string?> TipProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Tip", typeof(Tr));
    public static readonly AttachedProperty<string?> PlaceholderProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Placeholder", typeof(Tr));
    public static readonly AttachedProperty<string?> TitleProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Title", typeof(Tr));
    public static readonly AttachedProperty<string?> HeaderProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Header", typeof(Tr));

    static Tr()
    {
        TextProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            var text = Translate(e.NewValue);
            if (c is TextBlock block) block.Text = text;
            else if (c is TextBox box) box.Text = text;
        });
        ContentProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (c is ContentControl content) content.Content = Translate(e.NewValue);
        });
        WatermarkProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (c is TextBox box) box.Watermark = Translate(e.NewValue);
        });
        TipProperty.Changed.AddClassHandler<Control>((c, e) => ToolTip.SetTip(c, Translate(e.NewValue)));
        PlaceholderProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (c is ComboBox combo) combo.PlaceholderText = Translate(e.NewValue);
        });
        TitleProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (c is Window window) window.Title = Translate(e.NewValue);
        });
        HeaderProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (c is MenuItem item) item.Header = Translate(e.NewValue);
        });
    }

    static string? Translate(object? value) => value is string s ? T(s) : null;

    public static string? GetText(Control c) => c.GetValue(TextProperty);
    public static void SetText(Control c, string? v) => c.SetValue(TextProperty, v);
    public static string? GetContent(Control c) => c.GetValue(ContentProperty);
    public static void SetContent(Control c, string? v) => c.SetValue(ContentProperty, v);
    public static string? GetWatermark(Control c) => c.GetValue(WatermarkProperty);
    public static void SetWatermark(Control c, string? v) => c.SetValue(WatermarkProperty, v);
    public static string? GetTip(Control c) => c.GetValue(TipProperty);
    public static void SetTip(Control c, string? v) => c.SetValue(TipProperty, v);
    public static string? GetPlaceholder(Control c) => c.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(Control c, string? v) => c.SetValue(PlaceholderProperty, v);
    public static string? GetTitle(Control c) => c.GetValue(TitleProperty);
    public static void SetTitle(Control c, string? v) => c.SetValue(TitleProperty, v);
    public static string? GetHeader(Control c) => c.GetValue(HeaderProperty);
    public static void SetHeader(Control c, string? v) => c.SetValue(HeaderProperty, v);
}
