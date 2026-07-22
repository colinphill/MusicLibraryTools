#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MusicLibraryTools;

public static class FfmpegOptionTokenizer
{
    public static IReadOnlyList<string> Parse(string? options)
    {
        if (string.IsNullOrWhiteSpace(options))
            return [];

        var arguments = new List<string>();
        var current = new StringBuilder();
        bool inSingleQuotes = false;
        bool inDoubleQuotes = false;
        bool tokenStarted = false;
        for (int index = 0; index < options.Length; index++)
        {
            char value = options[index];
            if (value == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
                tokenStarted = true;
                continue;
            }
            if (value == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
                tokenStarted = true;
                continue;
            }
            if (value == '\\' && !inSingleQuotes && index + 1 < options.Length)
            {
                char next = options[index + 1];
                if (next == '\\' || next == '"' || (!inDoubleQuotes &&
                    (next == '\'' || char.IsWhiteSpace(next))))
                {
                    current.Append(next);
                    tokenStarted = true;
                    index++;
                    continue;
                }
            }
            if (char.IsWhiteSpace(value) && !inSingleQuotes && !inDoubleQuotes)
            {
                if (tokenStarted)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }
                continue;
            }
            current.Append(value);
            tokenStarted = true;
        }

        if (inSingleQuotes || inDoubleQuotes)
            throw new InvalidDataException(
                "Extra FFmpeg options contain an unmatched quotation mark.");
        if (tokenStarted)
            arguments.Add(current.ToString());
        return arguments;
    }
}
