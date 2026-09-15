namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static string AsciiToLower(string input)
    {
        var characters = input.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (characters[index] is >= 'A' and <= 'Z')
                characters[index] = (char)(characters[index] + 32);
        }
        return new string(characters);
    }
}
