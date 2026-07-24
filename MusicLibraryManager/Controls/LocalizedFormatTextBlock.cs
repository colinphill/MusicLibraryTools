using System.Globalization;
using global::Avalonia;
using global::Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Services;

namespace MusicLibraryManager.Controls;

/// <summary>
/// Formats a localized resource with one bound value and refreshes when the UI
/// culture changes. Use <see cref="UseCountVariant"/> with a numeric value for
/// resources named <c>Key.One</c> and <c>Key.Other</c>.
/// </summary>
public sealed class LocalizedFormatTextBlock : TextBlock
{
    public static readonly StyledProperty<string?>
        ResourceKeyProperty =
            AvaloniaProperty.Register<
                LocalizedFormatTextBlock,
                string?>(nameof(ResourceKey));

    public static readonly StyledProperty<object?>
        ValueProperty =
            AvaloniaProperty.Register<
                LocalizedFormatTextBlock,
                object?>(nameof(Value));

    public static readonly StyledProperty<bool>
        UseCountVariantProperty =
            AvaloniaProperty.Register<
                LocalizedFormatTextBlock,
                bool>(nameof(UseCountVariant));

    public string? ResourceKey
    {
        get => GetValue(ResourceKeyProperty);
        set => SetValue(ResourceKeyProperty, value);
    }

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool UseCountVariant
    {
        get => GetValue(UseCountVariantProperty);
        set => SetValue(
            UseCountVariantProperty,
            value);
    }

    public LocalizedFormatTextBlock()
    {
        PropertyChanged += OnOwnPropertyChanged;
        AttachedToVisualTree += (_, _) =>
        {
            AvaloniaLocalizationResourceBridge
                .ResourcesApplied +=
                OnLocalizationResourcesApplied;
            UpdateText();
        };
        DetachedFromVisualTree += (_, _) =>
            AvaloniaLocalizationResourceBridge
                .ResourcesApplied -=
                OnLocalizationResourcesApplied;
    }

    private void OnOwnPropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ResourceKeyProperty ||
            e.Property == ValueProperty ||
            e.Property == UseCountVariantProperty)
            UpdateText();
    }

    private void OnLocalizationResourcesApplied(
        object? sender,
        EventArgs e) =>
        UpdateText();

    private void UpdateText()
    {
        if (ResourceKey is not { Length: > 0 } key)
        {
            Text = "";
            return;
        }

        ILocalizationService? localization =
            App.Services?.GetService<
                ILocalizationService>();
        if (UseCountVariant &&
            TryConvertCount(
                Value,
                out long count))
        {
            Text = localization is null
                ? LocalizedText.FormatCount(
                    key,
                    count)
                : localization.FormatCount(
                    key,
                    count);
            return;
        }

        Text = localization is null
            ? LocalizedText.Format(key, Value)
            : localization.Format(key, Value);
    }

    private static bool TryConvertCount(
        object? value,
        out long count)
    {
        try
        {
            count = Convert.ToInt64(
                value,
                CultureInfo.InvariantCulture);
            return value is not null;
        }
        catch (Exception error)
            when (error is FormatException or
                  InvalidCastException or
                  OverflowException)
        {
            count = 0;
            return false;
        }
    }
}
