// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/model-config.ts (the JSON.parse SyntaxError
// messages Pi interpolates there and in settings, auth, trust, theme, MCP, RPC and SSE parsing, as Node >=22.19 / V8 12.4 words them).
namespace PiSharp.Contracts.Compatibility;

/// <summary>
/// The message of the SyntaxError JSON.parse throws for a text, as Node 22's V8 (12.4) words it, or null when the text is valid
/// JSON. Pi requires Node &gt;= 22.19.0, so these are the texts Pi's "Failed to parse ...: &lt;message&gt;" errors carry.
/// </summary>
public static class JsJsonSyntax
{
    private const int MaxContextCharacters = 10;
    private const int MaxShortSourceLength = MaxContextCharacters * 2;

    private enum Token { Eos, String, Number, True, False, Null, Whitespace, LBrace, RBrace, LBrack, RBrack, Colon, Comma, Illegal }

    /// <summary>JSON.parse's SyntaxError message for <paramref name="text"/>, or null when JSON.parse accepts it.</summary>
    public static string? Error(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try { new Parser(text).ParseJson(); return null; }
        catch (Failure failure) { return failure.Message; }
    }

    /// <summary>JSON.parse's message for a text another parser rejected; <paramref name="fallback"/> where JSON.parse would accept it.</summary>
    public static string Describe(string text, string fallback) => Error(text) ?? fallback;

    private sealed class Failure(string message) : Exception(message);

    private enum Container { Object, Array }

    private sealed class Parser(string source)
    {
        private int cursor;

        private int Peek() => cursor >= source.Length ? -1 : source[cursor];
        private Token PeekToken() => cursor >= source.Length ? Token.Eos : OneChar(source[cursor]);

        private static Token OneChar(int c) => c switch
        {
            '"' => Token.String,
            '-' or (>= '0' and <= '9') => Token.Number,
            't' => Token.True, 'f' => Token.False, 'n' => Token.Null,
            ' ' or '\t' or '\n' or '\r' => Token.Whitespace,
            '{' => Token.LBrace, '}' => Token.RBrace, '[' => Token.LBrack, ']' => Token.RBrack, ':' => Token.Colon, ',' => Token.Comma,
            _ => Token.Illegal
        };

        private void SkipWhitespace() { while (cursor < source.Length && OneChar(source[cursor]) == Token.Whitespace) cursor++; }

        private bool Check(Token token)
        {
            SkipWhitespace();
            if (PeekToken() != token) return false;
            cursor++; return true;
        }

        private void ExpectNext(Token token, string? message = null)
        {
            SkipWhitespace();
            if (PeekToken() == token) cursor++;
            else throw Report(PeekToken(), message);
        }

        internal void ParseJson()
        {
            ParseValue();
            if (!Check(Token.Eos)) throw Report(PeekToken(), "Unexpected non-whitespace character after JSON");
        }

        private void ParseValue()
        {
            var stack = new Stack<Container>();
            while (true)
            {
                // Produce a value.
                while (true)
                {
                    SkipWhitespace();
                    var token = PeekToken();
                    switch (token)
                    {
                        case Token.String: cursor++; ScanString(); break;
                        case Token.Number: ScanNumber(); break;
                        case Token.LBrace:
                            cursor++;
                            if (Check(Token.RBrace)) break;
                            stack.Push(Container.Object);
                            ExpectNext(Token.String, "Expected property name or '}' in JSON");
                            ScanString();
                            ExpectNext(Token.Colon, "Expected ':' after property name in JSON");
                            continue;
                        case Token.LBrack:
                            cursor++;
                            if (Check(Token.RBrack)) break;
                            stack.Push(Container.Array);
                            continue;
                        case Token.True: ScanLiteral("true"); break;
                        case Token.False: ScanLiteral("false"); break;
                        case Token.Null: ScanLiteral("null"); break;
                        default: throw Report(token);
                    }
                    break;
                }
                // Consume the continuation.
                while (true)
                {
                    if (stack.Count == 0) return;
                    if (stack.Peek() == Container.Object)
                    {
                        if (Check(Token.Comma))
                        {
                            ExpectNext(Token.String, "Expected double-quoted property name in JSON");
                            ScanString();
                            ExpectNext(Token.Colon, "Expected ':' after property name in JSON");
                            break;
                        }
                        SkipWhitespace();
                        if (PeekToken() != Token.RBrace) throw Report(PeekToken(), "Expected ',' or '}' after property value in JSON");
                        cursor++; stack.Pop(); continue;
                    }
                    if (Check(Token.Comma)) break;
                    SkipWhitespace();
                    if (PeekToken() != Token.RBrack) throw Report(PeekToken(), "Expected ',' or ']' after array element in JSON");
                    cursor++; stack.Pop();
                }
            }
        }

