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
/// Which SASL mechanism may be used - and what happens when a server
/// suddenly offers less than it did last time.
/// </summary>
/// <remarks>
/// The choice itself is uncontroversial: RFC 6120, section 6.3.3 leaves it to
/// the client, and the client takes the strongest mechanism it knows.
///
/// Only the server's announcement is not authenticated. It does arrive over
/// TLS, but TLS only proves that the peer holds a certificate from a trusted
/// CA - and the classic man in the middle has one. Whoever follows the
/// announcement alone thereby also follows whoever forged it: the SCRAM offers
/// disappear from the features, PLAIN remains, and the client willingly sends
/// the password itself instead of a proof that it knows it. The same move as
/// the STARTTLS downgrade, one layer up.
///
/// Against that, two lower bounds that go through the same check:
///
/// <list type="bullet">
///   <item>
///     <b><see cref="Minimum"/></b> - what the caller demands. Takes effect
///     from the very first frame, but has to be set.
///   </item>
///   <item>
///     <b><see cref="Pinned"/></b> - what the last successful login used. Takes
///     effect by itself, but only from the second connection onwards.
///   </item>
/// </list>
///
/// The pinning is therefore a trust-on-first-use: if the man in the middle is
/// already there during the very first connection attempt, it pins his
/// downgrade instead of fending it off. That is why <see cref="Minimum"/>
/// exists beside it - whoever knows what their server can do says so here and
/// needs no first time.
///
/// What it does fend off even in this form is the attack that pays off: the
/// client rebuilds by itself after every drop, and a drop can be forced. So
/// without a lower bound it is enough to disturb the connection and catch the
/// second login.
/// </remarks>
internal sealed class SaslMechanismPolicy
{

    #region Data

    /// <summary>SASL PLAIN (RFC 4616) - the password itself.</summary>
    public const String Plain         = "PLAIN";

    /// <summary>SCRAM-SHA-1 (RFC 5802).</summary>
    public const String ScramSha1     = "SCRAM-SHA-1";

    /// <summary>SCRAM-SHA-256 (RFC 7677).</summary>
    public const String ScramSha256   = "SCRAM-SHA-256";

    /// <summary>
    /// The supported mechanisms, from the weakest to the strongest. The order
    /// is the ranking; the index is the strength.
    /// </summary>
    private static readonly String[] byStrength = [Plain, ScramSha1, ScramSha256];

    private String? minimum;

    #endregion

    #region Properties

    /// <summary>
    /// The weakest mechanism that may still be used; null demands nothing.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// On a name this ranking does not know. A typo would otherwise yield
    /// strength 0 and thereby, silently, no lower bound at all - exactly what
    /// whoever set it wanted to rule out.
    /// </exception>
    public String? Minimum
    {

        get => minimum;

        set
        {

            if (value is not null && !IsKnown(value))
                throw new ArgumentException(
                          $"Unknown SASL mechanism '{value}'. Known are: {String.Join(", ", byStrength)}.",
                          nameof(value));

            minimum = value;

        }

    }

    /// <summary>
    /// The mechanism the last login succeeded with, or null before the first
    /// one.
    /// </summary>
    public String? Pinned { get; private set; }

    #endregion


    #region (static) Strength  (mechanism)

    /// <summary>
    /// The strength of a mechanism; 0 for every one this ranking does not know.
    /// </summary>
    /// <remarks>
    /// Compared ordinally. SASL mechanism names are upper case per RFC 4422,
    /// section 3.1; a "Plain" is not a spelling variant but a different name.
    /// </remarks>
    public static Int32 Strength(String? mechanism)

        => mechanism is null
               ? 0
               : Array.IndexOf(byStrength, mechanism) + 1;

    #endregion

    #region (static) IsKnown   (mechanism)

    /// <summary>Does this ranking know the mechanism?</summary>
    public static Boolean IsKnown(String mechanism)

        => Strength(mechanism) > 0;

    #endregion

    #region (static) Strongest (Offered)

    /// <summary>
    /// The strongest mechanism offered, or null if there is no known one among
    /// them.
    /// </summary>
    /// <remarks>
    /// Selected by the ranking, not by the order of the announcement - that one
    /// is determined by the server, and the server is right here the party not
    /// to be trusted.
    /// </remarks>
    public static String? Strongest(IEnumerable<String> Offered)

        => Offered.Where (IsKnown).
                   OrderByDescending(Strength).
                   FirstOrDefault();

    #endregion

    #region EnsureAcceptable(Mechanism)

    /// <summary>
    /// Throws when the mechanism is below either of the two lower bounds.
    /// </summary>
    /// <exception cref="AuthenticationException">On a downgrade.</exception>
    public void EnsureAcceptable(String Mechanism)
    {

        var strength = Strength(Mechanism);

        if (strength < Strength(minimum))
            throw new AuthenticationException(
                      $"SASL downgrade fended off: The server offers at most {Mechanism}, " +
                      $"but at least {minimum} is demanded.");

        if (strength < Strength(Pinned))
            throw new AuthenticationException(
                      $"SASL downgrade fended off: The server offers at most {Mechanism}, " +
                      $"but the last login went through {Pinned}.");

    }

    #endregion

    #region Remember       (Mechanism)

    /// <summary>
    /// Remembers the mechanism as the lower bound for the next connection.
    /// </summary>
    /// <remarks>
    /// Belongs after the successful login, not before it: a failure says
    /// nothing about what this server can do, and therefore must not pin
    /// anything either.
    /// </remarks>
    public void Remember(String Mechanism)
    {
        Pinned = Mechanism;
    }

    #endregion

}
