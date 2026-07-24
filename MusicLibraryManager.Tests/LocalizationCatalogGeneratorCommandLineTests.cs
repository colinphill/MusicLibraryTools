using System.Globalization;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class LocalizationCatalogGeneratorCommandLineTests
{
    [Fact]
    public void Generator_failure_is_reported_as_a_nonzero_exit_without_escaping()
    {
        using var error = new StringWriter(
            CultureInfo.InvariantCulture);
        var expected = new InvalidDataException(
            "Translation coverage is incomplete.");

        int exitCode = CatalogGeneratorCommandLine.Run(
            ["--check"],
            _ => throw expected,
            error);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            $"Translation coverage is incomplete.{Environment.NewLine}",
            error.ToString());
    }

    [Fact]
    public void Successful_generator_exit_is_preserved_without_error_output()
    {
        using var error = new StringWriter(
            CultureInfo.InvariantCulture);

        int exitCode = CatalogGeneratorCommandLine.Run(
            ["--check"],
            args => args.Length + 6,
            error);

        Assert.Equal(7, exitCode);
        Assert.Equal(string.Empty, error.ToString());
    }
}
