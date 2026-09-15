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

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// XEP-0426: what counts as one character in a message body.
/// </summary>
/// <remarks>
/// <b>Unicode code points</b> - not bytes, not the units .NET counts in, and
/// not what the eye takes for one character. Every offset that points into a
/// body is counted this way; here that is XEP-0428, and through it XEP-0461.
///
/// The three answers agree for as long as the text stays in the Latin alphabet
/// and part company the moment it does not. A globe is one code point, two
/// chars in .NET and four bytes in UTF-8; an astronaut with a skin tone is
/// several code points and one thing to look at.
///
/// <b>.NET counts in the wrong one of the three.</b> <c>Length</c> gives UTF-16
/// units, so a program that writes that number into a <c>start</c> or an
/// <c>end</c> has not made a rounding error - it has named a different place.
/// And it does so quietly: for a text without such characters both counts are
/// equal, so every test written in ASCII passes. That is what this class is
/// for. The conversion has to happen where the number goes onto the wire or
/// comes off it, and it has to be visible enough that nobody skips it.
///
/// An unpaired surrogate counts as one. It is not a code point at all, but it
/// is one thing in the string - and well-formed XML cannot carry one anyway, so
/// the case only arises on our own side of the parser.
/// </remarks>
public static class CharacterCounting
{

    /// <summary>
    /// How many characters, in the counting of XEP-0426, this text has.
    /// </summary>
    public static int Count(string text)
        => Characters(text, text.Length);

    /// <summary>
    /// The .NET index at which the given number of characters is reached.
    /// </summary>
    /// <remarks>
    /// Clamped at both ends: below zero is the beginning, and beyond the text
    /// is its end. An offset from the wire is a foreign number - the answer to
    /// one that does not fit is the nearest place that does, not an exception
    /// that ends a conversation over a quotation mark.
    /// </remarks>
    public static int Index(string text, int characters)
    {

        if (characters <= 0)
            return 0;

        var index = 0;
        var seen  = 0;

        while (index < text.Length && seen < characters)
        {
            index += IsPair(text, index) ? 2 : 1;
            seen  += 1;
        }

        return index;

    }

    /// <summary>
    /// How many characters, in the counting of XEP-0426, lie before this .NET
    /// index.
    /// </summary>
    /// <remarks>
    /// An index that falls between the halves of a surrogate pair counts the
    /// whole pair. There is no smaller answer: half of a character is not a
    /// number of characters.
    /// </remarks>
    public static int Characters(string text, int index)
    {

        if (index <= 0)
            return 0;

        var at   = 0;
        var seen = 0;

        while (at < index && at < text.Length)
        {
            at   += IsPair(text, at) ? 2 : 1;
            seen += 1;
        }

        return seen;

    }

    /// <summary>
    /// Do the two chars at this place form one code point?
    /// </summary>
    private static bool IsPair(string text, int index)
        => char.IsHighSurrogate(text[index])     &&
           index + 1 < text.Length               &&
           char.IsLowSurrogate(text[index + 1]);

}
