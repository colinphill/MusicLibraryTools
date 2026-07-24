using System.Globalization;
using System.Text;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.UI.Tests;

internal sealed class TestPseudoLocalizationService :
    ILocalizationService
{
    private readonly ILocalizationService _inner;

    public TestPseudoLocalizationService(
        ILocalizationService inner,
        bool expanded = true)
    {
        _inner = inner;
        IsExpanded = expanded;
        _inner.CultureChanged +=
            OnInnerCultureChanged;
    }

    public bool IsExpanded { get; private set; }

    public CultureInfo CurrentUICulture =>
        _inner.CurrentUICulture;

    public IReadOnlyList<CultureInfo>
        SupportedCultures =>
            _inner.SupportedCultures;

    public event EventHandler? CultureChanged;

    public string Get(string key) =>
        IsExpanded
            ? Expand(_inner.Get(key))
            : _inner.Get(key);

    public string Format(
        string key,
        params object?[] arguments) =>
        string.Format(
            CultureInfo.CurrentCulture,
            Get(key),
            arguments);

    public string FormatCount(
        string key,
        long count,
        params object?[] arguments)
    {
        object?[] formatArguments =
            [count, .. arguments];
        return Format(
            count == 1
                ? $"{key}.One"
                : $"{key}.Other",
            formatArguments);
    }

    public IReadOnlyDictionary<string, string>
        Snapshot() =>
        _inner.Snapshot()
            .Keys
            .ToDictionary(
                key => key,
                Get,
                StringComparer.Ordinal);

    public void SetCulture(string cultureName) =>
        _inner.SetCulture(cultureName);

    public void SetExpanded(bool expanded)
    {
        if (IsExpanded == expanded)
            return;
        IsExpanded = expanded;
        CultureChanged?.Invoke(
            this,
            EventArgs.Empty);
    }

    private void OnInnerCultureChanged(
        object? sender,
        EventArgs e) =>
        CultureChanged?.Invoke(
            this,
            EventArgs.Empty);

    private static string Expand(string value)
    {
        var result = new StringBuilder(
            (int)Math.Ceiling(
                value.Length * 1.4) + 2);
        int visibleCharacters = 0;
        int expansionCharacters = 0;
        result.Append('\u27E6');
        for (int index = 0;
             index < value.Length;
             index++)
        {
            char current = value[index];
            if (current == '{')
            {
                int closing = value.IndexOf(
                    '}',
                    index + 1);
                if (closing >= 0)
                {
                    result.Append(
                        value,
                        index,
                        closing - index + 1);
                    index = closing;
                    continue;
                }
            }

            result.Append(current);
            visibleCharacters++;
            int targetExpansion =
                visibleCharacters * 2 / 5;
            while (expansionCharacters <
                   targetExpansion)
            {
                result.Append('\u02D0');
                expansionCharacters++;
            }
        }
        result.Append('\u27E7');
        return result.ToString();
    }
}
