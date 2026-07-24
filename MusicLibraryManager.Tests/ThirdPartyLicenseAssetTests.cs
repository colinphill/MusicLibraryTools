using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class ThirdPartyLicenseAssetTests
{
    private const string AvaloniaAssetName =
        "AvaloniaUI-12.1.0-MIT.txt";
    private const string ImageSharpAssetName =
        "SixLabors.ImageSharp-3.1.12-LICENSE.txt";

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
    public void ImageSharp_license_matches_the_pinned_package_bytes()
    {
        byte[] license = File.ReadAllBytes(
            LicensePath(ImageSharpAssetName));

        Assert.Equal(
            "FDB7F24DB8A6838EBA1477242D2457BF6DB2F3682B7CB3A16824E9F2F07936C2",
            Convert.ToHexString(SHA256.HashData(license)));
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
        Assert.Equal("3.1.12", versions["SixLabors.ImageSharp"]);
        Assert.Equal(
            AvaloniaAssetName,
            $"AvaloniaUI-{versions["Avalonia"]}-MIT.txt");
        Assert.Equal(
            ImageSharpAssetName,
            $"SixLabors.ImageSharp-{versions["SixLabors.ImageSharp"]}-LICENSE.txt");
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
