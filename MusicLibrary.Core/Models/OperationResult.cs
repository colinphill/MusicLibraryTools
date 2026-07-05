namespace MusicLibrary.Core.Models;

/// <summary>
/// A success/failure wrapper so services can report load/parse failures as data instead of
/// throwing across the ViewModel boundary (the parsers throw a variety of exceptions).
/// </summary>
public readonly record struct OperationResult<T>(bool Success, T? Value, string? Error)
{
    public static OperationResult<T> Ok(T value) => new(true, value, null);
    public static OperationResult<T> Fail(string error) => new(false, default, error);
}
