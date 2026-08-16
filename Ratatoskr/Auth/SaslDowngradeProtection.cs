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
using System.Security.Cryptography;

#endregion

namespace org.GraphDefined.Vanaheimr.Ratatoskr;

/// <summary>
/// XEP-0474: SASL SCRAM Downgrade Protection - the announcement, hashed.
/// </summary>
/// <remarks>
/// The lower bounds in <c>SaslMechanismPolicy</c> are a guess about what a
/// server ought to be able to do. This is the measurement instead: the server
/// hashes the list it announced and puts the result into its
/// server-first-message as the attribute <c>h</c>; the client hashes the list
/// it actually received and compares.
///
/// <b>What makes it work is where the attribute sits, and it costs nothing to
/// get right.</b> RFC 5802 builds the AuthMessage out of
/// client-first-bare + server-first + client-final-without-proof, verbatim -
/// so anything the server writes into its server-first-message is already
/// covered by the client proof and the server signature. A man in the middle
/// who strikes SCRAM-SHA-256 from the features and then forges an <c>h</c> to
/// match his shortened list cannot also produce the proof, because he does not
/// know the password. He is left with a choice between a hash the client
/// refuses and a signature the client refuses.
///
/// <b>The one thing this does not cover</b> is a server that never sends
/// <c>h</c> at all - which is every server that has not implemented an
/// experimental XEP, and a man in the middle who simply omits it. Absence is
/// therefore not a failure here, only an unanswered question, and
/// <c>SCRAMAuthenticator.DowngradeProtection</c> reports which of the three it
/// was. The lower bounds stay for that reason: they need no cooperation from
/// the far side.
/// </remarks>
public static class SaslDowngradeProtection
{

    #region Data

    /// <summary>
    /// Between two entries of a list: RS, %x1E.
    /// </summary>
    public const Char EntrySeparator    = '\u001E';

    /// <summary>
    /// Between the mechanisms and the channel-binding types: US, %x1F.
    /// </summary>
    public const Char SectionSeparator  = '\u001F';

    #endregion


    #region (static) HashInput(Mechanisms, ChannelBindingTypes = null)

    /// <summary>
    /// The string the hash is taken over (XEP-0474, section 4).
    /// </summary>
    /// <param name="Mechanisms">
    /// Every SASL mechanism the server announced - all of them, not only the
    /// ones this implementation knows. The point is what was on offer.
    /// </param>
    /// <param name="ChannelBindingTypes">
    /// The channel-binding types out of XEP-0440, or null/empty when the
    /// server announced none. The separator and this section are written only
    /// when there is something to write: a server that announces no
    /// channel bindings hashes the mechanisms alone.
    /// </param>
    /// <remarks>
    /// Sorted "using the i;octet collation" - byte order, RFC 4790, section
    /// 9.3. <see cref="StringComparer.Ordinal"/> compares UTF-16 code units,
    /// which is the same order for these two lists and only for them: SASL
    /// mechanism names are 1 to 20 characters of A-Z, 0-9, hyphen and
    /// underscore (RFC 4422, section 3.1), and channel-binding type names are
    /// ASCII too (RFC 5056, section 7). Nothing here reaches U+0080, where the
    /// two orders begin to differ.
    /// </remarks>
    public static String HashInput(IEnumerable<String>   Mechanisms,
                                   IEnumerable<String>?  ChannelBindingTypes   = null)
    {

        var builder = new StringBuilder();

        builder.Append(String.Join(EntrySeparator,
                                   Mechanisms.OrderBy(m => m, StringComparer.Ordinal)));

        var bindings = ChannelBindingTypes?.OrderBy(t => t, StringComparer.Ordinal).ToArray() ?? [];

        if (bindings.Length > 0)
        {
            builder.Append(SectionSeparator);
            builder.Append(String.Join(EntrySeparator, bindings));
        }

        return builder.ToString();

    }

    #endregion

    #region (static) Hash(Mechanism, Input)

    /// <summary>
    /// base64 of the hash over <paramref name="Input"/>, taken with the hash of
    /// the SCRAM mechanism in use - SHA-1 for SCRAM-SHA-1, SHA-256 for
    /// SCRAM-SHA-256.
    /// </summary>
    public static String Hash(SCRAMMechanism Mechanism, String Input)
    {

        var bytes = Encoding.UTF8.GetBytes(Input);

        return Convert.ToBase64String(
                   Mechanism == SCRAMMechanism.ScramSha256
                       ? SHA256.HashData(bytes)
                       : SHA1.  HashData(bytes)
               );

    }

    #endregion

    #region (static) Expected(Mechanism, Mechanisms, ChannelBindingTypes = null)

    /// <summary>
    /// The value the attribute <c>h</c> has to carry for this announcement.
    /// </summary>
    public static String Expected(SCRAMMechanism        Mechanism,
                                  IEnumerable<String>   Mechanisms,
                                  IEnumerable<String>?  ChannelBindingTypes   = null)

        => Hash(Mechanism,
                HashInput(Mechanisms, ChannelBindingTypes));

    #endregion

}

/// <summary>
/// What became of the downgrade protection during a SCRAM exchange.
/// </summary>
/// <remarks>
/// Three states and not two, because "not checked" and "checked and correct"
/// are the same colour to anyone who only asks whether the login worked - the
/// same mistake as reading a green test run without its skip count.
/// </remarks>
public enum SaslDowngradeProtectionResult
{

    /// <summary>
    /// The server sent no <c>h</c>. Every server that has not implemented
    /// XEP-0474 looks like this, and so does a man in the middle who leaves it
    /// out - the two are not distinguishable from here.
    /// </summary>
    NotOffered,

    /// <summary>
    /// The server sent an <c>h</c> that matches the announcement this client
    /// received, and the proof covers it.
    /// </summary>
    Verified,

    /// <summary>
    /// The server sent an <c>h</c> over a different announcement than the one
    /// that arrived here. The exchange is broken off.
    /// </summary>
    Mismatch

}
