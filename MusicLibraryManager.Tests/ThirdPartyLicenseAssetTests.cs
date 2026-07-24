using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class ThirdPartyLicenseAssetTests
{
    private const string AvaloniaAssetName =
        "AvaloniaUI-12.1.0-MIT.txt";
    private const string SkiaSharpAssetName =
        "SkiaSharp-4.150.1-MIT.txt";
    private const string SkiaSharpNoticesAssetName =
        "SkiaSharp-4.150.1-THIRD-PARTY-NOTICES.txt";

    [Fact]
    public void Avalonia_license_matches_the_pinned_upstream_bytes()
    {
        byte[] license = File.ReadAllBytes(
            LicensePath(AvaloniaAssetName));

        Assert.Equal(
            "213814D306090074D234D760239FF0F67EB9B8D20EEFB4D5631BB39DBE0B769B",
            Convert.ToHexString(SHA256.HashData(license)));
    }

    [Fact]
    public void SkiaSharp_license_matches_the_pinned_package_bytes()
    {
        byte[] license = File.ReadAllBytes(
            LicensePath(SkiaSharpAssetName));

        Assert.Equal(
            "89101E35A8C66FD4D6DFFC1763259161D35CB564C169714EC227A768C89F2938",
            Convert.ToHexString(SHA256.HashData(license)));
    }

    [Fact]
    public void SkiaSharp_third_party_notices_match_the_pinned_native_package_bytes()
    {
        byte[] notices = File.ReadAllBytes(
            LicensePath(SkiaSharpNoticesAssetName));

        Assert.Equal(
            "21504C46C4C58AA64C1055BD2DCBC5F9A136B4B8C412ED3CC6740E22C5B127F5",
            Convert.ToHexString(SHA256.HashData(notices)));
    }

    [Fact]
    public void Avalonia_license_preserves_the_required_MIT_terms()
    {
        string license = File.ReadAllText(
            LicensePath(AvaloniaAssetName));
        string normalizedLicense =
            Regex.Replace(
                license,
                @"\s+",
                " ");

        Assert.Contains(
            "Copyright (c) AvaloniaUI OÜ",
            normalizedLicense,
            StringComparison.Ordinal);
        Assert.Contains(
            "Permission is hereby granted, free of charge, to any person obtaining a copy",
            normalizedLicense,
            StringComparison.Ordinal);
        Assert.Contains(
            "The above copyright notice and this permission notice shall be included in all",
            normalizedLicense,
            StringComparison.Ordinal);
        Assert.Contains(
            "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND",
            normalizedLicense,
            StringComparison.Ordinal);
        Assert.Contains(
            "IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM",
            normalizedLicense,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SkiaSharp_license_preserves_the_required_MIT_terms()
    {
        string license = File.ReadAllText(
            LicensePath(SkiaSharpAssetName));
        string normalizedLicense =
            Regex.Replace(
                license,
                @"\s+",
                " ");

        Assert.Contains(
            "Copyright (c) 2015-2016 Xamarin, Inc.",
            normalizedLicense,
            StringComparison.Ordinal);
        Assert.Contains(
            "Copyright (c) 2017-2018 Microsoft Corporation.",
            normalizedLicense,
            StringComparison.Ordinal);
        Assert.Contains(
            "Permission is hereby granted, free of charge, to any person obtaining a copy",
            normalizedLicense,
            StringComparison.Ordinal);
        Assert.Contains(
            "The above copyright notice and this permission notice shall be included in all",
            normalizedLicense,
            StringComparison.Ordinal);
        Assert.Contains(
            "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND",
            normalizedLicense,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SkiaSharp_notices_preserve_the_required_third_party_attribution()
    {
        string notices = File.ReadAllText(
            LicensePath(SkiaSharpNoticesAssetName));

        Assert.StartsWith(
            "THIRD-PARTY SOFTWARE NOTICES AND INFORMATION",
            notices,
            StringComparison.Ordinal);
        Assert.Contains(
            "Do not translate or localize",
            notices,
            StringComparison.Ordinal);
        Assert.Contains(
            "SkiaSharp and HarfBuzzSharp incorporate third party material",
            notices,
            StringComparison.Ordinal);
        Assert.Contains(
            "# skia",
            notices,
            StringComparison.Ordinal);
    }

    [Fact]
    public void License_asset_versions_match_central_package_versions()
    {
        XDocument packages = XDocument.Load(
            Path.Combine(
                FindRepositoryRoot(),
                "Directory.Packages.props"));
        Dictionary<string, string> versions = packages
            .Descendants("PackageVersion")
            .ToDictionary(
                element =>
                    (string?)element.Attribute("Include") ??
                    string.Empty,
                element =>
                    (string?)element.Attribute("Version") ??
                    string.Empty,
                StringComparer.OrdinalIgnoreCase);

        Assert.Equal("12.1.0", versions["Avalonia"]);
        Assert.Equal("4.150.1", versions["SkiaSharp"]);
        Assert.Equal(
            "4.150.1",
            versions["SkiaSharp.NativeAssets.Linux"]);
        Assert.False(
            versions.ContainsKey("SixLabors.ImageSharp"));
        Assert.Equal(
            AvaloniaAssetName,
            $"AvaloniaUI-{versions["Avalonia"]}-MIT.txt");
        Assert.Equal(
            SkiaSharpAssetName,
            $"SkiaSharp-{versions["SkiaSharp"]}-MIT.txt");
        Assert.Equal(
            SkiaSharpNoticesAssetName,
            $"SkiaSharp-{versions["SkiaSharp"]}-THIRD-PARTY-NOTICES.txt");
    }

    [Fact]
    public void Manager_embeds_and_publishes_only_the_current_license_assets()
    {
        XDocument project = XDocument.Load(
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager",
                "MusicLibraryManager.csproj"));
        var embedded = new HashSet<string>(
            project
                .Descendants("AvaloniaResource")
                .Select(element =>
                    (string?)element.Attribute("Include") ??
                    string.Empty),
            StringComparer.OrdinalIgnoreCase);
        var published = new HashSet<string>(
            project
                .Descendants("None")
                .Select(element =>
                    (string?)element.Attribute("TargetPath") ??
                    string.Empty)
                .Where(path =>
                    path.StartsWith(
                        "ThirdPartyLicenses",
                        StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        Assert.Contains(
            $@"Assets\Licenses\{SkiaSharpAssetName}",
            embedded);
        Assert.Contains(
            $@"Assets\Licenses\{SkiaSharpNoticesAssetName}",
            embedded);
        Assert.Contains(
            $@"ThirdPartyLicenses\{SkiaSharpAssetName}",
            published);
        Assert.Contains(
            $@"ThirdPartyLicenses\{SkiaSharpNoticesAssetName}",
            published);
        Assert.DoesNotContain(
            embedded,
            path => path.Contains(
                "ImageSharp",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            published,
            path => path.Contains(
                "ImageSharp",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Packaging_requires_SkiaSharp_legal_assets_and_rejects_ImageSharp()
    {
        string packageScript = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "MusicLibraryManager",
                "Package.ps1"));

        Assert.Contains(
            SkiaSharpAssetName,
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            SkiaSharpNoticesAssetName,
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "SixLabors.ImageSharp.dll",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "SixLabors.ImageSharp-3.1.12-LICENSE.txt",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-SkiaRuntime $publishRoot $rid",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "libSkiaSharp.dll",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "libSkiaSharp.so",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "libSkiaSharp.dylib",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-FileHash",
            packageScript,
            StringComparison.Ordinal);
    }

    private static string LicensePath(string assetName) =>
        Path.Combine(
            FindRepositoryRoot(),
            "MusicLibraryManager",
            "Assets",
            "Licenses",
            assetName);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current =
            new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        current.FullName,
                        "Directory.Packages.props")) &&
                Directory.Exists(
                    Path.Combine(
                        current.FullName,
                        "MusicLibraryManager")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the MusicLibraryTools repository root.");
    }
}
