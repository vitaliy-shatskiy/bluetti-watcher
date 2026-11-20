using System.Text;
using BluettiWatcher.Io;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// A config command line (<c>actions.eco</c>: an executable and its arguments, as written) split into the executable and
/// an argument list by the Windows rules a program's own parser applies (CommandLineToArgvW): passed back through
/// <c>ArgumentList</c>, each program receives the same arguments as from the line.
/// </summary>
internal static class CommandLineArgs
{
    public static (string Exe, IReadOnlyList<string> Args) Split(string line)
    {
        var (exe, rest) = ProcessRunner.SplitCommandLine(line);
        var args = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < rest.Length; i++)
        {
            char c = rest[i];
            if (c == '\\')
            {
                int n = 0;
                while (i < rest.Length && rest[i] == '\\') { n++; i++; }
                if (i < rest.Length && rest[i] == '"')
                {
                    // 2n backslashes + quote = n backslashes and a quote toggle; 2n+1 = n backslashes and a literal quote.
                    current.Append('\\', n / 2);
                    if (n % 2 == 1) current.Append('"');
                    else quoted = !quoted;
                }
                else
                {
                    current.Append('\\', n);
                    i--;
                }
                any = true;
            }
            else if (c == '"')
            {
                // Inside quotes a doubled quote is a literal quote.
                if (quoted && i + 1 < rest.Length && rest[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
                any = true;
            }
            else if (!quoted && c is ' ' or '\t')
            {
                if (any) args.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any) args.Add(current.ToString());
        return (exe, args);
    }
}
