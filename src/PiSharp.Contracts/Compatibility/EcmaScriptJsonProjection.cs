using System.Globalization;
using System.Numerics;
using System.Text;

namespace PiSharp.Contracts.Compatibility;

public sealed record EcmaScriptJsonProjectionOptions(int MaximumInputCharacters = 1_048_576,
    int MaximumInputBytes = 4_194_304, int MaximumOutputCharacters = 1_048_576,
    int MaximumOutputBytes = 4_194_304, int MaximumDepth = 32, int MaximumNodes = 65_536,
    int MaximumPropertiesPerObject = 4_096, int MaximumNumbers = 4_096,
    int MaximumNumberCharacters = 4_096, int MaximumTotalNumberCharacters = 65_536,
    int MaximumStringCharacters = 1_048_576);

public enum EcmaScriptJsonProjectionFailure { InvalidJson, ResourceLimit }
public sealed class EcmaScriptJsonProjectionException : Exception
{
    public EcmaScriptJsonProjectionFailure Failure { get; }
    internal EcmaScriptJsonProjectionException(EcmaScriptJsonProjectionFailure failure)
        : base(failure == EcmaScriptJsonProjectionFailure.ResourceLimit
            ? "ECMAScript JSON projection exceeds configured limits." : "Invalid ECMAScript JSON projection input.") => Failure = failure;
}

/// <summary>
/// Pure opt-in JSON.parse(text) then JSON.stringify(value) projection, without reviver/replacer/space.
/// Owned native input remains unchanged. Numbers use exact integer rounding and shortest-decimal search.
/// </summary>
public static class EcmaScriptJsonProjection
{
    public static string Project(JsonData value, EcmaScriptJsonProjectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Project(value.ToString(), options, cancellationToken);
    }
    public static string Project(string json, EcmaScriptJsonProjectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        var limits = options ?? new();
        if (limits.MaximumInputCharacters <= 0 || limits.MaximumInputBytes <= 0 || limits.MaximumOutputCharacters <= 0 ||
            limits.MaximumOutputBytes <= 0 || limits.MaximumDepth is < 1 or > 64 || limits.MaximumNodes <= 0 ||
            limits.MaximumPropertiesPerObject <= 0 || limits.MaximumNumbers <= 0 || limits.MaximumNumberCharacters <= 0 ||
            limits.MaximumNumberCharacters > 16_384 || limits.MaximumTotalNumberCharacters <= 0 || limits.MaximumStringCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        if (json.Length > limits.MaximumInputCharacters || Encoding.UTF8.GetByteCount(json) > limits.MaximumInputBytes) throw Limit();
        var root = new Parser(json, limits, cancellationToken).Parse();
        var output = new Output(limits, cancellationToken);
        output.Write(root);
        cancellationToken.ThrowIfCancellationRequested();
        return output.ToString();
    }

    private enum Kind { Object, Array, String, Number, True, False, Null }
    private sealed class Node(Kind kind)
    {
        internal readonly Kind Kind = kind;
        internal string Text = "";
        internal ulong Number;
        internal List<Member> Members = [];
        internal List<Node> Items = [];
    }
    private sealed class Member(string name, Node value)
    { internal readonly string Name = name; internal Node Value = value; }

    // Decode UTF-16 ourselves: framework JSON string access rejects escaped lone surrogates,
    // which JSON.parse retains and well-formed JSON.stringify escapes. No executable JS values enter this tree.
    private sealed class Parser(string input, EcmaScriptJsonProjectionOptions limits, CancellationToken token)
    {
        private int position, nodes, numbers;
        private long numberCharacters;
        internal Node Parse()
        {
            White(); var result = Value(0); White();
            if (position != input.Length) throw Invalid();
            return result;
        }
        private Node Value(int parentDepth)
        {
            token.ThrowIfCancellationRequested();
            if (++nodes > limits.MaximumNodes) throw Limit();
            if (position >= input.Length) throw Invalid();
            var character = input[position];
            if (character is '{' or '[')
            {
                if (parentDepth >= limits.MaximumDepth) throw Limit();
                position++;
                var result = new Node(character == '{' ? Kind.Object : Kind.Array);
                White(); var closing = character == '{' ? '}' : ']';
                if (Take(closing)) return result;
                var names = new Dictionary<string, Member>(StringComparer.Ordinal); var properties = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (character == '{')
                    {
                        if (++properties > limits.MaximumPropertiesPerObject) throw Limit();
                        if (position >= input.Length || input[position] != '"') throw Invalid();
                        var name = String(); White(); if (!Take(':')) throw Invalid(); White();
                        var value = Value(parentDepth + 1);
                        // JSON.parse updates the value without recreating the property's insertion position.
                        if (names.TryGetValue(name, out var old)) old.Value = value;
                        else { var member = new Member(name, value); names.Add(name, member); result.Members.Add(member); }
                    }
                    else result.Items.Add(Value(parentDepth + 1));
                    White(); if (Take(closing)) return result;
                    if (!Take(',')) throw Invalid(); White();
                }
            }
            if (character == '"') return new(Kind.String) { Text = String() };
            if (character is '-' or >= '0' and <= '9') return Number();
            foreach (var (literal, kind) in new[] { ("true", Kind.True), ("false", Kind.False), ("null", Kind.Null) })
                if (input.AsSpan(position).StartsWith(literal, StringComparison.Ordinal))
                { position += literal.Length; return new(kind); }
            throw Invalid();
        }
        private Node Number()
        {
            var start = position;
            if (++numbers > limits.MaximumNumbers) throw Limit();
            Take('-');
            if (!Take('0'))
            {
                if (position >= input.Length || input[position] is < '1' or > '9') throw Invalid();
                while (Digit()) AdvanceNumber(start);
            }
            if (Take('.')) { if (!Digit()) throw Invalid(); while (Digit()) AdvanceNumber(start); }
            if (Take('e') || Take('E'))
            {
                if (!Take('+')) Take('-');
                if (!Digit()) throw Invalid(); while (Digit()) AdvanceNumber(start);
            }
            var length = position - start;
            numberCharacters += length;
            if (length > limits.MaximumNumberCharacters || numberCharacters > limits.MaximumTotalNumberCharacters) throw Limit();
            return new(Kind.Number) { Number = DecimalNumber(input.AsSpan(start, length), token) };
        }
        private bool Digit() => position < input.Length && input[position] is >= '0' and <= '9';
        private void AdvanceNumber(int start)
        {
            token.ThrowIfCancellationRequested();
            if (position - start >= limits.MaximumNumberCharacters) throw Limit();
            position++;
        }
        private string String()
        {
            position++; var value = new StringBuilder();
            while (position < input.Length)
            {
                token.ThrowIfCancellationRequested();
                var character = input[position++];
                if (character == '"') return value.ToString();
                if (character < 0x20) throw Invalid();
                if (character == '\\')
                {
                    if (position >= input.Length) throw Invalid();
                    character = input[position++];
                    character = character switch
                    {
                        '"' or '\\' or '/' => character, 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t',
                        'u' => Unicode(), _ => throw Invalid()
                    };
                }
                if (value.Length >= limits.MaximumStringCharacters) throw Limit();
                value.Append(character);
            }
            throw Invalid();
        }
        private char Unicode()
        {
            if (input.Length - position < 4) throw Invalid();
            var value = 0;
            for (var index = 0; index < 4; index++)
            {
                var character = input[position++];
                var digit = character is >= '0' and <= '9' ? character - '0' :
                    character is >= 'a' and <= 'f' ? character - 'a' + 10 :
                    character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
                if (digit < 0) throw Invalid(); value = value * 16 + digit;
            }
            return (char)value;
        }
        private void White()
        {
            while (position < input.Length && input[position] is ' ' or '\t' or '\r' or '\n')
            { token.ThrowIfCancellationRequested(); position++; }
        }
        private bool Take(char character)
        { if (position >= input.Length || input[position] != character) return false; position++; return true; }
    }

