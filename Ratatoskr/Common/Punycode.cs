/*
 * Copyright (c) 2010-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Ratatoskr <https://www.github.com/Vanaheimr/Ratatoskr>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Text;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// Punycode per RFC 3492: the encoding that makes a Unicode domain label fit
/// into ASCII.
/// </summary>
/// <remarks>
/// <b>Computed here and not taken from the runtime</b>, although .NET brings
/// something similar along in <c>IdnMapping</c>. The reason is not pride:
/// on .NET, <c>IdnMapping</c> brings its own interpretation (UTS 46 via ICU),
/// which maps where IDNA2008 rejects - uppercase letters, for instance. Whoever
/// wants to check the validity of a label must not hand that check to something
/// that straightens things out beforehand.
///
/// The computation itself is the bootstring algorithm from section 6, with the
/// parameters from section 5. It is checked against the eleven examples from
/// section 7.1 - in both directions.
/// </remarks>
public static class Punycode
{

    #region Data

    private const Int32  Base         = 36;
    private const Int32  TMin         = 1;
    private const Int32  TMax         = 26;
    private const Int32  Skew         = 38;
    private const Int32  Damp         = 700;
    private const Int32  InitialBias  = 72;
    private const Int32  InitialN     = 0x80;
    private const Char   Delimiter    = '-';

    /// <summary>The largest code point number Unicode knows.</summary>
    private const Int32  MaxCodePoint = 0x10FFFF;

    #endregion

    #region Decode(Punycode)

    /// <summary>
    /// Decodes a Punycode label - or returns <c>null</c> if it is not one.
    /// </summary>
    public static String? Decode(String Punycode)
    {

        if (Punycode.Length == 0)
            return null;

        var output     = new List<Int32>();
        var n          = InitialN;
        var i          = 0;
        var bias       = InitialBias;

        // The last delimiter separates the ASCII part from the rest (section 6.2).
        var delimiter  = Punycode.LastIndexOf(Delimiter);

        if (delimiter > 0)
        {

            foreach (var character in Punycode[..delimiter])
            {

                if (character >= 0x80)
                    return null;

                output.Add(character);

            }

        }

        for (var index = delimiter < 0 ? 0 : delimiter + 1; index < Punycode.Length; )
        {

            var previousI = i;
            var weight    = 1;

            for (var k = Base; ; k += Base)
            {

                if (index >= Punycode.Length)
                    return null;

                var digit = Digit(Punycode[index++]);

                if (digit < 0)
                    return null;

                // Overflow: a label that could only be written with more than
                // 31 bits is not one.
                if (digit > (Int32.MaxValue - i) / weight)
                    return null;

                i += digit * weight;

                var t = k <= bias            ? TMin
                            : k >= bias + TMax ? TMax
                            : k - bias;

                if (digit < t)
                    break;

                if (weight > Int32.MaxValue / (Base - t))
                    return null;

                weight *= Base - t;

            }

            bias = Adapt(i - previousI, output.Count + 1, previousI == 0);

            if (i / (output.Count + 1) > Int32.MaxValue - n)
                return null;

            n += i / (output.Count + 1);
            i %= output.Count + 1;

            if (n > MaxCodePoint || (n >= 0xD800 && n <= 0xDFFF))
                return null;

            output.Insert(i++, n);

        }

        var sb = new StringBuilder(output.Count);

        foreach (var codePoint in output)
            sb.Append(Char.ConvertFromUtf32(codePoint));

        return sb.ToString();

    }

    #endregion

    #region Encode(Text)

    /// <summary>
    /// Encodes a label - or returns <c>null</c> if that is not possible.
    /// </summary>
    /// <remarks>
    /// The delimiter is there even when nothing non-ASCII follows (section 6.3):
    /// <c>abc</c> becomes <c>abc-</c>. For an A-label that is of no consequence -
    /// the prefix <c>xn--</c> stands in front of it anyway, and the re-encoding
    /// check compares against the same computation.
    /// </remarks>
    public static String? Encode(String Text)
    {

        var codePoints = new List<Int32>();

        for (var i = 0; i < Text.Length; i++)
        {

            if (Char.IsHighSurrogate(Text[i]) && i + 1 < Text.Length && Char.IsLowSurrogate(Text[i + 1]))
            {
                codePoints.Add(Char.ConvertToUtf32(Text[i], Text[i + 1]));
                i++;
            }

            else if (Char.IsSurrogate(Text[i]))
                return null;

            else
                codePoints.Add(Text[i]);

        }

        var sb        = new StringBuilder();
        var n         = InitialN;
        var delta     = 0;
        var bias      = InitialBias;

        foreach (var codePoint in codePoints)
            if (codePoint < 0x80)
                sb.Append((Char) codePoint);

        var handled  = sb.Length;
        var basics   = handled;

        if (basics > 0)
            sb.Append(Delimiter);

        while (handled < codePoints.Count)
        {

            // The next code point not yet handled.
            var m = Int32.MaxValue;

            foreach (var codePoint in codePoints)
                if (codePoint >= n && codePoint < m)
                    m = codePoint;

            if (m - n > (Int32.MaxValue - delta) / (handled + 1))
                return null;

            delta += (m - n) * (handled + 1);
            n      = m;

            foreach (var codePoint in codePoints)
            {

                if (codePoint < n)
                {

                    if (++delta == 0)
                        return null;

                }

                else if (codePoint == n)
                {

                    var q = delta;

                    for (var k = Base; ; k += Base)
                    {

                        var t = k <= bias            ? TMin
                                    : k >= bias + TMax ? TMax
                                    : k - bias;

                        if (q < t)
                            break;

                        sb.Append(Character(t + (q - t) % (Base - t)));
                        q = (q - t) / (Base - t);

                    }

                    sb.Append(Character(q));

                    bias    = Adapt(delta, handled + 1, handled == basics);
                    delta   = 0;
                    handled++;

                }

            }

            delta++;
            n++;

        }

        return sb.ToString();

    }

    #endregion

    #region (private) Bootstring computation

    /// <summary>RFC 3492, section 6.1: the bias adaptation.</summary>
    private static Int32 Adapt(Int32 Delta, Int32 Count, Boolean FirstAdaptation)
    {

        Delta = FirstAdaptation ? Delta / Damp : Delta / 2;
        Delta += Delta / Count;

        var k = 0;

        while (Delta > ((Base - TMin) * TMax) / 2)
        {
            Delta /= Base - TMin;
            k     += Base;
        }

        return k + (Base - TMin + 1) * Delta / (Delta + Skew);

    }

    /// <summary>The value of a digit of the base-36 alphabet, or -1.</summary>
    private static Int32 Digit(Char Character)

        => Character switch {
               >= 'a' and <= 'z'  => Character - 'a',
               >= 'A' and <= 'Z'  => Character - 'A',
               >= '0' and <= '9'  => Character - '0' + 26,
               _                  => -1
           };

    /// <summary>The digit for a value - lowercase letters, then digits.</summary>
    private static Char Character(Int32 Digit)

        => (Char) (Digit < 26 ? Digit + 'a' : Digit - 26 + '0');

    #endregion

}
