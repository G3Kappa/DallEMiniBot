using System.Text.RegularExpressions;

public readonly struct SanitizedFileName
{
    // https://msdn.microsoft.com/en-us/library/aa365247.aspx#naming_conventions
    // http://stackoverflow.com/questions/146134/how-to-remove-illegal-characters-from-path-and-filenames
    private static readonly Regex removeInvalidChars = new($"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public readonly string Value;

    public SanitizedFileName(string fileName, string replacement = "_") => Value = removeInvalidChars.Replace(fileName, replacement);

}