    private sealed class Output(EcmaScriptJsonProjectionOptions limits, CancellationToken token)
    {
        private readonly StringBuilder text = new();
        private long bytes;
        internal void Write(Node value)
        {
            token.ThrowIfCancellationRequested();
            switch (value.Kind)
            {
                case Kind.Object:
                    Append("{"); var first = true;
                    foreach (var member in value.Members.Select((member, index) => (member, index, arrayIndex: ArrayIndex(member.Name)))
                        .OrderBy(item => item.arrayIndex is null ? 1 : 0).ThenBy(item => item.arrayIndex ?? 0).ThenBy(item => item.index))
                    {
                        if (!first) Append(","); first = false;
                        Quote(member.member.Name); Append(":"); Write(member.member.Value);
                    }
                    Append("}"); break;
                case Kind.Array:
                    Append("[");
                    for (var index = 0; index < value.Items.Count; index++) { if (index != 0) Append(","); Write(value.Items[index]); }
                    Append("]"); break;
                case Kind.String: Quote(value.Text); break;
                case Kind.Number: Append(NumberText(value.Number, token)); break;
                case Kind.True: Append("true"); break;
                case Kind.False: Append("false"); break;
                case Kind.Null: Append("null"); break;
            }
        }
        private void Quote(string value)
        {
            Append("\"");
            for (var index = 0; index < value.Length; index++)
            {
                token.ThrowIfCancellationRequested(); var character = value[index];
                var escape = character switch { '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\t' => "\\t", '\n' => "\\n", '\f' => "\\f", '\r' => "\\r", _ => null };
                if (escape is not null) Append(escape);
                else if (character < 0x20 || char.IsSurrogate(character) &&
                    !(char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])))
                    Append("\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture));
                else if (char.IsHighSurrogate(character))
                { Charge(2, 4); text.Append(character); text.Append(value[++index]); }
                else { Charge(1, character < 0x80 ? 1 : character < 0x800 ? 2 : 3); text.Append(character); }
            }
            Append("\"");
        }
        private void Append(string value)
        { token.ThrowIfCancellationRequested(); Charge(value.Length, Encoding.UTF8.GetByteCount(value)); text.Append(value); }
        private void Charge(int characters, int addedBytes)
        {
            if ((long)text.Length + characters > limits.MaximumOutputCharacters || bytes + addedBytes > limits.MaximumOutputBytes) throw Limit();
            bytes += addedBytes;
        }
        public override string ToString() => text.ToString();
    }
    private static uint? ArrayIndex(string name)
    {
        if (name.Length is 0 or > 10 || name.Length > 1 && name[0] == '0') return null;
        ulong value = 0;
        foreach (var character in name) { if (character is < '0' or > '9') return null; value = value * 10 + (uint)(character - '0'); }
        return value < uint.MaxValue ? (uint)value : null;
    }

    private const ulong MaximumFinite = 0x7fefffffffffffff;
    private const ulong Infinity = 0x7ff0000000000000;
    private const ulong Sign = 0x8000000000000000;
    private static readonly BigInteger IntervalDenominator = BigInteger.One << 1075;

    // Exact decimal rational -> IEEE binary64, including halfway, subnormal and overflow boundaries.
    private static ulong DecimalNumber(ReadOnlySpan<char> raw, CancellationToken token)
    {
        var negative = raw[0] == '-'; var index = negative ? 1 : 0; var digits = new StringBuilder(raw.Length);
        var fractional = 0; var afterPoint = false;
        while (index < raw.Length && raw[index] is not ('e' or 'E'))
        {
            token.ThrowIfCancellationRequested();
            if (raw[index] == '.') afterPoint = true;
            else { digits.Append(raw[index]); if (afterPoint) fractional++; }
            index++;
        }
        var exponent = 0; var exponentNegative = false;
        if (index < raw.Length)
        {
            index++;
            if (raw[index] is '+' or '-') { exponentNegative = raw[index] == '-'; index++; }
            var saturation = raw.Length + 400;
            for (; index < raw.Length; index++)
            { token.ThrowIfCancellationRequested(); exponent = Math.Min(saturation, exponent * 10 + raw[index] - '0'); }
        }
        if (exponentNegative) exponent = -exponent;
        var coefficient = digits.ToString().TrimStart('0');
        if (coefficient.Length == 0) return negative ? Sign : 0;
        var scale = exponent - fractional;
        var end = coefficient.Length; while (coefficient[end - 1] == '0') { end--; scale++; }
        coefficient = coefficient[..end];
        var magnitude = coefficient.Length + scale - 1;
        if (magnitude > 308) return Infinity | (negative ? Sign : 0);
        if (magnitude < -324) return negative ? Sign : 0;
        token.ThrowIfCancellationRequested();
        var numerator = BigInteger.Parse(coefficient, NumberStyles.None, CultureInfo.InvariantCulture);
        var denominator = BigInteger.One;
        if (scale >= 0) numerator *= BigInteger.Pow(10, scale); else denominator = BigInteger.Pow(10, -scale);
        // At the overflow halfway, the even hypothetical successor is 2^1024 (Infinity in binary64).
        if (numerator >= (denominator * ((BigInteger.One << 54) - 1) << 970)) return Infinity | (negative ? Sign : 0);
        ulong lower = 0, upper = MaximumFinite;
        while (lower < upper)
        {
            token.ThrowIfCancellationRequested();
            var middle = lower + (upper - lower + 1) / 2;
            if (Compare(numerator, denominator, middle) >= 0) lower = middle; else upper = middle - 1;
        }
        var chosen = lower;
        if (lower != MaximumFinite)
        {
            var midpointUnits = Units(lower) + Units(lower + 1);
            var comparison = ((numerator << 1075).CompareTo(denominator * midpointUnits));
            if (comparison > 0 || comparison == 0 && (lower & 1) != 0) chosen++;
        }
        return chosen | (negative ? Sign : 0);
    }
    private static int Compare(BigInteger numerator, BigInteger denominator, ulong bits)
    {
        var fraction = bits & 0x000fffffffffffff; var biased = (int)(bits >> 52);
        var significand = biased == 0 ? new BigInteger(fraction) : new BigInteger(fraction | 0x0010000000000000);
        var exponent = biased == 0 ? -1074 : biased - 1075;
        return exponent < 0 ? (numerator << -exponent).CompareTo(denominator * significand) :
            numerator.CompareTo((denominator * significand) << exponent);
    }
    // Positive finite binary64 in integral units of 2^-1074; permits an exact hypothetical 2^1024 successor.
    private static BigInteger Units(ulong bits)
    {
        if (bits == Infinity) return BigInteger.One << 2098;
        var fraction = bits & 0x000fffffffffffff; var biased = (int)(bits >> 52);
        return biased == 0 ? new BigInteger(fraction) : new BigInteger(fraction | 0x0010000000000000) << (biased - 1);
    }
    private static string NumberText(ulong signedBits, CancellationToken token)
    {
        var bits = signedBits & ~Sign;
        if (bits == 0) return "0";
        if (bits == Infinity) return "null";
        var value = Units(bits) << 1;
        var minimum = Units(bits - 1) + Units(bits); var maximum = Units(bits) + Units(bits + 1);
        var inclusive = (bits & 1) == 0;
        var n = value.ToString(CultureInfo.InvariantCulture).Length - IntervalDenominator.ToString(CultureInfo.InvariantCulture).Length + 1;
        while (ComparePower(value, n - 1) < 0) n--;
        while (ComparePower(value, n) >= 0) n++;
        for (var k = 1; k <= 17; k++)
        {
            BigInteger? best = null; BigInteger bestDistance = default, bestDenominator = default; var bestN = 0;
            for (var trialN = n - 1; trialN <= n + 1; trialN++)
            {
                token.ThrowIfCancellationRequested();
                var scale = trialN - k;
                var multiplier = scale < 0 ? BigInteger.Pow(10, -scale) : BigInteger.One;
                var divisor = scale >= 0 ? IntervalDenominator * BigInteger.Pow(10, scale) : IntervalDenominator;
                var low = BigInteger.DivRem(minimum * multiplier, divisor, out var lowRemainder);
                if (!lowRemainder.IsZero || !inclusive) low++;
                var high = BigInteger.DivRem(maximum * multiplier, divisor, out var highRemainder);
                if (highRemainder.IsZero && !inclusive) high--;
                low = BigInteger.Max(low, BigInteger.Pow(10, k - 1)); high = BigInteger.Min(high, BigInteger.Pow(10, k) - 1);
                if (low > high) continue;
                var candidate = BigInteger.DivRem(value * multiplier, divisor, out var remainder);
                var midpoint = (remainder << 1).CompareTo(divisor);
                if (midpoint > 0 || midpoint == 0 && !candidate.IsEven) candidate++;
                candidate = BigInteger.Max(low, BigInteger.Min(candidate, high));
                var candidateDenominator = scale < 0 ? multiplier : BigInteger.One;
                var candidateNumerator = scale >= 0 ? candidate * BigInteger.Pow(10, scale) : candidate;
                var distance = BigInteger.Abs(candidateNumerator * IntervalDenominator - value * candidateDenominator);
                var ordering = best is null ? -1 : (distance * bestDenominator).CompareTo(bestDistance * candidateDenominator);
                if (ordering < 0 || ordering == 0 && candidate.IsEven && !best!.Value.IsEven)
                { best = candidate; bestN = trialN; bestDistance = distance; bestDenominator = candidateDenominator; }
            }
            if (best is { } shortest)
            {
                var digits = shortest.ToString(CultureInfo.InvariantCulture); string output;
                if (bestN >= k && bestN <= 21) output = digits + new string('0', bestN - k);
                else if (bestN > 0 && bestN <= 21) output = digits.Insert(bestN, ".");
                else if (bestN > -6 && bestN <= 0) output = "0." + new string('0', -bestN) + digits;
                else output = digits[..1] + (k == 1 ? "" : "." + digits[1..]) + "e" + (bestN - 1 < 0 ? "-" : "+") + Math.Abs(bestN - 1).ToString(CultureInfo.InvariantCulture);
                return (signedBits & Sign) != 0 ? "-" + output : output;
            }
        }
        throw new InvalidOperationException("ECMAScript numeric conversion did not settle.");
    }
    private static int ComparePower(BigInteger value, int exponent) => exponent >= 0 ?
        value.CompareTo(IntervalDenominator * BigInteger.Pow(10, exponent)) : (value * BigInteger.Pow(10, -exponent)).CompareTo(IntervalDenominator);
    private static EcmaScriptJsonProjectionException Invalid() => new(EcmaScriptJsonProjectionFailure.InvalidJson);
    private static EcmaScriptJsonProjectionException Limit() => new(EcmaScriptJsonProjectionFailure.ResourceLimit);
}
