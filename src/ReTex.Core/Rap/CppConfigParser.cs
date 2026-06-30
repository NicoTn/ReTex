using System.Globalization;
using System.Text;

namespace ReTex.Core.Rap;

/// <summary>
/// Parses a TEXT config.cpp into the same RapClass tree as RapReader (for mods that ship
/// unbinarized configs). Handles classes/inheritance, scalar + array properties, comments,
/// and string "" escaping. Preprocessor lines (#...) are skipped best-effort; unresolved
/// macro tokens are stored verbatim as strings.
/// </summary>
public static class CppConfigParser
{
    public static RapClass Parse(string text)
    {
        var s = new Scanner(StripComments(text));
        var root = new RapClass { Name = "" };
        ParseBody(s, root);
        return root;
    }

    private static void ParseBody(Scanner s, RapClass cls)
    {
        int guard = s.Length * 4 + 64;   // malformed input must never hang the parser
        while (true)
        {
            if (--guard < 0) return;
            s.SkipWs();
            if (s.End || s.Peek == '}') return;
            if (s.Peek == ';') { s.Next(); continue; }
            if (s.Peek == '#') { s.SkipLine(); continue; }

            string id = s.ReadIdent();
            if (id.Length == 0) { s.Next(); continue; }

            if (id == "class")
            {
                s.SkipWs();
                string name = s.ReadIdent();
                s.SkipWs();
                string parent = "";
                if (s.Peek == ':') { s.Next(); s.SkipWs(); parent = s.ReadIdent(); s.SkipWs(); }
                if (s.Peek == '{')
                {
                    s.Next();
                    var child = new RapClass { Name = name, Parent = parent };
                    ParseBody(s, child);
                    if (s.Peek == '}') s.Next();
                    s.SkipWs(); if (s.Peek == ';') s.Next();
                    cls.Classes.Add(child);
                }
                else { if (s.Peek == ';') s.Next(); cls.ExternalClasses.Add(name); }
            }
            else if (id is "delete" or "import")
            {
                s.SkipLine();
            }
            else
            {
                s.SkipWs();
                bool isArray = false;
                if (s.Peek == '[') { s.Next(); s.SkipWs(); if (s.Peek == ']') s.Next(); isArray = true; s.SkipWs(); }
                if (s.Peek == '+') s.Next();
                if (s.Peek == '=') s.Next();
                s.SkipWs();
                cls.Properties[id] = (isArray || s.Peek == '{') ? ParseArray(s) : ParseScalar(s);
                s.SkipWs(); if (s.Peek == ';') s.Next();
            }
        }
    }

    private static RapValue ParseArray(Scanner s)
    {
        var list = new List<RapValue>();
        int guard = s.Length * 4 + 64;
        s.SkipWs();
        if (s.Peek == '{') s.Next();
        while (true)
        {
            if (--guard < 0) break;
            s.SkipWs();
            if (s.End) break;
            if (s.Peek == '}') { s.Next(); break; }
            if (s.Peek is ',' or ';') { s.Next(); continue; }   // tolerate separators/stray ;
            int before = s.Pos;
            list.Add(s.Peek == '{' ? ParseArray(s) : ParseScalar(s));
            if (s.Pos == before) s.Next();   // guarantee forward progress
        }
        return new RapValue { Raw = list };
    }

    private static RapValue ParseScalar(Scanner s)
    {
        s.SkipWs();
        if (s.Peek == '"') return new RapValue { Raw = s.ReadString() };

        string tok = s.ReadUntilAny(",};").Trim();
        if (long.TryParse(tok, out var l)) return new RapValue { Raw = l };
        if (double.TryParse(tok, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return new RapValue { Raw = d };
        return new RapValue { Raw = tok };
    }

    /// <summary>Removes // and /* */ comments while respecting string literals.</summary>
    private static string StripComments(string t)
    {
        var sb = new StringBuilder(t.Length);
        bool inStr = false;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (inStr)
            {
                sb.Append(c);
                if (c == '"')
                {
                    if (i + 1 < t.Length && t[i + 1] == '"') { sb.Append('"'); i++; } // "" escape
                    else inStr = false;
                }
                continue;
            }
            if (c == '"') { inStr = true; sb.Append(c); continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; sb.Append('\n'); continue; }
            if (c == '/' && i + 1 < t.Length && t[i + 1] == '*') { i += 2; while (i + 1 < t.Length && !(t[i] == '*' && t[i + 1] == '/')) i++; i++; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private sealed class Scanner
    {
        private readonly string _s;
        private int _p;
        public Scanner(string s) => _s = s;

        public bool End => _p >= _s.Length;
        public char Peek => _p < _s.Length ? _s[_p] : '\0';
        public void Next() => _p++;
        public int Pos => _p;
        public int Length => _s.Length;

        public void SkipWs() { while (!End && char.IsWhiteSpace(_s[_p])) _p++; }
        public void SkipLine() { while (!End && _s[_p] != '\n') _p++; }

        public string ReadIdent()
        {
            int start = _p;
            while (!End && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '_')) _p++;
            return _s[start.._p];
        }

        public string ReadString()
        {
            var sb = new StringBuilder();
            _p++; // opening quote
            while (!End)
            {
                char c = _s[_p++];
                if (c == '"')
                {
                    if (_p < _s.Length && _s[_p] == '"') { sb.Append('"'); _p++; } // "" escape
                    else break;
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public string ReadUntilAny(string stops)
        {
            int start = _p;
            while (!End && !stops.Contains(_s[_p])) _p++;
            return _s[start.._p];
        }
    }
}
