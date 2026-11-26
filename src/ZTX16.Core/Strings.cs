using ZTX16.Collections;
using ZTX16.Core;

namespace ZTX16;

/// <summary>
/// String helpers for cartridge code (the VM's strings offer length, indexing, comparison and concatenation;
/// everything else is built here from those). All operations are ordinal; case mapping covers ASCII only.
/// </summary>
public static class Strings
{
    /// <summary>The <paramref name="length"/> characters of <paramref name="text"/> starting at <paramref name="start"/>.</summary>
    public static string Sub(string text, int start, int length)
    {
        if (start < 0 || length < 0 || start + length > text.Length)
        {
            Intrinsics.Fail("Strings.Sub: range outside the string");
        }

        if (start == 0 && length == text.Length)
        {
            return text;
        }

        string[] parts = new string[length];
        for (int i = 0; i < length; i++)
        {
            parts[i] = text[start + i].ToString();
        }

        return string.Concat(parts);
    }

    /// <summary>The characters of <paramref name="text"/> from <paramref name="start"/> to the end.</summary>
    public static string Sub(string text, int start) => Sub(text, start, text.Length - start);

    /// <summary>A string of <paramref name="count"/> copies of <paramref name="c"/>.</summary>
    public static string Repeat(char c, int count)
    {
        if (count <= 0)
        {
            return "";
        }

        string[] parts = new string[count];
        string one = c.ToString();
        for (int i = 0; i < count; i++)
        {
            parts[i] = one;
        }

        return string.Concat(parts);
    }

    /// <summary>Index of the first <paramref name="c"/> at or after <paramref name="start"/>, or −1.</summary>
    public static int IndexOf(string text, char c, int start = 0)
    {
        for (int i = start < 0 ? 0 : start; i < text.Length; i++)
        {
            if (text[i] == c)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Index of the first occurrence of <paramref name="value"/> at or after <paramref name="start"/>, or −1.</summary>
    public static int IndexOf(string text, string value, int start = 0)
    {
        for (int i = start < 0 ? 0 : start; i + value.Length <= text.Length; i++)
        {
            if (At(text, i, value))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Index of the last <paramref name="c"/>, or −1.</summary>
    public static int LastIndexOf(string text, char c)
    {
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == c)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>True if <paramref name="text"/> contains <paramref name="value"/>.</summary>
    public static bool Contains(string text, string value) => IndexOf(text, value) >= 0;

    /// <summary>True if <paramref name="text"/> starts with <paramref name="value"/>.</summary>
    public static bool StartsWith(string text, string value) => value.Length <= text.Length && At(text, 0, value);

    /// <summary>True if <paramref name="text"/> ends with <paramref name="value"/>.</summary>
    public static bool EndsWith(string text, string value) => value.Length <= text.Length && At(text, text.Length - value.Length, value);

    /// <summary><paramref name="text"/> without leading and trailing spaces and tabs.</summary>
    public static string Trim(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && IsSpace(text[start]))
        {
            start++;
        }

        while (end > start && IsSpace(text[end - 1]))
        {
            end--;
        }

        return Sub(text, start, end - start);
    }

    /// <summary>ASCII lower case.</summary>
    public static string ToLower(string text) => MapCase(text, 'A', 'Z', 32);

    /// <summary>ASCII upper case.</summary>
    public static string ToUpper(string text) => MapCase(text, 'a', 'z', -32);

    /// <summary>True if the strings are equal ignoring ASCII case.</summary>
    public static bool EqualsIgnoreCase(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (Lower(a[i]) != Lower(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The parts of <paramref name="text"/> between <paramref name="separator"/>s (empty parts kept).</summary>
    public static List<string> Split(string text, char separator)
    {
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == separator)
            {
                parts.Add(Sub(text, start, i - start));
                start = i + 1;
            }
        }

        return parts;
    }

    /// <summary>Words separated by spaces; a word in double quotes may contain spaces.</summary>
    public static List<string> Words(string text)
    {
        var words = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && IsSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            if (text[i] == '"')
            {
                int close = IndexOf(text, '"', i + 1);
                int end = close < 0 ? text.Length : close;
                words.Add(Sub(text, i + 1, end - i - 1));
                i = end + 1;
            }
            else
            {
                int start = i;
                while (i < text.Length && !IsSpace(text[i]))
                {
                    i++;
                }

                words.Add(Sub(text, start, i - start));
            }
        }

        return words;
    }

    /// <summary>The parts joined with <paramref name="separator"/>.</summary>
    public static string Join(List<string> parts, string separator)
    {
        if (parts.Count == 0)
        {
            return "";
        }

        string[] all = new string[(parts.Count * 2) - 1];
        for (int i = 0; i < parts.Count; i++)
        {
            all[i * 2] = parts[i];
            if (i > 0)
            {
                all[(i * 2) - 1] = separator;
            }
        }

        return string.Concat(all);
    }

    /// <summary><paramref name="text"/> with every <paramref name="oldValue"/> replaced by <paramref name="newValue"/>.</summary>
    public static string Replace(string text, string oldValue, string newValue)
    {
        if (oldValue.Length == 0)
        {
            return text;
        }

        var parts = new List<string>();
        int start = 0;
        int found = IndexOf(text, oldValue);
        while (found >= 0)
        {
            parts.Add(Sub(text, start, found - start));
            start = found + oldValue.Length;
            found = IndexOf(text, oldValue, start);
        }

        parts.Add(Sub(text, start));
        return Join(parts, newValue);
    }

    /// <summary><paramref name="text"/> padded with spaces on the right to <paramref name="width"/>.</summary>
    public static string PadRight(string text, int width) => text.Length >= width ? text : text + Repeat(' ', width - text.Length);

    /// <summary><paramref name="text"/> padded with spaces on the left to <paramref name="width"/>.</summary>
    public static string PadLeft(string text, int width) => text.Length >= width ? text : Repeat(' ', width - text.Length) + text;

    /// <summary>
    /// Parses a decimal integer (optional leading '-'). Returns false for anything else or on overflow.
    /// </summary>
    public static bool TryParseInt(string text, out int value)
    {
        value = 0;
        int i = 0;
        bool negative = text.Length > 0 && text[0] == '-';
        if (negative)
        {
            i = 1;
        }

        if (i >= text.Length)
        {
            return false;
        }

        long result = 0;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c < '0' || c > '9')
            {
                return false;
            }

            result = (result * 10) + (c - '0');
            if (result > 2147483648L)
            {
                return false;
            }
        }

        if (negative)
        {
            result = -result;
        }

        if (result > int.MaxValue || result < int.MinValue)
        {
            return false;
        }

        value = (int)result;
        return true;
    }

    private static bool At(string text, int index, string value)
    {
        for (int j = 0; j < value.Length; j++)
        {
            if (text[index + j] != value[j])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\r';

    private static char Lower(char c) => c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;

    private static string MapCase(string text, char from, char to, int shift)
    {
        bool changes = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] >= from && text[i] <= to)
            {
                changes = true;
                break;
            }
        }

        if (!changes)
        {
            return text;
        }

        string[] parts = new string[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            parts[i] = (c >= from && c <= to ? (char)(c + shift) : c).ToString();
        }

        return string.Concat(parts);
    }
}
