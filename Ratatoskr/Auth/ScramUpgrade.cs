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
/// XEP-0480: SASL Upgrade Tasks - teaching a server a mechanism it has no key
/// material for, without anybody typing their password again.
/// </summary>
/// <remarks>
/// SCRAM key material is derived through one hash and then stored, so a server
/// can only offer the variants it has material for. Moving an account from
/// SCRAM-SHA-1 to SCRAM-SHA-256 therefore means deriving afresh, which needs
/// the password - and the server does not have it. Which is why the usual
/// migration is "set every password again", with everybody locked out in
/// between. That is not a hypothetical: it is exactly what an ejabberd
/// <c>auth_scram_hash: sha256</c> costs today.
///
/// This is the way round it. The client still has the password, so after it has
/// authenticated with the mechanism that does work, the server hands it a salt
/// and an iteration count, the client computes the SaltedPassword for the new
/// mechanism, and the server derives the two keys from that. Nobody is locked
/// out and nobody is asked anything.
///
/// <b>What travels is password-equivalent for the new mechanism.</b> Whoever
/// reads the SaltedPassword can answer any SCRAM-SHA-256 challenge for that
/// account forever - it is not the password, but for this purpose that is a
/// distinction without a difference. So the exchange belongs over TLS and
/// nowhere else, and the client refuses it otherwise rather than trusting the
/// server to have asked responsibly.
/// </remarks>
public static class ScramUpgrade
{

    #region Data

    /// <summary>The namespace of the upgrade advertisement.</summary>
    public const String Namespace       = "urn:xmpp:sasl:upgrade:0";

    /// <summary>The namespace of the salt and hash elements.</summary>
    public const String DataNamespace   = "urn:xmpp:scram-upgrade:0";

    #endregion


    #region (static) TaskNameOf(Mechanism) / MechanismOf(TaskName)

    /// <summary>
    /// The task that upgrades to this mechanism - <c>UPGR-</c> and the
    /// mechanism's name.
    /// </summary>
    /// <remarks>
    /// Without the <c>-PLUS</c>, whatever the channel binding is doing. The
    /// suffix says how an exchange is bound to its connection; what is being
    /// stored here is key material, which is the same either way. A task called
    /// UPGR-SCRAM-SHA-256-PLUS would name a thing that does not exist.
    /// </remarks>
    public static String TaskNameOf(SCRAMMechanism Mechanism)

        => Mechanism switch {
               SCRAMMechanism.ScramSha256  => "UPGR-SCRAM-SHA-256",
               _                           => "UPGR-SCRAM-SHA-1"
           };

    /// <summary>
    /// The mechanism a task name upgrades to, or null for a name this
    /// implementation does not know.
    /// </summary>
    public static SCRAMMechanism? MechanismOf(String? TaskName)

        => TaskName switch {
               "UPGR-SCRAM-SHA-256"  => SCRAMMechanism.ScramSha256,
               "UPGR-SCRAM-SHA-1"    => SCRAMMechanism.ScramSha1,
               _                     => null
           };

    #endregion

    #region (static) SaltedPassword(Mechanism, Password, Salt, IterationCount)

    /// <summary>
    /// <c>Hi(Normalize(password), salt, i)</c> - the half of RFC 5802's
    /// derivation that needs the password, and therefore the only half the
    /// client can do.
    /// </summary>
    /// <remarks>
    /// SASLprep first, as everywhere else here: a password normalised one way
    /// on the login path and another way on this one would produce material
    /// that fails the very next connection, and it would fail as a wrong
    /// password rather than as a bad upgrade.
    /// </remarks>
    public static SaltedPassword SaltedPassword(SCRAMMechanism  Mechanism,
                                                String          Password,
                                                Byte[]          Salt,
                                                Int32           IterationCount)

        => Ratatoskr.SaltedPassword.Derive(Mechanism,
                                           SaslPrep.Prepare(Password),
                                           Salt,
                                           (UInt32) IterationCount);

    #endregion

}