        private void ScanLiteral(string literal)
        {
            var remaining = source.Length - cursor;
            if (remaining >= literal.Length && string.CompareOrdinal(source, cursor + 1, literal, 1, literal.Length - 1) == 0)
            {
                cursor += literal.Length; return;
            }
            cursor++;
            for (var index = 0; index < Math.Min(literal.Length - 1, remaining - 1); index++)
            {
                if (literal[1 + index] != source[cursor]) throw ReportCharacter(source[cursor]);
                cursor++;
            }
            throw Report(Token.Eos);
        }

        private void ScanString()
        {
            // The cursor is past the opening quote.
            while (true)
            {
                var c = Peek();
                if (c == '"') { cursor++; return; }
                if (c == '\\')
                {
                    cursor++;
                    var escape = Peek();
                    if (escape < 0 || escape > 0xFF) throw ReportCharacter(escape);
                    switch (escape)
                    {
                        case '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't': break;
                        case 'u':
                            for (var digit = 0; digit < 4; digit++)
                            {
                                cursor++;
                                if (!Uri.IsHexDigit((char)Math.Max(Peek(), 0)) || Peek() < 0) throw Report(Token.Illegal, "Bad Unicode escape in JSON");
                            }
                            break;
                        default: throw Report(Token.Illegal, "Bad escaped character in JSON");
                    }
                    cursor++; continue;
                }
                if (c < 0) throw Report(Token.Eos, "Unterminated string in JSON");
                if (c < 0x20) throw Report(Token.Illegal, "Bad control character in string literal in JSON");
                cursor++;
            }
        }

        private static bool IsDigit(int c) => c is >= '0' and <= '9';
        private static bool IsNumberPart(int c) => IsDigit(c) || c is '.' or 'e' or 'E' or '+' or '-';

        private void ScanNumber()
        {
            var negative = false;
            var c = Peek();
            if (c == '-') { negative = true; cursor++; c = Peek(); }
            if (c == '0')
            {
                cursor++; c = Peek();
                if (c is >= 0 and <= 0xFF && IsNumberPart(c))
                {
                    if (IsDigit(c)) throw Report(Token.Number);
                }
                else if (!negative) return;
            }
            else
            {
                var start = cursor;
                var stop = Math.Min(source.Length, cursor + 9);
                while (cursor < stop && IsDigit(source[cursor])) cursor++;
                if (start == cursor) throw Report(Token.Illegal, "No number after minus sign in JSON");
                c = Peek();
                if (c < 0 || c > 0xFF || !IsNumberPart(c)) return;
                while (IsDigit(Peek())) cursor++;
            }
            if (Peek() == '.')
            {
                cursor++;
                if (!IsDigit(Peek())) throw Report(Token.Illegal, "Unterminated fractional number in JSON");
                while (IsDigit(Peek())) cursor++;
            }
            if (Peek() is 'e' or 'E')
            {
                cursor++;
                if (Peek() is '-' or '+') cursor++;
                if (!IsDigit(Peek())) throw Report(Token.Illegal, "Exponent part is missing a number in JSON");
                while (IsDigit(Peek())) cursor++;
            }
        }

        private Failure ReportCharacter(int c) => Report(c < 0 ? Token.Eos : c <= 0x7F ? OneChar(c) : Token.Illegal);

        private Failure Report(Token token, string? message = null)
        {
            var position = cursor;
            if (message is not null) return new(message + " at position " + position + Location(position));
            switch (token)
            {
                case Token.Eos: return new("Unexpected end of JSON input");
                case Token.Number: return new("Unexpected number in JSON at position " + position + Location(position));
                case Token.String: return new("Unexpected string in JSON at position " + position + Location(position));
            }
            if (source is "[object Object]" or "undefined" or "Infinity" or "NaN") return new("\"" + source + "\" is not valid JSON");
            var character = source[position].ToString();
            if (source.Length <= MaxShortSourceLength)
                return new("Unexpected token '" + character + "', \"" + source + "\" is not valid JSON");
            if (position < MaxContextCharacters)
                return new("Unexpected token '" + character + "', \"" + source[..(position + MaxContextCharacters)] + "\"... is not valid JSON");
            if (position < source.Length - MaxContextCharacters)
                return new("Unexpected token '" + character + "', ...\"" + source.Substring(position - MaxContextCharacters, 2 * MaxContextCharacters) +
                    "\"... is not valid JSON");
            return new("Unexpected token '" + character + "', ...\"" + source[(position - MaxContextCharacters)..] + "\" is not valid JSON");
        }

        private string Location(int position)
        {
            // JSON allows only \r and \n as line terminators; \r\n counts once.
            var line = 1; var lastLineBreak = 0; var index = 0;
            for (; index < position; index++)
            {
                if (source[index] == '\r' && index < position - 1 && source[index + 1] == '\n') index++;
                if (source[index] is '\r' or '\n') { line++; lastLineBreak = index + 1; }
            }
            return " (line " + line + " column " + (1 + index - lastLineBreak) + ")";
        }
    }
}
