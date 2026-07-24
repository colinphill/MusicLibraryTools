using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class AboutView : UserControl
{
    private static readonly Uri AvaloniaLicenseUri =
        new(
            "avares://MusicLibraryManager/Assets/Licenses/AvaloniaUI-12.1.0-MIT.txt");
    private static readonly Uri ImageSharpLicenseUri =
        new(
            "avares://MusicLibraryManager/Assets/Licenses/SixLabors.ImageSharp-3.1.12-LICENSE.txt");

    public string ProductVersion { get; }
    public string ProductCopyright { get; }
    public string AvaloniaVersion { get; } = "12.1.0";
    public string ImageSharpVersion { get; } = "3.1.12";
    public string AvaloniaLicenseText { get; }
    public string ImageSharpLicenseText { get; }

    public AboutView()
    {
        Assembly assembly = typeof(App).Assembly;
        ProductVersion = ResolveProductVersion(assembly);
        ProductCopyright =
            assembly.GetCustomAttribute<
                    AssemblyCopyrightAttribute>()
                ?.Copyright ??
            "";
        AvaloniaLicenseText =
            ReadLicense(AvaloniaLicenseUri);
        ImageSharpLicenseText =
            ReadLicense(ImageSharpLicenseUri);

        InitializeComponent();
        DataContext = this;
        SizeChanged += (_, _) =>
            ApplyResponsiveLayout();
    }

    private static string ResolveProductVersion(
        Assembly assembly)
    {
        string? informational =
            assembly.GetCustomAttribute<
                    AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0];
        return assembly.GetName().Version
                   ?.ToString(3) ??
               "0.0.0";
    }

    private static string ReadLicense(Uri uri)
    {
        using Stream stream = AssetLoader.Open(uri);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private async void OnCopyAvaloniaLicense(
        object? sender,
        RoutedEventArgs e) =>
        await App.GetService<IPlatformService>()
            .CopyTextAsync(AvaloniaLicenseText);

    private async void OnCopyImageSharpLicense(
        object? sender,
        RoutedEventArgs e) =>
        await App.GetService<IPlatformService>()
            .CopyTextAsync(ImageSharpLicenseText);

    private void ApplyResponsiveLayout()
    {
        bool narrow =
            Bounds.Width > 0 &&
            Bounds.Width < 960;
        PackageGrid.ColumnDefinitions.Clear();
        PackageGrid.RowDefinitions.Clear();
        if (narrow)
        {
            PackageGrid.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Star));
            PackageGrid.RowDefinitions.Add(
                new RowDefinition(GridLength.Auto));
            PackageGrid.RowDefinitions.Add(
                new RowDefinition(new GridLength(16)));
            PackageGrid.RowDefinitions.Add(
                new RowDefinition(GridLength.Auto));
            Grid.SetColumn(AvaloniaPackageCard, 0);
            Grid.SetRow(AvaloniaPackageCard, 0);
            Grid.SetColumn(ImageSharpPackageCard, 0);
            Grid.SetRow(ImageSharpPackageCard, 2);
        }
        else
        {
            PackageGrid.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Star));
            PackageGrid.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(16)));
            PackageGrid.ColumnDefinitions.Add(
                new ColumnDefinition(GridLength.Star));
            PackageGrid.RowDefinitions.Add(
                new RowDefinition(GridLength.Auto));
            Grid.SetColumn(AvaloniaPackageCard, 0);
            Grid.SetRow(AvaloniaPackageCard, 0);
            Grid.SetColumn(ImageSharpPackageCard, 2);
            Grid.SetRow(ImageSharpPackageCard, 0);
        }
    }
}
